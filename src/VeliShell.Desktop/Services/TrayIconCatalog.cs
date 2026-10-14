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
}

/// <summary>
/// Best-effort read-only mirror of application notification icons. Explorer's
/// per-user PNG snapshots remain available when VeliShell temporarily hides
/// the taskbar. Only entries whose exact executable runs in this session are
/// used; this does not claim to be a live Shell_NotifyIcon enumeration.
/// </summary>
internal static class TrayIconCatalog
{
    private const int MaximumIcons = 48;
    private const int MaximumRegistryEntries = 256;
    private const int MaximumSnapshotBytes = 256 * 1024;
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
            var candidates = new List<TrayAppIcon>();
            foreach (var name in root.GetSubKeyNames().Take(MaximumRegistryEntries))
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    var executable = key?.GetValue("ExecutablePath") as string;
                    if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
                        !running.Contains(executable) || IgnoredProcesses.Contains(Path.GetFileName(executable))) continue;
                    var bytes = key?.GetValue("IconSnapshot") as byte[];
                    if (bytes is null || bytes.Length is < 32 or > MaximumSnapshotBytes ||
                        !bytes.AsSpan().StartsWith(PngHeader)) continue;
                    var displayName = (key?.GetValue("InitialTooltip") as string)?.Split('\n')[0].Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = Path.GetFileNameWithoutExtension(executable).Replace('_', ' ');
                    if (displayName.Length > 100) displayName = displayName[..100];
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        executable.ToUpperInvariant()))).ToLowerInvariant();
                    var promoted = Convert.ToString(key?.GetValue("IsPromoted")) == "1";
                    candidates.Add(new TrayAppIcon(id, displayName, executable, bytes, promoted));
                }
                catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
                {
                    // Explorer may replace individual cache keys while we are reading them.
                }
            }
            return candidates.OrderByDescending(icon => icon.Promoted)
                .ThenBy(icon => icon.Name, StringComparer.CurrentCultureIgnoreCase)
                .DistinctBy(icon => icon.Id, StringComparer.Ordinal)
                .Take(MaximumIcons).ToArray();
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static HashSet<string> RunningExecutablePaths()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (Path.IsPathFullyQualified(path)) running.Add(path);
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
