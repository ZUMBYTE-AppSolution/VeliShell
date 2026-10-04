using System.IO;
using System.Text;
using System.Text.Json;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Temporarily hides taskbar windows owned by the current Windows shell. After
/// Windows confirms the hide, the in-memory work area for each unchanged
/// monitor is expanded with non-persistent SPI_SETWORKAREA calls. Original work
/// areas are first written to an atomic recovery snapshot and restored before
/// the session ends or on the next launch. Explorer, Registry, taskbar
/// preferences and third-party appbar registrations are never modified.
/// </summary>
internal sealed class TaskbarVisibilityService : IDisposable
{
    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const int WorkAreaRecoveryVersion = 2;
    private const long MaximumRecoveryFileBytes = 64 * 1024;
    private static readonly TimeSpan[] RestoreRetryDelays =
    [
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(220)
    ];

    private readonly object _gate = new();
    private readonly Dictionary<nint, TrackedTaskbar> _tracked = [];
    private readonly Dictionary<string, WorkAreaSnapshot> _workAreas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskbarRecoverySnapshot> _taskbarRecovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskbarRecoveryMiss> _taskbarRecoveryMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WindowReflowTarget> _pendingWindowReflow = [];
    private nint _menuBarHandle;
    private bool _menuBarRegistered;
    private bool _workAreaNotificationPending;
    private bool _hideRequested;
    private bool _ownershipConfirmed;
    private bool _recoverySession;
    private bool _recoverExistingHiddenTaskbars;
    private bool _isHidden;
    private bool _disposed;
    private string _lastStatus = L("Taskbar.VisibleDefault");

    private static string WorkAreaRecoveryPath => Path.Combine(App.DataDirectory, "taskbar-workarea-recovery.json");

    /// <summary>
    /// Recovery is deliberately not performed in the constructor. App creates
    /// this service before acquiring its single-instance mutex; doing system
    /// recovery there would let a rejected second process disturb the owner.
    /// </summary>
    internal void RecoverWorkAreasAfterOwnershipConfirmed()
    {
        var queueVerification = false;
        lock (_gate)
        {
            _ownershipConfirmed = true;
            if (!LoadWorkAreaRecoveryCore())
            {
                if (File.Exists(WorkAreaRecoveryPath))
                {
                    _isHidden = true;
                    _lastStatus = L("Taskbar.WorkAreaRecoveryPending");
                }
                return;
            }
            var restored = RestoreCore();
            _isHidden = !restored;
            if (!restored) _lastStatus = L("Taskbar.WorkAreaRecoveryPending");
            queueVerification = !restored;
        }
        FlushWorkAreaEffects();
        if (queueVerification) QueueRestoreVerification();
    }

    internal bool IsHidden
    {
        get { lock (_gate) return _isHidden; }
    }

    internal string LastStatus
    {
        get { lock (_gate) return _lastStatus; }
    }

    /// <summary>
    /// Registers VeliShell's own top-edge appbar. Windows composes this
    /// reservation with the taskbar and third-party appbars, so no foreign
    /// reservation is guessed or overwritten. The registration is tied to the
    /// menu window and disappears if the process terminates unexpectedly.
    /// </summary>
    internal bool RegisterMenuBar(
        nint handle,
        uint callbackMessage,
        int requestedHeight,
        out NativeMethods.Rect reservedBounds)
    {
        reservedBounds = default;
        lock (_gate)
        {
            if (_disposed || handle == 0 || !NativeMethods.IsWindow(handle) ||
                callbackMessage < NativeMethods.WmApp || requestedHeight <= 0)
                return false;

            if (_menuBarRegistered && _menuBarHandle != handle)
                ReleaseMenuBarCore(_menuBarHandle);

            if (!_menuBarRegistered)
            {
                var registration = CreateAppBarData(handle, callbackMessage);
                if (NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref registration) == 0)
                    return false;
                _menuBarHandle = handle;
                _menuBarRegistered = true;
            }

            if (UpdateMenuBarCore(handle, requestedHeight, out reservedBounds)) return true;
            ReleaseMenuBarCore(handle);
            return false;
        }
    }

    /// <summary>
    /// Re-negotiates the appbar rectangle after DPI, display, or another
    /// appbar changes. Only VeliShell's registered window can be updated.
    /// </summary>
    internal bool UpdateMenuBar(nint handle, int requestedHeight, out NativeMethods.Rect reservedBounds)
    {
        reservedBounds = default;
        lock (_gate)
            return !_disposed && _menuBarRegistered && _menuBarHandle == handle &&
                   UpdateMenuBarCore(handle, requestedHeight, out reservedBounds);
    }

    internal void ReleaseMenuBar(nint handle)
    {
        lock (_gate)
        {
            if (!_menuBarRegistered || _menuBarHandle != handle) return;
            ReleaseMenuBarCore(handle);
        }
    }

    /// <summary>
    /// Starts a reversible hide session. A primary taskbar must be found before
    /// any taskbar window is changed. ShowWindowAsync reports the previous
    /// visibility state rather than command success, so handles are validated
    /// immediately before a best-effort visibility request is posted.
    /// </summary>
    internal bool Hide(bool recoverExistingHiddenTaskbars = false)
    {
        var queueRestore = false;
        bool result;
        lock (_gate)
        {
            if (!EnsureUsable()) return false;
            if (_hideRequested)
            {
                if (recoverExistingHiddenTaskbars) _recoverExistingHiddenTaskbars = true;
                result = ReconcileCore();
                queueRestore = !result && !_hideRequested &&
                               (_recoverySession || HasRestoreCandidatesCore() || _workAreas.Count > 0 || _taskbarRecovery.Count > 0);
            }
            else
            {
                if (!TryDiscoverTaskbars(out _, out var taskbars, out var discoveryError))
                {
                    _lastStatus = discoveryError;
                    return false;
                }
                if (!CaptureWorkAreasCore(taskbars))
                {
                    _lastStatus = L("Taskbar.WorkAreaSnapshotFailed");
                    return false;
                }
                _recoverExistingHiddenTaskbars = recoverExistingHiddenTaskbars;
                _hideRequested = true;
                result = ReconcileCore();
                if (!result)
                {
                    _hideRequested = false;
                    queueRestore = _recoverySession || HasRestoreCandidatesCore() || _workAreas.Count > 0 || _taskbarRecovery.Count > 0;
                }
            }

            if (!result && !queueRestore)
            {
                _tracked.Clear();
                _recoverExistingHiddenTaskbars = false;
                _isHidden = false;
            }
        }

        FlushWorkAreaEffects();
        if (queueRestore) QueueRestoreVerification();
        return result;
    }

    /// <summary>
    /// Hides the taskbars and reports success only after Windows confirms that
    /// every currently discovered shell taskbar is no longer visible.
    /// </summary>
    internal async Task<bool> HideAsync(
        bool recoverExistingHiddenTaskbars = false,
        CancellationToken cancellationToken = default)
    {
        var started = Hide(recoverExistingHiddenTaskbars);
        if (!started) return false;

        var workAreaFailure = false;
        var workAreasExpanded = false;
        foreach (var delay in RestoreRetryDelays)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !_hideRequested) return false;
                if (TryDiscoverTaskbars(out _, out var taskbars, out _)
                    && taskbars.All(taskbar => !NativeMethods.IsWindowVisible(taskbar.Handle)))
                {
                    foreach (var taskbar in taskbars)
                        TrackTaskbar(taskbar, wasVisible: false);
                    _isHidden = true;
                    _lastStatus = taskbars.Count == 1
                        ? L("Taskbar.Hidden")
                        : LF("Taskbar.HiddenDisplays", taskbars.Count);
                    if (TrySynchronizeWorkAreasCore(taskbars))
                    {
                        workAreasExpanded = true;
                    }
                    else
                    {
                        _hideRequested = false;
                        _lastStatus = L("Taskbar.WorkAreaExpandFailed");
                        workAreaFailure = true;
                    }

                }

                // Explorer can recreate taskbar windows while the asynchronous
                // command is in flight. Re-enumerate and request hiding again.
                if (!workAreaFailure && !workAreasExpanded) ReconcileCore();
            }
            if (workAreasExpanded) break;
            if (workAreaFailure) break;
        }

        if (workAreasExpanded)
        {
            FlushWorkAreaEffects();
            return true;
        }

        if (!workAreaFailure) lock (_gate)
        {
            _hideRequested = false;
            _lastStatus = L("Taskbar.HideUnconfirmed");
        }
        // Roll back only windows changed by this hide attempt. A shell taskbar
        // that was already invisible must not be uncovered by a failed attempt.
        await RestoreAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Re-enumerates all shell taskbars. This covers monitor changes and taskbar
    /// windows recreated by Explorer while a hide session is active.
    /// </summary>
    internal bool Reconcile()
    {
        var queueRestore = false;
        bool result;
        lock (_gate)
        {
            if (!EnsureUsable()) return false;
            result = _hideRequested ? ReconcileCore() : RestoreCore();
            queueRestore = !result && !_hideRequested &&
                           (_recoverySession || HasRestoreCandidatesCore() || _workAreas.Count > 0 || _taskbarRecovery.Count > 0);
        }

        FlushWorkAreaEffects();
        if (queueRestore) QueueRestoreVerification();
        return result;
    }

    /// <summary>
    /// Ends the hide session and restores only windows that were visible before
    /// this service hid them. Calls are safe to repeat.
    /// </summary>
    internal bool Restore(bool recoverCurrentShellTaskbars = false)
    {
        bool result;
        lock (_gate)
        {
            if (_disposed) return _tracked.Count == 0 && _workAreas.Count == 0 && _taskbarRecovery.Count == 0;
            _hideRequested = false;
            if (recoverCurrentShellTaskbars) _recoverySession = true;
            result = RestoreCore();
        }
        FlushWorkAreaEffects();
        return result;
    }

    /// <summary>
    /// Requests restoration and verifies it without blocking the UI thread. The
    /// tracked handles are retained until their visibility has actually changed.
    /// </summary>
    internal async Task<bool> RestoreAsync(
        bool recoverCurrentShellTaskbars = false,
        CancellationToken cancellationToken = default)
    {
        bool needsDelayedVerification;
        bool restoredImmediately;
        lock (_gate)
        {
            if (_disposed)
            {
                needsDelayedVerification = false;
                restoredImmediately = _tracked.Count == 0 && _workAreas.Count == 0 && _taskbarRecovery.Count == 0;
            }
            else
            {
            needsDelayedVerification = recoverCurrentShellTaskbars || _hideRequested || _recoverySession ||
                                           HasRestoreCandidatesCore() || _workAreas.Count > 0 || _taskbarRecovery.Count > 0;
                _hideRequested = false;
                if (recoverCurrentShellTaskbars) _recoverySession = true;
                restoredImmediately = RestoreCore(clearVisibleCandidates: !needsDelayedVerification);
            }
        }
        FlushWorkAreaEffects();
        if (!needsDelayedVerification) return restoredImmediately;

        foreach (var delay in RestoreRetryDelays)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            bool? passResult = null;
            lock (_gate)
            {
                if (_disposed)
                    passResult = _tracked.Count == 0 && _workAreas.Count == 0 && _taskbarRecovery.Count == 0;
                else if (_hideRequested)
                    passResult = false;
                // Re-enumerate and re-check retained candidates even if the
                // preceding pass looked visible. This closes the ShowWindowAsync
                // race without adopting unrelated, already-hidden taskbars.
                else if (RestoreCore(clearVisibleCandidates: true))
                    passResult = true;
            }
            FlushWorkAreaEffects();
            if (passResult.HasValue) return passResult.Value;
        }

        bool finalResult;
        lock (_gate)
        {
            finalResult = !_isHidden;
            if (!finalResult) _lastStatus = L("Taskbar.RestoreUnconfirmed");
        }
        FlushWorkAreaEffects();
        return finalResult;
    }

    private bool ReconcileCore()
    {
        if (!TryDiscoverTaskbars(out var shellProcessId, out var taskbars, out var error))
        {
            _lastStatus = error;
            _isHidden = HasRestoreCandidatesCore() || _workAreas.Count > 0 || _taskbarRecovery.Count > 0;
            return false;
        }

        // Drop stale handles before considering new windows. A destroyed HWND can
        // be reused, so every retained entry must still be the same shell window.
        foreach (var tracked in _tracked.Values.ToArray())
        {
            if (!IsExpectedTaskbar(tracked.Handle, shellProcessId, tracked.ClassName))
                _tracked.Remove(tracked.Handle);
        }

        if (taskbars.Any(taskbar =>
                NativeMethods.IsWindowVisible(taskbar.Handle) &&
                !_taskbarRecovery.ContainsKey(TaskbarRecoveryKey(taskbar.ClassName, taskbar.DeviceName))))
        {
            // The shell/display topology changed after the durable snapshot.
            // Abort instead of hiding an unjournaled taskbar or guessing a new
            // work area while the session is already active.
            _hideRequested = false;
            RollBackCore(shellProcessId);
            _lastStatus = L("Taskbar.HidePartial");
            return false;
        }

        var changed = 0;
        foreach (var taskbar in taskbars)
        {
            var wasVisible = NativeMethods.IsWindowVisible(taskbar.Handle);
            TrackTaskbar(taskbar, wasVisible);
            if (!wasVisible) continue;

            // Validate again immediately before changing another process's window.
            if (!IsExpectedTaskbar(taskbar.Handle, shellProcessId, taskbar.ClassName))
            {
                _lastStatus = L("Taskbar.HidePartial");
                _hideRequested = false;
                RollBackCore(shellProcessId);
                return false;
            }

            _ = NativeMethods.ShowWindowAsync(taskbar.Handle, NativeMethods.SwHide);
            changed++;
        }

        _isHidden = true;
        _lastStatus = changed > 0
            ? LF("Taskbar.HiddenDisplays", changed)
            : L("Taskbar.Hidden");

        // Explorer can recreate its appbar reservation after the taskbar window
        // was hidden (most commonly after a display/DPI change). Only repair the
        // work area after every taskbar is confirmed hidden. The synchronizer
        // accepts the exact journaled baseline, or a newly observed taskbar edge
        // that can be identified without touching another appbar reservation.
        if (changed == 0 && !TrySynchronizeWorkAreasCore(taskbars))
        {
            _hideRequested = false;
            RollBackCore(shellProcessId);
            _lastStatus = L("Taskbar.WorkAreaExpandFailed");
            return false;
        }
        return true;
    }

    private bool RestoreCore(bool clearVisibleCandidates = true)
    {
        if (_ownershipConfirmed && _workAreas.Count == 0 && _taskbarRecovery.Count == 0 &&
            File.Exists(WorkAreaRecoveryPath) && !LoadWorkAreaRecoveryCore())
        {
            _isHidden = true;
            _lastStatus = L("Taskbar.WorkAreaRecoveryPending");
            return false;
        }
        // Restore the desktop geometry first. This is synchronous and prevents
        // a stale full-monitor work area if taskbar window restoration needs a
        // later asynchronous verification pass.
        var workAreasRestored = RestoreWorkAreasCore();
        if (!_recoverySession && !HasRestoreCandidatesCore() && _taskbarRecovery.Count == 0)
        {
            _tracked.Clear();
            _recoverExistingHiddenTaskbars = false;
            _isHidden = !workAreasRestored;
            _lastStatus = workAreasRestored ? L("Taskbar.Visible") : L("Taskbar.RestoreUnconfirmed");
            return workAreasRestored;
        }

        if (!TryDiscoverTaskbars(out var shellProcessId, out var taskbars, out var error))
        {
            _isHidden = true;
            _lastStatus = error.Length > 0
                ? error + " " + L("Taskbar.Retry")
                : L("Taskbar.ShellRetry");
            return false;
        }

        if (!AdoptPersistedTaskbarsCore(shellProcessId, taskbars))
        {
            _isHidden = true;
            _lastStatus = L("Taskbar.RestoreUnconfirmed");
            return false;
        }

        // Explicit recovery (button/emergency hotkey) may restore current shell
        // taskbars even when this process did not hide them. Normal shutdown and
        // hide rollback are deliberately limited to RestoreOnExit candidates.
        if (_recoverySession)
            foreach (var taskbar in taskbars)
                TrackTaskbar(taskbar, wasVisible: true);

        var currentByHandle = taskbars.ToDictionary(taskbar => taskbar.Handle);
        foreach (var tracked in _tracked.Values.ToArray())
        {
            if (!currentByHandle.TryGetValue(tracked.Handle, out var currentTaskbar) ||
                !string.Equals(currentTaskbar.DeviceName, tracked.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                !IsExpectedTaskbar(tracked.Handle, shellProcessId, tracked.ClassName))
            {
                // The original window no longer exists or Explorer was recreated.
                // Never show a handle that may now belong to another window.
                _tracked.Remove(tracked.Handle);
                continue;
            }

            if (!tracked.RestoreOnExit)
            {
                _tracked.Remove(tracked.Handle);
                continue;
            }

            if (NativeMethods.IsWindowVisible(tracked.Handle))
            {
                _taskbarRecovery.Remove(TaskbarRecoveryKey(tracked.ClassName, tracked.DeviceName));
                // RestoreAsync keeps confirmed-visible handles for one delayed
                // pass so a previously posted hide cannot win the race.
                if (clearVisibleCandidates) _tracked.Remove(tracked.Handle);
                continue;
            }

            _ = NativeMethods.ShowWindowAsync(tracked.Handle, NativeMethods.SwShowNoActivate);
        }

        // ShowWindowAsync only posts the request. Success is reported only after
        // a later observation proves that every current shell taskbar is visible.
        if (_tracked.Values.Any(tracked =>
                tracked.RestoreOnExit && !NativeMethods.IsWindowVisible(tracked.Handle)))
        {
            _ = PersistWorkAreasCore();
            _isHidden = true;
            _lastStatus = L("Taskbar.Restoring");
            return false;
        }

        if (!clearVisibleCandidates)
        {
            var persisted = PersistWorkAreasCore();
            _isHidden = !workAreasRestored;
            _lastStatus = workAreasRestored && persisted ? L("Taskbar.VisibleChecking") : L("Taskbar.RestoreUnconfirmed");
            return workAreasRestored && persisted;
        }

        if (_recoverySession &&
            (!TryDiscoverTaskbars(out _, out var verifiedTaskbars, out error) ||
             verifiedTaskbars.Any(taskbar => !NativeMethods.IsWindowVisible(taskbar.Handle))))
        {
            foreach (var taskbar in verifiedTaskbars.Where(taskbar => !NativeMethods.IsWindowVisible(taskbar.Handle)))
            {
                TrackTaskbar(taskbar, wasVisible: true);
                _ = NativeMethods.ShowWindowAsync(taskbar.Handle, NativeMethods.SwShowNoActivate);
            }
            _isHidden = true;
            _lastStatus = L("Taskbar.Restoring");
            return false;
        }

        _recoverySession = false;
        _recoverExistingHiddenTaskbars = false;
        _tracked.Clear();
        var recoveryPersisted = PersistWorkAreasCore();
        var fullyRestored = workAreasRestored && recoveryPersisted && _taskbarRecovery.Count == 0;
        _isHidden = !fullyRestored;
        _lastStatus = fullyRestored ? L("Taskbar.Restored") : L("Taskbar.RestoreUnconfirmed");
        return fullyRestored;
    }

    private bool AdoptPersistedTaskbarsCore(uint shellProcessId, IReadOnlyList<TrackedTaskbar> currentTaskbars)
    {
        if (_taskbarRecovery.Count == 0) return true;
        if (!NativeMethods.TryAllMonitors(out var monitors)) return false;
        var devices = monitors.Select(monitor => MonitorKey(monitor.Info))
            .Where(device => device.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in _taskbarRecovery.Values.ToArray())
        {
            var recoveryKey = TaskbarRecoveryKey(snapshot.ClassName, snapshot.DeviceName);
            if (!devices.Contains(snapshot.DeviceName))
            {
                // The original monitor no longer exists. Never adopt a taskbar
                // from a different display merely because its HWND/class matches.
                _taskbarRecovery.Remove(recoveryKey);
                _taskbarRecoveryMisses.Remove(recoveryKey);
                continue;
            }

            var matches = currentTaskbars.Where(taskbar =>
                    string.Equals(taskbar.ClassName, snapshot.ClassName, StringComparison.Ordinal) &&
                    string.Equals(taskbar.DeviceName, snapshot.DeviceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0 && snapshot.ClassName == SecondaryTaskbarClass &&
                currentTaskbars.Any(taskbar => taskbar.ClassName == PrimaryTaskbarClass))
            {
                var miss = _taskbarRecoveryMisses.GetValueOrDefault(recoveryKey);
                var missCount = miss?.ShellProcessId == shellProcessId ? miss.Count + 1 : 1;
                _taskbarRecoveryMisses[recoveryKey] = new TaskbarRecoveryMiss(shellProcessId, missCount);
                if (ShouldRetireMissingSecondary(snapshot.ClassName, matches.Count, true, missCount))
                {
                    // Explorer is stable and the connected display no longer has
                    // a secondary taskbar (for example the Windows setting was
                    // turned off). rcWork was handled separately, so retaining
                    // this visibility-only marker would block recovery forever.
                    _taskbarRecovery.Remove(recoveryKey);
                    _taskbarRecoveryMisses.Remove(recoveryKey);
                }
                continue;
            }
            _taskbarRecoveryMisses.Remove(recoveryKey);
            if (matches.Count != 1) continue;
            var taskbar = matches[0];
            if (NativeMethods.IsWindowVisible(taskbar.Handle))
            {
                _taskbarRecovery.Remove(recoveryKey);
                continue;
            }
            TrackTaskbar(taskbar, wasVisible: snapshot.WasVisible);
        }
        return PersistWorkAreasCore();
    }

    private static bool ShouldRetireMissingSecondary(
        string className,
        int matchingTaskbars,
        bool primaryTaskbarStable,
        int stableMissCount) =>
        className == SecondaryTaskbarClass && matchingTaskbars == 0 && primaryTaskbarStable &&
        stableMissCount >= RestoreRetryDelays.Length + 1;

    private void RollBackCore(uint shellProcessId)
    {
        foreach (var tracked in _tracked.Values.ToArray())
        {
            if (!IsExpectedTaskbar(tracked.Handle, shellProcessId, tracked.ClassName))
            {
                _tracked.Remove(tracked.Handle);
                continue;
            }

            if (!tracked.RestoreOnExit)
            {
                _tracked.Remove(tracked.Handle);
                continue;
            }

            // Keep the handle until a later pass verifies visibility. A pending
            // asynchronous hide request may otherwise arrive after this method.
            _ = NativeMethods.ShowWindowAsync(tracked.Handle, NativeMethods.SwShowNoActivate);
        }

        var workAreasRestored = RestoreWorkAreasCore();
        _isHidden = HasRestoreCandidatesCore() || !workAreasRestored;
    }

    private void TrackTaskbar(TrackedTaskbar taskbar, bool wasVisible)
    {
        var restoreOnExit = wasVisible || _recoverExistingHiddenTaskbars;
        if (!_tracked.TryGetValue(taskbar.Handle, out var existing))
        {
            _tracked.Add(taskbar.Handle, taskbar with { RestoreOnExit = restoreOnExit });
            return;
        }

        if (restoreOnExit && !existing.RestoreOnExit)
            _tracked[taskbar.Handle] = existing with { RestoreOnExit = true };
    }

    private bool CaptureWorkAreasCore(IReadOnlyList<TrackedTaskbar> taskbars)
    {
        if (_workAreas.Count > 0 || _taskbarRecovery.Count > 0 || File.Exists(WorkAreaRecoveryPath)) return false;
        if (!NativeMethods.TryAllMonitors(out var monitors) || taskbars.Count == 0) return false;

        _workAreas.Clear();
        _taskbarRecovery.Clear();
        _taskbarRecoveryMisses.Clear();
        foreach (var taskbar in taskbars.Where(taskbar => NativeMethods.IsWindowVisible(taskbar.Handle)))
        {
            var monitorHandle = NativeMethods.MonitorFromWindow(taskbar.Handle, 2);
            var monitorMatches = monitors.Where(candidate => candidate.Handle == monitorHandle).ToList();
            if (monitorMatches.Count != 1 || !NativeMethods.GetWindowRect(taskbar.Handle, out var taskbarBounds))
            {
                _workAreas.Clear();
                _taskbarRecovery.Clear();
                return false;
            }

            var monitor = monitorMatches[0];
            if (!IsValidWorkArea(monitor.Info.Monitor, monitor.Info.Work))
            {
                _workAreas.Clear();
                _taskbarRecovery.Clear();
                return false;
            }
            var key = MonitorKey(monitor.Info);
            if (key.Length == 0 || _workAreas.ContainsKey(key) ||
                !TryReleaseOnlyTaskbarEdge(
                    monitor.Info.Monitor,
                    monitor.Info.Work,
                    taskbarBounds,
                    out var appliedWorkArea))
            {
                _workAreas.Clear();
                _taskbarRecovery.Clear();
                return false;
            }
            _workAreas[key] = new WorkAreaSnapshot(
                monitor.Handle,
                key,
                monitor.Info.Monitor,
                monitor.Info.Work,
                appliedWorkArea);
            var recoveryKey = TaskbarRecoveryKey(taskbar.ClassName, key);
            _taskbarRecovery[recoveryKey] = new TaskbarRecoverySnapshot(taskbar.ClassName, key, WasVisible: true);
        }
        if (PersistWorkAreasCore()) return true;
        _workAreas.Clear();
        _taskbarRecovery.Clear();
        return false;
    }

    private bool TrySynchronizeWorkAreasCore(IReadOnlyList<TrackedTaskbar> taskbars)
    {
        if (!NativeMethods.TryAllMonitors(out var monitors)) return false;

        var current = monitors
            .GroupBy(monitor => MonitorKey(monitor.Info), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        var taskbarsByDevice = taskbars
            .GroupBy(taskbar => taskbar.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        if (taskbarsByDevice.Count != taskbars.Count) return false;

        var previousSnapshots = _workAreas.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var planned = new List<WorkAreaMutation>();
        var journalChanged = false;
        foreach (var taskbar in taskbarsByDevice.Values)
        {
            if (!current.TryGetValue(taskbar.DeviceName, out var monitor) ||
                !NativeMethods.GetWindowRect(taskbar.Handle, out var taskbarBounds) ||
                !IsValidWorkArea(monitor.Info.Monitor, monitor.Info.Work))
                return false;

            _workAreas.TryGetValue(taskbar.DeviceName, out var snapshot);
            var action = ClassifyHiddenWorkArea(
                snapshot is not null,
                snapshot?.MonitorBounds ?? default,
                snapshot?.OriginalWorkArea ?? default,
                snapshot?.AppliedWorkArea ?? default,
                monitor.Info.Monitor,
                monitor.Info.Work,
                taskbarBounds);

            if (action == HiddenWorkAreaAction.Healthy) continue;
            if (action == HiddenWorkAreaAction.PreserveExternal)
            {
                // The taskbar edge is already released or a different appbar has
                // changed the same edge. Forget an obsolete geometry snapshot so
                // restore cannot overwrite that newer owner; taskbar visibility
                // remains independently journaled.
                if (snapshot is not null)
                {
                    _workAreas.Remove(taskbar.DeviceName);
                    journalChanged = true;
                }
                continue;
            }

            if (!_taskbarRecovery.ContainsKey(TaskbarRecoveryKey(taskbar.ClassName, taskbar.DeviceName)))
                return false;

            if (action == HiddenWorkAreaAction.Recapture)
            {
                if (!TryReleaseOnlyTaskbarEdge(
                        monitor.Info.Monitor,
                        monitor.Info.Work,
                        taskbarBounds,
                        out var refreshedApplied) ||
                    RectEquals(refreshedApplied, monitor.Info.Work))
                    return false;
                snapshot = new WorkAreaSnapshot(
                    monitor.Handle,
                    taskbar.DeviceName,
                    monitor.Info.Monitor,
                    monitor.Info.Work,
                    refreshedApplied);
                _workAreas[taskbar.DeviceName] = snapshot;
                journalChanged = true;
            }

            if (snapshot is null) return false;
            var reflowTargets = CaptureWindowReflowTargetsCore(
                snapshot.DeviceName, monitor.Info.Work, snapshot.AppliedWorkArea);
            if (reflowTargets is null)
            {
                RestoreSnapshotDictionary(previousSnapshots);
                return false;
            }
            planned.Add(new WorkAreaMutation(snapshot, monitor.Handle, monitor.Info.Work, reflowTargets));
        }

        // A refreshed topology/baseline must be durable before any system work
        // area is changed, otherwise a crash could leave no exact rollback data.
        if (journalChanged && !PersistWorkAreasCore())
        {
            RestoreSnapshotDictionary(previousSnapshots);
            return false;
        }

        var committedReflowTargets = new List<WindowReflowTarget>();
        foreach (var mutation in planned)
        {
            var snapshot = mutation.Snapshot;
            var verifiedBefore = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (!NativeMethods.GetMonitorInfo(mutation.MonitorHandle, ref verifiedBefore) ||
                !RectEquals(verifiedBefore.Monitor, snapshot.MonitorBounds) ||
                !RectEquals(verifiedBefore.Work, mutation.SourceWorkArea))
            {
                RollBackWorkAreasWithoutPublishingCore();
                return false;
            }

            var expanded = snapshot.AppliedWorkArea;
            if (!NativeMethods.SystemParametersInfo(NativeMethods.SpiSetWorkArea, 0, ref expanded, 0))
            {
                RollBackWorkAreasWithoutPublishingCore();
                return false;
            }

            var verified = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(mutation.MonitorHandle, ref verified) ||
                !RectEquals(verified.Work, snapshot.AppliedWorkArea))
            {
                RollBackWorkAreasWithoutPublishingCore();
                return false;
            }
            committedReflowTargets.AddRange(mutation.ReflowTargets);
        }
        if (planned.Count > 0) _pendingWindowReflow.AddRange(committedReflowTargets);
        // Do not broadcast WM_SETTINGCHANGE while the shell taskbar appbar is
        // still registered: Explorer handles that broadcast by claiming the
        // taskbar edge again, recreating the black reserved band. Cancelling a
        // pending restore broadcast also makes a concurrent hide transition win
        // deterministically. The SPI update is verified above and existing
        // maximized windows are reflowed explicitly by FlushWorkAreaEffects.
        _workAreaNotificationPending = ShouldBroadcastWorkAreaChange(taskbarHidden: true);
        return true;
    }

    private void RestoreSnapshotDictionary(IReadOnlyDictionary<string, WorkAreaSnapshot> snapshots)
    {
        _workAreas.Clear();
        foreach (var pair in snapshots) _workAreas[pair.Key] = pair.Value;
    }

    private static HiddenWorkAreaAction ClassifyHiddenWorkArea(
        bool hasSnapshot,
        NativeMethods.Rect snapshotMonitor,
        NativeMethods.Rect originalWorkArea,
        NativeMethods.Rect appliedWorkArea,
        NativeMethods.Rect currentMonitor,
        NativeMethods.Rect currentWorkArea,
        NativeMethods.Rect taskbarBounds)
    {
        if (hasSnapshot && RectEquals(snapshotMonitor, currentMonitor))
        {
            if (RectEquals(appliedWorkArea, currentWorkArea)) return HiddenWorkAreaAction.Healthy;
            if (RectEquals(originalWorkArea, currentWorkArea)) return HiddenWorkAreaAction.ApplySnapshot;
        }

        return IsExactTaskbarWorkAreaEdge(currentMonitor, currentWorkArea, taskbarBounds)
            ? HiddenWorkAreaAction.Recapture
            : HiddenWorkAreaAction.PreserveExternal;
    }

    private static bool IsExactTaskbarWorkAreaEdge(
        NativeMethods.Rect monitor,
        NativeMethods.Rect workArea,
        NativeMethods.Rect taskbar)
    {
        if (!IsValidWorkArea(monitor, workArea)) return false;
        const int edgeTolerance = 4;
        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        var taskbarWidth = Math.Min(taskbar.Right, monitor.Right) - Math.Max(taskbar.Left, monitor.Left);
        var taskbarHeight = Math.Min(taskbar.Bottom, monitor.Bottom) - Math.Max(taskbar.Top, monitor.Top);
        if (monitorWidth <= 0 || monitorHeight <= 0 || taskbarWidth <= 0 || taskbarHeight <= 0)
            return false;

        if (taskbarWidth >= taskbarHeight * 2)
        {
            var atTop = Math.Abs(taskbar.Top - monitor.Top) <= edgeTolerance;
            var atBottom = Math.Abs(taskbar.Bottom - monitor.Bottom) <= edgeTolerance;
            if (atTop == atBottom || taskbarHeight > monitorHeight / 3) return false;
            return atTop
                ? Math.Abs(workArea.Top - taskbar.Bottom) <= edgeTolerance
                : Math.Abs(workArea.Bottom - taskbar.Top) <= edgeTolerance;
        }

        if (taskbarHeight >= taskbarWidth * 2)
        {
            var atLeft = Math.Abs(taskbar.Left - monitor.Left) <= edgeTolerance;
            var atRight = Math.Abs(taskbar.Right - monitor.Right) <= edgeTolerance;
            if (atLeft == atRight || taskbarWidth > monitorWidth / 3) return false;
            return atLeft
                ? Math.Abs(workArea.Left - taskbar.Right) <= edgeTolerance
                : Math.Abs(workArea.Right - taskbar.Left) <= edgeTolerance;
        }

        return false;
    }

    private static bool ShouldBroadcastWorkAreaChange(bool taskbarHidden) => !taskbarHidden;

    private bool RestoreWorkAreasCore(bool publishEffects = true)
    {
        if (_workAreas.Count == 0) return true;
        if (!NativeMethods.TryAllMonitors(out var allMonitors)) return false;
        var monitors = allMonitors
            .GroupBy(monitor => MonitorKey(monitor.Info), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var restored = true;
        foreach (var snapshot in _workAreas.Values.ToArray())
        {
            if (!monitors.TryGetValue(snapshot.DeviceName, out var monitor) ||
                !RectEquals(snapshot.MonitorBounds, monitor.Info.Monitor))
            {
                // A disconnected or reconfigured display must never receive an
                // obsolete rectangle. Windows owns the new layout, so forget it.
                _workAreas.Remove(snapshot.DeviceName);
                continue;
            }

            if (RectEquals(monitor.Info.Work, snapshot.OriginalWorkArea))
            {
                _workAreas.Remove(snapshot.DeviceName);
                continue;
            }

            if (!ShouldRestoreWorkArea(
                    snapshot.MonitorBounds,
                    snapshot.OriginalWorkArea,
                    snapshot.AppliedWorkArea,
                    monitor.Info.Monitor,
                    monitor.Info.Work))
            {
                // Another appbar or a display change has claimed a different
                // work area. Do not overwrite that newer external state.
                _workAreas.Remove(snapshot.DeviceName);
                continue;
            }

            var original = snapshot.OriginalWorkArea;
            var reflowTargets = publishEffects
                ? CaptureWindowReflowTargetsCore(snapshot.DeviceName, snapshot.AppliedWorkArea, original)
                : [];
            if (reflowTargets is null)
            {
                restored = false;
                continue;
            }
            if (!NativeMethods.SystemParametersInfo(NativeMethods.SpiSetWorkArea, 0, ref original, 0))
            {
                restored = false;
                continue;
            }

            var verified = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor.Handle, ref verified) ||
                !RectEquals(verified.Work, snapshot.OriginalWorkArea))
            {
                restored = false;
                continue;
            }
            if (publishEffects)
            {
                _pendingWindowReflow.AddRange(reflowTargets);
                _workAreaNotificationPending = ShouldBroadcastWorkAreaChange(taskbarHidden: false);
            }
            _workAreas.Remove(snapshot.DeviceName);
        }
        var persisted = PersistWorkAreasCore();
        return restored && persisted && _workAreas.Count == 0;
    }

    private void RollBackWorkAreasWithoutPublishingCore()
    {
        _pendingWindowReflow.Clear();
        _workAreaNotificationPending = false;
        _ = RestoreWorkAreasCore(publishEffects: false);
        _pendingWindowReflow.Clear();
        _workAreaNotificationPending = false;
    }

    private static List<WindowReflowTarget>? CaptureWindowReflowTargetsCore(
        string deviceName,
        NativeMethods.Rect sourceWorkArea,
        NativeMethods.Rect targetWorkArea)
    {
        var handles = new List<nint>();
        if (!NativeMethods.EnumWindows((handle, _) =>
            {
                handles.Add(handle);
                return true;
            }, 0)) return null;

        var targets = new List<WindowReflowTarget>();
        foreach (var handle in handles)
        {
            if (!NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle) ||
                NativeMethods.IsIconic(handle) || !NativeMethods.IsZoomed(handle) ||
                !NativeMethods.GetWindowRect(handle, out var bounds))
                continue;
            var monitorHandle = NativeMethods.MonitorFromWindow(handle, 2);
            var monitor = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (monitorHandle == 0 || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitor) ||
                !string.Equals(MonitorKey(monitor), deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            // A normal maximized frame extends only slightly beyond rcWork.
            // Skip constrained/custom windows rather than forcing guessed bounds.
            var leftInset = bounds.Left - sourceWorkArea.Left;
            var topInset = bounds.Top - sourceWorkArea.Top;
            var rightInset = bounds.Right - sourceWorkArea.Right;
            var bottomInset = bounds.Bottom - sourceWorkArea.Bottom;
            if (Math.Abs(leftInset) > 96 || Math.Abs(topInset) > 96 ||
                Math.Abs(rightInset) > 96 || Math.Abs(bottomInset) > 96)
                continue;

            var target = new NativeMethods.Rect
            {
                Left = targetWorkArea.Left + leftInset,
                Top = targetWorkArea.Top + topInset,
                Right = targetWorkArea.Right + rightInset,
                Bottom = targetWorkArea.Bottom + bottomInset
            };
            if (target.Right <= target.Left || target.Bottom <= target.Top) continue;
            if (NativeMethods.GetWindowThreadProcessId(handle, out var processId) == 0 || processId == 0) continue;
            targets.Add(new WindowReflowTarget(handle, processId, deviceName, targetWorkArea, target));
        }
        return targets;
    }

    private void FlushWorkAreaEffects()
    {
        List<WindowReflowTarget> targets;
        bool notify;
        lock (_gate)
        {
            targets = [.. _pendingWindowReflow];
            _pendingWindowReflow.Clear();
            notify = _workAreaNotificationPending;
            _workAreaNotificationPending = false;
        }

        if (notify)
        {
            _ = NativeMethods.SendMessageTimeout(
                (nint)0xffff,
                NativeMethods.WmSettingChange,
                NativeMethods.SpiSetWorkArea,
                0,
                NativeMethods.SmtoBlock | NativeMethods.SmtoAbortIfHung,
                250,
                out _);
        }

        // A restore and re-hide can cross between the native mutation and this
        // out-of-lock flush. The newest target wins, and it is applied only if
        // the monitor still exposes the work area for which it was calculated.
        foreach (var target in targets.AsEnumerable().Reverse().DistinctBy(target => target.Handle))
        {
            if (!NativeMethods.IsWindow(target.Handle) || !NativeMethods.IsWindowVisible(target.Handle) ||
                NativeMethods.IsIconic(target.Handle) || !NativeMethods.IsZoomed(target.Handle))
                continue;
            NativeMethods.GetWindowThreadProcessId(target.Handle, out var processId);
            if (processId != target.ProcessId) continue;
            var monitorHandle = NativeMethods.MonitorFromWindow(target.Handle, 2);
            var monitor = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (monitorHandle == 0 || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitor) ||
                !string.Equals(MonitorKey(monitor), target.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                !RectEquals(monitor.Work, target.ExpectedWorkArea))
                continue;
            var bounds = target.TargetBounds;
            _ = NativeMethods.SetWindowPos(
                target.Handle,
                0,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                0x0004 | 0x0010 | 0x4000); // NOZORDER | NOACTIVATE | ASYNCWINDOWPOS
        }
    }

    private static bool TryReleaseOnlyTaskbarEdge(
        NativeMethods.Rect monitor,
        NativeMethods.Rect originalWorkArea,
        NativeMethods.Rect taskbar,
        out NativeMethods.Rect appliedWorkArea)
    {
        appliedWorkArea = originalWorkArea;
        const int edgeTolerance = 4;
        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        var taskbarWidth = Math.Min(taskbar.Right, monitor.Right) - Math.Max(taskbar.Left, monitor.Left);
        var taskbarHeight = Math.Min(taskbar.Bottom, monitor.Bottom) - Math.Max(taskbar.Top, monitor.Top);
        if (monitorWidth <= 0 || monitorHeight <= 0 || taskbarWidth <= 0 || taskbarHeight <= 0)
            return false;

        if (taskbarWidth >= taskbarHeight * 2)
        {
            var atTop = Math.Abs(taskbar.Top - monitor.Top) <= edgeTolerance;
            var atBottom = Math.Abs(taskbar.Bottom - monitor.Bottom) <= edgeTolerance;
            if (atTop == atBottom || taskbarHeight > monitorHeight / 3) return false;
            if (atTop) appliedWorkArea.Top = Math.Max(monitor.Top, originalWorkArea.Top - taskbarHeight);
            else appliedWorkArea.Bottom = Math.Min(monitor.Bottom, originalWorkArea.Bottom + taskbarHeight);
        }
        else if (taskbarHeight >= taskbarWidth * 2)
        {
            var atLeft = Math.Abs(taskbar.Left - monitor.Left) <= edgeTolerance;
            var atRight = Math.Abs(taskbar.Right - monitor.Right) <= edgeTolerance;
            if (atLeft == atRight || taskbarWidth > monitorWidth / 3) return false;
            if (atLeft) appliedWorkArea.Left = Math.Max(monitor.Left, originalWorkArea.Left - taskbarWidth);
            else appliedWorkArea.Right = Math.Min(monitor.Right, originalWorkArea.Right + taskbarWidth);
        }
        else return false;

        return IsValidWorkArea(monitor, appliedWorkArea) && Contains(appliedWorkArea, originalWorkArea);
    }

    private static bool ShouldRestoreWorkArea(
        NativeMethods.Rect originalMonitor,
        NativeMethods.Rect originalWorkArea,
        NativeMethods.Rect appliedWorkArea,
        NativeMethods.Rect currentMonitor,
        NativeMethods.Rect currentWorkArea) =>
        RectEquals(originalMonitor, currentMonitor) &&
        RectEquals(appliedWorkArea, currentWorkArea) &&
        IsValidWorkArea(originalMonitor, originalWorkArea) &&
        Contains(appliedWorkArea, originalWorkArea);

    private static string MonitorKey(NativeMethods.MonitorInfo monitor)
    {
        var device = (monitor.Device ?? "").Trim();
        return device.Length <= 64 ? device : "";
    }

    private static string TaskbarRecoveryKey(string className, string deviceName) =>
        className + "\u001f" + deviceName;

    private bool LoadWorkAreaRecoveryCore()
    {
        var path = WorkAreaRecoveryPath;
        if (!File.Exists(path)) return false;
        try
        {
            _taskbarRecoveryMisses.Clear();
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumRecoveryFileBytes) throw new InvalidDataException("Invalid work-area recovery file size.");
            var recovery = JsonSerializer.Deserialize<WorkAreaRecoveryFile>(File.ReadAllText(path));
            if (recovery is null || recovery.Version != WorkAreaRecoveryVersion ||
                recovery.Monitors is null || recovery.Monitors.Count > 32 ||
                recovery.Taskbars is null || recovery.Taskbars.Count > 32 ||
                recovery.Monitors.Count + recovery.Taskbars.Count < 1)
                throw new InvalidDataException("Invalid work-area recovery file.");

            if (!NativeMethods.TryAllMonitors(out var allMonitors)) return false;
            var current = allMonitors
                .GroupBy(monitor => MonitorKey(monitor.Info), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0 && group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in recovery.Monitors)
            {
                if (entry is null || entry.DeviceName is null || entry.MonitorBounds is null ||
                    entry.OriginalWorkArea is null || entry.AppliedWorkArea is null ||
                    entry.DeviceName.Length is < 1 or > 64 || !current.TryGetValue(entry.DeviceName, out var monitor))
                    continue;
                var monitorBounds = entry.MonitorBounds.ToNative();
                var original = entry.OriginalWorkArea.ToNative();
                var applied = entry.AppliedWorkArea.ToNative();
                if (!RectEquals(monitorBounds, monitor.Info.Monitor) ||
                    !IsValidWorkArea(monitorBounds, applied) ||
                    !IsValidWorkArea(monitorBounds, original) ||
                    !Contains(applied, original))
                    continue;
                _workAreas[entry.DeviceName] = new WorkAreaSnapshot(
                    monitor.Handle, entry.DeviceName, monitorBounds, original, applied);
            }

            foreach (var entry in recovery.Taskbars)
            {
                if (entry is null || !entry.WasVisible || entry.DeviceName is null ||
                    entry.ClassName is not (PrimaryTaskbarClass or SecondaryTaskbarClass) ||
                    entry.DeviceName.Length is < 1 or > 64 || !current.ContainsKey(entry.DeviceName))
                    continue;
                var key = TaskbarRecoveryKey(entry.ClassName, entry.DeviceName);
                _taskbarRecovery[key] = new TaskbarRecoverySnapshot(
                    entry.ClassName, entry.DeviceName, WasVisible: true);
            }

            if (_workAreas.Count > 0 || _taskbarRecovery.Count > 0) return true;
            return PersistWorkAreasCore() ? false : throw new IOException("Could not remove an empty recovery snapshot.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            App.Log("Could not load the taskbar work-area recovery snapshot", exception);
            try { File.Delete(path); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                App.Log("Could not remove an invalid work-area recovery snapshot", cleanupException);
            }
            _workAreas.Clear();
            _taskbarRecovery.Clear();
            _taskbarRecoveryMisses.Clear();
            return false;
        }
    }

    private bool PersistWorkAreasCore()
    {
        var path = WorkAreaRecoveryPath;
        var temp = path + ".tmp";
        var backup = path + ".bak";
        try
        {
            if (_workAreas.Count == 0 && _taskbarRecovery.Count == 0)
            {
                _taskbarRecoveryMisses.Clear();
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(backup)) File.Delete(backup);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var document = new WorkAreaRecoveryFile
            {
                Version = WorkAreaRecoveryVersion,
                Monitors = _workAreas.Values.Select(snapshot => new WorkAreaRecoveryEntry
                {
                    DeviceName = snapshot.DeviceName,
                    MonitorBounds = SerializableRect.FromNative(snapshot.MonitorBounds),
                    OriginalWorkArea = SerializableRect.FromNative(snapshot.OriginalWorkArea),
                    AppliedWorkArea = SerializableRect.FromNative(snapshot.AppliedWorkArea)
                }).ToList(),
                Taskbars = _taskbarRecovery.Values.Select(snapshot => new TaskbarRecoveryEntry
                {
                    ClassName = snapshot.ClassName,
                    DeviceName = snapshot.DeviceName,
                    WasVisible = snapshot.WasVisible
                }).ToList()
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(document));
            if (File.Exists(path))
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                if (File.Exists(backup)) File.Delete(backup);
            }
            else File.Move(temp, path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.Log("Could not persist the taskbar work-area recovery snapshot", exception);
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }
            return false;
        }
    }

    private static bool IsValidWorkArea(NativeMethods.Rect monitor, NativeMethods.Rect workArea) =>
        monitor.Right > monitor.Left && monitor.Bottom > monitor.Top &&
        workArea.Right > workArea.Left && workArea.Bottom > workArea.Top &&
        workArea.Left >= monitor.Left && workArea.Top >= monitor.Top &&
        workArea.Right <= monitor.Right && workArea.Bottom <= monitor.Bottom;

    private static bool Contains(NativeMethods.Rect outer, NativeMethods.Rect inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top &&
        outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    private static bool RectEquals(NativeMethods.Rect left, NativeMethods.Rect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

    private bool HasRestoreCandidatesCore() =>
        _tracked.Values.Any(taskbar => taskbar.RestoreOnExit);

    private void QueueRestoreVerification()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RestoreAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static bool TryDiscoverTaskbars(
        out uint shellProcessId,
        out List<TrackedTaskbar> taskbars,
        out string error)
    {
        taskbars = [];
        error = "";
        if (!TryGetShellProcessId(out shellProcessId))
        {
            error = L("Taskbar.ShellUnavailable");
            return false;
        }

        var candidates = new List<nint>();
        if (!NativeMethods.EnumWindows((handle, _) =>
            {
                candidates.Add(handle);
                return true;
            }, 0))
        {
            error = L("Taskbar.WindowsUnavailable");
            return false;
        }

        foreach (var handle in candidates)
        {
            if (!TryReadTaskbarClass(handle, out var className)) continue;
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId != shellProcessId) continue;
            var monitorHandle = NativeMethods.MonitorFromWindow(handle, 2);
            var monitor = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (monitorHandle == 0 || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitor) ||
                MonitorKey(monitor).Length == 0)
            {
                taskbars.Clear();
                error = L("Taskbar.WindowsUnavailable");
                return false;
            }
            taskbars.Add(new TrackedTaskbar(handle, className, MonitorKey(monitor)));
        }

        if (!taskbars.Any(taskbar => taskbar.ClassName == PrimaryTaskbarClass))
        {
            taskbars.Clear();
            error = L("Taskbar.PrimaryNotFound");
            return false;
        }

        taskbars = taskbars.DistinctBy(taskbar => taskbar.Handle).ToList();
        return true;
    }

    private static bool TryGetShellProcessId(out uint processId)
    {
        processId = 0;
        var shellWindow = NativeMethods.GetShellWindow();
        if (shellWindow == 0 || !NativeMethods.IsWindow(shellWindow)) return false;
        return NativeMethods.GetWindowThreadProcessId(shellWindow, out processId) != 0
               && processId != 0;
    }

    private static bool IsExpectedTaskbar(nint handle, uint shellProcessId, string expectedClass)
    {
        if (!NativeMethods.IsWindow(handle)
            || !TryReadTaskbarClass(handle, out var className)
            || !string.Equals(className, expectedClass, StringComparison.Ordinal))
            return false;

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId == shellProcessId;
    }

    private static bool TryReadTaskbarClass(nint handle, out string className)
    {
        var buffer = new StringBuilder(64);
        if (NativeMethods.GetClassName(handle, buffer, buffer.Capacity) == 0)
        {
            className = "";
            return false;
        }

        className = buffer.ToString();
        return className is PrimaryTaskbarClass or SecondaryTaskbarClass;
    }

    private static NativeMethods.AppBarData CreateAppBarData(nint handle, uint callbackMessage = 0) => new()
    {
        Size = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.AppBarData>()),
        Window = handle,
        CallbackMessage = callbackMessage,
        Edge = NativeMethods.AbeTop
    };

    private bool UpdateMenuBarCore(
        nint handle,
        int requestedHeight,
        out NativeMethods.Rect reservedBounds)
    {
        reservedBounds = default;
        if (requestedHeight <= 0 || handle == 0 || !NativeMethods.IsWindow(handle)) return false;

        // VeliShell currently presents one global menu bar on the primary
        // display. Supplying that monitor's actual virtual-desktop rectangle is
        // important for negative coordinates and mixed multi-monitor layouts.
        var monitor = NativeMethods.PrimaryMonitor().Monitor;
        var query = CreateAppBarData(handle);
        query.Bounds = monitor;
        if (NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref query) == 0 ||
            !TryCalculateTopAppBarBounds(monitor, query.Bounds, requestedHeight, out query.Bounds))
            return false;

        if (NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref query) == 0 ||
            !IsValidAppBarBounds(monitor, query.Bounds))
            return false;

        reservedBounds = query.Bounds;
        return NativeMethods.SetWindowPos(
            handle,
            0,
            reservedBounds.Left,
            reservedBounds.Top,
            reservedBounds.Right - reservedBounds.Left,
            reservedBounds.Bottom - reservedBounds.Top,
            0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    private static bool TryCalculateTopAppBarBounds(
        NativeMethods.Rect monitor,
        NativeMethods.Rect available,
        int requestedHeight,
        out NativeMethods.Rect result)
    {
        result = default;
        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        if (monitorWidth <= 0 || monitorHeight <= 0 || requestedHeight <= 0 ||
            requestedHeight > monitorHeight / 3)
            return false;

        // ABM_QUERYPOS has already accounted for all other appbars. Clamp its
        // answer to the selected monitor but never broaden it, which preserves
        // reservations on the left and right edges as well as the top edge.
        var left = Math.Max(monitor.Left, available.Left);
        var right = Math.Min(monitor.Right, available.Right);
        var top = Math.Max(monitor.Top, available.Top);
        var bottom = top + requestedHeight;
        if (right <= left || top >= monitor.Bottom || bottom > monitor.Bottom) return false;

        result = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        return true;
    }

    private static bool IsValidAppBarBounds(NativeMethods.Rect monitor, NativeMethods.Rect value) =>
        value.Left >= monitor.Left && value.Top >= monitor.Top &&
        value.Right <= monitor.Right && value.Bottom <= monitor.Bottom &&
        value.Right > value.Left && value.Bottom > value.Top;

    private void ReleaseMenuBarCore(nint handle)
    {
        if (_menuBarRegistered && _menuBarHandle == handle)
        {
            var removal = CreateAppBarData(handle);
            _ = NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref removal);
        }
        _menuBarRegistered = false;
        _menuBarHandle = 0;
    }

    private bool EnsureUsable()
    {
        if (!_disposed) return true;
        _lastStatus = L("Taskbar.Disposed");
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hideRequested = false;
            RestoreCore();
            if (_menuBarRegistered) ReleaseMenuBarCore(_menuBarHandle);
            _disposed = true;
        }
        FlushWorkAreaEffects();
        GC.SuppressFinalize(this);
    }

    private sealed record TrackedTaskbar(
        nint Handle,
        string ClassName,
        string DeviceName,
        bool RestoreOnExit = false);
    private sealed record TaskbarRecoverySnapshot(string ClassName, string DeviceName, bool WasVisible);
    private sealed record TaskbarRecoveryMiss(uint ShellProcessId, int Count);
    private sealed record WindowReflowTarget(
        nint Handle,
        uint ProcessId,
        string DeviceName,
        NativeMethods.Rect ExpectedWorkArea,
        NativeMethods.Rect TargetBounds);
    private sealed record WorkAreaMutation(
        WorkAreaSnapshot Snapshot,
        nint MonitorHandle,
        NativeMethods.Rect SourceWorkArea,
        IReadOnlyList<WindowReflowTarget> ReflowTargets);
    private sealed record WorkAreaSnapshot(
        nint Handle,
        string DeviceName,
        NativeMethods.Rect MonitorBounds,
        NativeMethods.Rect OriginalWorkArea,
        NativeMethods.Rect AppliedWorkArea);

    private enum HiddenWorkAreaAction
    {
        Healthy,
        ApplySnapshot,
        Recapture,
        PreserveExternal
    }

    private sealed class WorkAreaRecoveryFile
    {
        public int Version { get; set; }
        public List<WorkAreaRecoveryEntry> Monitors { get; set; } = [];
        public List<TaskbarRecoveryEntry> Taskbars { get; set; } = [];
    }

    private sealed class TaskbarRecoveryEntry
    {
        public string ClassName { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public bool WasVisible { get; set; }
    }

    private sealed class WorkAreaRecoveryEntry
    {
        public string DeviceName { get; set; } = "";
        public SerializableRect MonitorBounds { get; set; } = new();
        public SerializableRect OriginalWorkArea { get; set; } = new();
        public SerializableRect AppliedWorkArea { get; set; } = new();
    }

    private sealed class SerializableRect
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        internal NativeMethods.Rect ToNative() => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
        internal static SerializableRect FromNative(NativeMethods.Rect value) => new()
        {
            Left = value.Left,
            Top = value.Top,
            Right = value.Right,
            Bottom = value.Bottom
        };
    }
}
