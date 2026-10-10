using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Services;

internal static class Program
{
    private static readonly Assembly DesktopAssembly = typeof(VeliShell.Desktop.App).Assembly;

    private sealed class SafePreviewApp : VeliShell.Desktop.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--render-start-launcher", StringComparer.OrdinalIgnoreCase))
            return RenderStartLauncher();
        if (args.Contains("--render-folder", StringComparer.OrdinalIgnoreCase))
            return RenderFolderViews();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--live-workarea-reflow", StringComparer.OrdinalIgnoreCase))
            return RunLiveWorkAreaReflowProbe(application);
        if (args.Contains("--live-menubar-reservation", StringComparer.OrdinalIgnoreCase))
            return RunLiveMenuBarReservationProbe(application);
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml")
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml")
        });
        if (args.Contains("--render-shell-panels", StringComparer.OrdinalIgnoreCase))
            return RenderShellPanels(application);

        var anchor = new Border
        {
            Width = 80,
            Height = 80,
            Background = Brushes.SteelBlue,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(40)
        };
        var owner = new Window
        {
            Title = "VeliShell Interaction QA Owner",
            Width = 260,
            Height = 220,
            Left = 100,
            Top = 100,
            Content = new Grid { Children = { anchor } },
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false
        };
        var source = new Window
        {
            Title = "VeliShell Interaction QA Source",
            Width = 480,
            Height = 300,
            Left = 440,
            Top = 100,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Content = new Border
            {
                Background = new LinearGradientBrush(Colors.CornflowerBlue, Colors.MediumPurple, 35),
                Child = new TextBlock
                {
                    Text = "DWM LIVE PREVIEW",
                    Foreground = Brushes.White,
                    FontSize = 28,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        try
        {
            owner.Show();
            source.Show();
            owner.UpdateLayout();
            source.UpdateLayout();
            DrainDispatcher();

            TestThumbnail(owner, source, anchor);
            TestGhostAndMask(owner, anchor);
            TestDockPinDragOutPolicy();
            TestExplorerPinVerbContract();
            TestDockTileHoverMask();
            TestVeliShellAssetSurface();
            TestDockIconCustomizationContract();
            TestWebAppAndSteamIdentityContracts();
            TestPackagedAppFrameIdentity();
            TestBackgroundAppTracking();
            TestBackgroundAppCommands();
            TestFolderPopoverAndDesktopIconContracts();
            TestTaskbarRecoveryPolicy();
            TestMenuBarReservationPolicy();
            TestMenuBarCenters();
            TestTransientAudioPanelClose(owner, anchor);
            RenderMaskContactSheet(Path.Combine(AppContext.BaseDirectory, "squircle-sizes.png"));
            var iconSurfacesPath = Path.Combine(AppContext.BaseDirectory, "icon-surfaces.png");
            RenderIconSurfaceContactSheet(iconSurfacesPath);
            var trashSurfacesPath = Path.Combine(AppContext.BaseDirectory, "trash-surfaces.png");
            RenderTrashSurfaceContactSheet(trashSurfacesPath);
            TestSteamIconDesaturation();
            var steamStatusPath = Path.Combine(AppContext.BaseDirectory, "steam-status-light-dark.png");
            var hasSteamIconSample = RenderInstalledSteamStatusIcon(steamStatusPath);
            Console.WriteLine("PASS: DWM thumbnail registered, hidden and released cleanly across 12 cycles.");
            Console.WriteLine("PASS: Drag ghost snapped/followed/disposed at 32, 58 and 96 DIP.");
            Console.WriteLine("PASS: Dock drag-out carries no FileDrop/shortcut payload; feedback, drop and removal share the visible dock-plate boundary while internal reorder/external file-drop inputs remain available.");
            Console.WriteLine("PASS: Explorer static verbs quote one Unicode path and the same-user single-instance bridge forwards it without loading code into Explorer.");
            Console.WriteLine("PASS: Different source safe zones normalize to the same fixed 32, 58 and 96 DIP icons without a generated backdrop; source pixels cannot resize the artwork and common 1.42x hover scale is preserved.");
            Console.WriteLine("PASS: Bundled VeliShell app artwork expands its centered ~0.803 source safe zone to each fixed icon and uses the same p=4.37 contour without an accent plate.");
            Console.WriteLine("PASS: Dock hover labels contain only the application name, never icon-provider attribution.");
            Console.WriteLine("PASS: Settings exposes local/online/reset controls for pins, fixed dock elements, separate empty/full Recycle Bin states, and stable running-app identities.");
            Console.WriteLine("PASS: Browser app shortcuts retain per-site identity; Steam web helpers map only to their own Steam installation; live web API search is absent.");
            Console.WriteLine("PASS: Application Frame Host windows resolve to their packaged app ID, localized name and native icon; shell-app pins match the real app.");
            Console.WriteLine("PASS: Only previously visible same-session app processes appear after their last window closes; process exit, PID reuse, shell hosts, and Steam stay filtered.");
            Console.WriteLine("PASS: Force quit rejects a stale identity and terminates only its own exact QA process handle.");
            Console.WriteLine("PASS: Folder pins use a bounded root-confined popover; desktop icons and the VeliShell dock item remain explicit, reversible preferences.");
            Console.WriteLine("PASS: Taskbar rollback preserves pre-hidden windows; work-area recovery is edge-scoped, topology-safe, idempotent, and repairs journaled Explorer drift without removing the menu-bar reservation.");
            Console.WriteLine("PASS: Menu bar reserves a reversible top-edge appbar without overwriting foreign reservations; emergency taskbar restore wins deterministic layout interleavings.");
            Console.WriteLine("PASS: Menu-bar Control Center and combined VeliShell/Windows Notification Center expose bounded, reversible and privacy-preserving contracts; unpackaged builds fail closed.");
            Console.WriteLine("PASS: The audio-device panel closes without a reentrant deactivation crash.");
            Console.WriteLine($"PASS: Rendered real 58-DIP VeliShell/files/browser/notes/system surfaces to {iconSurfacesPath}");
            Console.WriteLine($"PASS: Rendered aligned freeform empty/full Recycle Bin surfaces to {trashSurfacesPath}");
            Console.WriteLine(hasSteamIconSample
                ? $"PASS: Rendered the installed Steam icon, desaturated for light and dark menu bars, to {steamStatusPath}"
                : "PASS: Steam icon conversion checked; no installed Steam icon available for a visual sample.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            source.Close();
            owner.Close();
            application.Shutdown();
        }
    }

    private static int RenderStartLauncher()
    {
        // WPF can dispatch Startup during a nested render frame even without
        // Run(). Override it so this preview never loads user settings or
        // changes Explorer/taskbar state.
        var application = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml")
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml")
        });
        var appType = typeof(VeliShell.Desktop.App);
        appType.GetProperty("Preferences")!.SetValue(application, new Settings
        {
            FirstRunCompleted = true,
            Pins = Settings.Defaults()
        });

        var entryType = RequireType("VeliShell.Desktop.Services.SearchEntry");
        var entries = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        foreach (var (name, target) in new[]
        {
            ("Editor", "notepad.exe"),
            ("Explorer", "explorer.exe"),
            ("Rechner", "calc.exe")
        })
            entries.Add(Activator.CreateInstance(entryType, name, target, "app", null)!);
        var index = appType.GetProperty("ProgramIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(application)!;
        index.GetType().GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(index, entries);

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "start-launcher-renders");
        Directory.CreateDirectory(outputDirectory);
        var launcherType = RequireType("VeliShell.Desktop.Views.StartLauncherWindow");
        try
        {
            foreach (var dark in new[] { false, true })
            {
                application.Resources.MergedDictionaries[0] = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
                };
                var launcher = (Window)(Activator.CreateInstance(
                    launcherType, BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: [application], culture: null)
                    ?? throw new InvalidOperationException("Could not create Start launcher."));
                launcherType.GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)!.Invoke(launcher, null);
                RenderShellPanel(launcher,
                    Path.Combine(outputDirectory, $"start-launcher-{(dark ? "dark" : "light")}.png"), dark);
            }

            // Hosted runners render WPF off-screen but have no reliable visible
            // desktop/work area for a PointToScreen assertion. The default QA
            // mode tests CalculateBounds on ordinary and compact monitors.
            var interactiveDesktop = !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                StringComparison.OrdinalIgnoreCase);
            var anchor = new Border { Width = 52, Height = 52, Background = Brushes.SteelBlue,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(80, 0, 0, 10) };
            var owner = new Window { Width = 600, Height = 90, Left = 100,
                Top = Math.Max(20, SystemParameters.WorkArea.Bottom - 110),
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false, Content = new Grid { Children = { anchor } } };
            Window? positioned = null;
            try
            {
                owner.Show();
                owner.UpdateLayout();
                positioned = (Window)(Activator.CreateInstance(
                    launcherType, BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: [application], culture: null)
                    ?? throw new InvalidOperationException("Could not create positioned Start launcher."));
                RequireMethod(launcherType, "ShowAbove").Invoke(positioned, [anchor, owner, false]);
                DrainDispatcher();
                if (interactiveDesktop)
                {
                    var anchorScreen = anchor.PointToScreen(new Point(0, 0));
                    var transform = PresentationSource.FromVisual(anchor)!.CompositionTarget!.TransformFromDevice;
                    var anchorDip = transform.Transform(anchorScreen);
                    Require(positioned.IsVisible && positioned.Top + positioned.Height < anchorDip.Y,
                        "The real Start launcher did not open directly above its dock button.");
                    Require(positioned.Left >= SystemParameters.WorkArea.Left - 1 &&
                            positioned.Left + positioned.Width <= SystemParameters.WorkArea.Right + 1,
                        "The real Start launcher extends beyond the monitor work area.");
                }
                RequireMethod(launcherType, "CloseOnce").Invoke(positioned, null);
                RequireMethod(launcherType, "CloseOnce").Invoke(positioned, null);
                Require(!positioned.IsVisible,
                    "Closing the Start launcher twice left the window open or caused a reentrant close.");
            }
            finally
            {
                if (positioned is not null)
                    RequireMethod(launcherType, "CloseOnce").Invoke(positioned, null);
                owner.Close();
            }

            Console.WriteLine(interactiveDesktop
                ? $"PASS: Real Start launcher rendered in both themes and opened above its dock anchor: {outputDirectory}"
                : $"PASS: Real Start launcher rendered in both themes; hosted-runner anchor geometry was checked by the default QA mode: {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RenderFolderViews()
    {
        var application = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml")
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml")
        });
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executables = new[] { "notepad.exe", "cmd.exe", "calc.exe", "mspaint.exe", "write.exe" };
        var entries = Enumerable.Range(0, 20)
            .Select(index => new VirtualFolderEntry(index.ToString(), $"App {index + 1}",
                Path.Combine(systemDirectory, executables[index % executables.Length])))
            .ToList();
        var folder = Settings.CreateVirtualFolder("Kreativ & Arbeit") with { VirtualItems = entries };
        var settings = new Settings { FirstRunCompleted = true, Pins = [folder] };
        typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(application, settings);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "folder-renders");
        Directory.CreateDirectory(outputDirectory);
        var popoverType = RequireType("VeliShell.Desktop.Views.FolderPopoverWindow");
        var tileType = RequireType("VeliShell.Desktop.Controls.DockTile");
        var itemType = RequireType("VeliShell.Desktop.Models.DockItem");
        try
        {
            foreach (var dark in new[] { false, true })
            {
                application.Resources.MergedDictionaries[0] = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
                };
                foreach (var mode in new[] { FolderDisplayMode.AppLauncher, FolderDisplayMode.CompactAppLauncher })
                {
                    folder = folder with { FolderMode = mode };
                    settings.Pins[0] = folder;
                    var popover = (Window)(Activator.CreateInstance(popoverType,
                        BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                        args: [application, folder], culture: null)
                        ?? throw new InvalidOperationException("Could not create app folder."));
                    RequireMethod(popoverType, "RenderVirtual").Invoke(popover, null);
                    var grid = (WrapPanel)popoverType.GetField("GridItemsPanel",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(popover)!;
                    Require(grid.Children.Count == (mode == FolderDisplayMode.CompactAppLauncher ? 16 : 9),
                        "App folder did not fill exactly one grid page.");
                    var pageControls = (FrameworkElement)popoverType.GetField("PageControls",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(popover)!;
                    Require(pageControls.Visibility == Visibility.Visible,
                        "A multi-page app folder lost its sideways navigation.");
                    RequireMethod(popoverType, "MovePage").Invoke(popover, [1]);
                    Require(grid.Children.Count == (mode == FolderDisplayMode.CompactAppLauncher ? 4 : 9),
                        "The next app-folder page contains the wrong number of icons.");
                    RequireMethod(popoverType, "MovePage").Invoke(popover, [-1]);
                    RenderShellPanel(popover, Path.Combine(outputDirectory,
                        $"app-folder-{(mode == FolderDisplayMode.CompactAppLauncher ? "4x4" : "3x3")}-{(dark ? "dark" : "light")}.png"), dark);
                }

                var item = Activator.CreateInstance(itemType)!;
                itemType.GetProperty("Key")!.SetValue(item, "pin:" + folder.Id);
                itemType.GetProperty("Name")!.SetValue(item, folder.Name);
                itemType.GetProperty("Target")!.SetValue(item, folder.Target);
                itemType.GetProperty("IconId")!.SetValue(item, "virtual-folder");
                itemType.GetProperty("Pin")!.SetValue(item, folder);
                var tile = (FrameworkElement)(Activator.CreateInstance(tileType,
                    BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                    args: [item, 58d], culture: null)
                    ?? throw new InvalidOperationException("Could not create dock folder tile."));
                var preview = (FrameworkElement)RequireField(tileType, "_folderPreview").GetValue(tile)!;
                var label = (TextBlock)RequireField(tileType, "_folderName").GetValue(tile)!;
                Require(preview.Visibility == Visibility.Visible && label.Text == folder.Name,
                    "The dock folder lost its live preview or caption.");
                var dockStage = new Grid { Background = (Brush)application.FindResource("DockSurfaceGradient") };
                dockStage.Children.Add(tile);
                var tileWindow = new Window { Width = 180, Height = 145, Content = dockStage };
                RenderShellPanel(tileWindow, Path.Combine(outputDirectory,
                    $"dock-folder-{(dark ? "dark" : "light")}.png"), dark);
            }
            TestDockContextMenus(application);
            Console.WriteLine($"PASS: Glass folder previews and paged 3×3/4×4 app grids rendered in both themes: {outputDirectory}");
            Console.WriteLine("PASS: Dock-item menus contain only item actions; dock-wide actions stay on the plate, and menus are configured to dismiss on outside clicks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally { application.Shutdown(); }
    }

    private static void TestDockContextMenus(SafePreviewApp application)
    {
        var appPin = new Pin("menu-app", "Editor", "notepad.exe");
        var folderPin = Settings.CreateVirtualFolder("Work");
        application.Preferences.Pins = [appPin, folderPin];
        var dockType = RequireType("VeliShell.Desktop.Views.DockWindow");
        var itemType = RequireType("VeliShell.Desktop.Models.DockItem");
        var tileType = RequireType("VeliShell.Desktop.Controls.DockTile");
        var dock = Activator.CreateInstance(dockType, [application])!;
        var buildItemMenu = RequireMethod(dockType, "BuildItemMenu");

        object NewItem(string key, string name, string target = "", Pin? pin = null)
        {
            var item = Activator.CreateInstance(itemType)!;
            itemType.GetProperty("Key")!.SetValue(item, key);
            itemType.GetProperty("Name")!.SetValue(item, name);
            itemType.GetProperty("Target")!.SetValue(item, target);
            itemType.GetProperty("Pin")!.SetValue(item, pin);
            return item;
        }

        ContextMenu BuildMenu(string key, string name, string target = "", Pin? pin = null)
        {
            var item = NewItem(key, name, target, pin);
            var tile = Activator.CreateInstance(tileType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [item, 52d], culture: null)!;
            return (ContextMenu)buildItemMenu.Invoke(dock, [tile])!;
        }

        static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>()
            .Select(entry => entry.Header?.ToString() ?? "").ToArray();
        string Label(string key) => LocalizationService.Current.Get(key);
        var globalLabels = new[] { "Dock.PinProgram", "Dock.PinFolder", "FolderPopover.CreateVirtual",
            "Search.Open", "Dock.Settings", "Dock.Quit" }.Select(Label).ToHashSet();

        var appMenu = BuildMenu("pin:" + appPin.Id, appPin.Name, appPin.Target, appPin);
        var folderMenu = BuildMenu("pin:" + folderPin.Id, folderPin.Name, pin: folderPin);
        var trashMenu = BuildMenu("trash", "Recycle Bin", "shell:RecycleBinFolder");
        var startMenu = BuildMenu("start", "Start");
        var veliMenu = BuildMenu("velishell", "VeliShell");
        var overflow = NewItem("overflow", "More apps");
        var overflowEntries = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(itemType))!;
        overflowEntries.Add(NewItem("overflow:app", "Overflow App", "notepad.exe"));
        itemType.GetProperty("Overflow")!.SetValue(overflow, overflowEntries);
        var overflowMenu = (ContextMenu)RequireMethod(dockType, "BuildOverflowMenu")
            .Invoke(dock, [overflow, new Border()])!;
        foreach (var menu in new[] { appMenu, folderMenu, trashMenu, startMenu, veliMenu, overflowMenu })
        {
            Require(!Headers(menu).Any(globalLabels.Contains),
                "A Dock icon still shows global Dock commands or VeliShell settings.");
            Require(menu.Items.Count > 0 && menu.Items[^1] is not Separator,
                "A Dock icon has an empty or trailing-separator menu.");
        }
        Require(Headers(appMenu).Contains(Label("Dock.Open")) &&
                Headers(appMenu).Contains(Label("Dock.Remove")) &&
                !Headers(appMenu).Contains(Label("Dock.Reopen")) &&
                !Headers(appMenu).Contains(Label("Dock.MoveLeft")),
            "A stopped first-position app has duplicate or impossible commands.");
        Require(Headers(folderMenu).Contains(Label("FolderPopover.RenameTitle")) &&
                Headers(folderMenu).Contains(Label("FolderPopover.DisplayMode")) &&
                Headers(folderMenu).Contains(Label("Dock.MoveLeft")),
            "The virtual app folder lost its own actions.");
        Require(Headers(trashMenu).Contains(Label("Dock.RecycleOpen")) &&
                Headers(startMenu).Contains(Label("Dock.WindowsStart")) &&
                Headers(veliMenu).Contains(Label("Dock.RemoveVeliShell")) &&
                Headers(overflowMenu).SequenceEqual(["Overflow App"]),
            "A fixed Dock icon lost its contextual actions.");

        var plateMenu = new ContextMenu();
        RequireMethod(dockType, "AddCommonMenu").Invoke(dock, [plateMenu]);
        Require(Headers(plateMenu).Contains(Label("Dock.Settings")),
            "VeliShell settings are no longer available from the Dock plate.");
        var anchor = new Border { Width = 50, Height = 50 };
        RequireMethod(dockType, "OpenMenu").Invoke(dock, [plateMenu, anchor]);
        Require(plateMenu.IsOpen && !plateMenu.StaysOpen,
            "Dock context menus do not dismiss when the user clicks outside.");
        plateMenu.IsOpen = false;
    }

    /// <summary>
    /// Explicit opt-in probe for a Windows behavior that cannot be proven by a
    /// unit test: whether existing maximized windows receive a changed work
    /// area. The original rectangle is restored in a finally block and never
    /// written to the user profile. This is intentionally not part of normal CI.
    /// </summary>
    private static int RunLiveWorkAreaReflowProbe(Application application)
    {
        const uint spiSetWorkArea = 0x002F;
        const uint spifSendChange = 0x0002;
        var window = new Window
        {
            Title = "VeliShell work-area reflow probe",
            Width = 640,
            Height = 420,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        NativeRect original = default;
        nint capturedMonitor = 0;
        var capturedOriginal = false;
        try
        {
            window.Show();
            window.WindowState = WindowState.Maximized;
            SettleUi();
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, 2);
            capturedMonitor = monitor;
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            Require(GetMonitorInfo(monitor, ref info), "Could not read the primary monitor for the live work-area probe.");
            original = info.Work;
            capturedOriginal = true;
            var probe = original;
            var shrink = Math.Min(120, Math.Max(40, (probe.Bottom - probe.Top) / 10));
            probe.Bottom -= shrink;
            Require(probe.Bottom - probe.Top >= 320, "The current work area is too small for a safe live probe.");

            var baseline = WindowBounds(handle);
            Require(SystemParametersInfo(spiSetWorkArea, 0, ref probe, 0),
                $"SPI_SETWORKAREA without broadcast failed ({Marshal.GetLastWin32Error()}).");
            SettleUi();
            var withoutBroadcast = WindowBounds(handle);

            Require(SystemParametersInfo(spiSetWorkArea, 0, ref original, spifSendChange),
                $"First work-area restore failed ({Marshal.GetLastWin32Error()}).");
            SettleUi();
            var restoredOnce = WindowBounds(handle);

            Require(SystemParametersInfo(spiSetWorkArea, 0, ref probe, spifSendChange),
                $"SPI_SETWORKAREA with SPIF_SENDCHANGE failed ({Marshal.GetLastWin32Error()}).");
            SettleUi();
            var withBroadcast = WindowBounds(handle);
            _ = ShowWindow(handle, 3); // SW_MAXIMIZE: recompute the existing maximized placement.
            SettleUi();
            var afterRemaximize = WindowBounds(handle);
            var target = new NativeRect
            {
                Left = probe.Left + (baseline.Left - original.Left),
                Top = probe.Top + (baseline.Top - original.Top),
                Right = probe.Right + (baseline.Right - original.Right),
                Bottom = probe.Bottom + (baseline.Bottom - original.Bottom)
            };
            Require(SetWindowPos(handle, 0, target.Left, target.Top,
                    target.Right - target.Left, target.Bottom - target.Top, 0x0004 | 0x0010),
                $"SetWindowPos reflow failed ({Marshal.GetLastWin32Error()}).");
            SettleUi();
            var afterPlacement = WindowBounds(handle);
            Require(SystemParametersInfo(spiSetWorkArea, 0, ref original, spifSendChange),
                $"Second work-area restore failed ({Marshal.GetLastWin32Error()}).");
            Require(SetWindowPos(handle, 0, baseline.Left, baseline.Top,
                    baseline.Right - baseline.Left, baseline.Bottom - baseline.Top, 0x0004 | 0x0010),
                "Could not reset the probe window before SetWindowPlacement validation.");
            var windowPlacement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
            Require(GetWindowPlacement(handle, ref windowPlacement), "GetWindowPlacement failed.");
            Require(SystemParametersInfo(spiSetWorkArea, 0, ref probe, spifSendChange),
                $"Third SPI_SETWORKAREA failed ({Marshal.GetLastWin32Error()}).");
            Require(SetWindowPlacement(handle, ref windowPlacement),
                $"SetWindowPlacement failed ({Marshal.GetLastWin32Error()}).");
            SettleUi();
            var afterWindowPlacement = WindowBounds(handle);

            var flagsZeroReflowed = withoutBroadcast.Bottom <= baseline.Bottom - (shrink / 2);
            var sendChangeReflowed = withBroadcast.Bottom <= restoredOnce.Bottom - (shrink / 2);
            var remaximizeReflowed = afterRemaximize.Bottom <= restoredOnce.Bottom - (shrink / 2);
            var placementReflowed = afterPlacement.Bottom <= restoredOnce.Bottom - (shrink / 2);
            var windowPlacementReflowed = afterWindowPlacement.Bottom <= restoredOnce.Bottom - (shrink / 2);
            Console.WriteLine(
                $"LIVE: work={Format(original)} baseline={Format(baseline)} flags0={Format(withoutBroadcast)} " +
                $"restored={Format(restoredOnce)} sendChange={Format(withBroadcast)} " +
                $"remaximize={Format(afterRemaximize)} placement={Format(afterPlacement)} " +
                $"windowPlacement={Format(afterWindowPlacement)}; " +
                $"flags0Reflow={flagsZeroReflowed}; sendChangeReflow={sendChangeReflowed}; " +
                $"remaximizeReflow={remaximizeReflowed}; placementReflow={placementReflowed}; " +
                $"windowPlacementReflow={windowPlacementReflowed}");
            Require(placementReflowed || windowPlacementReflowed,
                "Neither verified window-placement fallback reflowed an existing maximized window.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (capturedOriginal)
            {
                var restore = original;
                if (!SystemParametersInfo(spiSetWorkArea, 0, ref restore, spifSendChange))
                    Console.Error.WriteLine($"CRITICAL: final work-area restore failed ({Marshal.GetLastWin32Error()}).");
                if (window.IsVisible) _ = ShowWindow(new WindowInteropHelper(window).Handle, 3);
                SettleUi();
                var verified = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
                if (capturedMonitor == 0 || !GetMonitorInfo(capturedMonitor, ref verified) ||
                    !SameRect(verified.Work, original))
                    throw new InvalidOperationException("The live probe could not verify the final original work area.");
                Console.WriteLine($"LIVE RESTORE VERIFIED: work={Format(verified.Work)}");
            }
            window.Close();
            application.Shutdown();
        }
    }

    private static int RunLiveMenuBarReservationProbe(Application application)
    {
        var window = new Window
        {
            Title = "VeliShell live menu-bar reservation probe",
            Width = 640,
            Height = 35,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        var maximizedWindow = new Window
        {
            Title = "VeliShell maximized-window reservation probe",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        object? service = null;
        nint handle = 0;
        var registered = false;
        object? originalWork = null;
        try
        {
            var nativeType = RequireType("VeliShell.Desktop.Native.NativeMethods");
            var serviceType = RequireType("VeliShell.Desktop.Services.TaskbarVisibilityService");
            var primary = RequireMethod(nativeType, "PrimaryMonitor");
            var monitorInfoType = nativeType.GetNestedType("MonitorInfo", BindingFlags.NonPublic)
                                  ?? throw new TypeLoadException("NativeMethods.MonitorInfo");
            var rectType = nativeType.GetNestedType("Rect", BindingFlags.NonPublic)
                           ?? throw new TypeLoadException("NativeMethods.Rect");
            var workField = monitorInfoType.GetField("Work")!;
            int Edge(object rect, string name) => (int)rectType.GetField(name)!.GetValue(rect)!;
            bool Same(object left, object right) =>
                Edge(left, "Left") == Edge(right, "Left") && Edge(left, "Top") == Edge(right, "Top") &&
                Edge(left, "Right") == Edge(right, "Right") && Edge(left, "Bottom") == Edge(right, "Bottom");

            originalWork = workField.GetValue(primary.Invoke(null, null)!)!;
            maximizedWindow.Show();
            maximizedWindow.WindowState = WindowState.Maximized;
            window.Show();
            SettleUi();
            handle = new WindowInteropHelper(window).Handle;
            service = Activator.CreateInstance(serviceType, nonPublic: true)
                      ?? throw new InvalidOperationException("Could not create the work-area service.");
            var register = RequireMethod(serviceType, "RegisterMenuBar");
            var registerArguments = new object[] { handle, 0x8056u, 35, Activator.CreateInstance(rectType)! };
            registered = (bool)register.Invoke(service, registerArguments)!;
            Require(registered, "Windows rejected the live top-edge appbar registration.");
            var reserved = registerArguments[3];
            SettleUi();
            var reservedWork = workField.GetValue(primary.Invoke(null, null)!)!;
            Require(Edge(reservedWork, "Top") >= Edge(reserved, "Bottom"),
                "A maximized-window work area can still overlap the registered menu bar.");
            Require(Edge(reservedWork, "Left") == Edge(originalWork, "Left") &&
                    Edge(reservedWork, "Right") == Edge(originalWork, "Right") &&
                    Edge(reservedWork, "Bottom") == Edge(originalWork, "Bottom"),
                "The menu-bar probe modified an unrelated work-area edge.");
            var maximizedBounds = WindowBounds(new WindowInteropHelper(maximizedWindow).Handle);
            const int nonClientFrameTolerance = 16;
            Require(maximizedBounds.Top >= Edge(reservedWork, "Top") - nonClientFrameTolerance &&
                    maximizedBounds.Bottom <= Edge(reservedWork, "Bottom") + nonClientFrameTolerance,
                "An existing maximized window was not reflowed below the menu-bar reservation.");

            RequireMethod(serviceType, "ReleaseMenuBar").Invoke(service, [handle]);
            registered = false;
            SettleUi();
            var restored = workField.GetValue(primary.Invoke(null, null)!)!;
            Require(Same(restored, originalWork),
                "The live menu-bar probe did not restore the original work area.");
            Console.WriteLine(
                $"LIVE MENU BAR VERIFIED: originalTop={Edge(originalWork, "Top")}; " +
                $"reservedBottom={Edge(reserved, "Bottom")}; workTop={Edge(reservedWork, "Top")}; " +
                $"maximizedTop={maximizedBounds.Top}; restore=exact");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (registered && service is not null && handle != 0)
            {
                try { RequireMethod(service.GetType(), "ReleaseMenuBar").Invoke(service, [handle]); }
                catch { }
            }
            if (service is IDisposable disposable) disposable.Dispose();
            if (window.IsVisible) window.Close();
            if (maximizedWindow.IsVisible) maximizedWindow.Close();
            application.Shutdown();
        }
    }

    private static void SettleUi()
    {
        DrainDispatcher();
        Thread.Sleep(450);
        DrainDispatcher();
    }

    private static NativeRect WindowBounds(nint handle)
    {
        Require(GetWindowRect(handle, out var bounds), "Could not read the probe window bounds.");
        return bounds;
    }

    private static string Format(NativeRect rect) => $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    private static bool SameRect(NativeRect left, NativeRect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint handle, ref NativeWindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint handle, ref NativeWindowPlacement placement);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref NativeRect value, uint flags);

    private static void TestThumbnail(Window owner, Window source, FrameworkElement anchor)
    {
        var type = RequireType("VeliShell.Desktop.Views.WindowThumbnailPreview");
        var preview = (Window)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [owner],
            culture: null)!;
        Invoke(type, preview, "Prepare");
        DrainDispatcher();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            Invoke(type, preview, "ShowFor",
                new WindowInteropHelper(source).Handle,
                anchor,
                source.Title,
                CreateImage());
            DrainDispatcher();

            var thumbnail = (nint)RequireField(type, "_thumbnail").GetValue(preview)!;
            Require(thumbnail != 0, $"DwmRegisterThumbnail failed in iteration {iteration + 1}.");
            Require(preview.IsVisible && Math.Abs(preview.Opacity - 1) < 0.001,
                "Preview window was not visibly presented.");
            var anchorRight = anchor.PointToScreen(new Point(anchor.ActualWidth, 0));
            var previewTopLeft = preview.PointToScreen(new Point(0, 0));
            Require(previewTopLeft.X >= anchorRight.X + 8,
                $"Preview was not positioned beside its menu-style anchor ({previewTopLeft.X} vs {anchorRight.X}).");

            Invoke(type, preview, "HidePreview");
            DrainDispatcher();
            thumbnail = (nint)RequireField(type, "_thumbnail").GetValue(preview)!;
            Require(thumbnail == 0 && Math.Abs(preview.Opacity) < 0.001,
                $"HidePreview did not release iteration {iteration + 1}.");
        }

        ((IDisposable)preview).Dispose();
        DrainDispatcher();
        Require(!preview.IsVisible, "Disposed preview window remained visible.");
    }

    private static void TestGhostAndMask(Window owner, FrameworkElement anchor)
    {
        var ghostType = RequireType("VeliShell.Desktop.Views.DragGhostWindow");
        var maskType = RequireType("VeliShell.Desktop.Controls.AppIconMask");
        var createMask = RequireMethod(maskType, "Create");
        var exponentField = maskType.GetField(
            "ContinuousCornerExponent",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(maskType.FullName, "ContinuousCornerExponent");
        Require(Math.Abs((double)exponentField.GetRawConstantValue()! - 4.37) < 0.000001,
            "The calibrated continuous macOS-style corner exponent is no longer 4.37.");
        foreach (var size in new[] { 32d, 58d, 96d })
        {
            var mask = (Geometry)createMask.Invoke(null, [size])!;
            Require(mask.IsFrozen, $"Mask at {size} DIP is not frozen.");
            Require(Math.Abs(mask.Bounds.Width - size) < 0.02 && Math.Abs(mask.Bounds.Height - size) < 0.02,
                $"Mask bounds at {size} DIP are not size-aligned: {mask.Bounds}.");
            Require(mask is StreamGeometry, "Mask is not the shared smooth StreamGeometry.");

            var ghost = (Window)Activator.CreateInstance(
                ghostType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [owner, CreateImage(), size],
                culture: null)!;
            Invoke(ghostType, ghost, "ShowAtCursor");
            DrainDispatcher();
            Require(ghost.IsVisible, $"Ghost at {size} DIP was not shown.");
            Require(FindClip(ghost.Content as DependencyObject) is StreamGeometry clip && clip.IsFrozen,
                $"Ghost at {size} DIP did not use the shared frozen mask.");

            var snapped = (bool)Invoke(ghostType, ghost, "SnapTo", anchor)!;
            DrainDispatcher();
            Require(snapped && (bool)RequireField(ghostType, "_snapped").GetValue(ghost)!,
                $"Ghost at {size} DIP did not enter snapped mode.");
            var anchorCenter = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
            var ghostCenter = ghost.PointToScreen(new Point(ghost.ActualWidth / 2, ghost.ActualHeight / 2));
            Require(Math.Abs(anchorCenter.X - ghostCenter.X) <= 2 && Math.Abs(anchorCenter.Y - ghostCenter.Y) <= 2,
                $"Ghost at {size} DIP is not centered on its target slot ({anchorCenter} vs {ghostCenter}).");
            Invoke(ghostType, ghost, "FollowCursor");
            Require(!(bool)RequireField(ghostType, "_snapped").GetValue(ghost)!,
                $"Ghost at {size} DIP did not return to cursor-follow mode.");

            ((IDisposable)ghost).Dispose();
            DrainDispatcher();
            Require(!ghost.IsVisible, $"Disposed ghost at {size} DIP remained visible.");
        }

        var folder = Settings.CreateVirtualFolder("Tools") with
        {
            VirtualItems = [new VirtualFolderEntry("editor", "Editor", "notepad.exe")]
        };
        var folderGhost = (Window)Activator.CreateInstance(ghostType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [owner, folder, 58d], culture: null)!;
        var folderShadow = (Grid)((Grid)folderGhost.Content).Children[0];
        Require(folderShadow.Children[0].GetType().Name == "DockFolderPreview",
            "Dragging a folder reverted to the obsolete static folder icon.");
        Invoke(ghostType, folderGhost, "ShowAtCursor");
        Require((bool)Invoke(ghostType, folderGhost, "SnapTo", anchor)!,
            "The folder preview ghost did not snap to its insertion slot.");
        ((IDisposable)folderGhost).Dispose();
    }

    private static void TestTransientAudioPanelClose(Window owner, FrameworkElement anchor)
    {
        var panelType = RequireType("VeliShell.Desktop.Views.AudioDevicesWindow");
        var closeOnce = panelType.GetMethod("CloseOnce", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(panelType.FullName, "CloseOnce");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var panel = (Window)(Activator.CreateInstance(panelType,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [], culture: null)
                ?? throw new InvalidOperationException("Could not construct the audio-device panel."));
            RequireMethod(panelType, "ShowRelativeTo").Invoke(panel, [anchor, owner, false]);
            DrainDispatcher();
            Require(panel.IsVisible, "The audio-device panel did not open for its close-race test.");
            if (attempt == 0)
            {
                closeOnce.Invoke(panel, null);
                closeOnce.Invoke(panel, null);
            }
            else panel.Close(); // owner-driven close must also survive deactivation
            DrainDispatcher();
            Require(!panel.IsVisible, "The audio-device panel remained visible after closing.");
        }
    }

    private static void TestDockTileHoverMask()
    {
        var surfaceType = RequireType("VeliShell.Desktop.Controls.AppIconSurface");
        var itemType = RequireType("VeliShell.Desktop.Models.DockItem");
        var tileType = RequireType("VeliShell.Desktop.Controls.DockTile");
        var samples = new (ImageSource Source, Rect Bounds, string Label)[]
        {
            (CreateAlphaPlateImage(25, 25, 230, 230),
                new Rect(25d / 256, 25d / 256, 206d / 256, 206d / 256), "macOS safe zone"),
            (CreateAlphaPlateImage(64, 80, 191, 207),
                new Rect(64d / 256, 80d / 256, 128d / 256, 128d / 256), "compact offset artwork"),
            (CreateAlphaPlateImage(0, 0, 255, 255),
                new Rect(0, 0, 1, 1), "full-canvas artwork")
        };

        foreach (var size in new[] { 32d, 58d, 96d })
        {
            foreach (var sample in samples)
            {
                var surface = (FrameworkElement)Activator.CreateInstance(
                    surfaceType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: [sample.Source, size],
                    culture: null)!;
                surface.Measure(new Size(size, size));
                surface.Arrange(new Rect(0, 0, size, size));
                surface.UpdateLayout();

                var iconImage = (FrameworkElement)RequireProperty(surfaceType, "IconImage")
                    .GetValue(surface)!;
                var plate = (FrameworkElement)RequireProperty(surfaceType, "PlateElement")
                    .GetValue(surface)!;
                var normalizedBounds = (Rect)RequireProperty(surfaceType, "NormalizedArtworkBounds")
                    .GetValue(surface)!;

                RequireSameRect(normalizedBounds, sample.Bounds,
                    $"{sample.Label} normalized source bounds at {size} DIP");
                Require(ReferenceEquals(VisualTreeHelper.GetParent(iconImage), plate),
                    $"{sample.Label} at {size} DIP does not keep the artwork in the common masked surface.");
                Require(surfaceType.GetProperty(
                            "AccentBackground",
                            BindingFlags.Instance | BindingFlags.NonPublic) is null &&
                        plate is Panel { Children.Count: 1 },
                    $"{sample.Label} at {size} DIP still contains a generated icon backdrop.");
                RequireSameSize(surface.RenderSize, new Size(size, size),
                    $"{sample.Label} surface at {size} DIP");
                RequireSameSize(plate.RenderSize, new Size(size, size),
                    $"{sample.Label} fixed plate at {size} DIP");

                Require(plate.ClipToBounds && plate.Clip is StreamGeometry && plate.Clip.IsFrozen,
                    $"{sample.Label} at {size} DIP does not use the shared frozen mask.");
                RequireSameRect(plate.Clip.Bounds, new Rect(0, 0, size, size),
                    $"{sample.Label} mask at {size} DIP");

                var plateBounds = plate.TransformToAncestor(surface)
                    .TransformBounds(new Rect(new Point(), plate.RenderSize));
                RequireSameRect(plateBounds, new Rect(0, 0, size, size),
                    $"{sample.Label} output plate at {size} DIP");

                var imageBounds = iconImage.TransformToAncestor(plate)
                    .TransformBounds(new Rect(new Point(), iconImage.RenderSize));
                var mappedArtworkBounds = new Rect(
                    imageBounds.X + normalizedBounds.X * imageBounds.Width,
                    imageBounds.Y + normalizedBounds.Y * imageBounds.Height,
                    normalizedBounds.Width * imageBounds.Width,
                    normalizedBounds.Height * imageBounds.Height);
                RequireSameRect(mappedArtworkBounds, new Rect(0, 0, size, size),
                    $"{sample.Label} mapped artwork at {size} DIP");
            }

            var item = Activator.CreateInstance(itemType)!;
            itemType.GetProperty("Key")!.SetValue(item, "qa");
            itemType.GetProperty("Name")!.SetValue(item, "QA");
            itemType.GetProperty("IconId")!.SetValue(item, "system");
            itemType.GetProperty("Attribution")!.SetValue(item, "Provider attribution must not appear here");
            var tile = (FrameworkElement)Activator.CreateInstance(
                tileType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [item, size],
                culture: null)!;
            tile.Measure(new Size(size + 22, size + 22));
            tile.Arrange(new Rect(0, 0, size + 22, size + 22));
            tile.UpdateLayout();
            Require(Equals(((Control)tile).ToolTip, "QA"),
                "Dock hover exposed icon-provider attribution instead of only the app name.");
            var tileSurface = (FrameworkElement)RequireField(tileType, "_iconSurface").GetValue(tile)!;
            Require(surfaceType.IsInstanceOfType(tileSurface),
                $"Dock tile at {size} DIP does not use the shared AppIconSurface.");
            Invoke(tileType, tile, "SetScale", 1.42d, false);
            var artworkHost = VisualTreeHelper.GetParent(tileSurface) as FrameworkElement;
            Require(artworkHost?.RenderTransform is TransformGroup transformGroup
                    && transformGroup.Children.Count == 2
                    && transformGroup.Children[0] is ScaleTransform scale
                    && transformGroup.Children[1] is TranslateTransform
                    && Math.Abs(scale.ScaleX - 1.42) < 0.001
                    && Math.Abs(scale.ScaleY - 1.42) < 0.001,
                $"Dock tile at {size} DIP did not scale its icon or folder preview as one unit.");
        }
    }

    private static void TestDockPinDragOutPolicy()
    {
        const string format = "VeliShell.DockPin.v1";
        var dockType = RequireType("VeliShell.Desktop.Views.DockWindow");
        var data = (IDataObject?)RequireMethod(dockType, "CreateDockPinDragData")
            .Invoke(null, ["qa-pin"])
            ?? throw new InvalidOperationException("Dock pin drag data was not created.");

        Require(data.GetDataPresent(format, autoConvert: false),
            "Dock pin drag data no longer contains the private reorder format.");
        Require(string.Equals(data.GetData(format, autoConvert: false) as string, "qa-pin", StringComparison.Ordinal),
            "Dock pin drag data no longer carries the exact pin identifier.");
        Require(!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                && !data.GetFormats(autoConvert: false).Contains(DataFormats.FileDrop, StringComparer.Ordinal)
                && !data.GetFormats(autoConvert: true).Contains(DataFormats.FileDrop, StringComparer.Ordinal),
            "Dock pin drag data exposes FileDrop and could create a file or shortcut in Explorer.");

        var policy = RequireMethod(dockType, "ShouldRemovePinAfterDrag");
        bool ShouldRemove(bool escapePressed, bool leftButtonReleased, bool cursorInsideDock) =>
            (bool)policy.Invoke(null, [escapePressed, leftButtonReleased, cursorInsideDock])!;

        Require(ShouldRemove(false, true, false),
            "An uncancelled release outside the dock no longer removes the pin.");
        Require(!ShouldRemove(false, true, true),
            "An internal reorder would incorrectly remove the pin.");
        Require(!ShouldRemove(true, true, false),
            "Escape would incorrectly remove the pin.");
        Require(!ShouldRemove(false, false, false),
            "Moving outside without releasing would incorrectly remove the pin.");

        var boundsPolicy = RequireMethod(dockType, "IsPointInsideDockBounds");
        bool Inside(Point point) => (bool)boundsPolicy.Invoke(
            null,
            [point, new Point(100, 100), new Point(300, 180)])!;
        Require(Inside(new Point(100, 100)) && Inside(new Point(300, 180)) &&
                Inside(new Point(210, 145)),
            "The visible dock-plate boundary no longer includes its edges and interior.");
        Require(!Inside(new Point(210, 99)) && !Inside(new Point(99, 145)) &&
                !Inside(new Point(301, 181)),
            "The transparent dock-window area is still treated as part of the visible dock plate.");

        var feedbackPolicy = RequireMethod(dockType, "DockDropEffect");
        DragDropEffects Effect(bool inside, bool hasPin, bool hasFiles) =>
            (DragDropEffects)feedbackPolicy.Invoke(null, [inside, hasPin, hasFiles])!;
        Require(Effect(true, true, false) == DragDropEffects.Move &&
                Effect(true, false, true) == DragDropEffects.Copy,
            "Valid internal reorders or external file drops lost their dock feedback.");
        Require(Effect(false, true, false) == DragDropEffects.None &&
                Effect(false, false, true) == DragDropEffects.None &&
                Effect(true, false, false) == DragDropEffects.None,
            "A private pin or external file drop still advertises a drop outside the visible dock plate.");
        var insidePlate = Inside(new Point(210, 145));
        var transparentWindowArea = Inside(new Point(210, 99));
        Require(!ShouldRemove(false, true, insidePlate) &&
                ShouldRemove(false, true, transparentWindowArea),
            "Drag removal and drop feedback no longer share the visible dock-plate boundary.");

        var shellLinkType = RequireType("VeliShell.Desktop.Services.ShellLinkService");
        Require(shellLinkType.GetMethod(
                    "CreateDragArtifact",
                    BindingFlags.Static | BindingFlags.NonPublic) is null,
            "The retired drag-out shortcut creation path is still present.");
        Require(RequireMethod(dockType, "TryReadDroppedPaths").IsStatic,
            "External file drops into the dock are no longer available.");
    }

    private static void TestExplorerPinVerbContract()
    {
        var registrationType = RequireType("VeliShell.Desktop.Services.ShellVerbRegistrationService");
        var buildCommand = RequireMethod(registrationType, "BuildCommand");
        const string executable = @"C:\Program Files\VeliShell\VeliShell.exe";
        var command = (string)buildCommand.Invoke(null, [executable])!;
        Require(command == "\"C:\\Program Files\\VeliShell\\VeliShell.exe\" --pin-to-dock \"%1\"",
            "The Explorer verb command no longer quotes both the executable and the Shell-supplied path.");

        var verbKeys = (string[])(registrationType.GetField(
            "VerbKeys", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new MissingFieldException(registrationType.FullName, "VerbKeys"));
        Require(verbKeys.SequenceEqual([
                @"Software\Classes\*\shell\VeliShell.PinToDock",
                @"Software\Classes\Directory\shell\VeliShell.PinToDock"
            ]),
            "The Explorer verb no longer covers exactly files/programs and file-system directories.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "VeliShell-IpcQa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, "Über & Leerzeichen.txt");
        File.WriteAllText(path, "qa");
        var pipeName = "Zumbyte.VeliShell.PinToDock.Qa." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridgeType = RequireType("VeliShell.Desktop.Services.SingleInstancePinBridge");
        var bridge = (IDisposable)(Activator.CreateInstance(
            bridgeType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new Func<string, bool>(value => { received.TrySetResult(value); return true; }), pipeName],
            culture: null) ?? throw new InvalidOperationException("The pin bridge could not be created."));
        try
        {
            RequireMethod(bridgeType, "Start").Invoke(bridge, null);
            var forward = (Task<bool>)RequireMethod(bridgeType, "ForwardAsync")
                .Invoke(null, [path, TimeSpan.FromSeconds(5), pipeName])!;
            Require(forward.GetAwaiter().GetResult(), "The running-instance bridge rejected a valid path.");
            Require(string.Equals(received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                    Path.GetFullPath(path), StringComparison.Ordinal),
                "The running-instance bridge did not preserve the exact Unicode path.");
        }
        finally
        {
            bridge.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void TestVeliShellAssetSurface()
    {
        var iconServiceType = RequireType("VeliShell.Desktop.Services.IconService");
        var surfaceType = RequireType("VeliShell.Desktop.Controls.AppIconSurface");
        var maskType = RequireType("VeliShell.Desktop.Controls.AppIconMask");
        var source = (ImageSource)RequireMethod(iconServiceType, "For")
            .Invoke(null, ["velishell", "", null])!;
        const double expectedPlateSide = 0.803;
        const double sampleTolerance = 2d / 256;

        var exponentField = maskType.GetField(
            "ContinuousCornerExponent",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(maskType.FullName, "ContinuousCornerExponent");
        Require(Math.Abs((double)exponentField.GetRawConstantValue()! - 4.37) < 0.000001,
            "The bundled VeliShell app icon is no longer tested with the calibrated p=4.37 mask.");

        foreach (var size in new[] { 32d, 58d, 96d })
        {
            var surface = (FrameworkElement)Activator.CreateInstance(
                surfaceType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [source, size],
                culture: null)!;
            surface.Measure(new Size(size, size));
            surface.Arrange(new Rect(0, 0, size, size));
            surface.UpdateLayout();

            var iconImage = (FrameworkElement)RequireProperty(surfaceType, "IconImage")
                .GetValue(surface)!;
            var plate = (FrameworkElement)RequireProperty(surfaceType, "PlateElement")
                .GetValue(surface)!;
            var normalized = (Rect)RequireProperty(surfaceType, "NormalizedArtworkBounds")
                .GetValue(surface)!;

            Require(Math.Abs(normalized.Width - normalized.Height) <= 1d / 256,
                $"VeliShell app artwork bounds at {size} DIP are not square: {normalized}.");
            Require(Math.Abs(normalized.Width - expectedPlateSide) <= sampleTolerance,
                $"VeliShell app artwork safe zone at {size} DIP is {normalized.Width:0.####}, expected about {expectedPlateSide:0.###}.");
            Require(Math.Abs(normalized.Left + normalized.Width / 2 - 0.5) <= 1d / 256
                    && Math.Abs(normalized.Top + normalized.Height / 2 - 0.5) <= 1d / 256,
                $"VeliShell app artwork at {size} DIP is not centered in its sampled source: {normalized}.");
            RequireSameSize(surface.RenderSize, new Size(size, size),
                $"VeliShell app surface at {size} DIP");
            RequireSameSize(plate.RenderSize, new Size(size, size),
                $"VeliShell app fixed plate at {size} DIP");
            Require(surfaceType.GetProperty(
                        "AccentBackground",
                        BindingFlags.Instance | BindingFlags.NonPublic) is null &&
                    plate is Panel { Children.Count: 1 },
                $"VeliShell app at {size} DIP still contains a generated icon backdrop.");
            Require(plate.ClipToBounds && plate.Clip is StreamGeometry && plate.Clip.IsFrozen,
                $"VeliShell app plate at {size} DIP does not use the shared frozen p=4.37 mask.");
            RequireSameRect(
                plate.Clip.Bounds,
                new Rect(0, 0, size, size),
                $"VeliShell app mask bounds at {size} DIP");

            var imageBounds = iconImage.TransformToAncestor(plate)
                .TransformBounds(new Rect(new Point(), iconImage.RenderSize));
            var mappedArtworkBounds = new Rect(
                imageBounds.X + normalized.X * imageBounds.Width,
                imageBounds.Y + normalized.Y * imageBounds.Height,
                normalized.Width * imageBounds.Width,
                normalized.Height * imageBounds.Height);
            RequireSameRect(mappedArtworkBounds, new Rect(0, 0, size, size),
                $"VeliShell app normalized artwork at {size} DIP");
        }

        RequireImageMatchesMask(source, expectedPlateSide, 0.03,
            "Bundled VeliShell app icon alpha contour");
    }

    private static void TestDockIconCustomizationContract()
    {
        var customIcons = RequireType("VeliShell.Desktop.Services.CustomIconService");
        var import = customIcons.GetMethod("Import", BindingFlags.Static | BindingFlags.NonPublic);
        Require(import is not null &&
                customIcons.GetMethod("TryLoad", BindingFlags.Static | BindingFlags.NonPublic) is not null,
            "The app-owned local icon import/load contract is missing.");

        var malformedPath = Path.Combine(Path.GetTempPath(), $"velishell-invalid-icon-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(malformedPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
            try
            {
                import!.Invoke(null, [malformedPath]);
                throw new InvalidOperationException("Malformed PNG unexpectedly imported as a dock icon.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                // Expected: WIC FileFormatException/FormatException is contained
                // and translated into the settings window's normal invalid-icon path.
            }
        }
        finally
        {
            try { File.Delete(malformedPath); }
            catch (IOException) { }
        }

        var preferences = RequireType("VeliShell.Desktop.Views.PreferencesWindow");
        foreach (var field in new[] { "SystemIconList", "RunningIconList", "PinList" })
            Require(RequireField(preferences, field) is not null,
                $"Settings no longer exposes the {field} icon-management surface.");
        Require(preferences.GetField("IconApiKeyInput", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "The App Store icon picker must not ask for an API key.");
        foreach (var method in new[]
                 {
                     "ChooseLocalIconForPin", "ChooseLocalIconForDockElement", "ResetPinIcon",
                     "ResetDockIcon", "ChooseOnlineIconForRunningAppAsync",
                     "ChooseOnlineIconForDockElementAsync"
                 })
            Require(preferences.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                $"Settings icon action {method} is missing.");

        var dock = RequireType("VeliShell.Desktop.Views.DockWindow");
        Require(dock.GetMethod("GetConfigurableRunningApps", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "Running dock elements are not available to icon settings.");

        var gallery = RequireType("VeliShell.Desktop.Services.AppStoreIconService");
        int Constant(string name) => (int)(gallery.GetField(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetRawConstantValue()
            ?? throw new MissingFieldException(gallery.FullName, name));
        Require(Constant("MaximumSearchBytes") == 1024 * 1024 &&
                Constant("MaximumImageBytes") == 5 * 1024 * 1024 &&
                Constant("MaximumCacheEntries") == 64,
            "The Apple icon provider no longer has the expected bounded response and cache limits.");
        var memoryType = gallery.GetNestedType("MemoryImage", BindingFlags.NonPublic)
                         ?? throw new TypeLoadException("MemoryImage");
        var attributionType = RequireType("VeliShell.Desktop.Services.AppStoreIconAttribution");
        var attribution = Activator.CreateInstance(
            attributionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["App Store · QA", "https://apps.apple.com/us/app/example/id123"],
            culture: null)!;
        var onlineBitmap = (BitmapSource)CreateAlphaPlateImage(0, 0, 255, 255);
        var memory = Activator.CreateInstance(
            memoryType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [onlineBitmap, DateTimeOffset.UtcNow.AddMinutes(5), attribution],
            culture: null)!;
        var cache = (gallery.GetField("MemoryCache", BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new MissingFieldException(gallery.FullName, "MemoryCache")).GetValue(null)!;
        var cacheItem = cache.GetType().GetProperty("Item")
                        ?? throw new MissingMemberException(cache.GetType().FullName, "Item");
        var hash = new string('a', 64);
        cacheItem.SetValue(cache, memory,
            [VeliShell.Core.ItunesSearchApi.ProviderId + ":" + hash + ":" + hash]);
        try
        {
            var reference = new VeliShell.Core.IconReference(
                VeliShell.Core.ItunesSearchApi.ProviderId,
                hash,
                VeliShell.Core.ItunesSearchApi.CatalogVersion,
                hash);
            var rendered = (ImageSource)RequireMethod(
                    RequireType("VeliShell.Desktop.Services.IconService"), "For")
                .Invoke(null, ["velishell", "", reference])!;
            Require(ReferenceEquals(rendered, onlineBitmap),
                "A valid online override for the fixed VeliShell dock element was ignored.");
        }
        finally
        {
            cache.GetType().GetMethod("Clear", Type.EmptyTypes)!.Invoke(cache, null);
        }
    }

    private static void TestFolderPopoverAndDesktopIconContracts()
    {
        var browserType = RequireType("VeliShell.Desktop.Services.FolderBrowserService");
        var tryCreateRoot = RequireMethod(browserType, "TryCreateRoot");
        var tryResolveLocation = RequireMethod(browserType, "TryResolveLocation");
        var isWithinRoot = RequireMethod(browserType, "IsWithinRoot");
        var enumerateAsync = RequireMethod(browserType, "EnumerateAsync");
        var canOpenEntry = RequireMethod(browserType, "CanOpenEntry");
        var maximumEntries = (int)(browserType.GetField(
            "MaximumEntries", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()
            ?? throw new MissingFieldException(browserType.FullName, "MaximumEntries"));
        var maximumDepth = (int)(browserType.GetField(
            "MaximumDepth", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()
            ?? throw new MissingFieldException(browserType.FullName, "MaximumDepth"));
        Require(maximumEntries == 120 && maximumDepth == 16,
            "Pinned-folder enumeration no longer has the expected strict entry/depth bounds.");

        var temporary = Path.Combine(Path.GetTempPath(), $"VeliShellFolderQa-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "root");
        var child = Path.Combine(root, "child");
        var outside = Path.Combine(temporary, "outside");
        try
        {
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(outside);
            for (var index = 0; index < maximumEntries + 12; index++)
                File.WriteAllText(Path.Combine(root, $"item-{index:000}.txt"), "qa");

            var rootArguments = new object?[] { root, null };
            Require((bool)tryCreateRoot.Invoke(null, rootArguments)! &&
                    string.Equals(rootArguments[1] as string, root, StringComparison.OrdinalIgnoreCase),
                "A normal local folder can no longer become a safe popover root.");
            Require((bool)isWithinRoot.Invoke(null, [root, child])! &&
                    !(bool)isWithinRoot.Invoke(null, [root, outside])!,
                "Folder-popover root confinement accepts a sibling-path escape.");

            var resolveChild = new object?[] { root, child, null };
            var resolveOutside = new object?[] { root, outside, null };
            Require((bool)tryResolveLocation.Invoke(null, resolveChild)! &&
                    !(bool)tryResolveLocation.Invoke(null, resolveOutside)!,
                "Folder navigation no longer accepts only existing descendants of its pinned root.");

            var deep = root;
            for (var depth = 0; depth <= maximumDepth; depth++)
            {
                deep = Path.Combine(deep, $"d{depth}");
                Directory.CreateDirectory(deep);
            }
            var resolveDeep = new object?[] { root, deep, null };
            Require(!(bool)tryResolveLocation.Invoke(null, resolveDeep)!,
                "Folder navigation exceeded its maximum descendant depth.");

            var enumerationTask = (Task)enumerateAsync.Invoke(null,
                [root, root, CancellationToken.None])!;
            enumerationTask.GetAwaiter().GetResult();
            var result = enumerationTask.GetType().GetProperty("Result")!.GetValue(enumerationTask)!;
            var entries = ((System.Collections.IEnumerable)result.GetType().GetProperty("Entries")!
                    .GetValue(result)!).Cast<object>().ToList();
            Require(entries.Count == maximumEntries &&
                    (bool)result.GetType().GetProperty("IsTruncated")!.GetValue(result)!,
                "Folder enumeration is no longer capped with an explicit truncated result.");
            Require(entries.Count > 0 && (bool)canOpenEntry.Invoke(null, [root, root, entries[0]])!,
                "A freshly enumerated root-confined entry failed its click-time revalidation.");

            var popoverType = RequireType("VeliShell.Desktop.Views.FolderPopoverWindow");
            Require(popoverType.GetMethod("CanOpen", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                    popoverType.GetMethod("NavigateAsync", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                    popoverType.GetMethod("OpenExplorer_Click", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                "The pinned-folder popover lost its guarded navigation or Explorer escape hatch.");
            var dockType = RequireType("VeliShell.Desktop.Views.DockWindow");
            Require(dockType.GetField("_folderPopover", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                    dockType.GetMethod("ShowFolderPopover", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                "The dock no longer owns a single closeable folder-popover lifecycle.");

            var desktopType = RequireType("VeliShell.Desktop.Services.DesktopIconVisibilityService");
            foreach (var method in new[]
                     {
                         "RecoverAfterOwnershipConfirmed", "Hide", "ReconcileHidden", "Restore",
                         "TryFindDesktopView", "TryWriteRecovery"
                     })
                Require(desktopType.GetMethod(method,
                            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic) is not null,
                    $"Desktop-icon safety contract is missing {method}.");

            // A rejected second process reaches App.OnExit without ever
            // owning the Explorer view. Its fresh service instance must leave
            // the primary process' crash-recovery journal untouched.
            var foreignRecoveryPath = Path.Combine(temporary, "desktop-icons-recovery.json");
            File.WriteAllText(foreignRecoveryPath, "owned-by-primary-instance");
            var nonOwnerDesktopService = Activator.CreateInstance(
                desktopType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [foreignRecoveryPath],
                culture: null) ?? throw new InvalidOperationException(
                "Could not create desktop-icon recovery service.");
            Require((bool)RequireMethod(desktopType, "Restore")
                        .Invoke(nonOwnerDesktopService, null)! &&
                    File.Exists(foreignRecoveryPath),
                "A non-owner restore removed another instance's desktop-icon recovery journal.");
            ((IDisposable)nonOwnerDesktopService).Dispose();
            Require(File.Exists(foreignRecoveryPath),
                "A non-owner dispose removed another instance's desktop-icon recovery journal.");
            var nativeType = RequireType("VeliShell.Desktop.Native.NativeMethods");
            Require(nativeType.GetMethod("FindWindowEx", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                    nativeType.GetMethod("ShowWindow", BindingFlags.Static | BindingFlags.NonPublic) is not null,
                "Temporary desktop-view control lost its narrow native window primitives.");

            var persist = typeof(VeliShell.Desktop.App).GetMethod(
                "ShouldPersistDesktopIconHidePreference",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(VeliShell.Desktop.App).FullName,
                    "ShouldPersistDesktopIconHidePreference");
            bool Persist(bool requested, bool confirmed, bool stillHidden) =>
                (bool)persist.Invoke(null, [requested, confirmed, stillHidden])!;
            Require(Persist(true, true, false) && Persist(true, false, true) &&
                    !Persist(false, true, false) && Persist(false, false, true),
                "Desktop-icon preference persistence can forget an unconfirmed hidden view.");

            var preferencesType = RequireType("VeliShell.Desktop.Views.PreferencesWindow");
            Require(RequireField(preferencesType, "DesktopIconsSwitch") is not null &&
                    RequireField(preferencesType, "DesktopIconsStatus") is not null &&
                    preferencesType.GetMethod("RestoreDesktopIcons_Click",
                        BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                "Settings no longer exposes desktop-icon opt-in and explicit restore controls.");
            Require(typeof(VeliShell.Core.Settings).GetProperty("ShowVeliShellDockItem") is not null &&
                    typeof(VeliShell.Core.Settings).GetProperty("HideDesktopIcons") is not null,
                "Reversible desktop and fixed dock-item preferences are missing from Settings.");
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TestTaskbarRecoveryPolicy()
    {
        var serviceType = RequireType("VeliShell.Desktop.Services.TaskbarVisibilityService");
        var trackedType = serviceType.GetNestedType("TrackedTaskbar", BindingFlags.NonPublic)
                          ?? throw new TypeLoadException("TrackedTaskbar");
        var track = RequireMethod(serviceType, "TrackTaskbar");
        var hasCandidates = RequireMethod(serviceType, "HasRestoreCandidatesCore");

        object NewService() => Activator.CreateInstance(serviceType, nonPublic: true)!;
        object NewTaskbar(int handle) => Activator.CreateInstance(
            trackedType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [(nint)handle, "Shell_TrayWnd", @"\\.\DISPLAY1", false],
            culture: null)!;
        bool HasCandidates(object service) => (bool)hasCandidates.Invoke(service, null)!;

        var freshHide = NewService();
        var previouslyHidden = NewTaskbar(101);
        track.Invoke(freshHide, [previouslyHidden, false]);
        Require(!HasCandidates(freshHide),
            "A taskbar that was invisible before a fresh hide became an exit-restore candidate.");
        track.Invoke(freshHide, [previouslyHidden, true]);
        Require(HasCandidates(freshHide),
            "A later visible taskbar was not upgraded to an exit-restore candidate.");

        var crashRecovery = NewService();
        RequireField(serviceType, "_recoverExistingHiddenTaskbars").SetValue(crashRecovery, true);
        track.Invoke(crashRecovery, [NewTaskbar(102), false]);
        Require(HasCandidates(crashRecovery),
            "Persisted crash recovery did not adopt an already-hidden shell taskbar.");

        var postpone = typeof(VeliShell.Desktop.App).GetMethod(
            "ShouldPostponeShutdown",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(VeliShell.Desktop.App).FullName, "ShouldPostponeShutdown");
        bool Postpone(bool restored, bool hidden, bool force) =>
            (bool)postpone.Invoke(null, [restored, hidden, force])!;
        Require(Postpone(false, true, false),
            "Normal shutdown did not wait for an unconfirmed hidden taskbar.");
        Require(!Postpone(true, true, false) && !Postpone(false, false, false) && !Postpone(false, true, true),
            "Shutdown was postponed after confirmation, without a hidden taskbar, or during fatal recovery.");

        var persist = typeof(VeliShell.Desktop.App).GetMethod(
            "ShouldPersistTaskbarHidePreference",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(VeliShell.Desktop.App).FullName,
                "ShouldPersistTaskbarHidePreference");
        bool Persist(bool hideRequested, bool confirmed, bool stillHidden) =>
            (bool)persist.Invoke(null, [hideRequested, confirmed, stillHidden])!;
        Require(Persist(true, true, true) && Persist(true, false, true),
            "A confirmed hide or an unconfirmed rollback did not retain the crash-recovery marker.");
        Require(!Persist(true, false, false) && !Persist(false, true, false) && Persist(false, false, true),
            "The persisted taskbar preference no longer mirrors unresolved hidden state.");

        var nativeType = RequireType("VeliShell.Desktop.Native.NativeMethods");
        var rectType = nativeType.GetNestedType("Rect", BindingFlags.NonPublic)
                       ?? throw new TypeLoadException("NativeMethods.Rect");
        object Rect(int left, int top, int right, int bottom)
        {
            var value = Activator.CreateInstance(rectType)!;
            rectType.GetField("Left")!.SetValue(value, left);
            rectType.GetField("Top")!.SetValue(value, top);
            rectType.GetField("Right")!.SetValue(value, right);
            rectType.GetField("Bottom")!.SetValue(value, bottom);
            return value;
        }
        int Edge(object rect, string field) => (int)rectType.GetField(field)!.GetValue(rect)!;

        var releaseEdge = RequireMethod(serviceType, "TryReleaseOnlyTaskbarEdge");
        var releaseArguments = new object[]
        {
            Rect(0, 0, 1920, 1080),
            Rect(0, 0, 1920, 1000), // a separate 40px appbar remains reserved
            Rect(0, 1040, 1920, 1080),
            Rect(0, 0, 0, 0)
        };
        Require((bool)releaseEdge.Invoke(null, releaseArguments)!,
            "A valid bottom shell taskbar edge was rejected.");
        Require(Edge(releaseArguments[3], "Bottom") == 1040,
            "Releasing the 40px shell taskbar also removed a different appbar reservation.");

        var ambiguousArguments = new object[]
        {
            Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1040),
            Rect(700, 990, 1220, 1030), Rect(0, 0, 0, 0)
        };
        Require(!(bool)releaseEdge.Invoke(null, ambiguousArguments)!,
            "An ambiguous floating taskbar geometry was allowed to change the work area.");

        var classifyHidden = RequireMethod(serviceType, "ClassifyHiddenWorkArea");
        string HiddenAction(
            bool hasSnapshot,
            object snapshotMonitor,
            object snapshotOriginal,
            object snapshotApplied,
            object currentMonitor,
            object currentWork,
            object taskbar) =>
            classifyHidden.Invoke(null,
                [hasSnapshot, snapshotMonitor, snapshotOriginal, snapshotApplied,
                    currentMonitor, currentWork, taskbar])!.ToString()!;
        var standardMonitor = Rect(0, 0, 1920, 1080);
        var menuAndTaskbarWork = Rect(0, 35, 1920, 1032);
        var menuOnlyWork = Rect(0, 35, 1920, 1080);
        var bottomTaskbar = Rect(0, 1032, 1920, 1080);
        Require(HiddenAction(true, standardMonitor, menuAndTaskbarWork, menuOnlyWork,
                    standardMonitor, menuOnlyWork, bottomTaskbar) == "Healthy",
            "An already expanded work area was scheduled for another mutation.");
        Require(HiddenAction(true, standardMonitor, menuAndTaskbarWork, menuOnlyWork,
                    standardMonitor, menuAndTaskbarWork, bottomTaskbar) == "ApplySnapshot",
            "Explorer reasserting the exact journaled taskbar reservation was not repairable.");
        Require(HiddenAction(true, Rect(0, 0, 1600, 900), Rect(0, 35, 1600, 852),
                    Rect(0, 35, 1600, 900), standardMonitor, menuAndTaskbarWork, bottomTaskbar) == "Recapture",
            "A changed monitor topology with an exact taskbar edge was not safely recaptured.");
        Require(HiddenAction(true, standardMonitor, menuAndTaskbarWork, menuOnlyWork,
                    standardMonitor, Rect(0, 35, 1920, 1000), bottomTaskbar) == "PreserveExternal",
            "A newer same-edge appbar reservation could be overwritten during reconciliation.");
        Require(HiddenAction(false, Rect(0, 0, 0, 0), Rect(0, 0, 0, 0), Rect(0, 0, 0, 0),
                    standardMonitor, menuOnlyWork, bottomTaskbar) == "PreserveExternal",
            "A work area whose taskbar edge was already released was incorrectly expanded again.");
        var shouldBroadcast = RequireMethod(serviceType, "ShouldBroadcastWorkAreaChange");
        Require(!(bool)shouldBroadcast.Invoke(null, [true])! &&
                (bool)shouldBroadcast.Invoke(null, [false])!,
            "A hidden-taskbar work-area release can still broadcast the setting change that makes Explorer reclaim its edge.");
        var reflowTargetType = serviceType.GetNestedType("WindowReflowTarget", BindingFlags.NonPublic)
                               ?? throw new TypeLoadException("TaskbarVisibilityService.WindowReflowTarget");
        Require(reflowTargetType.GetProperty("ExpectedWorkArea") is not null,
            "Queued window reflow targets are not guarded against a later restore/re-hide transition.");

        var shouldRestore = RequireMethod(serviceType, "ShouldRestoreWorkArea");
        var monitor = Rect(0, 0, 1920, 1080);
        var original = Rect(0, 0, 1920, 1040);
        var applied = Rect(0, 0, 1920, 1080);
        bool RestoreDecision(object currentMonitor, object currentWork) =>
            (bool)shouldRestore.Invoke(null, [monitor, original, applied, currentMonitor, currentWork])!;
        Require(RestoreDecision(monitor, applied),
            "An unchanged monitor with VeliShell's applied work area was not recoverable.");
        Require(!RestoreDecision(Rect(0, 0, 2560, 1440), applied),
            "A changed monitor topology could receive an obsolete work area.");
        Require(!RestoreDecision(monitor, Rect(0, 20, 1920, 1080)),
            "A newer third-party appbar work area could be overwritten during restore.");
        Require(!RestoreDecision(monitor, original),
            "An already restored work area was scheduled for another mutation.");

        Require(serviceType.GetMethod("RecoverWorkAreasAfterOwnershipConfirmed",
                    BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "Work-area crash recovery is not gated behind confirmed single-instance ownership.");

        var recoveryFileType = serviceType.GetNestedType("WorkAreaRecoveryFile", BindingFlags.NonPublic)
                               ?? throw new TypeLoadException("TaskbarVisibilityService.WorkAreaRecoveryFile");
        var recoveryEntryType = serviceType.GetNestedType("TaskbarRecoveryEntry", BindingFlags.NonPublic)
                                ?? throw new TypeLoadException("TaskbarVisibilityService.TaskbarRecoveryEntry");
        Require(recoveryFileType.GetProperty("Taskbars") is not null &&
                recoveryEntryType.GetProperty("ClassName") is not null &&
                recoveryEntryType.GetProperty("DeviceName") is not null &&
                recoveryEntryType.GetProperty("WasVisible") is not null,
            "The durable crash journal does not retain pre-hide taskbar visibility and monitor identity.");
        var tryAllMonitors = nativeType.GetMethod("TryAllMonitors", BindingFlags.Static | BindingFlags.NonPublic);
        Require(tryAllMonitors is not null,
            "Monitor capture can no longer report an incomplete enumeration as a hard failure.");
        var monitorArguments = new object?[] { null };
        Require((bool)tryAllMonitors!.Invoke(null, monitorArguments)! &&
                monitorArguments[0] is System.Collections.ICollection { Count: > 0 },
            "The complete native monitor snapshot could not be read on this Windows session.");
        Require(nativeType.GetMethod("SendMessageTimeout", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                serviceType.GetMethod("FlushWorkAreaEffects", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "Work-area changes do not expose the bounded notification/reflow transaction.");
        var retireSecondary = RequireMethod(serviceType, "ShouldRetireMissingSecondary");
        bool Retire(string className, int matches, bool primaryStable, int misses) =>
            (bool)retireSecondary.Invoke(null, [className, matches, primaryStable, misses])!;
        Require(!Retire("Shell_SecondaryTrayWnd", 0, true, 4) &&
                Retire("Shell_SecondaryTrayWnd", 0, true, 5) &&
                !Retire("Shell_SecondaryTrayWnd", 0, false, 5) &&
                !Retire("Shell_TrayWnd", 0, true, 5) &&
                !Retire("Shell_SecondaryTrayWnd", 1, true, 5),
            "Missing secondary-taskbar recovery is not bounded to a stable Explorer retry window.");

        var service = Activator.CreateInstance(serviceType, nonPublic: true)
                      ?? throw new InvalidOperationException("Could not create taskbar recovery service.");
        var notificationField = RequireField(serviceType, "_workAreaNotificationPending");
        notificationField.SetValue(service, true);
        Invoke(serviceType, service, "RollBackWorkAreasWithoutPublishingCore");
        Require(notificationField.GetValue(service) is false,
            "A partial work-area SET rollback retained an uncommitted reflow/broadcast operation.");
        ((IDisposable)service).Dispose();
    }

    private static void TestMenuBarCenters()
    {
        var notificationType = RequireType("VeliShell.Desktop.Services.NotificationCenterService");
        var notificationKindType = RequireType("VeliShell.Desktop.Services.VeliShellNotificationKind");
        var current = notificationType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!
                          .GetValue(null)
                      ?? throw new InvalidOperationException("Notification center singleton is unavailable.");
        var clear = RequireMethod(notificationType, "Clear");
        var publish = RequireMethod(notificationType, "Publish");
        var snapshot = RequireMethod(notificationType, "Snapshot");
        var markAllRead = RequireMethod(notificationType, "MarkAllRead");
        var unread = notificationType.GetProperty("UnreadCount", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new MissingMemberException(notificationType.FullName, "UnreadCount");
        var information = Enum.Parse(notificationKindType, "Information");

        clear.Invoke(current, null);
        publish.Invoke(current, ["qa-dedup", "First", "First message", information]);
        publish.Invoke(current, ["qa-dedup", "Replacement", "Replacement message", information]);
        var deduplicated = ((System.Collections.IEnumerable)snapshot.Invoke(current, null)!).Cast<object>().ToList();
        Require(deduplicated.Count == 1 &&
                string.Equals(deduplicated[0].GetType().GetProperty("Title")!.GetValue(deduplicated[0]) as string,
                    "Replacement", StringComparison.Ordinal),
            "Local notifications are not deduplicated by stable key.");

        for (var index = 0; index < 55; index++)
            publish.Invoke(current, [$"qa-{index}", $"Title {index}", "Message", information]);
        var bounded = ((System.Collections.IEnumerable)snapshot.Invoke(current, null)!).Cast<object>().Count();
        Require(bounded == 50, "The local notification center no longer enforces its 50-item bound.");
        Require((int)unread.GetValue(current)! == 50,
            "New local notifications no longer update the unread count.");
        markAllRead.Invoke(current, null);
        Require((int)unread.GetValue(current)! == 0,
            "Mark-all-read did not clear the local unread count.");
        clear.Invoke(current, null);

        var windowsSnapshotType = RequireType("VeliShell.Desktop.Services.WindowsNotificationSnapshot");
        var windowsSourceType = RequireType("VeliShell.Desktop.Services.VeliShellNotificationSource");
        var applyWindowsSnapshot = RequireMethod(notificationType, "ApplyWindowsSnapshot");
        var snapshotListType = typeof(List<>).MakeGenericType(windowsSnapshotType);
        var windowsSnapshots = (System.Collections.IList)Activator.CreateInstance(snapshotListType)!;
        var createdAt = new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.FromHours(2));
        var logo = new byte[] { 1, 2, 3, 4 };
        windowsSnapshots.Add(Activator.CreateInstance(
            windowsSnapshotType,
            [42u, "Mail", "Neue Nachricht", "Hallo", createdAt, logo])!);
        applyWindowsSnapshot.Invoke(current, [windowsSnapshots]);
        var combined = ((System.Collections.IEnumerable)snapshot.Invoke(current, null)!).Cast<object>().ToList();
        Require(combined.Count == 1 &&
                string.Equals(combined[0].GetType().GetProperty("AppDisplayName")!.GetValue(combined[0]) as string,
                    "Mail", StringComparison.Ordinal) &&
                Equals(combined[0].GetType().GetProperty("Source")!.GetValue(combined[0]),
                    Enum.Parse(windowsSourceType, "Windows")) &&
                Equals(combined[0].GetType().GetProperty("WindowsPlatformId")!.GetValue(combined[0]), 42u) &&
                ReferenceEquals(combined[0].GetType().GetProperty("AppLogo")!.GetValue(combined[0]), logo),
            "The combined notification snapshot lost Windows source/app/logo identity.");
        var emptyWindowsSnapshots = (System.Collections.IList)Activator.CreateInstance(snapshotListType)!;
        applyWindowsSnapshot.Invoke(current, [emptyWindowsSnapshots]);
        Require(!((System.Collections.IEnumerable)snapshot.Invoke(current, null)!).Cast<object>().Any(),
            "A Windows toast removed by the platform remained in the combined snapshot.");

        Require(notificationType.GetProperty("WindowsAccess", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                notificationType.GetEvent("WindowsAccessChanged", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                notificationType.GetMethod("RequestWindowsAccessAsync", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                notificationType.GetMethod("ConfigureWindowsNotificationsAsync", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                notificationType.GetMethod("DisableWindowsNotifications", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "The Windows notification consent/status lifecycle is incomplete.");
        var bridgeType = RequireType("VeliShell.Desktop.Services.WindowsNotificationListenerBridge");
        Require(bridgeType.GetMethod("Remove", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                bridgeType.GetMethod("ClearNotifications", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "Windows notifications must be removed only by explicit displayed IDs, never through a broad hidden clear wrapper.");
        var hasPackageIdentity = (bool)(bridgeType.GetProperty(
                "HasPackageIdentity", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new MissingMemberException(bridgeType.FullName, "HasPackageIdentity"));
        if (!hasPackageIdentity)
        {
            var tryCreateArguments = new object?[] { null, null };
            var tryCreate = bridgeType.GetMethod("TryCreate", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new MissingMethodException(bridgeType.FullName, "TryCreate");
            Require(!(bool)tryCreate.Invoke(null, tryCreateArguments)! &&
                    tryCreateArguments[0] is null &&
                    tryCreateArguments[1] is string { Length: > 0 },
                "The unpackaged MSI/portable build exposed a capability-gated notification listener.");
        }

        var menuType = RequireType("VeliShell.Desktop.Views.MenuBarWindow");
        Require(menuType.GetField("_controlCenter", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                menuType.GetField("_notificationCenter", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                menuType.GetMethod("ClosePanels", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "The menu bar no longer owns a single, closeable center-panel lifecycle.");
        Require(RequireType("VeliShell.Desktop.Views.ControlCenterWindow") is { } &&
                RequireType("VeliShell.Desktop.Views.NotificationCenterWindow") is { } &&
                RequireType("VeliShell.Desktop.Views.AudioDevicesWindow") is { },
            "A menu-bar center window is missing.");
        Require(menuType.GetField("_audioDevices", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                menuType.GetMethod("ShowAudioDevices", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "The menu-bar sound button no longer owns an anchored audio panel.");
        var dockType = RequireType("VeliShell.Desktop.Views.DockWindow");
        Require(dockType.GetMethod("ShowStartLauncher", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                RequireType("VeliShell.Desktop.Views.StartLauncherWindow") is { },
            "The dock Start button no longer opens its own anchored launcher.");
        var startPlacement = RequireMethod(RequireType("VeliShell.Desktop.Views.StartLauncherWindow"),
            "CalculateBounds");
        Rect StartBounds(Rect anchor, Rect workArea) =>
            (Rect)startPlacement.Invoke(null, [anchor, workArea, new Size(610, 640)])!;
        var leftStart = StartBounds(new Rect(44, 970, 52, 52), new Rect(0, 0, 1920, 1040));
        var middleStart = StartBounds(new Rect(960, 970, 52, 52), new Rect(0, 0, 1920, 1040));
        Require(leftStart.Left == 10 && middleStart.Left > 600 &&
                leftStart.Top == middleStart.Top && leftStart.Bottom < 970,
            "The launcher no longer follows its actual dock tile position above the Start button.");
        var compactStart = StartBounds(new Rect(22, 375, 52, 52), new Rect(0, 0, 430, 410));
        Require(compactStart.Left >= 0 && compactStart.Right <= 430 &&
                compactStart.Top >= 0 && compactStart.Bottom <= 410,
            "The dock-anchored launcher exceeds a compact monitor work area.");

        var placementType = RequireType("VeliShell.Desktop.Views.MenuPanelPlacement");
        var calculateBounds = RequireMethod(placementType, "CalculateBounds");
        Rect PanelBounds(Point anchor, Size desired, Rect workArea) =>
            (Rect)calculateBounds.Invoke(null, [anchor, desired, workArea])!;

        var ordinaryWorkArea = new Rect(0, 35, 1920, 1005);
        var ordinaryBounds = PanelBounds(
            new Point(1888, 35), new Size(376, 536), ordinaryWorkArea);
        Require(ordinaryBounds == new Rect(1512, 42, 376, 536),
            "Center-panel placement changed the authored size or anchor gap on a normal work area.");

        // 1366x768 at 150% scaling leaves roughly this many logical DIPs after
        // the menu-bar reservation. Both center windows must stay within it.
        var compactWorkArea = new Rect(0, 35, 911, 445);
        var compactControlBounds = PanelBounds(
            new Point(890, 35), new Size(376, 536), compactWorkArea);
        var compactNotificationBounds = PanelBounds(
            new Point(850, 35), new Size(370, 480), compactWorkArea);
        foreach (var bounds in new[] { compactControlBounds, compactNotificationBounds })
        {
            Require(bounds.Top >= compactWorkArea.Top &&
                    bounds.Bottom <= compactWorkArea.Bottom &&
                    bounds.Height > 0,
                "A center panel can extend above or below a compact high-DPI work area.");
        }
        Require(compactControlBounds.Height < 536 && compactNotificationBounds.Height < 480,
            "Compact work areas no longer constrain the usable center-panel height.");

        var controlStatusType = RequireType("VeliShell.Desktop.Services.SystemControlStatusService");
        var readStatus = controlStatusType.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic);
        Require(readStatus is not null &&
                controlStatusType.GetMethod("TrySetMasterVolume", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                controlStatusType.GetMethod("TrySetMuted", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                controlStatusType.GetMethod("ReadOutputDevices", BindingFlags.Static | BindingFlags.NonPublic) is not null &&
                controlStatusType.GetMethod("TrySetEndpointVolume", BindingFlags.Static | BindingFlags.NonPublic) is not null,
            "Control Center no longer exposes its bounded Windows status/audio contract.");
        var status = readStatus!.Invoke(null, null)!;
        var volume = status.GetType().GetProperty("MasterVolume")!.GetValue(status) as double?;
        Require(volume is null or >= 0 and <= 100,
            "The read-only system-volume probe returned a value outside 0–100 percent.");
        var outputs = (System.Collections.IEnumerable)controlStatusType
            .GetMethod("ReadOutputDevices", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var outputCount = 0;
        var defaultCount = 0;
        foreach (var output in outputs)
        {
            outputCount++;
            var outputType = output!.GetType();
            Require(outputType.GetProperty("Id")!.GetValue(output) is string { Length: > 0 } &&
                    outputType.GetProperty("Name")!.GetValue(output) is string { Length: > 0 },
                "Audio-device enumeration returned an unnamed or unidentifiable endpoint.");
            if ((bool)outputType.GetProperty("IsDefault")!.GetValue(output)!) defaultCount++;
            var endpointVolume = (double?)outputType.GetProperty("VolumePercent")!.GetValue(output);
            Require(endpointVolume is null or >= 0 and <= 100,
                "Audio-device enumeration returned a volume outside 0–100 percent.");
        }
        Require(outputCount <= 32 && defaultCount <= 1,
            "Audio-device enumeration is unbounded or marked multiple default outputs.");
    }

    private static int RenderShellPanels(Application application)
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "shell-panel-renders");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            PopulateSyntheticNotifications();
            foreach (var dark in new[] { false, true })
            {
                application.Resources.MergedDictionaries[0] = new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
                };
                var suffix = dark ? "dark" : "light";

                var controlType = RequireType("VeliShell.Desktop.Views.ControlCenterWindow");
                var control = (Window)(Activator.CreateInstance(controlType, nonPublic: true)
                              ?? throw new InvalidOperationException("Could not create Control Center."));
                RequireMethod(controlType, "RefreshStatus").Invoke(control, null);
                RenderShellPanel(control, Path.Combine(outputDirectory, $"control-center-{suffix}.png"), dark);

                var audioType = RequireType("VeliShell.Desktop.Views.AudioDevicesWindow");
                var audio = (Window)(Activator.CreateInstance(audioType, nonPublic: true)
                            ?? throw new InvalidOperationException("Could not create audio panel."));
                RequireMethod(audioType, "RefreshDevices").Invoke(audio, null);
                RenderShellPanel(audio, Path.Combine(outputDirectory, $"audio-devices-{suffix}.png"), dark);

                var notificationServiceType = RequireType("VeliShell.Desktop.Services.NotificationCenterService");
                var notificationService = notificationServiceType
                                              .GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!
                                              .GetValue(null)
                                          ?? throw new InvalidOperationException("Notification center singleton is unavailable.");
                var notificationType = RequireType("VeliShell.Desktop.Views.NotificationCenterWindow");
                var notification = (Window)(Activator.CreateInstance(
                    notificationType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: [notificationService],
                    culture: null) ?? throw new InvalidOperationException("Could not create Notification Center."));
                ((TextBlock)RequireField(notificationType, "DateLabel").GetValue(notification)!).Text =
                    "Sonntag, 4. Oktober";
                RequireMethod(notificationType, "RefreshItems").Invoke(notification, null);
                RenderShellPanel(
                    notification,
                    Path.Combine(outputDirectory, $"notification-center-{suffix}.png"),
                    dark);

                var chrome = new Window
                {
                    Width = 720,
                    Height = 320,
                    Content = CreateShellChromePreview()
                };
                RenderShellPanel(
                    chrome,
                    Path.Combine(outputDirectory, $"dock-menu-material-{suffix}.png"),
                    dark);
            }

            Console.WriteLine($"PASS: Rendered Light/Dark shell centers plus Dock/Menu Bar glass materials to {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            var notificationType = RequireType("VeliShell.Desktop.Services.NotificationCenterService");
            var notificationService = notificationType
                                          .GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!
                                          .GetValue(null);
            notificationType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(notificationService, null);
            application.Shutdown();
        }
    }

    private static void PopulateSyntheticNotifications()
    {
        var serviceType = RequireType("VeliShell.Desktop.Services.NotificationCenterService");
        var kindType = RequireType("VeliShell.Desktop.Services.VeliShellNotificationKind");
        var service = serviceType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!
                          .GetValue(null)
                      ?? throw new InvalidOperationException("Notification center singleton is unavailable.");
        RequireMethod(serviceType, "Clear").Invoke(service, null);
        RequireMethod(serviceType, "Publish").Invoke(service,
        [
            "render-update",
            "VeliShell ist aktuell",
            "Version 0.6.0 wurde erfolgreich geprüft.",
            Enum.Parse(kindType, "Success")
        ]);

        var snapshotType = RequireType("VeliShell.Desktop.Services.WindowsNotificationSnapshot");
        var snapshotListType = typeof(List<>).MakeGenericType(snapshotType);
        var snapshots = (System.Collections.IList)Activator.CreateInstance(snapshotListType)!;
        snapshots.Add(Activator.CreateInstance(snapshotType,
        [
            7001u,
            "Mail",
            "Neue Nachricht",
            "Dein Entwurf wurde gespeichert und ist bereit zur weiteren Bearbeitung.",
            DateTimeOffset.Now.AddMinutes(-4),
            null
        ])!);
        snapshots.Add(Activator.CreateInstance(snapshotType,
        [
            7002u,
            "Kalender",
            "Design-Abstimmung",
            "Heute um 18:30 Uhr",
            DateTimeOffset.Now.AddMinutes(-18),
            null
        ])!);
        RequireMethod(serviceType, "ApplyWindowsSnapshot").Invoke(service, [snapshots]);
    }

    private static FrameworkElement CreateShellChromePreview()
    {
        Brush ResourceBrush(string key) =>
            Application.Current.TryFindResource(key) as Brush
            ?? throw new InvalidOperationException($"Missing render brush '{key}'.");

        var root = new Grid { Background = Brushes.Transparent };
        var menu = new Border
        {
            Height = 30,
            Margin = new Thickness(18, 14, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = ResourceBrush("MenuBarSurface"),
            BorderBrush = ResourceBrush("GlassPanelStroke"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                Margin = new Thickness(12, 0, 12, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = "VeliShell     Datei     Fenster",
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "◉   ◌   20:26",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        root.Children.Add(menu);

        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var color in new[]
                 {
                     Color.FromRgb(56, 151, 255), Color.FromRgb(70, 211, 126),
                     Color.FromRgb(255, 187, 55), Color.FromRgb(177, 104, 255),
                     Color.FromRgb(236, 92, 116)
                 })
        {
            iconRow.Children.Add(new Border
            {
                Width = 48,
                Height = 48,
                Margin = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            });
        }

        var dockContent = new Grid();
        dockContent.Children.Add(new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(9.5),
            Background = ResourceBrush("DockMilkOverlay")
        });
        dockContent.Children.Add(new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(9.5),
            BorderBrush = ResourceBrush("DockInnerStroke"),
            BorderThickness = new Thickness(1)
        });
        dockContent.Children.Add(iconRow);
        var dock = new Border
        {
            Width = 390,
            Height = 80,
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(11),
            Background = ResourceBrush("DockSurfaceGradient"),
            BorderBrush = ResourceBrush("DockStroke"),
            BorderThickness = new Thickness(1),
            Child = dockContent,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 19,
                ShadowDepth = 4,
                Opacity = 0.22
            }
        };
        root.Children.Add(dock);
        return root;
    }

    private static void RenderShellPanel(Window window, string path, bool dark)
    {
        const int outerPadding = 30;
        var panelWidth = (int)Math.Ceiling(window.Width);
        var panelHeight = (int)Math.Ceiling(window.Height);
        var content = window.Content as FrameworkElement
                      ?? throw new InvalidOperationException($"{window.GetType().Name} has no renderable content.");
        window.Content = null;
        content.Width = panelWidth;
        content.Height = panelHeight;

        var wallpaper = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        if (dark)
        {
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(12, 23, 54), 0));
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(18, 47, 77), 0.48));
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(58, 21, 73), 1));
        }
        else
        {
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(196, 232, 255), 0));
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(226, 224, 255), 0.52));
            wallpaper.GradientStops.Add(new GradientStop(Color.FromRgb(255, 211, 239), 1));
        }

        var surface = new Grid
        {
            Width = panelWidth + outerPadding * 2,
            Height = panelHeight + outerPadding * 2,
            Background = wallpaper
        };
        TextElement.SetForeground(surface, (Brush)applicationResource("TextPrimary"));
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        surface.Children.Add(content);
        surface.Measure(new Size(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        surface.UpdateLayout();
        DrainDispatcher();

        var bitmap = new RenderTargetBitmap(
            (int)surface.Width,
            (int)surface.Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);

        surface.Children.Remove(content);
        window.Close();

        object applicationResource(string key) =>
            Application.Current.TryFindResource(key)
            ?? throw new InvalidOperationException($"Missing render resource '{key}'.");
    }

    private static void TestMenuBarReservationPolicy()
    {
        var serviceType = RequireType("VeliShell.Desktop.Services.TaskbarVisibilityService");
        var nativeType = RequireType("VeliShell.Desktop.Native.NativeMethods");
        var rectType = nativeType.GetNestedType("Rect", BindingFlags.NonPublic)
                       ?? throw new TypeLoadException("NativeMethods.Rect");
        object Rect(int left, int top, int right, int bottom)
        {
            var value = Activator.CreateInstance(rectType)!;
            rectType.GetField("Left")!.SetValue(value, left);
            rectType.GetField("Top")!.SetValue(value, top);
            rectType.GetField("Right")!.SetValue(value, right);
            rectType.GetField("Bottom")!.SetValue(value, bottom);
            return value;
        }
        int Edge(object rect, string field) => (int)rectType.GetField(field)!.GetValue(rect)!;

        var calculate = RequireMethod(serviceType, "TryCalculateTopAppBarBounds");
        var arguments = new object[]
        {
            Rect(-1920, 0, 0, 1080),
            Rect(-1880, 40, 0, 1080), // third-party left and top reservations
            35,
            Rect(0, 0, 0, 0)
        };
        Require((bool)calculate.Invoke(null, arguments)!,
            "A valid menu-bar reservation on a negative-coordinate monitor was rejected.");
        Require(Edge(arguments[3], "Left") == -1880 && Edge(arguments[3], "Top") == 40 &&
                Edge(arguments[3], "Right") == 0 && Edge(arguments[3], "Bottom") == 75,
            "The menu bar overwrote a foreign appbar reservation or ignored virtual-screen coordinates.");

        var invalid = new object[]
        {
            Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1080), 500, Rect(0, 0, 0, 0)
        };
        Require(!(bool)calculate.Invoke(null, invalid)!,
            "An implausibly large menu bar was allowed to reserve the work area.");

        Require(serviceType.GetMethod("RegisterMenuBar", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                serviceType.GetMethod("UpdateMenuBar", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                serviceType.GetMethod("ReleaseMenuBar", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "The menu bar does not expose a complete reversible appbar lifecycle.");
        Require(nativeType.GetMethod("SHAppBarMessage", BindingFlags.Static | BindingFlags.NonPublic) is not null,
            "The menu bar no longer uses the Windows appbar work-area contract.");

        var menuType = RequireType("VeliShell.Desktop.Views.MenuBarWindow");
        Require(menuType.GetProperty("WorkAreaReserved", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "The menu window cannot report whether Windows accepted its reservation.");
        Require(typeof(VeliShell.Desktop.App).GetMethod(
                    "SetMenuBarEnabledAsync", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "Menu-bar changes are no longer coordinated with hidden-taskbar recovery.");

        var appType = typeof(VeliShell.Desktop.App);
        var shouldRehide = RequireMethod(appType, "ShouldRehideTaskbarAfterMenuBarChange");
        bool Rehide(bool hideWasRequested, bool emergencyPending) =>
            (bool)shouldRehide.Invoke(null, [hideWasRequested, emergencyPending])!;
        Require(Rehide(true, false),
            "A normal menu-bar change no longer reapplies the requested hidden-taskbar state.");
        Require(!Rehide(true, true) && !Rehide(false, false) && !Rehide(false, true),
            "A pending emergency restore can be overridden by menu-bar re-hide policy.");
        var shouldUndoRehide = RequireMethod(appType, "ShouldUndoMenuBarRehide");
        bool UndoRehide(bool rehideAttempted, bool emergencyPending) =>
            (bool)shouldUndoRehide.Invoke(null, [rehideAttempted, emergencyPending])!;
        Require(UndoRehide(true, true) && !UndoRehide(true, false) &&
                !UndoRehide(false, true) && !UndoRehide(false, false),
            "An emergency arriving during the asynchronous menu re-hide no longer forces rollback.");

        // Deterministically model the critical ordering: the menu transition
        // owns the layout gate, then the emergency hotkey marks itself pending
        // before waiting for that gate. The menu transition must observe the
        // pending request and must not perform even a transient re-hide.
        using var layoutGate = new SemaphoreSlim(1, 1);
        using var menuOwnsGate = new ManualResetEventSlim();
        using var emergencyIsPending = new ManualResetEventSlim();
        var pendingEmergencyCount = 0;
        var rehideAttempts = 0;
        var emergencyRestores = 0;
        var menuTransition = Task.Run(() =>
        {
            layoutGate.Wait();
            try
            {
                menuOwnsGate.Set();
                emergencyIsPending.Wait();
                if (Rehide(true, Volatile.Read(ref pendingEmergencyCount) > 0))
                    Interlocked.Increment(ref rehideAttempts);
            }
            finally
            {
                layoutGate.Release();
            }
        });
        var emergencyRestore = Task.Run(() =>
        {
            menuOwnsGate.Wait();
            Interlocked.Increment(ref pendingEmergencyCount);
            emergencyIsPending.Set();
            layoutGate.Wait();
            try
            {
                Interlocked.Increment(ref emergencyRestores);
            }
            finally
            {
                layoutGate.Release();
                Interlocked.Decrement(ref pendingEmergencyCount);
            }
        });
        Require(Task.WaitAll([menuTransition, emergencyRestore], TimeSpan.FromSeconds(5)),
            "The menu/emergency layout interleaving deadlocked.");
        Require(rehideAttempts == 0 && emergencyRestores == 1 && pendingEmergencyCount == 0,
            "A queued emergency restore did not suppress the in-flight menu re-hide exactly once.");

        RequireField(appType, "_shellLayoutGate");
        RequireField(appType, "_emergencyRestoreRequests");
        Require(appType.GetMethod(
                    "RestoreTaskbarFromEmergencyHotkey", BindingFlags.Instance | BindingFlags.Public) is not null,
            "The emergency restore entry point is no longer available for serialized recovery.");
    }

    private static Geometry? FindClip(DependencyObject? root)
    {
        if (root is UIElement element && element.Clip is not null) return element.Clip;
        if (root is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindClip(VisualTreeHelper.GetChild(root, index)) is { } clip) return clip;
        return null;
    }

    private static UIElement? FindClippedElement(DependencyObject? root)
    {
        if (root is UIElement element && element.Clip is not null) return element;
        if (root is null) return null;
        if (root is ContentControl contentControl && contentControl.Content is DependencyObject content
            && FindClippedElement(content) is { } contentClip)
            return contentClip;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindClippedElement(VisualTreeHelper.GetChild(root, index)) is { } clipped) return clipped;
        return null;
    }

    private static void RenderMaskContactSheet(string path)
    {
        const int width = 360;
        const int height = 132;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 238, 244)), null, new Rect(0, 0, width, height));
            var x = 20d;
            foreach (var size in new[] { 32d, 58d, 96d })
            {
                var geometry = (Geometry)RequireMethod(RequireType("VeliShell.Desktop.Controls.AppIconMask"), "Create")
                    .Invoke(null, [size])!;
                drawing.PushTransform(new TranslateTransform(x, (height - size) / 2));
                drawing.DrawGeometry(
                    new LinearGradientBrush(Color.FromRgb(74, 155, 255), Color.FromRgb(86, 72, 210), 45),
                    new Pen(new SolidColorBrush(Color.FromRgb(43, 61, 105)), 1),
                    geometry);
                drawing.Pop();
                x += size + 28;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void TestSteamIconDesaturation()
    {
        var sourcePixels = new byte[] { 220, 70, 20, 255, 255, 255, 255, 77 };
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 8);
        var convert = RequireMethod(RequireType("VeliShell.Desktop.Services.SteamIconService"), "Desaturate");
        var result = (BitmapSource)convert.Invoke(null, [source])!;
        var pixels = new byte[8];
        result.CopyPixels(pixels, 8, 0);
        Require(pixels[0] == pixels[1] && pixels[1] == pixels[2] && pixels[3] == 255 &&
                pixels[4] == 255 && pixels[5] == 255 && pixels[6] == 255 && pixels[7] == 77 &&
                sourcePixels[0] == 220,
            "Steam artwork must keep its original alpha and shape while only removing color.");
    }

    private static bool RenderInstalledSteamStatusIcon(string path)
    {
        var steamService = RequireType("VeliShell.Desktop.Services.SteamClientService");
        var running = RequireMethod(steamService, "ReadRunning").Invoke(null, null);
        if (running is null) return false;
        var executable = (string)running.GetType().GetProperty("Executable")!.GetValue(running)!;
        var load = RequireMethod(RequireType("VeliShell.Desktop.Services.SteamIconService"), "FromExecutable");
        if (load.Invoke(null, [executable]) is not BitmapSource icon) return false;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Width = 160, Height = 80 };
        foreach (var background in new[]
        {
            Color.FromRgb(235, 240, 250),
            Color.FromRgb(26, 34, 53)
        })
        {
            panel.Children.Add(new Border
            {
                Width = 80,
                Height = 80,
                Background = new SolidColorBrush(background),
                Child = new Image
                {
                    Source = icon,
                    Width = 17,
                    Height = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        panel.Measure(new Size(160, 80));
        panel.Arrange(new Rect(0, 0, 160, 80));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, 320, 384, 384, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }

    private static void RenderIconSurfaceContactSheet(string path)
    {
        const int width = 410;
        const int height = 110;
        const double iconSide = 58;
        var iconServiceType = RequireType("VeliShell.Desktop.Services.IconService");
        var surfaceType = RequireType("VeliShell.Desktop.Controls.AppIconSurface");
        var iconFor = RequireMethod(iconServiceType, "For");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var id in new[] { "velishell", "files", "browser", "notes", "system" })
        {
            var source = (ImageSource)iconFor.Invoke(null, [id, "", null])!;
            var surface = (FrameworkElement)Activator.CreateInstance(
                surfaceType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [source, iconSide],
                culture: null)!;
            surface.Margin = new Thickness(6, 0, 6, 0);
            row.Children.Add(surface);
        }

        var dockBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        dockBrush.GradientStops.Add(new GradientStop(Color.FromRgb(57, 59, 69), 0));
        dockBrush.GradientStops.Add(new GradientStop(Color.FromRgb(29, 30, 37), 1));
        var dock = new Border
        {
            Width = 390,
            Height = 90,
            CornerRadius = new CornerRadius(27),
            Background = dockBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        var canvas = new Grid
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(18, 19, 24)),
            Children = { dock }
        };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderTrashSurfaceContactSheet(string path)
    {
        const int width = 220;
        const int height = 116;
        const double iconSide = 82;
        var iconServiceType = RequireType("VeliShell.Desktop.Services.IconService");
        var surfaceType = RequireType("VeliShell.Desktop.Controls.AppIconSurface");
        var iconFor = RequireMethod(iconServiceType, "For");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var id in new[] { "trash", "trash-full" })
        {
            var source = (ImageSource)iconFor.Invoke(null, [id, "", null])!;
            var surface = (FrameworkElement)Activator.CreateInstance(
                surfaceType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [source, iconSide, true],
                culture: null)!;
            surface.Margin = new Thickness(10, 0, 10, 0);
            row.Children.Add(surface);
            var plate = (FrameworkElement)RequireProperty(surfaceType, "PlateElement").GetValue(surface)!;
            Require(plate.Clip is null,
                $"The bundled {id} artwork was forced through the rounded app-tile mask.");
        }

        var canvas = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush(
                Color.FromRgb(55, 57, 66),
                Color.FromRgb(24, 25, 31),
                90),
            Children = { row }
        };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ImageSource CreateImage()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new LinearGradientBrush(Colors.DeepSkyBlue, Colors.MediumBlue, 45), null, new Rect(0, 0, 64, 64));
            context.DrawRoundedRectangle(Brushes.White, null, new Rect(12, 22, 40, 20), 5, 5);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateAlphaPlateImage(int left, int top, int right, int bottom)
    {
        const int side = 256;
        Require(left >= 0 && top >= 0 && right >= left && bottom >= top
                && right < side && bottom < side,
            $"Invalid synthetic artwork bounds {left},{top}..{right},{bottom}.");
        var stride = side * 4;
        var pixels = new byte[stride * side];
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = 208;
                pixels[offset + 1] = 112;
                pixels[offset + 2] = 32;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(side, side, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, side, side), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void RequireImageMatchesMask(
        ImageSource source,
        double normalizedPlateSide,
        double maximumMismatchFraction,
        string label)
    {
        const int sampleSide = 256;
        var actual = RenderAlpha(source, sampleSide);
        var expectedVisual = new DrawingVisual();
        var plateSide = sampleSide * normalizedPlateSide;
        var plateOrigin = (sampleSide - plateSide) / 2;
        var mask = (Geometry)RequireMethod(RequireType("VeliShell.Desktop.Controls.AppIconMask"), "Create")
            .Invoke(null, [plateSide])!;
        using (var drawing = expectedVisual.RenderOpen())
        {
            drawing.PushTransform(new TranslateTransform(plateOrigin, plateOrigin));
            drawing.DrawGeometry(Brushes.White, null, mask);
            drawing.Pop();
        }

        var expectedBitmap = new RenderTargetBitmap(
            sampleSide, sampleSide, 96, 96, PixelFormats.Pbgra32);
        expectedBitmap.Render(expectedVisual);
        var expected = CopyPixels(expectedBitmap);

        var mismatch = 0;
        for (var pixel = 0; pixel < sampleSide * sampleSide; pixel++)
        {
            var offset = pixel * 4 + 3;
            if ((actual[offset] >= 128) != (expected[offset] >= 128)) mismatch++;
        }

        var mismatchFraction = mismatch / (double)(sampleSide * sampleSide);
        Require(mismatchFraction <= maximumMismatchFraction,
            $"{label} differs from the shared p=4.37 mask in {mismatchFraction:P2} of sampled pixels " +
            $"(allowed {maximumMismatchFraction:P2}).");
    }

    private static byte[] RenderAlpha(ImageSource source, int pixelSide)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, new Rect(0, 0, pixelSide, pixelSide));
        var bitmap = new RenderTargetBitmap(pixelSide, pixelSide, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return CopyPixels(bitmap);
    }

    private static byte[] CopyPixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void TestPackagedAppFrameIdentity()
    {
        const string appId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var target = PackagedAppService.TargetForAppId(appId);
        Require(PackagedAppService.AppIdFromTarget(target) == appId &&
                PackagedAppService.AppIdFromTarget("shell:AppsFolder\\..\\ApplicationFrameHost.exe") is null,
            "Only a valid packaged-app shell target may be accepted as a dock pin.");

        var pin = LaunchService.PinFromPath(target);
        Require(pin is not null && pin.Target == target && pin.MatchProcess is null,
            "A packaged app must be pinnable by its own AUMID rather than the shared frame-host EXE.");
        var synthetic = new NativeWindow((nint)1, "Calculator", @"C:\WindowsApps\CalculatorApp.exe",
            "CalculatorApp", appId, ProcessId: 1234, DisplayName: "Calculator", IconTarget: target);
        Require(WindowCatalog.Matches(synthetic, pin!) &&
                !WindowCatalog.Matches(synthetic, pin! with { Target = PackagedAppService.TargetForAppId(
                    "Microsoft.WindowsStore_8wekyb3d8bbwe!App") }),
            "Packaged-app pins must match the exact app ID, not every Application Frame Host window.");

        // The real shell folder resolves localized labels and tile artwork.
        // A clean test VM may not have Calculator installed.
        var shellName = Task.Run(() => PackagedAppService.NameForAppId(appId)).GetAwaiter().GetResult();
        if (shellName is not null)
        {
            Require(!shellName.Equals("Application Frame Host", StringComparison.OrdinalIgnoreCase) &&
                    IconService.ForOriginalWindowsIcon("app", target) is not null,
                "An installed packaged app must expose its own localized name and icon.");
        }

        // If a packaged window is already open, exercise the same MTA path as
        // the dock's background window poll without launching other programs.
        var live = Task.Run(WindowCatalog.Read).GetAwaiter().GetResult();
        Console.WriteLine($"Packaged QA: {live.Count(window => window.AppUserModelId == appId)} Calculator window(s) found.");
        foreach (var window in live.Where(window => window.AppUserModelId == appId))
            Require(window.IconTarget == target && window.DisplayName == shellName &&
                    !window.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                    window.ProcessId > 0,
                "A live ApplicationFrameWindow must identify Calculator, not its frame host.");
    }

    private static void TestBackgroundAppTracking()
    {
        const int session = 7;
        const int pid = 4242;
        const string executable = @"C:\Apps\Example.exe";
        var processes = new Dictionary<int, BackgroundProcessIdentity>
        {
            [pid] = new(pid, 100, session, executable)
        };
        var tracker = new BackgroundAppTracker(
            id => processes.GetValueOrDefault(id), session);
        var window = new NativeWindow((nint)1, "Example", executable, "Example", ProcessId: pid);

        Require(tracker.Update([]).Count == 0,
            "A process that never owned a visible window must not appear in the menu bar.");
        Require(tracker.Update([window]).Count == 0,
            "An app with an open window must not appear as a background app.");
        var background = tracker.Update([]);
        Require(background.Count == 1 && background[0].ProcessId == pid &&
                background[0].Name == "Example" && tracker.IsStillRunning(background[0]),
            "A previously visible app still running in this session must appear after its last window closes.");
        Require(tracker.Update([window]).Count == 0,
            "Reopening an app window must hide its background indicator.");
        background = tracker.Update([]);
        processes[pid] = new(pid, 101, session, executable);
        Require(!tracker.IsStillRunning(background[0]) && tracker.Update([]).Count == 0,
            "A reused process ID must not inherit the former app's indicator or click action.");
        Require(tracker.Update([window]).Count == 0 && tracker.Update([]).Count == 1,
            "A newly observed process with the same ID may appear only after owning a new visible window.");
        const int secondPid = 4243;
        processes[secondPid] = new(secondPid, 150, session, executable);
        var secondWindow = window with { Handle = (nint)4, ProcessId = secondPid };
        tracker.Update([window, secondWindow]);
        Require(tracker.Update([window]).Count == 0,
            "Another visible window of the same app must suppress its background-process indicator.");
        Require(tracker.Update([]).Count == 1,
            "Multiple background processes for one app must collapse to one menu-bar indicator.");
        processes.Remove(secondPid);
        processes.Remove(pid);
        Require(tracker.Update([]).Count == 0,
            "The indicator must disappear when its process exits.");

        var ignored = new[] { "explorer.exe", "ApplicationFrameHost.exe", "RuntimeBroker.exe",
            "steam.exe", "steamwebhelper.exe", "msedgewebview2.exe", "svchost.exe" };
        foreach (var file in ignored)
            Require(!BackgroundAppTracker.IsEligibleWindow(new NativeWindow(
                    (nint)2, file, @"C:\Windows\" + file, Path.GetFileNameWithoutExtension(file),
                    ProcessId: 5000)),
                $"{file} must not become a generic background-app indicator.");

        processes[pid] = new(pid, 102, session + 1, executable);
        tracker.Update([window]);
        Require(tracker.Update([]).Count == 0,
            "A window from another Windows session must not create a background indicator.");

        const int webPid = 5252;
        const string browser = @"C:\Apps\chrome.exe";
        processes[webPid] = new(webPid, 200, session, browser);
        var webWindow = new NativeWindow((nint)3, "Gmail", browser, "chrome",
            WebApp: new WebAppShortcut("Gmail", @"C:\Links\Gmail.lnk", "Chrome._crx_gmail"),
            ProcessId: webPid);
        tracker.Update([webWindow]);
        Require(tracker.Update([]).Single().Name == "Gmail",
            "A previously visible web app must retain its own name, not the browser's process name.");

        const int packagedPid = 6262;
        const string packagedExe = @"C:\WindowsApps\CalculatorApp.exe";
        const string packagedAppId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var packagedTarget = PackagedAppService.TargetForAppId(packagedAppId);
        processes[packagedPid] = new(packagedPid, 250, session, packagedExe);
        var packagedWindow = new NativeWindow((nint)5, "Rechner", packagedExe, "CalculatorApp",
            packagedAppId, ProcessId: packagedPid, DisplayName: "Rechner", IconTarget: packagedTarget);
        tracker.Update([packagedWindow]);
        var packagedBackground = tracker.Update([]).Single(app => app.ProcessId == packagedPid);
        Require(packagedBackground.Name == "Rechner" && packagedBackground.IconTarget == packagedTarget &&
                packagedBackground.LaunchTarget == packagedTarget,
            "A background packaged app must keep its localized name and own icon instead of the frame host.");
    }

    private static void TestBackgroundAppCommands()
    {
        // Terminate only this test's own short-lived helper. A mismatched start
        // time must not be able to act on an otherwise valid PID.
        var executable = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var owned = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-n", "30", "127.0.0.1" }
        }) ?? throw new InvalidOperationException("Could not start the owned QA process.");
        try
        {
            var app = new BackgroundApp(owned.Id, owned.StartTime.ToUniversalTime().Ticks,
                "VeliShell QA helper", executable, executable, executable);
            Require(BackgroundAppCommands.ForceQuit(app with { StartTimeUtcTicks = app.StartTimeUtcTicks + 1 })
                    == BackgroundAppCommandResult.NotRunning && !owned.HasExited,
                "A stale menu entry must not terminate a process with a reused PID.");
            Require(BackgroundAppCommands.ForceQuit(app) == BackgroundAppCommandResult.Requested &&
                    owned.WaitForExit(5000),
                "The explicitly selected, exact process must be terminated without its process tree.");
        }
        finally
        {
            if (!owned.HasExited) owned.Kill();
        }
    }

    private static void TestWebAppAndSteamIdentityContracts()
    {
        var shortcutType = RequireType("VeliShell.Desktop.Services.ShellLinkService+ShortcutInfo");
        var webShortcut = Activator.CreateInstance(shortcutType,
            @"C:\Browser\chrome_proxy.exe", "--profile-directory=Default --app-id=abcdefghijklmnopabcdefghijklmnop",
            "Chrome._crx_abcdefghijklmnopabcdefghijklmnop", null)!;
        var regularShortcut = Activator.CreateInstance(shortcutType,
            @"C:\Browser\chrome.exe", "--new-window", null, null)!;
        var isWebApp = shortcutType.GetProperty("IsWebApp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(webShortcut);
        var isRegularApp = shortcutType.GetProperty("IsWebApp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(regularShortcut);
        Require(isWebApp is true && isRegularApp is false,
            "Browser web-app detection must require an app launch switch, not just a browser executable.");

        // A real installed browser shortcut, when available, exercises the
        // Windows property-store and icon-location interop in addition to the
        // synthetic identity cases. Clean CI machines may have no such app.
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (Directory.Exists(startMenu))
        {
            var read = RequireMethod(RequireType("VeliShell.Desktop.Services.ShellLinkService"), "Read");
            string? installedWebAppPath = null;
            try
            {
                foreach (var path in Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.AllDirectories)
                             .Where(path => path.Contains("Chrome-Apps", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Edge Apps", StringComparison.OrdinalIgnoreCase)).Take(64))
                {
                    var info = read.Invoke(null, [path]);
                    if (info is null || shortcutType.GetProperty("IsWebApp", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(info) is not true) continue;
                    Require(!string.IsNullOrWhiteSpace((string?)shortcutType.GetProperty("AppUserModelId")!
                            .GetValue(info)), "An installed web app must expose its Windows app identity.");
                    Require(File.Exists((string?)shortcutType.GetProperty("IconPath")!.GetValue(info)),
                        "An installed web app must expose its own icon file.");
                    installedWebAppPath = path;
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (installedWebAppPath is not null)
            {
                var fromIndexerThread = Task.Run(() => read.Invoke(null, [installedWebAppPath]))
                    .GetAwaiter().GetResult();
                Require(fromIndexerThread is not null &&
                        shortcutType.GetProperty("IsWebApp", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(fromIndexerThread) is true,
                    "Browser app metadata must also load from the background index thread.");
                var discover = RequireMethod(RequireType("VeliShell.Desktop.Services.SearchCatalogService"),
                    "Discover");
                var entries = ((System.Collections.IEnumerable)discover.Invoke(null, null)!).Cast<object>();
                Require(entries.Any(entry =>
                        string.Equals((string)entry.GetType().GetProperty("Target")!.GetValue(entry)!,
                            installedWebAppPath, StringComparison.OrdinalIgnoreCase) &&
                        (string)entry.GetType().GetProperty("Category")!.GetValue(entry)! == "webapp"),
                    "An installed web app must be indexed under its own shortcut and category.");
            }
        }

        var catalogType = RequireType("VeliShell.Desktop.Services.WebAppCatalog");
        var appType = RequireType("VeliShell.Desktop.Services.WebAppShortcut");
        var snapshot = catalogType.GetField("_snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = snapshot.GetValue(null);
        try
        {
            var apps = Array.CreateInstance(appType, 2);
            apps.SetValue(Activator.CreateInstance(appType, "Gmail", @"C:\Links\Gmail.lnk",
                "Chrome._crx_gmail"), 0);
            apps.SetValue(Activator.CreateInstance(appType, "YouTube", @"C:\Links\YouTube.lnk",
                "Chrome._crx_youtube"), 1);
            snapshot.SetValue(null, apps);
            var match = RequireMethod(catalogType, "Match");
            var gmail = match.Invoke(null, ["chrome._CRX_GMAIL"]);
            Require(gmail is not null && (string)appType.GetProperty("Name")!.GetValue(gmail)! == "Gmail",
                "A window AppUserModelID must resolve to the correct site title.");
            Require(match.Invoke(null, ["Chrome._crx_unknown"]) is null,
                "An unrelated browser window must not inherit another web app's identity.");
        }
        finally { snapshot.SetValue(null, previous); }

        var ownerMethod = RequireMethod(RequireType("VeliShell.Desktop.Services.WindowCatalog"),
            "SteamOwnerExecutable");
        var steamService = RequireType("VeliShell.Desktop.Services.SteamClientService");
        var steamClientType = RequireType("VeliShell.Desktop.Services.SteamClient");
        var steamCommandType = RequireType("VeliShell.Desktop.Services.SteamClientCommand");
        var createSteamStart = RequireMethod(steamService, "CreateStartInfo");
        var root = Path.Combine(Path.GetTempPath(), "VeliShellQa-" + Guid.NewGuid().ToString("N"));
        var steamRoot = Path.Combine(root, "Steam");
        var helper = Path.Combine(steamRoot, "bin", "cef", "steamwebhelper.exe");
        var unrelated = Path.Combine(root, "Other", "steamwebhelper.exe");
        var gameHelper = Path.Combine(steamRoot, "steamapps", "common", "Game", "steamwebhelper.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gameHelper)!);
            var steamExecutable = Path.Combine(steamRoot, "steam.exe");
            File.WriteAllBytes(steamExecutable, []);
            Require(string.Equals((string)ownerMethod.Invoke(null, [helper])!,
                    Path.Combine(steamRoot, "steam.exe"), StringComparison.OrdinalIgnoreCase),
                "Steam's own web helper must join its Steam entry.");
            Require(string.Equals((string)ownerMethod.Invoke(null, [unrelated])!, unrelated,
                    StringComparison.OrdinalIgnoreCase),
                "An unrelated web helper must not be joined to Steam.");
            Require(string.Equals((string)ownerMethod.Invoke(null, [gameHelper])!, gameHelper,
                    StringComparison.OrdinalIgnoreCase),
                "A helper shipped inside a Steam game must not be merged with the Steam client.");
            var client = Activator.CreateInstance(steamClientType, 123, steamExecutable)!;
            foreach (var (command, uri) in new[]
            {
                ("Open", "steam://open/main"),
                ("Settings", "steam://open/settings"),
                ("Quit", "steam://exit")
            })
            {
                var action = Enum.Parse(steamCommandType, command);
                var start = (System.Diagnostics.ProcessStartInfo)createSteamStart.Invoke(null, [client, action])!;
                Require(start.FileName == steamExecutable && !start.UseShellExecute &&
                        start.ArgumentList.SequenceEqual(["--", uri]),
                    "Steam menu commands must target the running installation directly, without shell associations or process termination.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        var runningSteam = RequireMethod(steamService, "ReadRunning").Invoke(null, null);
        if (runningSteam is not null)
        {
            var executable = (string)steamClientType.GetProperty("Executable")!.GetValue(runningSteam)!;
            Require(Path.GetFileName(executable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(executable), "A background Steam process must be visible without a top-level window.");
        }

        Require(DesktopAssembly.GetType("VeliShell.Desktop.Services.LiveWebSearchService") is null,
            "The removed live-web API client must not ship in the app.");
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Type RequireType(string name) =>
        DesktopAssembly.GetType(name, throwOnError: true)!;

    private static MethodInfo RequireMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static FieldInfo RequireField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.FullName, name);

    private static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.FullName, name);

    private static object? Invoke(Type type, object target, string name, params object?[] args) =>
        RequireMethod(type, name).Invoke(target, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireSameSize(Size actual, Size expected, string label)
    {
        const double tolerance = 0.001;
        Require(Math.Abs(actual.Width - expected.Width) <= tolerance
                && Math.Abs(actual.Height - expected.Height) <= tolerance,
            $"{label} is {actual.Width:0.###}x{actual.Height:0.###}, expected " +
            $"{expected.Width:0.###}x{expected.Height:0.###}.");
    }

    private static void RequireSameRect(Rect actual, Rect expected, string label)
    {
        const double tolerance = 0.001;
        Require(Math.Abs(actual.X - expected.X) <= tolerance
                && Math.Abs(actual.Y - expected.Y) <= tolerance
                && Math.Abs(actual.Width - expected.Width) <= tolerance
                && Math.Abs(actual.Height - expected.Height) <= tolerance,
            $"{label} is {actual}, expected {expected}.");
    }
}
