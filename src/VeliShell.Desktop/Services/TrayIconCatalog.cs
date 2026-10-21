using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace VeliShell.Desktop.Services;

internal sealed record TrayAppIcon(string Id, string Name, string Executable, byte[] Snapshot, bool Promoted)
{
    internal string SnapshotHash => Convert.ToHexString(SHA256.HashData(Snapshot));
    internal IReadOnlyList<string> PreviousIds { get; init; } = [];
    internal string LaunchTarget { get; init; } = Executable;
    internal bool CanOpen => PackagedAppService.AppIdFromTarget(LaunchTarget) is not null || File.Exists(Executable);
    internal bool MatchesId(string id) => id.Equals(Id, StringComparison.Ordinal) || PreviousIds.Contains(id, StringComparer.Ordinal);
}

internal sealed record TrayIconCacheEntry(string? Executable, byte[]? Snapshot, string? Tooltip, bool Promoted);

/// <summary>
/// Best-effort read-only mirror of application notification icons. Explorer's
/// per-user PNG snapshots remain available when VeliShell temporarily hides
/// the taskbar. Only entries whose executable identity runs in this session are
/// used; this does not claim to be a live Shell_NotifyIcon enumeration.
/// </summary>
internal static class TrayIconCatalog
{
    internal const int MaximumIcons = 128;
    private const int MaximumRegistryEntries = 4096;
    private const int MaximumSnapshotBytes = 256 * 1024;
    private const int MaximumTotalSnapshotBytes = 16 * 1024 * 1024;
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "securityhealthsystray.exe", "velishell.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "applicationframehost.exe", "runtimebroker.exe", "textinputhost.exe",
        "sihost.exe", "taskhostw.exe", "conhost.exe", "dllhost.exe",
        // Steam has its own menu-bar indicator with its existing app actions.
        "dwm.exe", "rundll32.exe", "svchost.exe", "steam.exe", "steamwebhelper.exe",
        "msedgewebview2.exe"
    };
    private static readonly object CacheGate = new();
    private static IReadOnlyList<TrayAppIcon> _cached = [];
    private static DateTime _cacheUntilUtc;

    internal static IReadOnlyList<TrayAppIcon> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        lock (CacheGate)
        {
            if (DateTime.UtcNow < _cacheUntilUtc) return _cached;
            _cached = ReadCore();
            _cacheUntilUtc = DateTime.UtcNow.AddSeconds(8);
            return _cached;
        }
    }

    private static IReadOnlyList<TrayAppIcon> ReadCore()
    {
        var running = RunningExecutablePaths();
        if (running.Count == 0) return [];
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null) return [];
            return SelectIcons(ReadEntries(root), running.Keys).Select(icon =>
            {
                if (!running.TryGetValue(icon.Executable, out var pid) ||
                    !string.Equals(WindowCatalog.GetProcessPath(pid), icon.Executable, StringComparison.OrdinalIgnoreCase)) return icon;
                var package = PackagedAppService.ForProcess(pid);
                return package is null ? icon : icon with { LaunchTarget = package.Target };
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<TrayIconCacheEntry> ReadEntries(RegistryKey root)
    {
        foreach (var name in root.GetSubKeyNames().Take(MaximumRegistryEntries))
        {
            TrayIconCacheEntry? entry = null;
            try
            {
                using var key = root.OpenSubKey(name);
                entry = new(key?.GetValue("ExecutablePath") as string, key?.GetValue("IconSnapshot") as byte[],
                    key?.GetValue("InitialTooltip") as string, Convert.ToString(key?.GetValue("IsPromoted")) == "1");
            }
            catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
            {
                // Explorer may replace individual cache keys while we are reading them.
            }
            if (entry is not null) yield return entry;
        }
    }

    internal static IReadOnlyList<TrayAppIcon> SelectIcons(IEnumerable<TrayIconCacheEntry> entries,
        IEnumerable<string> runningPaths, Func<Guid, string?>? knownFolder = null)
    {
        var running = runningPaths.Select(path => AppIdentityService.NormalizeExecutable(path))
            .Where(path => path is not null).Cast<string>()
            .Where(path => !IgnoredProcesses.Contains(Path.GetFileName(path)))
            .GroupBy(path => AppIdentityService.ExecutableKey(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, TrayAppIcon>(StringComparer.OrdinalIgnoreCase);
        var snapshotBytes = 0;
        foreach (var entry in entries.Take(MaximumRegistryEntries))
        {
            var cachedPath = AppIdentityService.NormalizeExecutable(entry.Executable, knownFolder);
            var key = AppIdentityService.ExecutableKey(cachedPath);
            if (key is null || !running.TryGetValue(key, out var executable)) continue;
            var bytes = entry.Snapshot;
            if (bytes is null || bytes.Length is < 32 or > MaximumSnapshotBytes ||
                !bytes.AsSpan().StartsWith(PngHeader)) continue;
            var exists = candidates.TryGetValue(key, out var previous);
            var previousIds = (previous?.PreviousIds ?? []).Append(
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cachedPath!.ToUpperInvariant()))).ToLowerInvariant())
                .Distinct(StringComparer.Ordinal).Take(128).ToArray();
            if (exists && previous!.Promoted && !entry.Promoted)
            {
                candidates[key] = previous with { PreviousIds = previousIds };
                continue;
            }
            if (!exists && candidates.Count >= MaximumIcons) continue;
            var newByteCount = snapshotBytes - (previous?.Snapshot.Length ?? 0) + bytes.Length;
            if (newByteCount > MaximumTotalSnapshotBytes) continue;
            snapshotBytes = newByteCount;
            var displayName = PackagedAppService.KnownDisplayName(AppIdentityService.PackageFamilyFromPath(executable) ?? "") ??
                              entry.Tooltip?.Split('\n')[0].Trim();
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = Path.GetFileNameWithoutExtension(executable).Replace('_', ' ');
            if (displayName.Length > 100) displayName = displayName[..100];
            // The stable key retains package publisher + app-relative executable.
            // Old cache snapshots can survive a Store update without matching an unrelated ChatGPT.exe.
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).ToLowerInvariant();
            candidates[key] = new(id, displayName, executable, bytes, entry.Promoted || previous?.Promoted == true)
            {
                PreviousIds = previousIds
            };
        }
        return candidates.Values.OrderByDescending(icon => icon.Promoted)
            .ThenBy(icon => icon.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static Dictionary<string, uint> RunningExecutablePaths()
    {
        var running = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return running;
        }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session || process.HasExited) continue;
                    var path = WindowCatalog.GetProcessPath((uint)process.Id);
                    if (Path.IsPathFullyQualified(path)) running.TryAdd(path, (uint)process.Id);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                                              NotSupportedException or IOException or ArgumentException)
                {
                    // Other sessions and protected processes are not tray candidates.
                }
            }
        }
        return running;
    }
}
