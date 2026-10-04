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
    Test("Online icons are opt-in", () => Check(new Settings().OnlineIcons == OnlineIconMode.Disabled));
    Test("Clamp large icon size", () => { var s = new Settings { IconSize = 500 }; s.Normalize(); Check(s.IconSize == Settings.MaximumIconSize); });
    Test("Clamp small icon size", () => { var s = new Settings { IconSize = -1 }; s.Normalize(); Check(s.IconSize == Settings.MinimumIconSize); });
    Test("Handle NaN", () => { var s = new Settings { IconSize = double.NaN }; s.Normalize(); Check(s.IconSize == Settings.DefaultIconSize); });
    Test("Handle Infinity", () => { var s = new Settings { IconSize = double.PositiveInfinity }; s.Normalize(); Check(s.IconSize == Settings.DefaultIconSize); });
    Test("Invalid enum falls back", () => { var s = new Settings { Appearance = (Appearance)999 }; s.Normalize(); Check(s.Appearance == Appearance.System); });
    Test("Invalid language falls back", () => { var s = new Settings { Language = (UiLanguage)999 }; s.Normalize(); Check(s.Language == UiLanguage.System); });
    Test("Invalid startup mode falls back", () => { var s = new Settings { Startup = (StartupMode)999 }; s.Normalize(); Check(s.Startup == StartupMode.Disabled); });
    Test("Invalid update mode falls back", () => { var s = new Settings { Updates = (UpdateMode)999 }; s.Normalize(); Check(s.Updates == UpdateMode.Notify); });
    Test("Deduplicate pin IDs", () => { var s = new Settings { Pins = [new("a", "A", "a.exe"), new("A", "B", "b.exe")] }; s.Normalize(); Check(s.Pins.Count == 1); });
    Test("Filter missing targets", () => { var s = new Settings { Pins = [new("x", "", "")] }; s.Normalize(); Check(s.Pins.Count == 0); });
    Test("Generate missing IDs", () => { var s = new Settings { Pins = [new("", "A", "a.exe")] }; s.Normalize(); Check(s.Pins[0].Id.Length > 0); });
    Test("Normalize empty labels", () => { var s = new Settings { Pins = [new("a", "  ", "a.exe")] }; s.Normalize(); Check(s.Pins[0].Name == "Anwendung"); });
    Test("Limit pins", () => { var s = new Settings { Pins = Enumerable.Range(0, 50).Select(i => new Pin(i.ToString(), "A", "a.exe")).ToList() }; s.Normalize(); Check(s.Pins.Count == Settings.MaximumPins); });
    Test("Empty pins stay empty", () => { var s = new Settings { Pins = [] }; s.Normalize(); Check(s.Pins.Count == 0); });
    Test("Valid online icon survives normalization", () =>
    {
        var icon = new IconReference("macosicongallery", new string('c', 64), "search-data-v1", new string('a', 64));
        var s = new Settings { OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion,
            Pins = [new("a", "Firefox", "firefox.exe", "firefox", icon)] };
        s.Normalize();
        Check(s.SchemaVersion == Settings.CurrentSchemaVersion && s.Pins[0].Icon?.IconId == new string('c', 64));
    });
    Test("Retired macosicons reference is discarded", () =>
    {
        var icon = new IconReference("macosicons", new string('c', 64), "api-v1", new string('a', 64));
        var s = new Settings { Pins = [new("a", "Firefox", "firefox.exe", "firefox", icon)] };
        s.Normalize();
        Check(s.Pins[0].Icon is null);
    });
    Test("Old online consent disables provider", () =>
    {
        var s = new Settings { OnlineIcons = OnlineIconMode.AutomaticExactMatches, OnlineIconConsentVersion = 1 };
        s.Normalize();
        Check(s.OnlineIcons == OnlineIconMode.Disabled && s.OnlineIconConsentVersion == 1);
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
    Test("Gallery parser accepts lower camel case array", () =>
    {
        var json = Encoding.UTF8.GetBytes("""
            [{"id":"finder-2026-09-23","name":"Finder","date":"2026-09-23","dateDisplay":"Sep 23, 2026","designer":null,"developer":"Apple","src":"https://cdn.jim-nielsen.com/thumb.png"}]
            """);
        var entries = MacOsIconGalleryCatalog.Parse(json);
        Check(entries.Count == 1 && entries[0].Id == "finder-2026-09-23" && entries[0].Developer == "Apple");
    });
    Test("Gallery parser rejects non-array root", () =>
    {
        try
        {
            MacOsIconGalleryCatalog.Parse(Encoding.UTF8.GetBytes("{\"id\":\"finder\"}"));
            throw new InvalidOperationException("Expected parser failure.");
        }
        catch (InvalidDataException) { }
    });
    Test("Default Explorer maps to Apple Finder", () =>
    {
        var plan = MacOsIconGalleryCatalog.CreatePlan(new Pin("files", "Dateien", "explorer.exe", "explorer"));
        Check(plan is { ExactName: "Finder", RequireAppleDeveloper: true });
    });
    Test("Pin id has mapping priority", () =>
    {
        var plan = MacOsIconGalleryCatalog.CreatePlan(new Pin("system", "Chrome", "chrome.exe", "chrome"));
        Check(plan is { ExactName: "System Settings", RequireAppleDeveloper: true });
    });
    Test("Windows media player maps to QuickTime Player", () =>
    {
        var plan = MacOsIconGalleryCatalog.CreatePlan(new Pin("media", "Media Player", "wmplayer.exe", "wmplayer"));
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
            Check(MacOsIconGalleryCatalog.CreatePlan(item.Pin) is
                { RequireAppleDeveloper: true } plan && plan.ExactName == item.Name);
    });
    Test("Third-party app remains an exact non-Apple match", () =>
    {
        var plan = MacOsIconGalleryCatalog.CreatePlan(new Pin("steam", "Steam", "steam.exe", "steam"));
        Check(plan is { ExactName: "Steam", RequireAppleDeveloper: false });
    });
    Test("Third-party alias falls back to exact visible name", () =>
    {
        var plans = MacOsIconGalleryCatalog.CreatePlans(
            new Pin("chat", "Teams", "ms-teams.exe", "msteams"));
        Check(plans.Count >= 2 && plans[0].ExactName == "Microsoft Teams" &&
              plans.Any(plan => plan.ExactName == "Teams") &&
              plans.All(plan => !plan.RequireAppleDeveloper));
    });
    Test("System mapping requires Apple developer", () =>
    {
        var entries = new[]
        {
            new MacOsIconGalleryEntry("finder-third-2027-01-01", "Finder", "2027-01-01", null, null, "Other", "x"),
            new MacOsIconGalleryEntry("finder-apple-2026-09-23", "Finder", "2026-09-23", null, null, "Apple", "x")
        };
        var plan = MacOsIconGalleryCatalog.CreatePlan(new Pin("files", "Dateien", "explorer.exe", "explorer"))!;
        Check(MacOsIconGalleryCatalog.FindLatestExact(entries, plan)?.Developer == "Apple");
    });
    var store = new SettingsStore(temp);
    Test("Missing settings return defaults", () => Check(store.Load().IconSize == Settings.DefaultIconSize));
    Test("JSON round trip", () =>
    {
        var icon = new IconReference("macosicongallery", new string('d', 64), "search-data-v1", new string('b', 64));
        store.Save(new Settings { Appearance = Appearance.Dark, Language = UiLanguage.English, Startup = StartupMode.UserLogin, Updates = UpdateMode.AutomaticDownload, IconSize = 61, AutoHide = true, HideTaskbar = true,
            OnlineIcons = OnlineIconMode.OnDemand, OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion,
            Pins = [new("steam", "Steam", "steam.exe", "steam", icon)] });
        var s = store.Load();
        Check(s.Appearance == Appearance.Dark && s.Language == UiLanguage.English && s.Startup == StartupMode.UserLogin && s.Updates == UpdateMode.AutomaticDownload && s.IconSize == 61 && s.AutoHide && s.HideTaskbar
            && s.OnlineIcons == OnlineIconMode.OnDemand && s.Pins[0].Icon?.IconId == new string('d', 64));
    });
    Test("Backup created on second save", () => { store.Save(new Settings { IconSize = 43 }); Check(File.Exists(store.FilePath + ".bak")); });
    Test("Recover corrupt JSON from backup", () => { File.WriteAllText(store.FilePath, "{broken"); var s = store.Load(); Check(s.IconSize == 61 && store.LoadWarning is not null); });
    Test("Recover corrupt JSON without backup", () => { File.Delete(store.FilePath + ".bak"); var s = store.Load(); Check(s.IconSize == Settings.DefaultIconSize); });
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
Console.WriteLine($"\n{count - failures}/{count} passed.");
return failures == 0 ? 0 : 1;
