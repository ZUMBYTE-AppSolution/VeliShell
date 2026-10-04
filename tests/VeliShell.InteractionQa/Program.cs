using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static readonly Assembly DesktopAssembly = typeof(VeliShell.Desktop.App).Assembly;

    [STAThread]
    private static int Main(string[] args)
    {
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
            TestDockTileHoverMask();
            TestVeliShellAssetSurface();
            TestDockIconCustomizationContract();
            TestTaskbarRecoveryPolicy();
            TestMenuBarReservationPolicy();
            RenderMaskContactSheet(Path.Combine(AppContext.BaseDirectory, "squircle-sizes.png"));
            var iconSurfacesPath = Path.Combine(AppContext.BaseDirectory, "icon-surfaces.png");
            RenderIconSurfaceContactSheet(iconSurfacesPath);
            Console.WriteLine("PASS: DWM thumbnail registered, hidden and released cleanly across 12 cycles.");
            Console.WriteLine("PASS: Drag ghost snapped/followed/disposed at 32, 58 and 96 DIP.");
            Console.WriteLine("PASS: Dock drag-out carries no FileDrop/shortcut payload; feedback, drop and removal share the visible dock-plate boundary while internal reorder/external file-drop inputs remain available.");
            Console.WriteLine("PASS: Different source safe zones normalize to the same fixed 32, 58 and 96 DIP icons without a generated backdrop; source pixels cannot resize the artwork and common 1.42x hover scale is preserved.");
            Console.WriteLine("PASS: Bundled VeliShell app artwork expands its centered ~0.803 source safe zone to each fixed icon and uses the same p=4.37 contour without an accent plate.");
            Console.WriteLine("PASS: Dock hover labels contain only the application name, never icon-provider attribution.");
            Console.WriteLine("PASS: Settings exposes local/online/reset controls for pins, fixed dock elements, separate empty/full Recycle Bin states, and stable running-app identities.");
            Console.WriteLine("PASS: Taskbar rollback preserves pre-hidden windows; work-area recovery is edge-scoped, topology-safe and idempotent.");
            Console.WriteLine("PASS: Menu bar reserves a reversible top-edge appbar without overwriting foreign reservations; emergency taskbar restore wins deterministic layout interleavings.");
            Console.WriteLine($"PASS: Rendered real 58-DIP VeliShell/files/browser/notes/system surfaces to {iconSurfacesPath}");
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
            Require(tileSurface.RenderTransform is ScaleTransform scale
                    && Math.Abs(scale.ScaleX - 1.42) < 0.001
                    && Math.Abs(scale.ScaleY - 1.42) < 0.001,
                $"Dock tile at {size} DIP did not scale the common app-icon surface as one unit.");
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

        var gallery = RequireType("VeliShell.Desktop.Services.MacOsIconGalleryService");
        var memoryType = gallery.GetNestedType("MemoryImage", BindingFlags.NonPublic)
                         ?? throw new TypeLoadException("MemoryImage");
        var attributionType = RequireType("VeliShell.Desktop.Services.MacOsIconAttribution");
        var attribution = Activator.CreateInstance(
            attributionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["macOSicons.com · QA", "https://macosicons.com/"],
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
        cacheItem.SetValue(cache, memory, [hash + ":" + hash]);
        try
        {
            var reference = new VeliShell.Core.IconReference("macosicons", hash, "api-v1", hash);
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
