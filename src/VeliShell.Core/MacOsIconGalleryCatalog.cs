using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VeliShell.Core;

public sealed record MacOsIconGalleryEntry(
    string Id,
    string Name,
    string Date,
    string? DateDisplay,
    string? Designer,
    string? Developer,
    string Source);

public sealed record MacOsIconGalleryPlan(string ExactName, string ExactKey, bool RequireAppleDeveloper);

/// <summary>
/// Pure catalog parsing and exact local matching. This type performs no network
/// access, so app names never leave VeliShell while a match is selected.
/// </summary>
public static class MacOsIconGalleryCatalog
{
    public const int MaximumEntries = 5_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "app", "application", "anwendung", "browser", "editor", "files", "dateien",
        "program", "programm", "settings", "einstellungen", "system"
    };

    private static readonly IReadOnlyDictionary<string, string> SystemAppMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["files"] = "Finder",
            ["explorer"] = "Finder",
            ["fileexplorer"] = "Finder",
            ["windowsexplorer"] = "Finder",
            ["browser"] = "Safari",
            ["msedge"] = "Safari",
            ["microsoftedge"] = "Safari",
            ["notes"] = "TextEdit",
            ["notepad"] = "TextEdit",
            ["wordpad"] = "TextEdit",
            ["system"] = "System Settings",
            ["windowssystemsettings"] = "System Settings",
            ["systemsettings"] = "System Settings",
            ["mssettings"] = "System Settings",
            ["windowssettings"] = "System Settings",
            ["control"] = "System Settings",
            ["calculator"] = "Calculator",
            ["calculatorapp"] = "Calculator",
            ["calc"] = "Calculator",
            ["rechner"] = "Calculator",
            ["windowscalculator"] = "Calculator",
            ["microsoftwindowscalculator"] = "Calculator",
            ["hxcalendar"] = "Calendar",
            ["kalender"] = "Calendar",
            ["windowslivecalendar"] = "Calendar",
            ["microsoftwindowslivecalendar"] = "Calendar",
            ["windowsphotos"] = "Photos",
            ["microsoftphotos"] = "Photos",
            ["microsoftwindowsphotos"] = "Photos",
            ["photosapp"] = "Photos",
            ["time"] = "Clock",
            ["uhr"] = "Clock",
            ["windowsclock"] = "Clock",
            ["alarmsclock"] = "Clock",
            ["windowsalarms"] = "Clock",
            ["microsoftwindowsalarms"] = "Clock",
            ["windowsterminal"] = "Terminal",
            ["microsoftwindowsterminal"] = "Terminal",
            ["wt"] = "Terminal",
            ["openconsole"] = "Terminal",
            ["powershell"] = "Terminal",
            ["pwsh"] = "Terminal",
            ["cmd"] = "Terminal",
            ["snippingtool"] = "Screenshot",
            ["screenclip"] = "Screenshot",
            ["screensketch"] = "Screenshot",
            ["microsoftscreensketch"] = "Screenshot",
            ["microsoftstore"] = "App Store",
            ["winstore"] = "App Store",
            ["windowsstore"] = "App Store",
            ["winstoreapp"] = "App Store",
            ["microsoftwindowsstore"] = "App Store",
            ["hxoutlook"] = "Mail",
            ["outlookforwindows"] = "Mail",
            ["windowsmail"] = "Mail",
            ["mail"] = "Mail",
            ["microsoftwindowslivemail"] = "Mail",
            ["windowscommunicationsapps"] = "Mail",
            ["microsoftwindowscommunicationsapps"] = "Mail",
            ["mspaint"] = "Preview",
            ["paint"] = "Preview",
            ["microsoftpaint"] = "Preview",
            ["wmplayer"] = "QuickTime Player",
            ["mediaplayer"] = "QuickTime Player",
            ["microsoftmediaplayer"] = "QuickTime Player",
            ["musicui"] = "Music",
            ["zunemusic"] = "Music",
            ["microsoftzunemusic"] = "Music",
            ["groove"] = "Music",
            ["moviesandtv"] = "QuickTime Player",
            ["zunevideo"] = "QuickTime Player",
            ["microsoftzunevideo"] = "QuickTime Player",
            ["windowscamera"] = "Photo Booth",
            ["microsoftwindowscamera"] = "Photo Booth",
            ["taskmgr"] = "Activity Monitor",
            ["eventvwr"] = "Console",
            ["diskmgmt"] = "Disk Utility",
            ["soundrecorder"] = "Voice Memos",
            ["windowssoundrecorder"] = "Voice Memos",
            ["microsoftwindowssoundrecorder"] = "Voice Memos",
            ["microsoftstickynotes"] = "Stickies",
            ["microsoftnotes"] = "Stickies",
            ["microsoftmicrosoftstickynotes"] = "Stickies",
            ["mstsc"] = "Screen Sharing",
            ["quickassist"] = "Screen Sharing",
            ["microsoftcorporationiiquickassist"] = "Screen Sharing",
            ["msinfo32"] = "System Information",
            ["charmap"] = "Font Book",
            ["bingweather"] = "Weather",
            ["msnweather"] = "Weather",
            ["windowsmaps"] = "Maps",
            ["microsoftwindowsmaps"] = "Maps",
            ["peopleapp"] = "Contacts",
            ["microsoftpeople"] = "Contacts",
            ["microsofttodo"] = "Reminders",
            ["microsofttodos"] = "Reminders",
            ["windowstodo"] = "Reminders",
            ["windowsbackup"] = "Time Machine",
            ["microsoftwindowsnotepad"] = "TextEdit",
            ["windowseinstellungen"] = "System Settings"
        };

    private static readonly IReadOnlyDictionary<string, string> ExactThirdPartyNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chrome"] = "Google Chrome", ["googlechrome"] = "Google Chrome",
            ["firefox"] = "Firefox", ["code"] = "Visual Studio Code",
            ["devenv"] = "Visual Studio", ["winword"] = "Microsoft Word",
            ["excel"] = "Microsoft Excel", ["powerpnt"] = "Microsoft PowerPoint",
            ["outlook"] = "Microsoft Outlook", ["teams"] = "Microsoft Teams",
            ["msteams"] = "Microsoft Teams", ["onedrive"] = "Microsoft OneDrive",
            ["obs64"] = "OBS Studio", ["obs32"] = "OBS Studio", ["vlc"] = "VLC",
            ["discord"] = "Discord", ["spotify"] = "Spotify", ["steam"] = "Steam",
            ["telegram"] = "Telegram", ["telegramdesktop"] = "Telegram",
            ["signal"] = "Signal", ["signaldesktop"] = "Signal",
            ["whatsapp"] = "WhatsApp", ["brave"] = "Brave Browser",
            ["bravebrowser"] = "Brave Browser", ["opera"] = "Opera",
            ["operagx"] = "Opera GX", ["vivaldi"] = "Vivaldi",
            ["thunderbird"] = "Thunderbird", ["notepadplusplus"] = "Notepad++",
            ["githubdesktop"] = "GitHub Desktop", ["dockerdesktop"] = "Docker Desktop",
            ["slack"] = "Slack", ["zoom"] = "Zoom", ["postman"] = "Postman",
            ["figma"] = "Figma", ["blender"] = "Blender", ["gimp"] = "GIMP",
            ["inkscape"] = "Inkscape", ["audacity"] = "Audacity"
        };

    public static IReadOnlyList<MacOsIconGalleryEntry> Parse(ReadOnlySpan<byte> json)
    {
        CatalogPayload?[]? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CatalogPayload?[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Der macOS Icon Gallery-Katalog ist kein gültiges JSON-Array.", exception);
        }

        if (payload is null || payload.Length == 0 || payload.Length > MaximumEntries)
            throw new InvalidDataException("Der macOS Icon Gallery-Katalog hat eine unerwartete Größe.");

        var entries = new List<MacOsIconGalleryEntry>(payload.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in payload)
        {
            if (item is null || !IsSafeId(item.Id) || !IsSafeText(item.Name, 180) ||
                !IsIsoDate(item.Date) || !IsSafeText(item.Src, 1_024) ||
                !IsOptionalSafeText(item.DateDisplay, 80) ||
                !IsOptionalSafeText(item.Designer, 180) ||
                !IsOptionalSafeText(item.Developer, 180) || !ids.Add(item.Id))
                throw new InvalidDataException("Das Schema des macOS Icon Gallery-Katalogs hat sich geändert.");

            entries.Add(new MacOsIconGalleryEntry(
                item.Id,
                item.Name.Trim(),
                item.Date,
                CleanOptional(item.DateDisplay),
                CleanOptional(item.Designer),
                CleanOptional(item.Developer),
                item.Src.Trim()));
        }

        return entries;
    }

    public static MacOsIconGalleryPlan? CreatePlan(Pin pin) => CreatePlans(pin).FirstOrDefault();

    /// <summary>
    /// Builds exact local match candidates in priority order. Windows system
    /// analogies remain a single Apple-only candidate. Third-party process
    /// aliases may fall back to the exact visible or executable name when the
    /// Gallery does not contain the curated long-form name.
    /// </summary>
    public static IReadOnlyList<MacOsIconGalleryPlan> CreatePlans(Pin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var pinId = Normalize(pin.Id ?? "");
        var process = Normalize(pin.MatchProcess ?? "");
        var targetName = Normalize(ExtractTargetName(pin.Target) ?? "");
        var displayName = Normalize(pin.Name ?? "");

        var systemName = TryMap(SystemAppMappings, pinId)
                         ?? TryMap(SystemAppMappings, process)
                         ?? TryMapTarget(SystemAppMappings, pin.Target)
                         ?? TryMap(SystemAppMappings, targetName)
                         ?? TryMap(SystemAppMappings, displayName);
        if (!string.IsNullOrWhiteSpace(systemName))
            return [new MacOsIconGalleryPlan(systemName, Normalize(systemName), RequireAppleDeveloper: true)];

        var candidates = new[]
            {
                TryMap(ExactThirdPartyNames, process),
                TryMapTarget(ExactThirdPartyNames, pin.Target),
                TryMap(ExactThirdPartyNames, targetName),
                SelectUnmappedName(pin.Name, displayName),
                SelectUnmappedName(ExtractTargetName(pin.Target), targetName),
                SelectUnmappedName(pin.MatchProcess, process)
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .DistinctBy(Normalize, StringComparer.Ordinal)
            .Select(candidate => new MacOsIconGalleryPlan(
                candidate,
                Normalize(candidate),
                RequireAppleDeveloper: false))
            .ToArray();
        return candidates;
    }

    public static MacOsIconGalleryEntry? FindLatestExact(
        IEnumerable<MacOsIconGalleryEntry> entries,
        MacOsIconGalleryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(plan);
        return entries
            .Where(entry => string.Equals(Normalize(entry.Name), plan.ExactKey, StringComparison.Ordinal))
            .Where(entry => !plan.RequireAppleDeveloper ||
                            string.Equals(Normalize(entry.Developer ?? ""), "apple", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Date, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static string Normalize(string value)
    {
        value ??= "";
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (character == '+') builder.Append("plus");
            else if (character == '#') builder.Append("sharp");
        }
        return builder.ToString();
    }

    private static string? TryMap(IReadOnlyDictionary<string, string> mappings, string normalized) =>
        mappings.TryGetValue(normalized, out var name) ? name : null;

    private static string? TryMapTarget(IReadOnlyDictionary<string, string> mappings, string? target)
    {
        foreach (var identifier in ExtractTargetIdentifiers(target))
        {
            var mapped = TryMap(mappings, Normalize(identifier));
            if (mapped is not null) return mapped;
        }
        return null;
    }

    private static IEnumerable<string> ExtractTargetIdentifiers(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) yield break;
        var value = target.Trim().Trim('"');
        var basic = ExtractTargetName(value);
        if (!string.IsNullOrWhiteSpace(basic)) yield return basic;

        // Packaged Windows apps are commonly represented as an AUMID, either
        // directly or below shell:AppsFolder. Keep only its exact package/app
        // identifiers; the publisher-family suffix is not part of the app name.
        var separator = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
        var aumid = separator >= 0 && separator + 1 < value.Length
            ? value[(separator + 1)..]
            : value;
        var bang = aumid.IndexOf('!');
        if (bang <= 0) yield break;

        var appId = aumid[(bang + 1)..];
        if (!string.IsNullOrWhiteSpace(appId)) yield return appId;

        var packageFamily = aumid[..bang];
        var familySuffix = packageFamily.IndexOf('_');
        var packageName = familySuffix > 0 ? packageFamily[..familySuffix] : packageFamily;
        if (packageName.Contains('.', StringComparison.Ordinal)) yield return packageName;
    }

    private static string? SelectUnmappedName(string? value, string normalized) =>
        IsSafeText(value, 180) && normalized.Length >= 2 && !GenericNames.Contains(normalized)
            ? value!.Trim()
            : null;

    private static string? ExtractTargetName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var value = target.Trim().Trim('"');
        var colon = value.IndexOf(':');
        if (colon is >= 2 and <= 32 && value[..colon].All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.'))
            return value[..colon];
        try
        {
            var file = Path.GetFileName(value);
            return string.IsNullOrWhiteSpace(file) ? null : Path.GetFileNameWithoutExtension(file);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSafeId(string? value) => value is { Length: >= 3 and <= 180 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsIsoDate(string? value) => value is { Length: 10 } &&
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool IsOptionalSafeText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || IsSafeText(value, maximumLength);

    private static string? CleanOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class CatalogPayload
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Date { get; init; } = "";
        public string? DateDisplay { get; init; }
        public string? Designer { get; init; }
        public string? Developer { get; init; }
        public string Src { get; init; } = "";
    }
}
