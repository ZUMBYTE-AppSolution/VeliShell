using VeliShell.Core;
using System.Text;

var failures = 0;
var count = 0;
void Test(string name, Action run)
{
    count++;
    try { run(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL  {name}: {error.Message}"); }
}
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
var temp = Path.Combine(Path.GetTempPath(), "VeliShellTests-" + Guid.NewGuid().ToString("N"));
try
{
    Test("SemVer parses release tag", () => Check(SemanticVersion.Parse("v0.3.0").ToString() == "0.3.0"));
    Test("SemVer orders prerelease before release", () => Check(SemanticVersion.Parse("1.0.0-rc.2") < SemanticVersion.Parse("1.0.0")));
    Test("SemVer orders numeric prerelease identifiers numerically", () => Check(SemanticVersion.Parse("1.0.0-rc.9") < SemanticVersion.Parse("1.0.0-rc.10")));
    Test("SemVer ignores build metadata for precedence", () => Check(SemanticVersion.Parse("1.2.3+one").CompareTo(SemanticVersion.Parse("1.2.3+two")) == 0));
    Test("SemVer rejects ambiguous leading zero", () => Check(!SemanticVersion.TryParse("1.02.3", out _)));
    Test("Defaults: four pins", () => Check(new Settings().Pins.Count == 4));
    Test("Default appearance: system", () => Check(new Settings().Appearance == Appearance.System));
    Test("Default language follows system", () => Check(new Settings().Language == UiLanguage.System));
    Test("Startup is opt-in", () => Check(new Settings().Startup == StartupMode.Disabled));
    Test("Update checks default to notify", () => Check(new Settings().Updates == UpdateMode.Notify));
    Test("Taskbar hiding is opt-in", () => Check(!new Settings().HideTaskbar));
    Test("Desktop-icon hiding is opt-in", () => Check(!new Settings().HideDesktopIcons));
    Test("VeliShell dock item is visible by default", () => Check(new Settings().ShowVeliShellDockItem));
    Test("Onboarding is required only until completion", () =>
        Check(!new Settings().FirstRunCompleted));
    Test("Windows Start dock item is opt-in", () =>
        Check(!new Settings().ShowWindowsStartDockItem));
    Test("Mac icon style is the default", () => Check(new Settings().IconStyle == DockIconStyle.Mac));
    Test("Menu bar is opt-in", () => Check(!new Settings().MenuBarEnabled));
    Test("Online icons are opt-in", () => Check(new Settings().OnlineIcons == OnlineIconMode.Disabled));
    Test("Clamp large icon size", () => { var s = new Settings { IconSize = 500 }; s.Normalize(); Check(s.IconSize == Settings.MaximumIconSize); });
    Test("Clamp small icon size", () => { var s = new Settings { IconSize = -1 }; s.Normalize(); Check(s.IconSize == Settings.MinimumIconSize); });
    Test("Handle NaN", () => { var s = new Settings { IconSize = double.NaN }; s.Normalize(); Check(s.IconSize == Settings.DefaultIconSize); });
    Test("Handle Infinity", () => { var s = new Settings { IconSize = double.PositiveInfinity }; s.Normalize(); Check(s.IconSize == Settings.DefaultIconSize); });
    Test("Invalid enum falls back", () => { var s = new Settings { Appearance = (Appearance)999 }; s.Normalize(); Check(s.Appearance == Appearance.System); });
    Test("Invalid language falls back", () => { var s = new Settings { Language = (UiLanguage)999 }; s.Normalize(); Check(s.Language == UiLanguage.System); });
    Test("Invalid startup mode falls back", () => { var s = new Settings { Startup = (StartupMode)999 }; s.Normalize(); Check(s.Startup == StartupMode.Disabled); });
    Test("Invalid update mode falls back", () => { var s = new Settings { Updates = (UpdateMode)999 }; s.Normalize(); Check(s.Updates == UpdateMode.Notify); });
    Test("Invalid icon style falls back", () => { var s = new Settings { IconStyle = (DockIconStyle)999 }; s.Normalize(); Check(s.IconStyle == DockIconStyle.Mac); });
    Test("Deduplicate pin IDs", () => { var s = new Settings { Pins = [new("a", "A", "a.exe"), new("A", "B", "b.exe")] }; s.Normalize(); Check(s.Pins.Count == 1); });
    Test("Filter missing targets", () => { var s = new Settings { Pins = [new("x", "", "")] }; s.Normalize(); Check(s.Pins.Count == 0); });
    Test("Generate missing IDs", () => { var s = new Settings { Pins = [new("", "A", "a.exe")] }; s.Normalize(); Check(s.Pins[0].Id.Length > 0); });
    Test("Normalize empty labels", () => { var s = new Settings { Pins = [new("a", "  ", "a.exe")] }; s.Normalize(); Check(s.Pins[0].Name == "Anwendung"); });
    Test("Limit pins", () => { var s = new Settings { Pins = Enumerable.Range(0, 50).Select(i => new Pin(i.ToString(), "A", "a.exe")).ToList() }; s.Normalize(); Check(s.Pins.Count == Settings.MaximumPins); });
    Test("Empty pins stay empty", () => { var s = new Settings { Pins = [] }; s.Normalize(); Check(s.Pins.Count == 0); });
    Test("Explorer pin command accepts one exact Unicode path", () =>
    {
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "Über & Leerzeichen.txt");
        File.WriteAllText(file, "test");
        Check(ShellPinCommand.TryParse(["--pin-to-dock", file], out var parsed)
              && string.Equals(parsed, Path.GetFullPath(file), StringComparison.Ordinal));
    });
    Test("Explorer pin command accepts a directory and normalizes its ending", () =>
    {
        var directory = Path.Combine(temp, "Ordner mit Leerzeichen");
        Directory.CreateDirectory(directory);
        Check(ShellPinCommand.TryParse(["--PIN-TO-DOCK", directory + Path.DirectorySeparatorChar], out var parsed)
              && string.Equals(parsed, directory, StringComparison.Ordinal)
              && ShellPinCommand.RefersToSameExistingPath(directory, directory + Path.DirectorySeparatorChar));
    });
    Test("Explorer pin command rejects extra, relative and missing paths", () =>
    {
        var existing = Path.Combine(temp, "Über & Leerzeichen.txt");
        Check(!ShellPinCommand.TryParse(["--pin-to-dock", existing, "second"], out _));
        Check(!ShellPinCommand.TryParse(["--pin-to-dock", ".\\relative.txt"], out _));
        Check(!ShellPinCommand.TryParse(["--pin-to-dock", Path.Combine(temp, "missing.txt")], out _));
        Check(!ShellPinCommand.TryParse(["--other", existing], out _));
    });
    Test("Valid online icon survives normalization", () =>
    {
        var icon = new IconReference(ItunesSearchApi.ProviderId, new string('c', 64),
            ItunesSearchApi.CatalogVersion, new string('a', 64));
        var s = new Settings { OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion,
            Pins = [new("a", "Firefox", "firefox.exe", "firefox", icon)] };
        s.Normalize();
        Check(s.SchemaVersion == Settings.CurrentSchemaVersion && s.Pins[0].Icon?.IconId == new string('c', 64));
    });
    Test("Legacy macOSicons cache reference survives normalization", () =>
    {
        var icon = new IconReference("macosicons", new string('c', 64), "api-v1", new string('a', 64));
        var s = new Settings { Pins = [new("a", "Firefox", "firefox.exe", "firefox", icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon == icon);
    });
    Test("Retired Gallery reference is discarded", () =>
    {
        var icon = new IconReference("macosicongallery", new string('c', 64), "search-data-v1", new string('a', 64));
        var s = new Settings { Pins = [new("a", "Firefox", "firefox.exe", "firefox", icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon is null);
    });
    Test("Valid local icon survives normalization", () =>
    {
        var hash = new string('a', 64);
        var icon = new IconReference("velishell-custom", hash, "1", hash);
        var s = new Settings { Pins = [new("a", "A", "a.exe", null, icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon == icon);
    });
    Test("Local icon requires a content-addressed reference", () =>
    {
        var icon = new IconReference("velishell-custom", new string('a', 64), "1", new string('b', 64));
        var s = new Settings { Pins = [new("a", "A", "a.exe", null, icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon is null);
    });
    Test("Dock icon overrides keep fixed and stable running identities", () =>
    {
        var hash = new string('c', 64);
        var icon = new IconReference("velishell-custom", hash, "1", hash);
        var running = Settings.RunningDockIconKey(@"C:\Apps\Example.exe", "Example");
        var sameRunning = Settings.RunningDockIconKey("c:/apps/example.exe", "ignored");
        var s = new Settings
        {
            DockIconOverrides = new Dictionary<string, IconReference>
            {
                ["trash-empty"] = icon,
                ["start"] = icon,
                [running.ToUpperInvariant()] = icon,
                ["unknown-internal-element"] = icon
            }
        };
        s.Normalize();
        Check(running == sameRunning && s.GetDockIconOverride("trash-empty") == icon &&
              s.GetDockIconOverride("start") == icon &&
              s.GetDockIconOverride(running) == icon && s.DockIconOverrides.Count == 3);
    });
    Test("Old online consent disables provider", () =>
    {
        var s = new Settings { OnlineIcons = OnlineIconMode.AutomaticExactMatches, OnlineIconConsentVersion = 1 };
        s.Normalize();
        Check(s.OnlineIcons == OnlineIconMode.Disabled && s.OnlineIconConsentVersion == 1);
    });
    Test("Automatic icon selection migrates to picker", () =>
    {
        var s = new Settings
        {
            OnlineIcons = OnlineIconMode.AutomaticExactMatches,
            OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion
        };
        s.Normalize();
        Check(s.OnlineIcons == OnlineIconMode.OnDemand);
    });
    Test("Invalid online icon is discarded", () =>
    {
        var icon = new IconReference("other", "../bad", "latest", "nope");
        var s = new Settings { Pins = [new("a", "A", "a.exe", null, icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon is null);
    });
    Test("Move pin to first insertion slot", () =>
    {
        var pins = new List<Pin> { new("a", "A", "a.exe"), new("b", "B", "b.exe"), new("c", "C", "c.exe") };
        Check(PinOrder.MoveToInsertionIndex(pins, "c", 0));
        Check(pins.Select(p => p.Id).SequenceEqual(["c", "a", "b"]));
    });
    Test("Move pin to final insertion slot", () =>
    {
        var pins = new List<Pin> { new("a", "A", "a.exe"), new("b", "B", "b.exe"), new("c", "C", "c.exe") };
        Check(PinOrder.MoveToInsertionIndex(pins, "a", pins.Count));
        Check(pins.Select(p => p.Id).SequenceEqual(["b", "c", "a"]));
    });
    Test("Move pin between later items", () =>
    {
        var pins = new List<Pin> { new("a", "A", "a.exe"), new("b", "B", "b.exe"), new("c", "C", "c.exe"), new("d", "D", "d.exe") };
        Check(PinOrder.MoveToInsertionIndex(pins, "b", 3));
        Check(pins.Select(p => p.Id).SequenceEqual(["a", "c", "b", "d"]));
    });
    Test("Pin move is case insensitive", () =>
    {
        var pins = new List<Pin> { new("Alpha", "A", "a.exe"), new("b", "B", "b.exe") };
        Check(PinOrder.MoveToInsertionIndex(pins, "ALPHA", 2));
        Check(pins.Select(p => p.Id).SequenceEqual(["b", "Alpha"]));
    });
    Test("Pin move clamps insertion slot", () =>
    {
        var pins = new List<Pin> { new("a", "A", "a.exe"), new("b", "B", "b.exe"), new("c", "C", "c.exe") };
        Check(PinOrder.MoveToInsertionIndex(pins, "c", -20));
        Check(pins.Select(p => p.Id).SequenceEqual(["c", "a", "b"]));
        Check(PinOrder.MoveToInsertionIndex(pins, "c", 200));
        Check(pins.Select(p => p.Id).SequenceEqual(["a", "b", "c"]));
    });
    Test("Unknown and unchanged pin moves are no-ops", () =>
    {
        var pins = new List<Pin> { new("a", "A", "a.exe"), new("b", "B", "b.exe") };
        Check(!PinOrder.MoveToInsertionIndex(pins, "missing", 0));
        Check(!PinOrder.MoveToInsertionIndex(pins, "a", 1));
        Check(pins.Select(p => p.Id).SequenceEqual(["a", "b"]));
    });
    Test("Magnification at center", () => Check(Math.Abs(DockMath.ScaleAt(0, 52, true) - 1.42) < 0.001));
    Test("No magnification outside radius", () => Check(DockMath.ScaleAt(200, 52, true) == 1));
    Test("Disabled magnification", () => Check(DockMath.ScaleAt(0, 52, false) == 1));
    Test("Symmetric magnification", () => Check(DockMath.ScaleAt(-30, 52, true) == DockMath.ScaleAt(30, 52, true)));
    Test("Capacity bounded", () => Check(DockMath.VisibleCapacity(1920, 52) >= 20 && DockMath.VisibleCapacity(100, 52) == 3));
    Test("Recycle Bin unavailable state", () => Check(RecycleBinState.From(false, 42) == RecycleBinFillState.Unavailable));
    Test("Recycle Bin empty state", () => Check(RecycleBinState.From(true, 0) == RecycleBinFillState.Empty));
    Test("Recycle Bin full state", () => Check(RecycleBinState.From(true, 1) == RecycleBinFillState.Full));
    Test("Recycle Bin ignores invalid negative count", () => Check(RecycleBinState.From(true, -1) == RecycleBinFillState.Empty));
    Test("iTunes Search parser keeps App Store software metadata", () =>
    {
        var json = Encoding.UTF8.GetBytes("""
            {"resultCount":1,"results":[{"wrapperType":"software","kind":"mac-software","trackId":462054704,"trackName":"Microsoft Word","artistName":"Microsoft Corporation","sellerName":"Microsoft Corporation","bundleId":"com.microsoft.Word","primaryGenreName":"Productivity","artworkUrl100":"https://is1-ssl.mzstatic.com/icon/100x100bb.png","artworkUrl512":"https://is1-ssl.mzstatic.com/icon/512x512bb.png","trackViewUrl":"https://apps.apple.com/de/app/microsoft-word/id462054704?mt=12"}]}
            """);
        var hits = ItunesSearchApi.ParseSearchResponse(json);
        Check(hits.Count == 1 && hits[0].TrackId == 462054704 && hits[0].TrackName == "Microsoft Word" &&
              hits[0].DeveloperName == "Microsoft Corporation" && hits[0].BundleId == "com.microsoft.Word" &&
              hits[0].ArtworkUrl512!.EndsWith("512x512bb.png", StringComparison.Ordinal));
    });
    Test("iTunes Search parser accepts software kind and prefers seller", () =>
    {
        var json = Encoding.UTF8.GetBytes("""
            {"resultCount":1,"results":[{"wrapperType":"software","kind":"software","trackId":1,"trackName":"Example","artistName":"Artist","sellerName":"Seller LLC","artworkUrl100":"https://is1-ssl.mzstatic.com/icon.jpg","trackViewUrl":"https://apps.apple.com/us/app/example/id1"}]}
            """);
        var hit = ItunesSearchApi.ParseSearchResponse(json).Single();
        Check(hit.DeveloperName == "Seller LLC");
    });
    Test("iTunes Search parser rejects an array root", () =>
    {
        try
        {
            ItunesSearchApi.ParseSearchResponse(Encoding.UTF8.GetBytes("[]"));
            throw new InvalidOperationException("Expected parser failure.");
        }
        catch (InvalidDataException) { }
    });
    Test("iTunes Search parser rejects inconsistent result counts", () =>
    {
        try
        {
            ItunesSearchApi.ParseSearchResponse(Encoding.UTF8.GetBytes("{\"resultCount\":2,\"results\":[]}"));
            throw new InvalidOperationException("Expected parser failure.");
        }
        catch (InvalidDataException) { }
    });
    Test("iTunes Search request is bounded HTTPS Mac software without a key", () =>
    {
        var uri = ItunesSearchApi.CreateSoftwareSearchUri("Visual Studio Code", "de");
        Check(uri.Scheme == Uri.UriSchemeHttps && uri.Host == "itunes.apple.com" &&
              uri.Query.Contains("entity=macSoftware", StringComparison.Ordinal) &&
              uri.Query.Contains("limit=25", StringComparison.Ordinal) &&
              uri.Query.Contains("country=DE", StringComparison.Ordinal) &&
              !uri.Query.Contains("key", StringComparison.OrdinalIgnoreCase));
    });
    Test("iTunes Search country falls back safely", () =>
        Check(ItunesSearchApi.NormalizeCountryCode("de") == "DE" &&
              ItunesSearchApi.NormalizeCountryCode("../") == "US"));
    Test("Default Explorer maps to Apple Finder", () =>
    {
        var plan = MacOsIconSearchCatalog.CreatePlan(new Pin("files", "Dateien", "explorer.exe", "explorer"));
        Check(plan is { ExactName: "Finder", RequireAppleDeveloper: true });
    });
    Test("Pin id has mapping priority", () =>
    {
        var plan = MacOsIconSearchCatalog.CreatePlan(new Pin("system", "Chrome", "chrome.exe", "chrome"));
        Check(plan is { ExactName: "System Settings", RequireAppleDeveloper: true });
    });
    Test("Windows media player maps to QuickTime Player", () =>
    {
        var plan = MacOsIconSearchCatalog.CreatePlan(new Pin("media", "Media Player", "wmplayer.exe", "wmplayer"));
        Check(plan is { ExactName: "QuickTime Player", RequireAppleDeveloper: true });
    });
    Test("Curated Windows aliases map to exact Apple app names", () =>
    {
        var cases = new (Pin Pin, string Name)[]
        {
            (new("uwp-calendar", "Anwendung", "shell:AppsFolder\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar"), "Calendar"),
            (new("uwp-clock", "Anwendung", "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"), "Clock"),
            (new("terminal", "Terminal", "wt.exe", "OpenConsole"), "Terminal"),
            (new("tasks", "Task-Manager", "taskmgr.exe", "taskmgr"), "Activity Monitor"),
            (new("events", "Ereignisanzeige", "eventvwr.msc", "eventvwr"), "Console"),
            (new("disks", "Datenträgerverwaltung", "diskmgmt.msc", "diskmgmt"), "Disk Utility"),
            (new("voice", "Sprachrekorder", "shell:AppsFolder\\Microsoft.WindowsSoundRecorder", "SoundRecorder"), "Voice Memos"),
            (new("text", "WordPad", "wordpad.exe", "wordpad"), "TextEdit")
        };
        foreach (var item in cases)
            Check(MacOsIconSearchCatalog.CreatePlan(item.Pin) is
                { RequireAppleDeveloper: true } plan && plan.ExactName == item.Name);
    });
    Test("Third-party app remains an exact non-Apple match", () =>
    {
        var plan = MacOsIconSearchCatalog.CreatePlan(new Pin("steam", "Steam", "steam.exe", "steam"));
        Check(plan is { ExactName: "Steam", RequireAppleDeveloper: false });
    });
    Test("Third-party alias falls back to exact visible name", () =>
    {
        var plans = MacOsIconSearchCatalog.CreatePlans(
            new Pin("chat", "Teams", "ms-teams.exe", "msteams"));
        Check(plans.Count >= 2 && plans[0].ExactName == "Microsoft Teams" &&
              plans.Any(plan => plan.ExactName == "Teams") &&
              plans.All(plan => !plan.RequireAppleDeveloper));
    });
    var store = new SettingsStore(temp);
    Test("Missing settings return defaults", () => Check(store.Load().IconSize == Settings.DefaultIconSize));
    Test("JSON round trip", () =>
    {
        var icon = new IconReference(ItunesSearchApi.ProviderId, new string('d', 64),
            ItunesSearchApi.CatalogVersion, new string('b', 64));
        store.Save(new Settings { Appearance = Appearance.Dark, Language = UiLanguage.English, Startup = StartupMode.UserLogin, Updates = UpdateMode.AutomaticDownload, IconSize = 61, AutoHide = true, HideTaskbar = true, HideDesktopIcons = true, ShowVeliShellDockItem = false, WindowsNotificationsEnabled = true,
            IconStyle = DockIconStyle.Windows, MenuBarEnabled = true, MenuBarAutoHide = true, MenuBarAlwaysOnTop = false,
            OnlineIcons = OnlineIconMode.OnDemand, OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion,
            DockIconOverrides = new Dictionary<string, IconReference> { ["trash-full"] = new("velishell-custom", new string('e', 64), "1", new string('e', 64)) },
            Pins = [new("steam", "Steam", "steam.exe", "steam", icon)] });
        var s = store.Load();
        Check(s.Appearance == Appearance.Dark && s.Language == UiLanguage.English && s.Startup == StartupMode.UserLogin && s.Updates == UpdateMode.AutomaticDownload && s.IconSize == 61 && s.AutoHide && s.HideTaskbar && s.HideDesktopIcons && !s.ShowVeliShellDockItem && s.WindowsNotificationsEnabled
            && s.IconStyle == DockIconStyle.Windows && s.MenuBarEnabled && s.MenuBarAutoHide && !s.MenuBarAlwaysOnTop
            && s.OnlineIcons == OnlineIconMode.OnDemand && s.Pins[0].Icon?.IconId == new string('d', 64)
            && s.GetDockIconOverride("trash-full")?.IconId == new string('e', 64));
    });
    Test("Backup created on second save", () => { store.Save(new Settings { IconSize = 43 }); Check(File.Exists(store.FilePath + ".bak")); });
    Test("Recover corrupt JSON from backup", () => { File.WriteAllText(store.FilePath, "{broken"); var s = store.Load(); Check(s.IconSize == 61 && store.LoadWarning is not null); });
    Test("Recover corrupt JSON without backup", () => { File.Delete(store.FilePath + ".bak"); var s = store.Load(); Check(s.IconSize == Settings.DefaultIconSize); });
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
Console.WriteLine($"\n{count - failures}/{count} passed.");
return failures == 0 ? 0 : 1;
