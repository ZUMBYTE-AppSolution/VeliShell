using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.Core;

public enum Appearance { System, Light, Dark }

public enum UiLanguage { System, German, English }

public enum StartupMode { Disabled, UserLogin }

public enum UpdateMode { Manual, Notify, AutomaticDownload }
public enum UpdateChannel { Stable, Test }

public enum OnlineIconMode { Disabled, OnDemand, AutomaticExactMatches }

public enum DockIconStyle { Mac, Windows }

public enum FolderDisplayMode { List, Grid, AppLauncher, CompactAppLauncher, DirectOpen }

public enum PinKind { Item, VirtualFolder }

public sealed record VirtualFolderEntry(string Id, string Name, string Target, IconReference? Icon = null);

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
    IconReference? Icon = null,
    FolderDisplayMode FolderMode = FolderDisplayMode.List,
    PinKind Kind = PinKind.Item,
    List<VirtualFolderEntry>? VirtualItems = null);

public sealed class Settings
{
    public const int CurrentSchemaVersion = 15;
    public const int CurrentOnlineIconConsentVersion = 5;
    public const double MinimumIconSize = 24;
    public const double DefaultIconSize = 52;
    public const double MaximumIconSize = 96;
    public const int MaximumPins = 32;
    public const int MaximumDockIconOverrides = 132;
    public const int MaximumVirtualFolderItems = 24;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Appearance Appearance { get; set; } = Appearance.System;
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public StartupMode Startup { get; set; } = StartupMode.Disabled;
    public UpdateMode Updates { get; set; } = UpdateMode.Notify;
    public UpdateChannel UpdateSource { get; set; } = UpdateChannel.Stable;
    public double IconSize { get; set; } = DefaultIconSize;
    public double GlassGloss { get; set; } = 50;
    public double GlassTransparency { get; set; } = 50;
    public bool NativeFramesEnabled { get; set; }
    public int NativeFramesConsentVersion { get; set; }
    public bool NativeModernMaterialsEnabled { get; set; }
    public int NativeModernMaterialsConsentVersion { get; set; }
    public bool NativeModernFramesEnabled { get; set; }
    public int NativeModernFramesConsentVersion { get; set; }
    public List<string> NativeFrameExclusions { get; set; } = [];
    public bool Magnification { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ShowRunningApps { get; set; } = true;
    public bool DockOnAllDisplays { get; set; }
    public bool RunningAppsPerDisplay { get; set; }
    public bool AutoHide { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideTaskbar { get; set; }
    public bool HideDesktopIcons { get; set; }
    public bool ShowVeliShellDockItem { get; set; } = true;
    public bool ShowWindowsStartDockItem { get; set; }
    public bool WindowsNotificationsEnabled { get; set; }
    public DockIconStyle IconStyle { get; set; } = DockIconStyle.Mac;
    public bool AdaptiveIconBackgrounds { get; set; }
    public bool MenuBarEnabled { get; set; }
    public bool MenuBarOnAllDisplays { get; set; }
    public bool MenuBarAutoHide { get; set; }
    public bool MenuBarAlwaysOnTop { get; set; } = true;
    public bool MenuBarAlwaysShowTrayApps { get; set; }
    public List<string> HiddenTrayIconIds { get; set; } = [];
    public List<string> PreferredTrayIconIds { get; set; } = [];
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
        if (NativeFramesConsentVersion != 1) { NativeFramesConsentVersion = 0; NativeFramesEnabled = false; }
        if (NativeModernMaterialsConsentVersion != 1) { NativeModernMaterialsConsentVersion = 0; NativeModernMaterialsEnabled = false; }
        if (NativeModernFramesConsentVersion != 1) { NativeModernFramesConsentVersion = 0; NativeModernFramesEnabled = false; }
        NativeFrameExclusions = (NativeFrameExclusions ?? []).Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024)
            .Select(value => value.Trim()).Where(value => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (Path.IsPathFullyQualified(value) || value.IndexOfAny(['/', '\\', ':', '*', '?']) < 0))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
        if (!Enum.IsDefined(Appearance)) Appearance = Appearance.System;
        if (!Enum.IsDefined(Language)) Language = UiLanguage.System;
        if (!Enum.IsDefined(Startup)) Startup = StartupMode.Disabled;
        if (!Enum.IsDefined(Updates)) Updates = UpdateMode.Notify;
        if (!Enum.IsDefined(UpdateSource)) UpdateSource = UpdateChannel.Stable;
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
        GlassGloss = double.IsFinite(GlassGloss) ? Math.Clamp(GlassGloss, 0, 100) : 50;
        GlassTransparency = double.IsFinite(GlassTransparency)
            ? Math.Clamp(GlassTransparency, 0, 100) : 50;
        Pins = (Pins ?? Defaults())
            .Where(p => p is not null && (p.Kind == PinKind.VirtualFolder || !string.IsNullOrWhiteSpace(p.Target)))
            .Select(NormalizePin)
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
        HiddenTrayIconIds = (HiddenTrayIconIds ?? [])
            .Where(id => id is { Length: 64 } && id.All(Uri.IsHexDigit))
            .Select(id => id.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToList();
        PreferredTrayIconIds = (PreferredTrayIconIds ?? [])
            .Where(id => id is { Length: 64 } && id.All(Uri.IsHexDigit))
            .Select(id => id.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToList();
    }

    public IconReference? GetDockIconOverride(string key) =>
        DockIconOverrides.TryGetValue(key, out var icon) ? icon : null;

    public static Pin CreateVirtualFolder(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        return new Pin(id, name.Trim(), "velishell:folder:" + id, FolderMode: FolderDisplayMode.AppLauncher,
            Kind: PinKind.VirtualFolder, VirtualItems: []);
    }

    public bool AddToVirtualFolder(string folderId, Pin item, bool removeDockPin)
    {
        var index = Pins.FindIndex(pin => pin.Kind == PinKind.VirtualFolder &&
            string.Equals(pin.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || item.Kind != PinKind.Item || string.IsNullOrWhiteSpace(item.Target)) return false;
        var folder = Pins[index];
        var entries = folder.VirtualItems?.ToList() ?? [];
        if (entries.Count >= MaximumVirtualFolderItems || entries.Any(entry =>
                string.Equals(entry.Target, item.Target, StringComparison.OrdinalIgnoreCase))) return false;
        entries.Add(new VirtualFolderEntry(item.Id, item.Name, item.Target, item.Icon));
        Pins[index] = folder with { VirtualItems = entries };
        if (removeDockPin) Pins.RemoveAll(pin => pin.Kind == PinKind.Item &&
            string.Equals(pin.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public bool RemoveFromVirtualFolder(string folderId, string entryId, bool moveToDock)
    {
        var index = Pins.FindIndex(pin => pin.Kind == PinKind.VirtualFolder &&
            string.Equals(pin.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        var folder = Pins[index];
        var entries = folder.VirtualItems?.ToList() ?? [];
        var entry = entries.FirstOrDefault(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || moveToDock && Pins.Count >= MaximumPins) return false;
        entries.Remove(entry);
        Pins[index] = folder with { VirtualItems = entries };
        if (moveToDock) Pins.Insert(index + 1, new Pin(Guid.NewGuid().ToString("N"), entry.Name,
            entry.Target, Icon: entry.Icon));
        return true;
    }

    private static Pin NormalizePin(Pin pin)
    {
        var kind = pin.Kind == PinKind.VirtualFolder ? PinKind.VirtualFolder : PinKind.Item;
        var id = string.IsNullOrWhiteSpace(pin.Id) ? Guid.NewGuid().ToString("N") : pin.Id;
        if (kind == PinKind.VirtualFolder && (id.Length > 64 || id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-'))))
            id = Guid.NewGuid().ToString("N");
        var name = string.IsNullOrWhiteSpace(pin.Name) ? "Anwendung" : pin.Name.Trim();
        if (name.Length > 100) name = name[..100];
        return pin with
        {
            Id = id,
            Name = name,
            Target = kind == PinKind.VirtualFolder ? "velishell:folder:" + id : pin.Target,
            MatchProcess = kind == PinKind.VirtualFolder ? null : pin.MatchProcess,
            Icon = NormalizeIcon(pin.Icon),
            FolderMode = kind == PinKind.VirtualFolder ?
                pin.FolderMode is FolderDisplayMode.AppLauncher or FolderDisplayMode.CompactAppLauncher
                    ? pin.FolderMode : FolderDisplayMode.AppLauncher :
                Enum.IsDefined(pin.FolderMode) ? pin.FolderMode : FolderDisplayMode.List,
            Kind = kind,
            VirtualItems = kind == PinKind.VirtualFolder
                ? (pin.VirtualItems ?? [])
                    .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Target))
                    .Select(entry => entry with
                    {
                        Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                        Name = string.IsNullOrWhiteSpace(entry.Name) ? "Anwendung" : entry.Name.Trim()[..Math.Min(100, entry.Name.Trim().Length)],
                        Icon = NormalizeIcon(entry.Icon)
                    })
                    .DistinctBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumVirtualFolderItems).ToList()
                : null
        };
    }

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
        // Current App Store, Wikipedia/Commons and macOSicons selections and legacy cache entries
        // remain readable. The legacy cache itself has no network path.
        if (icon is null) return null;
        var isAppStore = string.Equals(icon.Provider, ItunesSearchApi.ProviderId, StringComparison.Ordinal);
        var isLegacyMacOsIcons = string.Equals(icon.Provider, "macosicons", StringComparison.Ordinal);
        var isLocal = string.Equals(icon.Provider, "velishell-custom", StringComparison.Ordinal);
        var isWikipedia = string.Equals(icon.Provider, WikipediaIconApi.ProviderId, StringComparison.Ordinal);
        if (!isAppStore && !isLegacyMacOsIcons && !isLocal && !isWikipedia) return null;
        var id = icon.IconId?.Trim() ?? "";
        if (id.Length is < 1 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        var version = icon.CatalogVersion?.Trim() ?? "";
        if (version.Length is < 1 or > 32 || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) return null;
        var hash = icon.ContentSha256?.Trim().ToLowerInvariant() ?? "";
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) return null;
        if (isLocal && (id.Length != 64 || !string.Equals(id, hash, StringComparison.OrdinalIgnoreCase) || version != "1"))
            return null;
        if (isWikipedia && (id.Length != 64 || version != WikipediaIconApi.CatalogVersion)) return null;
        var provider = isLocal ? "velishell-custom" : isWikipedia ? WikipediaIconApi.ProviderId :
            isAppStore ? ItunesSearchApi.ProviderId : "macosicons";
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
