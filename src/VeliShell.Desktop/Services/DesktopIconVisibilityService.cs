using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Temporarily hides only Explorer's desktop list view. No Explorer option,
/// registry value or policy is changed. A small recovery journal lets the next
/// VeliShell process restore a list view left hidden by an interrupted run.
/// </summary>
internal sealed class DesktopIconVisibilityService : IDisposable
{
    private const int RecoveryVersion = 1;
    private const int MaximumRecoveryBytes = 4096;
    private const string DesktopViewClass = "SHELLDLL_DefView";
    private const string DesktopListClass = "SysListView32";
    private const string DesktopListCaption = "FolderView";
    private readonly string _recoveryPath;
    private DesktopViewSnapshot? _ownedSnapshot;

    internal DesktopIconVisibilityService(string? recoveryPath = null)
    {
        _recoveryPath = recoveryPath ?? Path.Combine(App.DataDirectory, "desktop-icons-recovery.json");
    }

    internal bool IsHidden { get; private set; }
    internal string LastStatus { get; private set; } = L("DesktopIcons.Ready");

    internal bool RecoverAfterOwnershipConfirmed()
    {
        if (!TryLoadRecovery(out var journal))
            return !File.Exists(_recoveryPath);

        if (!TryFindDesktopView(out var current) || !Matches(journal, current))
        {
            DeleteRecovery();
            LastStatus = L("DesktopIcons.RecoveryStale");
            return true;
        }

        if (!journal.WasVisible || current.IsVisible)
        {
            DeleteRecovery();
            LastStatus = L("DesktopIcons.Recovered");
            return true;
        }

        if (!SetVisible((nint)current.ListView, visible: true))
        {
            _ownedSnapshot = current;
            IsHidden = true;
            LastStatus = L("DesktopIcons.RestoreFailed");
            return false;
        }

        DeleteRecovery();
        LastStatus = L("DesktopIcons.Recovered");
        return true;
    }

    internal bool Hide()
    {
        if (_ownedSnapshot is { } owned &&
            TryFindDesktopView(out var currentOwned) &&
            Matches(owned, currentOwned) && !currentOwned.IsVisible)
        {
            IsHidden = true;
            LastStatus = L("DesktopIcons.Hidden");
            return true;
        }

        if (!TryFindDesktopView(out var current))
        {
            IsHidden = false;
            LastStatus = L("DesktopIcons.Unavailable");
            return false;
        }

        if (!current.IsVisible)
        {
            // The user or another tool already hid the view. Do not claim
            // ownership and, importantly, do not show it again on shutdown.
            _ownedSnapshot = null;
            IsHidden = false;
            DeleteRecovery();
            LastStatus = L("DesktopIcons.AlreadyHidden");
            return true;
        }

        var journal = current with { WasVisible = true };
        if (!TryWriteRecovery(journal))
        {
            IsHidden = false;
            LastStatus = L("DesktopIcons.RecoveryUnavailable");
            return false;
        }

        if (!SetVisible((nint)current.ListView, visible: false))
        {
            SetVisible((nint)current.ListView, visible: true);
            DeleteRecovery();
            IsHidden = false;
            LastStatus = L("DesktopIcons.HideFailed");
            return false;
        }

        _ownedSnapshot = journal;
        IsHidden = true;
        LastStatus = L("DesktopIcons.Hidden");
        return true;
    }

    internal bool ReconcileHidden()
    {
        if (_ownedSnapshot is { } owned && TryFindDesktopView(out var current) && Matches(owned, current))
        {
            if (!current.IsVisible)
            {
                IsHidden = true;
                return true;
            }

            // Explorer made the exact view visible again. Re-apply the still
            // enabled opt-in using the existing journal.
            if (SetVisible((nint)current.ListView, visible: false))
            {
                IsHidden = true;
                LastStatus = L("DesktopIcons.Hidden");
                return true;
            }
        }

        // Explorer may have restarted. The old handle and process identity are
        // no longer ours; discard them before taking ownership of the new view.
        _ownedSnapshot = null;
        IsHidden = false;
        DeleteRecovery();
        return Hide();
    }

    internal bool Restore()
    {
        if (_ownedSnapshot is not { } owned)
        {
            // This process did not hide the Explorer desktop view. In
            // particular, a rejected second VeliShell instance also reaches
            // application shutdown. It must not remove the active owner's
            // crash-recovery journal.
            IsHidden = false;
            LastStatus = L("DesktopIcons.Visible");
            return true;
        }

        if (!TryFindDesktopView(out var current) || !Matches(owned, current))
        {
            // A restarted Explorer owns a new desktop view and normally applies
            // its own visibility state. Never show an unowned replacement view.
            _ownedSnapshot = null;
            IsHidden = false;
            DeleteRecovery();
            LastStatus = L("DesktopIcons.Visible");
            return true;
        }

        if (owned.WasVisible && !current.IsVisible && !SetVisible((nint)current.ListView, visible: true))
        {
            IsHidden = true;
            LastStatus = L("DesktopIcons.RestoreFailed");
            return false;
        }

        _ownedSnapshot = null;
        IsHidden = false;
        DeleteRecovery();
        LastStatus = L("DesktopIcons.Visible");
        return true;
    }

    private static bool SetVisible(nint window, bool visible)
    {
        if (!NativeMethods.IsWindow(window)) return false;
        NativeMethods.ShowWindow(window, visible ? NativeMethods.SwShowNoActivate : NativeMethods.SwHide);
        return NativeMethods.IsWindowVisible(window) == visible;
    }

    private static bool TryFindDesktopView(out DesktopViewSnapshot snapshot)
    {
        snapshot = default!;
        var shellWindow = NativeMethods.GetShellWindow();
        if (shellWindow == 0) return false;
        NativeMethods.GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        if (shellProcessId == 0 || !TryGetProcessStartTicks(shellProcessId, out var startTicks)) return false;

        nint listView = 0;
        nint host = 0;
        NativeMethods.EnumWindows((candidate, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(candidate, out var candidateProcessId);
            if (candidateProcessId != shellProcessId) return true;
            var view = NativeMethods.FindWindowEx(candidate, 0, DesktopViewClass, null);
            if (view == 0) return true;
            var list = NativeMethods.FindWindowEx(view, 0, DesktopListClass, DesktopListCaption);
            if (list == 0) list = NativeMethods.FindWindowEx(view, 0, DesktopListClass, null);
            if (list == 0 || !HasExpectedClass(view, DesktopViewClass) || !HasExpectedClass(list, DesktopListClass))
                return true;
            host = candidate;
            listView = list;
            return false;
        }, 0);

        if (listView == 0 || host == 0) return false;
        snapshot = new DesktopViewSnapshot(
            (long)listView,
            (long)host,
            shellProcessId,
            startTicks,
            NativeMethods.IsWindowVisible(listView),
            WasVisible: false);
        return true;
    }

    private static bool HasExpectedClass(nint window, string expected)
    {
        var buffer = new StringBuilder(64);
        return NativeMethods.GetClassName(window, buffer, buffer.Capacity) > 0 &&
               string.Equals(buffer.ToString(), expected, StringComparison.Ordinal);
    }

    private static bool TryGetProcessStartTicks(uint processId, out long ticks)
    {
        ticks = 0;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            ticks = process.StartTime.ToUniversalTime().Ticks;
            return ticks > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }

    private static bool Matches(DesktopViewSnapshot expected, DesktopViewSnapshot current) =>
        expected.ListView == current.ListView &&
        expected.Host == current.Host &&
        expected.ShellProcessId == current.ShellProcessId &&
        expected.ShellStartTimeUtcTicks == current.ShellStartTimeUtcTicks;

    private bool TryLoadRecovery(out DesktopViewSnapshot journal)
    {
        journal = default!;
        try
        {
            if (!File.Exists(_recoveryPath)) return false;
            var info = new FileInfo(_recoveryPath);
            if (info.Length is <= 0 or > MaximumRecoveryBytes) throw new InvalidDataException();
            var recovery = JsonSerializer.Deserialize<DesktopIconRecovery>(File.ReadAllText(_recoveryPath));
            if (recovery is null || recovery.Version != RecoveryVersion || recovery.View is null ||
                recovery.View.ListView <= 0 || recovery.View.Host <= 0 || recovery.View.ShellProcessId == 0 ||
                recovery.View.ShellStartTimeUtcTicks <= 0)
                throw new InvalidDataException();
            journal = recovery.View;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidDataException)
        {
            App.Log("Could not read the desktop-icon recovery journal", exception);
            DeleteRecovery();
            LastStatus = L("DesktopIcons.RecoveryInvalid");
            return false;
        }
    }

    private bool TryWriteRecovery(DesktopViewSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_recoveryPath);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);
            var temporary = _recoveryPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new DesktopIconRecovery
            {
                Version = RecoveryVersion,
                View = snapshot
            }));
            File.Move(temporary, _recoveryPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.Log("Could not write the desktop-icon recovery journal", exception);
            return false;
        }
    }

    private void DeleteRecovery()
    {
        try
        {
            File.Delete(_recoveryPath);
            File.Delete(_recoveryPath + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.Log("Could not remove the desktop-icon recovery journal", exception);
        }
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
    public void Dispose() => Restore();

    private sealed record DesktopViewSnapshot(
        long ListView,
        long Host,
        uint ShellProcessId,
        long ShellStartTimeUtcTicks,
        bool IsVisible,
        bool WasVisible);

    private sealed class DesktopIconRecovery
    {
        public int Version { get; set; }
        public DesktopViewSnapshot? View { get; set; }
    }
}
