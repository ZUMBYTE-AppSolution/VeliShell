using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.Core;

public enum Appearance { System, Light, Dark }

public enum UiLanguage { System, German, English }

public enum StartupMode { Disabled, UserLogin }

public enum UpdateMode { Manual, Notify, AutomaticDownload }

public enum OnlineIconMode { Disabled, OnDemand, AutomaticExactMatches }

public enum DockIconStyle { Mac, Windows }

public sealed record IconReference(
    string Provider,
    string IconId,
    string CatalogVersion,
    string ContentSha256);

public sealed record Pin(
    string Id,
    string Name,
    string Target,
    string? MatchProcess = null,
    IconReference? Icon = null);

public sealed class Settings
{
    public const int CurrentSchemaVersion = 8;
    public const int CurrentOnlineIconConsentVersion = 4;
    public const double MinimumIconSize = 32;
    public const double DefaultIconSize = 52;
    public const double MaximumIconSize = 96;
    public const int MaximumPins = 32;
    public const int MaximumDockIconOverrides = 132;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Appearance Appearance { get; set; } = Appearance.System;
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public StartupMode Startup { get; set; } = StartupMode.Disabled;
    public UpdateMode Updates { get; set; } = UpdateMode.Notify;
    public double IconSize { get; set; } = DefaultIconSize;
    public bool Magnification { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ShowRunningApps { get; set; } = true;
    public bool AutoHide { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideTaskbar { get; set; }
    public bool HideDesktopIcons { get; set; }
    public bool ShowVeliShellDockItem { get; set; } = true;
    public bool ShowWindowsStartDockItem { get; set; }
    public bool WindowsNotificationsEnabled { get; set; }
    public DockIconStyle IconStyle { get; set; } = DockIconStyle.Mac;
    public bool MenuBarEnabled { get; set; }
    public bool MenuBarAutoHide { get; set; }
    public bool MenuBarAlwaysOnTop { get; set; } = true;
    public OnlineIconMode OnlineIcons { get; set; } = OnlineIconMode.Disabled;
    public int OnlineIconConsentVersion { get; set; }
    public bool FirstRunCompleted { get; set; }
    public List<Pin> Pins { get; set; } = Defaults();
    public Dictionary<string, IconReference> DockIconOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static List<Pin> Defaults() =>
    [
        new("files", "Dateien", "explorer.exe", "explorer"),
        new("browser", "Browser", "microsoft-edge:", "msedge"),
        new("notes", "Editor", "notepad.exe", "notepad"),
        new("system", "Windows-Einstellungen", "ms-settings:", "SystemSettings")
    ];

    public void Normalize()
    {
        // Only our own preferences are normalized. No Windows setting is changed.
        SchemaVersion = CurrentSchemaVersion;
        if (!Enum.IsDefined(Appearance)) Appearance = Appearance.System;
        if (!Enum.IsDefined(Language)) Language = UiLanguage.System;
        if (!Enum.IsDefined(Startup)) Startup = StartupMode.Disabled;
        if (!Enum.IsDefined(Updates)) Updates = UpdateMode.Notify;
        if (!Enum.IsDefined(IconStyle)) IconStyle = DockIconStyle.Mac;
        if (!Enum.IsDefined(OnlineIcons)) OnlineIcons = OnlineIconMode.Disabled;
        OnlineIconConsentVersion = Math.Clamp(OnlineIconConsentVersion, 0, CurrentOnlineIconConsentVersion);
        if (OnlineIconConsentVersion < CurrentOnlineIconConsentVersion)
            OnlineIcons = OnlineIconMode.Disabled;
        // API results are now presented to the user with creator attribution;
        // unattended exact-match replacement is intentionally retired.
        if (OnlineIcons == OnlineIconMode.AutomaticExactMatches)
            OnlineIcons = OnlineIconMode.OnDemand;
        IconSize = double.IsFinite(IconSize)
            ? Math.Clamp(IconSize, MinimumIconSize, MaximumIconSize)
            : DefaultIconSize;
        Pins = (Pins ?? Defaults())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Target))
            .Select(p => p with
            {
                Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Anwendung" : p.Name.Trim(),
                Icon = NormalizeIcon(p.Icon)
            })
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPins).ToList();
        DockIconOverrides = (DockIconOverrides ?? new Dictionary<string, IconReference>())
            .Select(pair => (Key: NormalizeDockIconKey(pair.Key), Icon: NormalizeIcon(pair.Value)))
            .Where(pair => pair.Key is not null && pair.Icon is not null)
            .DistinctBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumDockIconOverrides)
            .ToDictionary(
                pair => pair.Key!,
                pair => pair.Icon!,
                StringComparer.OrdinalIgnoreCase);
    }

    public IconReference? GetDockIconOverride(string key) =>
        DockIconOverrides.TryGetValue(key, out var icon) ? icon : null;

    public static string RunningDockIconKey(string executable, string processName)
    {
        var identity = string.IsNullOrWhiteSpace(executable) ? processName : executable;
        identity = identity.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return "running-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IconReference? NormalizeIcon(IconReference? icon)
    {
        // Current App Store and macOSicons selections and legacy cache entries
        // remain readable. The legacy cache itself has no network path.
        if (icon is null) return null;
        var isAppStore = string.Equals(icon.Provider, ItunesSearchApi.ProviderId, StringComparison.Ordinal);
        var isLegacyMacOsIcons = string.Equals(icon.Provider, "macosicons", StringComparison.Ordinal);
        var isLocal = string.Equals(icon.Provider, "velishell-custom", StringComparison.Ordinal);
        if (!isAppStore && !isLegacyMacOsIcons && !isLocal) return null;
        var id = icon.IconId?.Trim() ?? "";
        if (id.Length is < 1 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        var version = icon.CatalogVersion?.Trim() ?? "";
        if (version.Length is < 1 or > 32 || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) return null;
        var hash = icon.ContentSha256?.Trim().ToLowerInvariant() ?? "";
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) return null;
        if (isLocal && (id.Length != 64 || !string.Equals(id, hash, StringComparison.OrdinalIgnoreCase) || version != "1"))
            return null;
        var provider = isLocal ? "velishell-custom" : isAppStore ? ItunesSearchApi.ProviderId : "macosicons";
        return new IconReference(provider, id.ToLowerInvariant(), version, hash);
    }

    private static string? NormalizeDockIconKey(string? key)
    {
        key = key?.Trim().ToLowerInvariant();
        if (key is "start" or "velishell" or "overflow" or "trash-empty" or "trash-full") return key;
        if (key is not { Length: 72 } || !key.StartsWith("running-", StringComparison.Ordinal)) return null;
        return key[8..].All(Uri.IsHexDigit) ? key : null;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadWarning { get; private set; }

    public SettingsStore(string directoryPath) => DirectoryPath = directoryPath;

    public Settings Load()
    {
        LoadWarning = null;
        if (!File.Exists(FilePath)) return new Settings();
        try { return Read(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "Die Einstellungen waren nicht lesbar. " + e.Message;
            try
            {
                var backup = Read(FilePath + ".bak");
                LoadWarning += " Die letzte Sicherung wurde geladen.";
                return backup;
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException)
            {
                return new Settings();
            }
        }
    }

    private static Settings Read(string path)
    {
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions)
                    ?? throw new JsonException("Leere Konfiguration.");
        value.Normalize();
        return value;
    }

    public void Save(Settings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(FilePath))
        {
            // File.Replace provides an atomic replacement and a previous-version backup.
            File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
        }
        else File.Move(temp, FilePath);
    }
}
