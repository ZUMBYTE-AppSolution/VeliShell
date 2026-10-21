using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using Ellipse = System.Windows.Shapes.Ellipse;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

internal static partial class Program
{
    // Explicitly headless: no Window.Show, real app startup, input, process
    // termination, registry writes, or changes to the user's VeliShell settings.
    private static int VerifyAppDiscovery()
    {
        var checks = 0;
        void Check(bool pass, string message) { Require(pass, message); checks++; }
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeliShell", "settings.json");
        var settingsBefore = File.Exists(settingsPath) ? SHA256.HashData(File.ReadAllBytes(settingsPath)) : [];
        try
        {
            var folderId = new Guid("6D809377-6AF0-444B-8957-A3773F02200E");
            var root = @"C:\Program Files";
            Check(AppIdentityService.NormalizeExecutable("{" + folderId + @"}\StreamDeck\StreamDeck.exe", id => id == folderId ? root : null)
                  == root + @"\StreamDeck\StreamDeck.exe", "A tray Known Folder GUID was discarded.");
            Check(AppIdentityService.NormalizeExecutable("{00000000-0000-0000-0000-000000000000}\\App.exe", _ => null) is null,
                "An unknown Known Folder identifier became a guessed executable.");
            Check(AppIdentityService.NormalizeExecutable(@"\\?\C:\Apps\Tool.exe") == @"C:\Apps\Tool.exe",
                "Extended-length and normal executable paths differ.");
            Check(AppIdentityService.NormalizeExecutable("\"C:\\Apps\\Tool.exe\"") == @"C:\Apps\Tool.exe",
                "Quoted executable path was not normalized.");
            Check(AppIdentityService.NormalizeExecutable("%LOCALAPPDATA%\\Tool\\Tool.exe") ==
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tool", "Tool.exe"),
                "Environment variables in a tray path were not expanded.");
            foreach (var invalid in new[] { "Tool.exe", "chatgpt://", "C:\\Apps\\Tool.exe --open", "C:\\Apps\\Bad\n.exe", "shell:AppsFolder\\Example!App" })
                Check(AppIdentityService.NormalizeExecutable(invalid)?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) != true,
                    "A non-executable target was accepted as an executable identity.");

            const string codexId = "OpenAI.Codex_2p2nqsd0c76g0!App";
            const string chatId = "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT";
            const string codexOld = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            const string codexNew = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.2.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            const string chatExe = @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            const string chatClassic = @"C:\Users\Test\AppData\Local\ChatGPT\app-1.2.3\ChatGPT.exe";
            const string codexClassic = @"C:\Users\Test\AppData\Local\Codex\app-1.2.3\ChatGPT.exe";
            Check(AppIdentityService.PackageFamilyFromPath(codexOld) == "OpenAI.Codex_2p2nqsd0c76g0",
                "A Windows Store family was parsed incorrectly.");
            Check(AppIdentityService.SameExecutable(codexOld, codexNew), "A package update broke the stable executable identity.");
            Check(!AppIdentityService.SameExecutable(codexOld, chatExe), "ChatGPT and Codex were merged by shared EXE name.");
            Check(!AppIdentityService.SameExecutable(codexOld, codexNew.Replace("2p2nqsd0c76g0", "otherpublisher")),
                "Different package publishers were merged.");
            Check(!AppIdentityService.SameExecutable(codexOld, codexNew.Replace("app\\ChatGPT.exe", "helper\\ChatGPT.exe")),
                "Different executables in one package were merged.");
            Check(AppIdentityService.SameExecutable(chatClassic, chatClassic.Replace("app-1.2.3", "app-3.4.5-beta.2")),
                "A versioned classic-app update lost its activity match.");
            Check(!AppIdentityService.SameExecutable(chatClassic, codexClassic), "Distinct classic installation roots were merged.");

            var windows = new[] {
                new NativeWindow((nint)1, "Codex", codexNew, "ChatGPT", codexId, ProcessId: 101),
                new NativeWindow((nint)2, "ChatGPT", chatExe, "ChatGPT", chatId, ProcessId: 102),
                new NativeWindow((nint)3, "ChatGPT classic", chatClassic, "ChatGPT", ProcessId: 103),
                new NativeWindow((nint)4, "Codex classic", codexClassic, "ChatGPT", "com.openai.codex", ProcessId: 104)
            };
            var pins = new[] {
                new Pin("codex", "Codex", PackagedAppService.TargetForAppId(codexId), "ChatGPT"),
                new Pin("chat", "ChatGPT", PackagedAppService.TargetForAppId(chatId), "ChatGPT"),
                new Pin("classic-chat", "ChatGPT classic", chatClassic, "ChatGPT"),
                new Pin("classic-codex", "Codex classic", codexClassic, "ChatGPT")
            };
            for (var pinIndex = 0; pinIndex < pins.Length; pinIndex++)
                for (var windowIndex = 0; windowIndex < windows.Length; windowIndex++)
                    Check(WindowCatalog.Matches(windows[windowIndex], pins[pinIndex]) == (pinIndex == windowIndex),
                        $"Shared ChatGPT.exe identity crossed app boundaries ({pinIndex}, {windowIndex}).");
            Check(WindowCatalog.Matches(windows[0], new Pin("old", "Codex", codexOld, "ChatGPT")),
                "An existing executable pin failed after a Store update.");
            Check(!WindowCatalog.Matches(windows[1], new Pin("old", "Codex", codexOld, "ChatGPT")),
                "An old Store executable pin matched a different product.");
            Check(WindowCatalog.Matches(windows[0] with { Executable = @"D:\ExternalStoreApp\ChatGPT.exe" }, pins[0]),
                "A Store app in an external/sparse package location was not matched by AUMID.");
            Check(WindowCatalog.Matches(windows[3], new Pin("explicit", "Codex", "shell:AppsFolder\\com.openai.codex")),
                "An unpackaged AppsFolder AUMID was rejected.");
            Check(WindowCatalog.Matches(windows[0] with { WindowAppUserModelId = "com.openai.codex" },
                new Pin("alias", "Codex", "shell:AppsFolder\\com.openai.codex")), "The explicit window AUMID was ignored.");
            Check(WindowCatalog.GroupKey(windows[0]) != WindowCatalog.GroupKey(windows[1]),
                "Running-app grouping combined distinct Store applications.");
            Check(WindowCatalog.GroupKey(windows[0] with { AppUserModelId = "app.first", IconTarget = null }) !=
                  WindowCatalog.GroupKey(windows[0] with { AppUserModelId = "app.second", IconTarget = null }),
                "Explicit AUMIDs were ignored when no shell icon target was available.");
            Check(PackagedAppService.KnownDisplayName(codexId) == "Codex" &&
                  PackagedAppService.KnownDisplayName(chatId) == "ChatGPT", "OpenAI product names still derive from the shared EXE.");
            Check(!WindowCatalog.Matches(windows[0], pins[0] with { Kind = PinKind.VirtualFolder }),
                "Virtual folders acquired a running-app indicator.");
            foreach (var invalid in new[] { "shell:AppsFolder\\..\\App.exe", "shell:AppsFolder\\bad:id", "shell:AppsFolder\\bad\nApp" })
                Check(PackagedAppService.AppIdFromTarget(invalid) is null, "An invalid AppsFolder identity was accepted.");
            Check(PackagedAppService.AppIdFromTarget("::{4234d49b-0245-4df3-b780-3893943456e1}\\" + codexId) == codexId,
                "An AppsFolder PIDL parsing name was not recognized.");

            // Real COM shell links, written only to this test's own fixture directory.
            var fixture = Path.Combine(AppContext.BaseDirectory, "app-discovery-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var link = Path.Combine(fixture, "Codex.lnk");
            WriteDiscoveryLink(link, codexOld, null, null);
            Check(LaunchService.PinFromPath(link)?.Target == link, "The selected .lnk was rewritten into an EXE or another package.");
            Check(LaunchService.PinFromPath(link)?.Name == "Codex", "A new Codex shortcut inherited the shared ChatGPT.exe label.");
            Check(WindowCatalog.Matches(windows[0], new Pin("lnk", "Codex", link)) &&
                  !WindowCatalog.Matches(windows[1], new Pin("lnk", "Codex", link)), "A .lnk did not retain its exact app identity.");
            var shellOnlyLink = Path.Combine(fixture, "Store app.lnk");
            WriteDiscoveryLink(shellOnlyLink, null, null, codexId);
            Check(ShellLinkService.Read(shellOnlyLink)?.AppUserModelId == codexId,
                "A non-filesystem shell link without GetPath was discarded.");
            Check(WindowCatalog.Matches(windows[0], new Pin("shell-link", "Codex", shellOnlyLink)),
                "A non-filesystem Store shortcut has no activity match.");
            Check(LaunchService.PinFromPath(shellOnlyLink)?.Target == shellOnlyLink, "A Store .lnk lost its launch reference.");
            nint appPidl = 0;
            try
            {
                var lookup = System.Threading.Tasks.Task.Run(() =>
                {
                    var initialized = NativeMethods.CoInitializeEx(0, 0);
                    try
                    {
                        var code = NativeMethods.SHParseDisplayName(PackagedAppService.TargetForAppId(codexId),
                            0, out var pidl, 0, out _);
                        return (code, pidl);
                    }
                    finally { if (initialized >= 0) NativeMethods.CoUninitialize(); }
                }).GetAwaiter().GetResult();
                appPidl = lookup.pidl;
                if (lookup.code == 0 && appPidl != 0)
                {
                    var pidlLink = Path.Combine(fixture, "Store PIDL.lnk");
                    WriteDiscoveryLink(pidlLink, null, null, null, appPidl);
                    Check(AppIdentityService.ForTarget(pidlLink).AppId == codexId,
                        "An actual installed AppsFolder PIDL link lost its package identity.");
                    Check(WindowCatalog.Matches(windows[0], new Pin("pidl", "Codex", pidlLink)) &&
                          !WindowCatalog.Matches(windows[1], new Pin("pidl", "Codex", pidlLink)),
                        "A PIDL-only shell link was not matched to its exact Store app.");
                    Console.WriteLine("PASS: installed Store-app PIDL shortcut read without launching the app.");
                }
                else Console.WriteLine("INFO: installed Codex PIDL fixture unavailable; synthetic shell-link tests remain covered.");
            }
            finally { if (appPidl != 0) Marshal.FreeCoTaskMem(appPidl); }
            var squirrel = Path.Combine(fixture, "ChatGPT classic.lnk");
            WriteDiscoveryLink(squirrel, @"C:\Users\Test\AppData\Local\ChatGPT\Update.exe", "--processStart \"ChatGPT.exe\"", null);
            Check(WindowCatalog.Matches(windows[2], new Pin("squirrel", "ChatGPT", squirrel, "Update")),
                "A Squirrel shortcut matched Update.exe instead of the real app window.");
            Check(!WindowCatalog.Matches(windows[3], new Pin("squirrel", "ChatGPT", squirrel, "ChatGPT")),
                "A Squirrel shortcut crossed into another install root.");
            Check(AppIdentityService.ExecutableForShortcut(@"C:\Apps\Update.exe", "--processStart ..\\Other\\ChatGPT.exe") ==
                  @"C:\Apps\Update.exe", "Squirrel arguments escaped their installation root.");
            var missingLink = new Pin("missing", "ChatGPT", Path.Combine(fixture, "Missing.lnk"), "ChatGPT");
            Check(!WindowCatalog.Matches(windows[0], missingLink), "A broken .lnk guessed a shared process name.");
            var browser = windows[0] with { Executable = @"C:\Browser\browser.exe", ProcessName = "browser",
                AppUserModelId = "Browser.WebMail", WebApp = new WebAppShortcut("Mail", "Mail.lnk", "Browser.WebMail") };
            Check(WindowCatalog.Matches(browser, new Pin("mail", "Mail", "shell:AppsFolder\\Browser.WebMail")),
                "An AppsFolder pin for a web app was rejected.");
            Check(!WindowCatalog.Matches(browser, new Pin("browser", "Browser", @"C:\Browser\browser.exe", "browser")),
                "A standalone web app was marked as a regular browser window.");
            var uriPin = new Pin("protocol", "ChatGPT", "chatgpt://", "ChatGPT");
            Check(AppIdentityService.Matches(windows[1], uriPin, new(chatId, chatExe)) &&
                  !AppIdentityService.Matches(windows[0], uriPin, new(chatId, chatExe)), "A URI association merged ChatGPT and Codex.");
            Check(!AppIdentityService.Matches(windows[0], uriPin, new()), "An unresolved protocol guessed an executable from its scheme.");
            Check(!WindowCatalog.Matches(windows[0], new Pin("ambiguous", "ChatGPT", "VeliShell.Unregistered.ChatGPT.exe", "ChatGPT")),
                "An unregistered relative EXE target guessed the Codex process by basename.");
            Check(!AppIdentityService.Matches(windows[0], uriPin, new(chatId, codexNew)),
                "A conflicting AppUserModelID degraded to executable matching.");
            Check(AppIdentityService.Matches(windows[1] with { AppUserModelId = null }, uriPin, new(chatId, chatExe)),
                "An identity-less window could not fall back to the exact associated executable.");
            var explorer = AppIdentityService.ForTarget("explorer.exe");
            Check(explorer.Executable is not null && WindowCatalog.Matches(new NativeWindow((nint)9, "Files", explorer.Executable,
                "explorer"), Settings.Defaults().Single(pin => pin.Id == "files")), "The default Explorer pin lost its activity match.");
            var notepadId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
            var modernNotepad = new NativeWindow((nint)10, "Editor", @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1.2.0.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
                "Notepad", notepadId);
            Check(WindowCatalog.Matches(modernNotepad, new Pin("system-notepad", "Editor",
                Path.Combine(Environment.SystemDirectory, "notepad.exe"), "notepad")), "The Windows Notepad forwarder did not match Store Notepad.");
            Check(!WindowCatalog.Matches(modernNotepad, new Pin("other-notepad", "Other Editor", @"C:\OtherVendor\notepad.exe", "notepad")),
                "An unrelated notepad.exe inherited the Windows Store forwarder identity.");
            Check(AppIdentityService.ForTarget("notepad.exe").Executable is not null,
                "The default relative Notepad pin could not be resolved without process-name guessing.");
            Check(!WindowCatalog.IsAppWindowCandidate(true, (nint)1, 0) &&
                  WindowCatalog.IsAppWindowCandidate(true, (nint)1, 0x40000) &&
                  !WindowCatalog.IsAppWindowCandidate(true, 0, NativeMethods.WsExToolWindow) &&
                  !WindowCatalog.IsAppWindowCandidate(false, 0, 0), "Taskbar-eligible owned windows were filtered incorrectly.");

            var png = new byte[40];
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(png, 0);
            var tray = TrayIconCatalog.SelectIcons([
                new("{" + folderId + @"}\StreamDeck\StreamDeck.exe", png, "Stream Deck", true),
                new(codexOld, png, "ChatGPT", false), new(codexNew, png, "ChatGPT", true),
                new(chatExe, png, "ChatGPT", true), new(@"C:\Dead\Old.exe", png, "Dead", true),
                new(@"C:\Windows\explorer.exe", png, "Explorer", true),
                new(@"C:\Apps\Broken.exe", [0, 1, 2], "Broken", true)
            ], [root + @"\StreamDeck\StreamDeck.exe", codexNew, chatExe, @"C:\Windows\explorer.exe", @"C:\Apps\Broken.exe"],
                id => id == folderId ? root : null);
            Check(tray.Count == 3, "Tray expansion, same-app deduplication or filtering failed.");
            Check(tray.Single(icon => icon.Name == "Codex").Executable == codexNew, "A stale tray entry did not bind to the live package version.");
            Check(tray.Any(icon => icon.Name == "ChatGPT" && icon.Executable == chatExe), "ChatGPT was deduplicated against Codex.");
            var storeTray = tray.Single(icon => icon.Name == "Codex") with { LaunchTarget = PackagedAppService.TargetForAppId(codexId) };
            Check(storeTray.CanOpen && storeTray.LaunchTarget != storeTray.Executable,
                "A Store tray icon requires direct execution of its versioned EXE.");
            var oldOnly = TrayIconCatalog.SelectIcons([new(codexOld, png, "ChatGPT", false)], [codexNew]);
            Check(oldOnly.Single().Id == tray.Single(icon => icon.Name == "Codex").Id, "Tray visibility identity changes with package version.");
            var legacyId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(codexOld.ToUpperInvariant()))).ToLowerInvariant();
            Check(tray.Single(icon => icon.Name == "Codex").MatchesId(legacyId), "Existing hidden/preferred tray IDs were lost.");
            var reverse = TrayIconCatalog.SelectIcons([new(codexNew, png, "ChatGPT", true), new(codexOld, png, "ChatGPT", false)], [codexNew]);
            Check(reverse.Single().MatchesId(legacyId) && reverse.Single().Promoted,
                "A non-promoted duplicate lost its legacy visibility ID or replaced the promoted icon.");
            Check(TrayIconCatalog.SelectIcons([new(codexOld, png, "ChatGPT", false)], [chatExe]).Count == 0,
                "A stale Codex tray icon matched a running ChatGPT product.");
            var behindStale = Enumerable.Range(0, 300).Select(index => new TrayIconCacheEntry(@"C:\Stale\" + index + ".exe", png, "stale", false))
                .Append(new(root + @"\StreamDeck\StreamDeck.exe", png, "Stream Deck", false));
            Check(TrayIconCatalog.SelectIcons(behindStale, [root + @"\StreamDeck\StreamDeck.exe"]).Count == 1,
                "The old 256-entry registry cutoff still discards active apps.");
            var manyRunning = Enumerable.Range(0, 160).Select(index => @"C:\Apps\Tool" + index + ".exe").ToArray();
            Check(TrayIconCatalog.SelectIcons(manyRunning.Select(path => new TrayIconCacheEntry(path, png, "Tool", false)), manyRunning).Count ==
                  TrayIconCatalog.MaximumIcons, "The tray mirror is unbounded or retains its old 48-app limit.");
            var bigPng = new byte[256 * 1024];
            png[..8].CopyTo(bigPng, 0);
            Check(TrayIconCatalog.SelectIcons(manyRunning.Select(path => new TrayIconCacheEntry(path, bigPng, "Tool", false)), manyRunning).Count == 64,
                "Tray snapshots exceed their 16 MiB aggregate budget.");

            // The real rendering control uses this exact matched-window list.
            var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(app, new Settings { Pins = [] });
            // Own, hidden HWND only: prove that generic (non-browser) explicit
            // window IDs survive the same MTA worker lookup used by the dock.
            using (var hidden = new HwndSource(new HwndSourceParameters("VeliShell identity QA - hidden")
                { Width = 1, Height = 1, PositionX = -30000, PositionY = -30000, WindowStyle = 0 }))
            {
                var iid = typeof(NativeMethods.IPropertyStore).GUID;
                NativeMethods.IPropertyStore? store = null;
                try
                {
                    Check(NativeMethods.SHGetPropertyStoreForWindow(hidden.Handle, ref iid, out store) == 0 && store is not null,
                        "Cannot read the own hidden test HWND property store.");
                    var property = NativeMethods.AppUserModelIdKey;
                    var value = new NativeMethods.PropVariant { VariantType = 31, PointerValue = Marshal.StringToCoTaskMemUni("com.openai.codex") };
                    try { store!.SetValue(ref property, ref value); }
                    finally { NativeMethods.PropVariantClear(ref value); }
                    Check(System.Threading.Tasks.Task.Run(() => WindowCatalog.ReadWindowAppId(hidden.Handle)).GetAwaiter().GetResult() == "com.openai.codex",
                        "The worker cannot read a generic explicit window AUMID.");
                    Check(!NativeMethods.IsWindowVisible(hidden.Handle), "The identity test HWND was made visible.");
                }
                finally { if (store is not null) Marshal.ReleaseComObject(store); }
            }
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{theme}.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
                for (var index = 0; index < pins.Length; index++)
                {
                    var pin = pins[index];
                    var item = new DockItem { Key = "pin:" + pin.Id, Name = pin.Name, Target = pin.Target, Pin = pin,
                        Windows = windows.Where(window => WindowCatalog.Matches(window, pin)).ToList() };
                    var tile = new DockTile(item, 32);
                    var indicator = (Ellipse)RequireField(typeof(DockTile), "_indicator").GetValue(tile)!;
                    Check(indicator.Visibility == Visibility.Visible && item.Windows.Count == 1,
                        $"The {theme} dock activity dot did not use the app-specific match.");
                    tile.UpdateItem(new DockItem { Key = item.Key, Name = item.Name, Target = item.Target, Pin = pin });
                    Check(indicator.Visibility == Visibility.Hidden, "The activity dot remained after the last matching window closed.");
                }
            }

            // Read-only integration against already-running apps; never launches one.
            var live = System.Threading.Tasks.Task.Run(WindowCatalog.Read).GetAwaiter().GetResult();
            foreach (var window in live.Where(window => window.AppUserModelId is not null))
            {
                var pin = new Pin("live", "live", PackagedAppService.TargetForAppId(window.AppUserModelId!));
                Check(WindowCatalog.Matches(window, pin), "A live window failed its own AUMID match.");
            }
            var liveTray = System.Threading.Tasks.Task.Run(TrayIconCatalog.Read).GetAwaiter().GetResult();
            Check(liveTray.Select(icon => icon.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == liveTray.Count,
                "Live tray discovery returned duplicate app identities.");
            Check((File.Exists(settingsPath) ? SHA256.HashData(File.ReadAllBytes(settingsPath)) : []).SequenceEqual(settingsBefore),
                "Headless discovery changed the user's settings.");
            if (StableLaunchTargetService.IsRegisteredProtocol("ms-settings:"))
            {
                var protocol = AppIdentityService.ForTarget("ms-settings:");
                Check(protocol.AppId is not null || protocol.Executable is not null,
                    "The registered Windows Settings protocol has no read-only association identity.");
            }
            Console.WriteLine($"PASS: {checks} app-discovery checks; {live.Count} existing windows; {liveTray.Count} live tray apps.");
            Console.WriteLine("No windows shown, apps launched/terminated, registry entries or user settings changed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void WriteDiscoveryLink(string path, string? executable, string? arguments, string? appId, nint itemIdList = 0)
    {
        object instance = new ShellLinkComObject();
        try
        {
            var link = (IShellLinkW)instance;
            if (itemIdList != 0) link.SetIDList(itemIdList);
            if (executable is not null) link.SetPath(executable);
            if (arguments is not null) link.SetArguments(arguments);
            if (appId is not null)
            {
                var key = NativeMethods.AppUserModelIdKey;
                var value = new NativeMethods.PropVariant { VariantType = 31, PointerValue = Marshal.StringToCoTaskMemUni(appId) };
                try { ((NativeMethods.IPropertyStore)instance).SetValue(ref key, ref value); }
                finally { NativeMethods.PropVariantClear(ref value); }
            }
            ((IPersistFile)instance).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
}
