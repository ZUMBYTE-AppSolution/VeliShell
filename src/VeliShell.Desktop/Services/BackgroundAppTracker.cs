using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace VeliShell.Desktop.Services;

internal sealed record BackgroundProcessIdentity(
    int ProcessId, long StartTimeUtcTicks, int SessionId, string Executable);

internal sealed record BackgroundApp(
    int ProcessId, long StartTimeUtcTicks, string Name, string Executable,
    string IconTarget, string LaunchTarget);

/// <summary>
/// Remembers only processes that actually owned a visible app window while
/// VeliShell was running. This is not a general process or service scanner.
/// </summary>
internal sealed class BackgroundAppTracker
{
    private const int MaximumObservedProcesses = 128;
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "runtimebroker.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "textinputhost.exe", "svchost.exe", "sihost.exe", "taskhostw.exe",
        "conhost.exe", "dllhost.exe", "dwm.exe", "rundll32.exe",
        "steam.exe", "steamwebhelper.exe", "msedgewebview2.exe", "velishell.exe"
    };

    private readonly Func<int, BackgroundProcessIdentity?> _readProcess;
    private readonly int _sessionId;
    private readonly Dictionary<int, ObservedApp> _observed = [];
    private long _sequence;

    internal BackgroundAppTracker(Func<int, BackgroundProcessIdentity?> readProcess, int sessionId)
    {
        _readProcess = readProcess;
        _sessionId = sessionId;
    }

    internal static BackgroundAppTracker ForCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        return new BackgroundAppTracker(ReadProcess, current.SessionId);
    }

    internal IReadOnlyList<BackgroundApp> Update(IReadOnlyList<NativeWindow> windows)
    {
        var visibleIds = windows.Where(window => window.ProcessId > 0)
            .Select(window => window.ProcessId).ToHashSet();
        var visibleTargets = windows.Where(IsEligibleWindow)
            .Select(window => !string.IsNullOrWhiteSpace(window.WebApp?.ShortcutPath) &&
                              File.Exists(window.WebApp.ShortcutPath)
                ? window.WebApp.ShortcutPath : window.Executable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in windows.Where(IsEligibleWindow).GroupBy(window => window.ProcessId))
        {
            // Prefer the named shortcut of a browser web app to the browser's
            // generic executable when only that web app owned the window.
            var window = group.FirstOrDefault(candidate => candidate.WebApp is not null) ?? group.First();
            var identity = _readProcess(group.Key);
            if (!MatchesWindow(identity, window)) continue;

            var name = window.WebApp?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(identity!.Executable).Replace('_', ' ');
            var shortcut = window.WebApp?.ShortcutPath;
            var target = !string.IsNullOrWhiteSpace(shortcut) && File.Exists(shortcut)
                ? shortcut : identity!.Executable;
            _observed[group.Key] = new ObservedApp(identity!, name.Trim(), target, ++_sequence);
        }

        var background = new List<(BackgroundApp App, long Sequence)>();
        foreach (var (pid, observed) in _observed.ToArray())
        {
            if (visibleIds.Contains(pid)) continue;
            var current = _readProcess(pid);
            if (!SameProcess(current, observed.Process))
            {
                _observed.Remove(pid);
                continue;
            }
            background.Add((new BackgroundApp(pid, current!.StartTimeUtcTicks,
                observed.Name, current.Executable, observed.Target, observed.Target), observed.Sequence));
        }

        if (_observed.Count > MaximumObservedProcesses)
        {
            foreach (var pid in _observed.OrderBy(pair => pair.Value.Sequence)
                         .Take(_observed.Count - MaximumObservedProcesses)
                         .Select(pair => pair.Key).ToArray())
                _observed.Remove(pid);
        }

        return background.OrderByDescending(item => item.Sequence)
            .Where(item => _observed.ContainsKey(item.App.ProcessId))
            .Where(item => !visibleTargets.Contains(item.App.LaunchTarget))
            .DistinctBy(item => item.App.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.App).ToArray();
    }

    internal bool IsStillRunning(BackgroundApp app)
    {
        var current = _readProcess(app.ProcessId);
        return current is not null && current.ProcessId == app.ProcessId &&
               current.SessionId == _sessionId &&
               current.StartTimeUtcTicks == app.StartTimeUtcTicks &&
               string.Equals(current.Executable, app.Executable, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsEligibleWindow(NativeWindow window) =>
        window.ProcessId > 0 &&
        Path.IsPathFullyQualified(window.Executable) &&
        Path.GetExtension(window.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        !IgnoredExecutables.Contains(Path.GetFileName(window.Executable));

    private bool MatchesWindow(BackgroundProcessIdentity? identity, NativeWindow window) =>
        identity is not null && identity.ProcessId == window.ProcessId && identity.SessionId == _sessionId &&
        string.Equals(identity.Executable, window.Executable, StringComparison.OrdinalIgnoreCase);

    private static bool SameProcess(BackgroundProcessIdentity? current, BackgroundProcessIdentity original) =>
        current is not null && current.ProcessId == original.ProcessId &&
        current.SessionId == original.SessionId &&
        current.StartTimeUtcTicks == original.StartTimeUtcTicks &&
        string.Equals(current.Executable, original.Executable, StringComparison.OrdinalIgnoreCase);

    private static BackgroundProcessIdentity? ReadProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return null;
            var executable = WindowCatalog.GetProcessPath((uint)pid);
            return Path.IsPathFullyQualified(executable) && File.Exists(executable)
                ? new BackgroundProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks,
                    process.SessionId, executable)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception or
                                          InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private sealed record ObservedApp(
        BackgroundProcessIdentity Process, string Name, string Target, long Sequence);
}
