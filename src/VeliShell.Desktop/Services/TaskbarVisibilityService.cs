using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Temporarily hides taskbar windows owned by the current Windows shell.
/// This service changes only window visibility; it does not modify Explorer,
/// the registry, taskbar settings, or the system work area.
/// </summary>
internal sealed class TaskbarVisibilityService : IDisposable
{
    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private static readonly TimeSpan[] RestoreRetryDelays =
    [
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(220)
    ];

    private readonly object _gate = new();
    private readonly Dictionary<nint, TrackedTaskbar> _tracked = [];
    private bool _hideRequested;
    private bool _recoverySession;
    private bool _recoverExistingHiddenTaskbars;
    private bool _isHidden;
    private bool _disposed;
    private string _lastStatus = L("Taskbar.VisibleDefault");

    internal bool IsHidden
    {
        get { lock (_gate) return _isHidden; }
    }

    internal string LastStatus
    {
        get { lock (_gate) return _lastStatus; }
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
                               (_recoverySession || HasRestoreCandidatesCore());
            }
            else
            {
                _recoverExistingHiddenTaskbars = recoverExistingHiddenTaskbars;
                _hideRequested = true;
                result = ReconcileCore();
                if (!result)
                {
                    _hideRequested = false;
                    queueRestore = _recoverySession || HasRestoreCandidatesCore();
                }
            }

            if (!result && !queueRestore)
            {
                _tracked.Clear();
                _recoverExistingHiddenTaskbars = false;
                _isHidden = false;
            }
        }

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
                    return true;
                }

                // Explorer can recreate taskbar windows while the asynchronous
                // command is in flight. Re-enumerate and request hiding again.
                ReconcileCore();
            }
        }

        lock (_gate)
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
                           (_recoverySession || HasRestoreCandidatesCore());
        }

        if (queueRestore) QueueRestoreVerification();
        return result;
    }

    /// <summary>
    /// Ends the hide session and restores only windows that were visible before
    /// this service hid them. Calls are safe to repeat.
    /// </summary>
    internal bool Restore(bool recoverCurrentShellTaskbars = false)
    {
        lock (_gate)
        {
            if (_disposed) return _tracked.Count == 0;
            _hideRequested = false;
            if (recoverCurrentShellTaskbars) _recoverySession = true;
            return RestoreCore();
        }
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
        lock (_gate)
        {
            if (_disposed) return _tracked.Count == 0;
            needsDelayedVerification = recoverCurrentShellTaskbars || _hideRequested || _recoverySession ||
                                       HasRestoreCandidatesCore();
            _hideRequested = false;
            if (recoverCurrentShellTaskbars) _recoverySession = true;
            var restoredImmediately = RestoreCore(clearVisibleCandidates: !needsDelayedVerification);
            if (!needsDelayedVerification) return restoredImmediately;
        }

        foreach (var delay in RestoreRetryDelays)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return _tracked.Count == 0;
                if (_hideRequested) return false;
                // Re-enumerate and re-check retained candidates even if the
                // preceding pass looked visible. This closes the ShowWindowAsync
                // race without adopting unrelated, already-hidden taskbars.
                if (RestoreCore(clearVisibleCandidates: true)) return true;
            }
        }

        lock (_gate)
        {
            if (!_isHidden) return true;
            _lastStatus = L("Taskbar.RestoreUnconfirmed");
            return false;
        }
    }

    private bool ReconcileCore()
    {
        if (!TryDiscoverTaskbars(out var shellProcessId, out var taskbars, out var error))
        {
            _lastStatus = error;
            _isHidden = HasRestoreCandidatesCore();
            return false;
        }

        // Drop stale handles before considering new windows. A destroyed HWND can
        // be reused, so every retained entry must still be the same shell window.
        foreach (var tracked in _tracked.Values.ToArray())
        {
            if (!IsExpectedTaskbar(tracked.Handle, shellProcessId, tracked.ClassName))
                _tracked.Remove(tracked.Handle);
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
        return true;
    }

    private bool RestoreCore(bool clearVisibleCandidates = true)
    {
        if (!_recoverySession && !HasRestoreCandidatesCore())
        {
            _tracked.Clear();
            _recoverExistingHiddenTaskbars = false;
            _isHidden = false;
            _lastStatus = L("Taskbar.Visible");
            return true;
        }

        if (!TryDiscoverTaskbars(out var shellProcessId, out var taskbars, out var error))
        {
            _isHidden = true;
            _lastStatus = error.Length > 0
                ? error + " " + L("Taskbar.Retry")
                : L("Taskbar.ShellRetry");
            return false;
        }

        // Explicit recovery (button/emergency hotkey) may restore current shell
        // taskbars even when this process did not hide them. Normal shutdown and
        // hide rollback are deliberately limited to RestoreOnExit candidates.
        if (_recoverySession)
            foreach (var taskbar in taskbars)
                TrackTaskbar(taskbar, wasVisible: true);

        var currentHandles = taskbars.Select(taskbar => taskbar.Handle).ToHashSet();
        foreach (var tracked in _tracked.Values.ToArray())
        {
            if (!currentHandles.Contains(tracked.Handle) ||
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
            _isHidden = true;
            _lastStatus = L("Taskbar.Restoring");
            return false;
        }

        if (!clearVisibleCandidates)
        {
            _isHidden = false;
            _lastStatus = L("Taskbar.VisibleChecking");
            return true;
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
        _isHidden = false;
        _lastStatus = L("Taskbar.Restored");
        return true;
    }

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

        _isHidden = HasRestoreCandidatesCore();
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
            taskbars.Add(new TrackedTaskbar(handle, className));
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
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private sealed record TrackedTaskbar(nint Handle, string ClassName, bool RestoreOnExit = false);
}
