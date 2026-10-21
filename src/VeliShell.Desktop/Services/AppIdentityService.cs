using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record LaunchIdentity(string? AppId = null, string? Executable = null, string? ForwardedAppId = null);

/// <summary>Read-only shell identity lookup. Never launches or resolves a link with UI.</summary>
internal static class AppIdentityService
{
    private static readonly ConcurrentDictionary<Guid, string> KnownFolders = new();
    private static readonly ConcurrentDictionary<string, (long Expires, LaunchIdentity Identity)> Protocols =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (long Expires, string? Path)> ExecutableNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex PackagePath = new(
        @"^(?<root>.+\\WindowsApps\\)(?<name>[^\\]+?)_\d+\.\d+\.\d+\.\d+_[^\\_]+_[^\\]*_(?<publisher>[^\\_]+)\\(?<relative>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex AppVersionDirectory = new(
        @"(?<=\\)app-\d+(?:\.\d+){1,3}(?:-[a-z0-9.-]+)?(?=\\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ProcessStart = new(
        "(?:^|\\s)--processStart(?:=|\\s+)(?:\"(?<exe>[^\"]+)\"|(?<exe>[^\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static string? NormalizeExecutable(string? value, Func<Guid, string?>? knownFolder = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.Any(char.IsControl)) return null;
        try
        {
            var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
            if (path.Length > 39 && path[0] == '{' && path[37] == '}' && path[38] == '\\')
            {
                if (!Guid.TryParse(path[..38], out var id)) return null;
                var root = (knownFolder ?? ReadKnownFolder)(id);
                if (string.IsNullOrWhiteSpace(root)) return null;
                path = Path.Combine(root, path[39..]);
            }
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '*', '?', '<', '>', '|']) >= 0) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static string? ReadKnownFolder(Guid id)
    {
        if (KnownFolders.TryGetValue(id, out var cached)) return cached;
        var initialized = NativeMethods.CoInitializeEx(0, 0);
        nint text = 0;
        try
        {
            // No CREATE/INIT flags: never creates or redirects Windows folders.
            if (NativeMethods.SHGetKnownFolderPath(ref id, 0x14000, 0, out text) != 0 || text == 0) return null;
            var path = Marshal.PtrToStringUni(text);
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (KnownFolders.Count < 128) KnownFolders.TryAdd(id, path);
            return path;
        }
        finally
        {
            if (text != 0) Marshal.FreeCoTaskMem(text);
            if (initialized >= 0) NativeMethods.CoUninitialize();
        }
    }

    internal static string? PackageFamilyFromPath(string? executable)
    {
        var path = NormalizeExecutable(executable);
        if (path is null) return null;
        Match match;
        try { match = PackagePath.Match(path); }
        catch (RegexMatchTimeoutException) { return null; }
        return match.Success ? match.Groups["name"].Value + "_" + match.Groups["publisher"].Value : null;
    }

    internal static string? ExecutableKey(string? executable)
    {
        var path = NormalizeExecutable(executable);
        if (path is null) return null;
        Match package;
        try { package = PackagePath.Match(path); }
        catch (RegexMatchTimeoutException) { return path; }
        // Preserve the package publisher AND relative executable, not just ChatGPT.exe.
        if (package.Success)
            return "package:" + package.Groups["name"].Value + "_" + package.Groups["publisher"].Value +
                   "\\" + package.Groups["relative"].Value;
        try { return AppVersionDirectory.Replace(path, "app-<version>"); }
        catch (RegexMatchTimeoutException) { return path; }
    }

    internal static bool SameExecutable(string? first, string? second) =>
        ExecutableKey(first) is { } key && ExecutableKey(second) is { } other &&
        key.Equals(other, StringComparison.OrdinalIgnoreCase);

    internal static LaunchIdentity ForTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 32767 || target.Any(char.IsControl)) return new();
        if (PackagedAppService.AppIdFromTarget(target) is { } id) return new(id);
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var link = ShellLinkService.Read(target);
            if (link is null) return new();
            var linkedExecutable = ExecutableForShortcut(link.Target, link.Arguments);
            return new(link.AppUserModelId ?? PackagedAppService.AppIdFromTarget(link.Target), linkedExecutable,
                SystemForwarderAppId(linkedExecutable));
        }
        if ((NormalizeExecutable(target) ?? ResolveExecutableName(target)) is { } executable &&
            executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new(Executable: executable, ForwardedAppId: SystemForwarderAppId(executable));
        if (target.Length > 2048 || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return new();
        var now = Environment.TickCount64;
        if (Protocols.TryGetValue(uri.Scheme, out var cached) && now < cached.Expires) return cached.Identity;
        if (!StableLaunchTargetService.IsRegisteredProtocol(target))
        {
            if (Protocols.Count >= 128) Protocols.Clear();
            Protocols[uri.Scheme] = (now + 15000, new());
            return new();
        }
        // Ask the current Windows association; never assume scheme == process name.
        var handler = QueryProtocol(uri.Scheme, 2);
        var identity = new LaunchIdentity(QueryProtocol(uri.Scheme, 21),
            ExecutableForShortcut(handler, handler?.EndsWith("\\Update.exe", StringComparison.OrdinalIgnoreCase) == true
                ? QueryProtocol(uri.Scheme, 1) : null));
        if (Protocols.Count > 128) Protocols.Clear();
        Protocols[uri.Scheme] = (now + 15000, identity);
        return identity;
    }

    private static string? ResolveExecutableName(string name)
    {
        if (name.Length is < 5 or > 260 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(['\\', '/', ':', '"']) >= 0 || name.Any(char.IsControl)) return null;
        var now = Environment.TickCount64;
        if (ExecutableNames.TryGetValue(name, out var cached) && now < cached.Expires) return cached.Path;
        string? path = null;
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                if (NormalizeExecutable(key?.GetValue(null) as string) is { } registered)
                { path = registered; break; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // Shell defaults such as explorer.exe/notepad.exe must not fall back to
        // basename matching. Resolve trusted Windows locations, never the CWD.
        if (path is null)
            foreach (var directory in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        if (ExecutableNames.Count >= 128) ExecutableNames.Clear();
        ExecutableNames[name] = (now + 15000, path);
        return path;
    }

    internal static string? SystemForwarderAppId(string? executable)
    {
        var path = NormalizeExecutable(executable);
        if (path is null) return null;
        var directory = Path.GetDirectoryName(path);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.Equals(directory, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory, windows, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory, Path.Combine(windows, "SysWOW64"), StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetFileName(path).ToLowerInvariant() switch
        {
            "notepad.exe" => "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            "mspaint.exe" => "Microsoft.Paint_8wekyb3d8bbwe!App",
            "calc.exe" => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            _ => null
        };
    }

    internal static string? ExecutableForShortcut(string? target, string? arguments)
    {
        var path = NormalizeExecutable(target);
        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.GetFileName(path).Equals("Update.exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(arguments) || arguments.Length > 32768) return path;
        Match match;
        try { match = ProcessStart.Match(arguments); }
        catch (RegexMatchTimeoutException) { return path; }
        var app = match.Groups["exe"].Value;
        if (!match.Success || app.IndexOfAny(['\\', '/', ':', '"']) >= 0 ||
            !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || app.Any(char.IsControl)) return path;
        // Squirrel's launcher is not the app window owner. This is an identity
        // pattern only, never an executable path we launch or write into the pin.
        return Path.Combine(Path.GetDirectoryName(path)!, "app-0.0", app);
    }

    private static string? QueryProtocol(string scheme, uint kind)
    {
        var initialized = NativeMethods.CoInitializeEx(0, 0);
        try
        {
            uint length = 0;
            NativeMethods.AssocQueryString(0x1000, kind, scheme, null, null, ref length);
            if (length is 0 or > 32768) return null;
            var text = new StringBuilder((int)length);
            return NativeMethods.AssocQueryString(0x1000, kind, scheme, null, text, ref length) == 0 && text.Length > 0
                ? text.ToString() : null;
        }
        finally { if (initialized >= 0) NativeMethods.CoUninitialize(); }
    }

    internal static bool Matches(NativeWindow window, Pin pin, LaunchIdentity identity)
    {
        if (pin.Kind == PinKind.VirtualFolder) return false;
        if (!string.IsNullOrWhiteSpace(identity.AppId))
        {
            if (string.Equals(identity.AppId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(identity.AppId, window.WindowAppUserModelId, StringComparison.OrdinalIgnoreCase)) return true;
            // An explicit identity conflict must never degrade to a shared EXE name.
            if (!string.IsNullOrWhiteSpace(window.AppUserModelId) ||
                !string.IsNullOrWhiteSpace(window.WindowAppUserModelId) || identity.Executable is null) return false;
        }
        if (identity.Executable is not null)
            return SameExecutable(identity.Executable, window.Executable) ||
                   identity.ForwardedAppId is not null &&
                   string.Equals(identity.ForwardedAppId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        // An unresolved target is not evidence that an unrelated process with
        // the same basename is this app. Keep old MatchProcess fields readable,
        // but never use them to override an unknown/conflicting shell identity.
        return false;
    }
}
