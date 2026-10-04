using System.Reflection;
using System.IO;
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
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
            TestDockTileHoverMask();
            TestVeliShellAssetSurface();
            TestTaskbarRecoveryPolicy();
            RenderMaskContactSheet(Path.Combine(AppContext.BaseDirectory, "squircle-sizes.png"));
            var iconSurfacesPath = Path.Combine(AppContext.BaseDirectory, "icon-surfaces.png");
            RenderIconSurfaceContactSheet(iconSurfacesPath);
            Console.WriteLine("PASS: DWM thumbnail registered, hidden and released cleanly across 12 cycles.");
            Console.WriteLine("PASS: Drag ghost snapped/followed/disposed at 32, 58 and 96 DIP.");
            Console.WriteLine("PASS: Different source safe zones normalize to the same fixed 32, 58 and 96 DIP plates; source pixels cannot resize the dock icon and common 1.42x hover scale is preserved.");
            Console.WriteLine("PASS: Bundled VeliShell app artwork expands its centered ~0.803 source safe zone to each fixed plate and uses the same p=4.37 contour.");
            Console.WriteLine("PASS: Taskbar rollback preserves pre-hidden windows and normal exit waits for confirmed recovery.");
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
                    args: [sample.Source, size, null],
                    culture: null)!;
                surface.Measure(new Size(size, size));
                surface.Arrange(new Rect(0, 0, size, size));
                surface.UpdateLayout();

                var accentBackground = (FrameworkElement)RequireProperty(surfaceType, "AccentBackground")
                    .GetValue(surface)!;
                var iconImage = (FrameworkElement)RequireProperty(surfaceType, "IconImage")
                    .GetValue(surface)!;
                var plate = (FrameworkElement)RequireProperty(surfaceType, "PlateElement")
                    .GetValue(surface)!;
                var normalizedBounds = (Rect)RequireProperty(surfaceType, "NormalizedArtworkBounds")
                    .GetValue(surface)!;

                RequireSameRect(normalizedBounds, sample.Bounds,
                    $"{sample.Label} normalized source bounds at {size} DIP");
                Require(ReferenceEquals(VisualTreeHelper.GetParent(accentBackground), plate)
                        && ReferenceEquals(VisualTreeHelper.GetParent(iconImage), plate),
                    $"{sample.Label} at {size} DIP does not keep background and image in the common plate.");
                RequireSameSize(surface.RenderSize, new Size(size, size),
                    $"{sample.Label} surface at {size} DIP");
                RequireSameSize(plate.RenderSize, new Size(size, size),
                    $"{sample.Label} fixed plate at {size} DIP");
                RequireSameSize(accentBackground.RenderSize, new Size(size, size),
                    $"{sample.Label} accent background at {size} DIP");

                Require(plate.ClipToBounds && plate.Clip is StreamGeometry && plate.Clip.IsFrozen,
                    $"{sample.Label} at {size} DIP does not use the shared frozen mask.");
                RequireSameRect(plate.Clip.Bounds, new Rect(0, 0, size, size),
                    $"{sample.Label} mask at {size} DIP");

                var plateBounds = plate.TransformToAncestor(surface)
                    .TransformBounds(new Rect(new Point(), plate.RenderSize));
                RequireSameRect(plateBounds, new Rect(0, 0, size, size),
                    $"{sample.Label} output plate at {size} DIP");

                var backgroundBounds = accentBackground.TransformToAncestor(plate)
                    .TransformBounds(new Rect(new Point(), accentBackground.RenderSize));
                RequireSameRect(backgroundBounds, new Rect(0, 0, size, size),
                    $"{sample.Label} accent bounds at {size} DIP");

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
            var tile = (FrameworkElement)Activator.CreateInstance(
                tileType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [item, size],
                culture: null)!;
            tile.Measure(new Size(size + 22, size + 22));
            tile.Arrange(new Rect(0, 0, size + 22, size + 22));
            tile.UpdateLayout();
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
                args: [source, size, null],
                culture: null)!;
            surface.Measure(new Size(size, size));
            surface.Arrange(new Rect(0, 0, size, size));
            surface.UpdateLayout();

            var accentBackground = (FrameworkElement)RequireProperty(surfaceType, "AccentBackground")
                .GetValue(surface)!;
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
            RequireSameSize(accentBackground.RenderSize, new Size(size, size),
                $"VeliShell app accent background at {size} DIP");
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
            args: [(nint)handle, "Shell_TrayWnd", false],
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
                args: [source, iconSide, null],
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
