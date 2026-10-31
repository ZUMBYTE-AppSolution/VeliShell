using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

internal static partial class Program
{
    private sealed class TrayMenuFake : INativeTrayMenuBackend
    {
        internal int Reads, Shows;
        internal bool Available = true;
        internal IReadOnlyList<NativeTrayElement> Targets = [];
        internal NativeTrayElement? Last;
        public IReadOnlyList<NativeTrayElement> Read() { Reads++; return Targets; }
        public bool Show(NativeTrayElement target) { Shows++; Last = target; return Available; }
    }

    private static int VerifyShortcutTray()
    {
        var checks = 0;
        void Check(bool pass, string message) { Require(pass, message); checks++; }
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeliShell", "settings.json");
        var before = File.Exists(profile) ? SHA256.HashData(File.ReadAllBytes(profile)) : [];
        var output = Path.Combine(AppContext.BaseDirectory, "shortcut-tray-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string Exe(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0x4d, 0x5a, 0, 0]); return path; }
        try
        {
            var root = Path.Combine(output, "Discord");
            var launcher = Exe(Path.Combine(root, "Discord.exe"));
            var updater = Exe(Path.Combine(root, "Update.exe"));
            var old = Exe(Path.Combine(root, "app-1.0.9", "Discord.exe"));
            var current = Exe(Path.Combine(root, "app-1.0.10", "Discord.exe"));
            var other = Exe(Path.Combine(output, "Another App", "app-1.0.10", "Discord.exe"));
            var rootLink = Path.Combine(output, "Discord root.lnk");
            var argLink = Path.Combine(output, "Discord args.lnk");
            var updateLink = Path.Combine(output, "Discord update.lnk");
            var staleLink = Path.Combine(output, "Discord old.lnk");
            WriteDiscoveryLink(rootLink, launcher, null, "com.squirrel.Discord.Discord");
            WriteDiscoveryLink(argLink, launcher, "--processStart Discord.exe", "com.squirrel.Discord.Discord");
            WriteDiscoveryLink(updateLink, updater, "--processStart \"Discord.exe\"", "com.squirrel.Discord.Discord");
            WriteDiscoveryLink(staleLink, old, "--profile \"user data\"", "old.squirrel.Discord");
            object working = new ShellLinkComObject();
            try
            {
                ((IPersistFile)working).Load(staleLink, 0);
                ((IShellLinkW)working).SetWorkingDirectory(Path.GetDirectoryName(old)!);
                ((IPersistFile)working).Save(staleLink, true);
            }
            finally { Marshal.FinalReleaseComObject(working); }
            var originalHash = SHA256.HashData(File.ReadAllBytes(staleLink));
            var links = new[] { rootLink, argLink, updateLink, staleLink };
            foreach (var appId in new string?[] { null, "com.squirrel.Discord.Discord", "updated.Discord.Window" })
            {
                var window = new NativeWindow((nint)1, "Discord", current, "Discord", appId);
                foreach (var link in links)
                {
                    var pin = new Pin("discord", "Discord", link, "Update");
                    Check(WindowCatalog.Matches(window, pin), "A launcher shortcut did not match its versioned window.");
                    Check(!WindowCatalog.Matches(window with { Executable = other }, pin), "A generic shortcut ID crossed installation roots.");
                    var matcher = WindowCatalog.MatcherFor(pin);
                    Check(new[] { window }.Count(matcher) == 1 && !new[] { window }.Any(w => !matcher(w)),
                        "A matched pin would still create a second running Dock entry.");
                }
            }
            Check(StableLaunchTargetService.PreferShortcutFrom(old, "Discord", [rootLink]) == rootLink,
                "The stable root launcher .lnk was not preferred to a versioned EXE.");
            Check(StableLaunchTargetService.PreferShortcutFrom(old, "Discord", [updateLink]) == updateLink,
                "A Squirrel Update.exe shortcut was not preferred.");
            Check(StableLaunchTargetService.PrepareLaunch(old, [rootLink]).Target == rootLink,
                "An existing direct EXE pin was not upgraded to the stable launcher at click time.");
            Check(StableLaunchTargetService.PrepareLaunch(rootLink).Target == rootLink,
                "A healthy Windows shortcut lost its native launch semantics.");
            Check(StableLaunchTargetService.PrepareLaunch(old, []).Target == launcher,
                "A stable root EXE was not used when no shortcut existed.");
            File.Delete(old); // Only this test's own dummy fixture, never an installed application.
            Check(StableLaunchTargetService.PrepareLaunch(old, [rootLink]).Target == rootLink,
                "A removed app-version EXE could not recover its stable shortcut.");
            var repaired = StableLaunchTargetService.PrepareLaunch(staleLink);
            Check(repaired.Target == current && repaired.Arguments == "--profile \"user data\"",
                "An obsolete shortcut did not preserve the original application arguments during repair.");
            Check(repaired.WorkingDirectory == Path.GetDirectoryName(current), "An obsolete working directory was not updated.");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(staleLink))),
                "Shortcut recovery rewrote the original .lnk file.");
            File.Delete(launcher);
            Check(StableLaunchTargetService.PrepareLaunch(old, []).Target == current,
                "The numeric newest matching app version was not found within its own root.");
            Check(VersionedAppReference.FromExecutable(old)?.FindCurrentExecutable() == current,
                "Deleted old app paths lost their stable installation scope.");
            Check(VersionedAppReference.FromExecutable(old)?.Matches(other) == false,
                "A versioned reference followed a same-named EXE in another root.");
            Check(new VersionedAppReference(output, "../outside.exe").FindCurrentExecutable() is null,
                "An invalid reference escaped its installation root.");
            Check(new VersionedAppReference(Path.GetPathRoot(output)!, "Discord.exe").FindCurrentExecutable() is null,
                "Automatic recovery searched an entire drive.");
            foreach (var opaque in new[] { "chatgpt://", "https://example.com/help", "shell:AppsFolder\\Example_publisher!App", output })
                Check(StableLaunchTargetService.PrepareLaunch(opaque).Target == opaque,
                    "Repair altered an unrelated protocol, shell target or folder.");

            var discord = new NativeTrayElement(10, 100, "Discord", [1, 2, 3]);
            var fake = new TrayMenuFake { Targets = [discord, new(10, 100, "Steam", [4, 5, 6])] };
            var bridge = new NativeTrayMenuBridge(fake);
            bridge.Refresh(); bridge.Refresh();
            Check(fake.Reads == 1, "Multiple menu bars repeated the UIA scan instead of reusing a bounded snapshot.");
            var target = bridge.FindTarget("Discord", current);
            Check(target == discord, "The unique original tray target was not matched.");
            Check(!bridge.TryShow(target, userRequested: false) && fake.Shows == 0,
                "A native menu was opened during background discovery.");
            Check(bridge.TryShow(target, userRequested: true) && fake.Shows == 1 && fake.Last == discord,
                "The explicit original-menu action did not dispatch to the selected target.");
            Check(!bridge.TryShow(discord with { ExplorerStarted = 101 }, true) && fake.Shows == 1,
                "A stale Explorer identity was dispatched.");
            Check(!bridge.TryShow(discord with { RuntimeId = [9, 9] }, true), "An unknown native UI element was dispatched.");
            Check(!bridge.TryShow(discord with { Name = "Discord\0Other" }, true), "A truncated native caption could dispatch another target.");
            Check(NativeTrayMenuBridge.SelectTarget([discord, discord with { RuntimeId = [7, 8] }], "Discord", current) is null,
                "An ambiguous original icon caption opened an arbitrary app menu.");
            Check(NativeTrayMenuBridge.SelectTarget([discord, discord], "Discord", current) == discord,
                "Duplicate UIA references were mistaken for two distinct icons.");
            Check(NativeTrayMenuBridge.SelectTarget([discord with { Name = "ChatGPT" }], "Codex", "ChatGPT.exe") is null,
                "The shared executable basename confused Codex with the ChatGPT tray menu.");
            Check(NativeTrayMenuBridge.SelectTarget([discord], "Discord Client", current, "Discord") == discord,
                "The original tray tooltip could not identify an app with a different display name.");
            Check(NativeTrayMenuBridge.SelectTarget([discord with { Name = "Microphone in use by Discord" }], "Discord", current) is null,
                "A Windows status icon mentioning Discord was mistaken for Discord's own tray icon.");
            fake.Available = false;
            Check(!bridge.TryShow(target, true), "A native provider failure was reported as an opened app menu.");
            var callbackGuid = Guid.NewGuid();
            var registered = new TrayCallbackTarget(1, 2, 3, 4, 0x8001, 4, callbackGuid) { Executable = current };
            Check(TrayRelayController.SelectTarget([registered], current, callbackGuid) == registered,
                "A registered tray GUID was not matched to its exact executable.");
            Check(TrayRelayController.SelectTarget([registered], other, callbackGuid) is null,
                "Native tray callbacks crossed unrelated installation roots.");
            Check(TrayRelayController.SelectTarget([registered, registered with { Id = 5, Guid = Guid.NewGuid() }], current) is null,
                "Multiple live tray registrations were resolved by guessing.");
            Check(TrayRelayController.SelectTarget([registered], current, Guid.NewGuid()) is null,
                "A stale registry GUID selected another live icon.");
            Check(Marshal.SizeOf<NativeMethods.TrayCallback>() == 56, "Native callback ABI layout mismatch.");
            var consent = new Settings { TrayBridgeEnabled = true }; consent.Normalize();
            Check(!consent.TrayBridgeEnabled, "The Explorer bridge enabled without stored consent.");
            consent.TrayBridgeConsentVersion = 1; consent.TrayBridgeEnabled = true; consent.Normalize();
            Check(consent.TrayBridgeEnabled, "Explicit tray consent did not persist.");

            // ABI/validation smoke only. NEVER invokes a real app context menu.
            Check(NativeMethods.VeliShellTrayProtocol() == 1, "The packaged native UIA client cannot be loaded.");
            Check(NativeMethods.VeliShellShowTrayContextMenu(0, 0, [], 0, "") < 0,
                "The native client accepted an invalid target.");
            Check(NativeMethods.VeliShellShowTrayContextMenu((uint)Environment.ProcessId, 1, [1], 1, "QA") < 0,
                "The native client accepted a non-Explorer target.");

            var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
            using var themes = new ThemeService();
            typeof(VeliShell.Desktop.App).GetProperty("Themes")!.SetValue(app, themes);
            var settings = new Settings { Pins = [], ReducedMotion = true, MenuBarEnabled = true, ShowRunningApps = false };
            typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(app, settings);
            foreach (var language in new[] { UiLanguage.German, UiLanguage.English })
            {
                settings.Language = language; LocalizationService.Current.Apply(language);
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    settings.Appearance = theme == "Dark" ? Appearance.Dark : Appearance.Light;
                    themes.Apply(settings.Appearance);
                    var bar = new MenuBarWindow(app);
                    try
                    {
                        foreach (var hidden in new[] { false, true })
                        {
                            var icon = new TrayAppIcon("qa", "Discord", current, [], true);
                            var menu = bar.BuildTrayIconMenu(new Button(), icon, hidden);
                            Check(!menu.IsOpen && !bar.IsVisible, "Menu construction opened a real UI window.");
                            Check(menu.Items.Count == 3 && menu.Items[0] is MenuItem native &&
                                Equals(native.Header, LocalizationService.Current.Get("MenuBar.OriginalMenu")),
                                "The original-menu entry is missing from the top of the context menu.");
                            var section = menu.Items[2] as MenuItem;
                            Check(section is not null && Equals(section.Header, "VeliShell") && !section.IsSubmenuOpen,
                                "VeliShell actions are not grouped in a collapsed bottom submenu.");
                            Check(section!.Items.OfType<MenuItem>().Any(item => Equals(item.Header,
                                LocalizationService.Current.Get(hidden ? "MenuBar.TrayShow" : "MenuBar.TrayHide"))),
                                "Hidden icons no longer offer their context menu/show action.");
                            var pin = new Pin("discord", "Discord", argLink);
                            var item = new DockItem { Key = "pin:discord", Name = "Discord", Target = argLink, Pin = pin,
                                Windows = [new NativeWindow((nint)1, "Discord", current, "Discord")] };
                            var tile = new DockTile(item, 32);
                            var indicator = (System.Windows.Shapes.Ellipse)RequireField(typeof(DockTile), "_indicator").GetValue(tile)!;
                            Check(indicator.Visibility == Visibility.Visible, "The shortcut's activity dot is hidden.");
                        }
                    }
                    finally { bar.Close(); }
                    var view = new PreferencesWindow(app);
                    try
                    {
                        var toggle = (CheckBox)view.FindName("TrayBridgeSwitch");
                        Check(toggle.Content is string label && label.Length > 12 && !string.IsNullOrWhiteSpace(((TextBlock)view.FindName("TrayBridgeStatus")).Text),
                            "Tray bridge settings lost their translated label or status.");
                        var card = (Border)((StackPanel)toggle.Parent).Parent;
                        ((Panel)card.Parent).Children.Remove(card);
                        System.Windows.Documents.TextElement.SetForeground(card, view.Foreground);
                        var wrapper = new Grid { Width = 560, Background = (System.Windows.Media.Brush)app.FindResource("WindowSurface"), Children = { card } };
                        wrapper.Measure(new Size(560, double.PositiveInfinity));
                        wrapper.Arrange(new Rect(new Point(), wrapper.DesiredSize)); wrapper.UpdateLayout();
                        Check(toggle.ActualWidth >= 200 && toggle.ActualHeight >= 20, "Tray settings toggle is clipped.");
                        SaveIconBackgroundRender(wrapper, Path.Combine(output, $"tray-settings-{language}-{theme}.png"), 560, (int)Math.Ceiling(wrapper.ActualHeight));
                        Check(new System.Windows.Interop.WindowInteropHelper(view).Handle == 0, "Tray settings check opened a desktop window.");
                    }
                    finally { view.Close(); }
                }
            }
            Check((File.Exists(profile) ? SHA256.HashData(File.ReadAllBytes(profile)) : []).SequenceEqual(before),
                "The background tests changed the real VeliShell settings.");
            Console.WriteLine($"PASS: {checks} shortcut/update/native-tray checks. Real COM shortcut fixtures; no app launched, native app menu opened, taskbar changed or user settings written.");
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
