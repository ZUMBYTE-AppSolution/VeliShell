using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

internal static partial class Program
{
    private static int VerifyAdaptiveIconBackgrounds()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        var settings = new Settings { Pins = [], ShowRunningApps = false, AdaptiveIconBackgrounds = true };
        typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(app, settings);
        using var themes = new ThemeService();
        typeof(VeliShell.Desktop.App).GetProperty("Themes")!.SetValue(app, themes);
        var output = Path.Combine(AppContext.BaseDirectory, "adaptive-icon-renders");
        Directory.CreateDirectory(output);
        var count = 0;
        void Check(bool pass, string message) { Require(pass, message); count++; }
        try
        {
            foreach (var colour in new[] { Colors.White, Colors.Black, Colors.DodgerBlue, Colors.Crimson, Colors.LimeGreen, Colors.Gray })
            {
                var source = IconBackgroundGlyph(colour);
                var background = AppIconBackground.For(source);
                Check(ReferenceEquals(background, AppIconBackground.For(source)), "Palette was recalculated instead of cached.");
                Check(background.HasArtwork && background.Brush.IsFrozen, "No immutable palette for visible source.");
                foreach (var stop in background.Brush.GradientStops)
                    Check(AppIconBackground.Contrast(AppIconBackground.Luminance(colour), AppIconBackground.Luminance(stop.Color)) >= 3,
                        "A monochrome foreground lacks contrast against a gradient endpoint: " + colour + " / " + stop.Color + " / " + AppIconBackground.Contrast(AppIconBackground.Luminance(colour), AppIconBackground.Luminance(stop.Color)));
                foreach (var side in new[] { 24d, 32d, 58d, 96d })
                {
                    var surface = new AppIconSurface(source, side);
                    var originalWidth = surface.IconImage.Width;
                    Check(((Panel)surface.PlateElement).Background is null, "Off must retain the existing transparent artwork.");
                    surface.SetAdaptiveBackground(true);
                    surface.Measure(new Size(side, side)); surface.Arrange(new Rect(0, 0, side, side)); surface.UpdateLayout();
                    Check(surface.HasAdaptiveBackground && surface.PlateElement.RenderSize == new Size(side, side), "Background changed configured dock size.");
                    Check(surface.PlateElement.Clip is StreamGeometry { IsFrozen: true }, "Background and foreground must share the continuous icon mask.");
                    Check(Math.Abs(surface.IconImage.Width / originalWidth - .78) < .001 && ReferenceEquals(surface.IconImage.Source, source), "Artwork is not uniformly inset or its source was changed.");
                    foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(side * dpi / 96), (int)Math.Ceiling(side * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(surface);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        Check(pixels[3] == 0, "A square background or halo leaks outside the shared rounded corner.");
                    }
                    surface.SetAdaptiveBackground(false);
                    Check(!surface.HasAdaptiveBackground && ((Panel)surface.PlateElement).Background is null && surface.IconImage.Effect is null && Math.Abs(surface.IconImage.Width - originalWidth) < .001,
                        "Disabling the setting must fully restore the previous artwork layout.");
                }
                var freeform = new AppIconSurface(source, 58, true); freeform.SetAdaptiveBackground(true);
                Check(!freeform.HasAdaptiveBackground && freeform.PlateElement.Clip is null && freeform.IconImage.Width == 58, "Freeform trash/folder artwork was wrapped or resized.");
            }
            var transparent = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Pbgra32, null, new byte[64 * 64 * 4], 64 * 4);
            Check(!AppIconBackground.For(transparent).HasArtwork, "Invisible artwork must not invent a coloured icon.");
            var transparentRgb = new byte[64 * 64 * 4];
            for (var i = 0; i < transparentRgb.Length; i += 4) { transparentRgb[i] = 255; transparentRgb[i + 2] = 255; }
            Check(!AppIconBackground.Analyze(transparentRgb).HasArtwork, "Invisible RGB values polluted the palette.");
            Check(!AppIconBackground.Analyze([1, 2, 3]).HasArtwork, "Partial pixel input was not bounded.");

            // Seed only this QA process's memory cache: no AppData writes, real
            // icon imports, network requests or user-cache cleanup.
            var custom = IconBackgroundGlyph(Colors.Coral);
            var hash = new string('e', 64);
            var reference = new IconReference(CustomIconService.Provider, hash, CustomIconService.FormatVersion, hash);
            var memory = (ConcurrentDictionary<string, BitmapSource>)typeof(CustomIconService).GetField("MemoryCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            memory[hash] = custom;
            try
            {
                foreach (var style in Enum.GetValues<DockIconStyle>())
                {
                    settings.IconStyle = style;
                    themes.Apply(Appearance.Dark);
                    var native = new DockItem { Key = "pin:qa", Name = "Native", Target = "", IconId = "qa-native" };
                    var tile = new DockTile(native, 58, true);
                    Check(tile.HasAdaptiveBackground, "Native fallback not styled in " + style);
                    var replaced = new DockItem { Key = native.Key, Name = "Custom", IconId = native.IconId, Icon = reference };
                    var resolved = IconService.Resolve(replaced.IconId, replaced.Target, reference, style);
                    Check(!resolved.IsOriginal && ReferenceEquals(resolved.Source, custom), "Custom artwork provenance lost in " + style);
                    tile.UpdateItem(replaced, true);
                    Check(!tile.HasAdaptiveBackground, "Chosen artwork received an automatic backdrop in " + style);
                    tile.UpdateItem(native, true); Check(tile.HasAdaptiveBackground, "Resetting an override did not restore the optional background.");
                    tile.UpdateItem(native, false); Check(!tile.HasAdaptiveBackground, "Live preference disable did not affect an existing tile.");
                    var bundled = new DockTile(new DockItem { Key = "pin:files", Name = "Files", IconId = "files", Target = "explorer.exe" }, 58, true);
                    Check(bundled.HasAdaptiveBackground == (style == DockIconStyle.Windows), "Authored Mac / original Windows icon eligibility is incorrect.");
                    foreach (var id in new[] { "velishell", "trash", "start", "overflow" })
                        Check(!new DockTile(new DockItem { Key = id, Name = id, IconId = id }, 58, true).HasAdaptiveBackground, "Authored utility artwork was changed.");
                    Check(!new DockTile(new DockItem { Key = "pin:folder", Name = "Folder", IconId = "folder-shortcut" }, 58, true).HasAdaptiveBackground, "Folder source shape was changed.");
                }
            }
            finally { memory.TryRemove(hash, out _); }

            var ghostArtwork = (AppIconSurface)typeof(DragGhostWindow).GetMethod("CreateAppArtwork", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [custom, 58d, false, true])!;
            Check(ghostArtwork.HasAdaptiveBackground && ((Panel)ghostArtwork.PlateElement).Background == AppIconBackground.For(custom).Brush,
                "Drag ghost does not use the same palette / shared surface.");
            settings.IconStyle = DockIconStyle.Mac;
            foreach (var language in new[] { UiLanguage.German, UiLanguage.English })
            foreach (var dark in new[] { false, true })
            {
                settings.Language = language;
                themes.Apply(dark ? Appearance.Dark : Appearance.Light);
                LocalizationService.Current.Apply(language);
                var view = new PreferencesWindow(app);
                typeof(PreferencesWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [2]);
                var toggle = (CheckBox)view.FindName("AdaptiveIconBackgroundsSwitch");
                Check(toggle.IsChecked == true && toggle.Content is string label && label.Length > 12, "Option label / checked state missing in settings.");
                var card = (Border)((StackPanel)toggle.Parent).Parent;
                ((Panel)card.Parent).Children.Remove(card);
                TextElement.SetForeground(card, view.Foreground);
                var wrapper = new Grid { Width = 560, Background = (Brush)app.FindResource("WindowSurface"), Children = { card } };
                wrapper.Measure(new Size(560, double.PositiveInfinity));
                wrapper.Arrange(new Rect(new Point(), wrapper.DesiredSize)); wrapper.UpdateLayout();
                Check(toggle.ActualHeight >= 20 && toggle.ActualWidth >= 200, $"Background option was clipped or lost its label: {toggle.ActualWidth} × {toggle.ActualHeight}, card {card.DesiredSize}.");
                SaveIconBackgroundRender(wrapper, Path.Combine(output, $"settings-{language}-{(dark ? "dark" : "light")}.png"), 560, (int)Math.Ceiling(wrapper.ActualHeight));
                Check(new WindowInteropHelper(view).Handle == 0, "Settings QA created a desktop window.");
                view.Close();
                if (language == UiLanguage.German) RenderIconBackgroundSheet(output, dark);
            }
            // Existing default behaviour stays covered without opening any UI.
            TestDockTileHoverMask(); TestVeliShellAssetSurface();
            Console.WriteLine($"PASS: {count} adaptive icon checks, original/custom provenance in both styles, persistent opt-in, shared drag artwork, full-size masks, 24–96 DIP / 100–200% scaling.");
            Console.WriteLine("PASS: four real settings-card renders plus light/dark icon sheets; no windows shown, input, real preferences, taskbar, native frames or app processes changed.");
            Console.WriteLine(output);
            app.Shutdown(); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL adaptive icon background QA: " + error); app.Shutdown(1); return 1; }
    }

    private static BitmapSource IconBackgroundGlyph(Color colour)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawGeometry(new SolidColorBrush(colour), null, Geometry.Parse("M18,18 L34,18 L50,59 L66,18 L82,18 L57,82 L43,82 Z"));
        var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static void RenderIconBackgroundSheet(string output, bool dark)
    {
        var samples = new List<(string Label, ImageSource Source)>
        {
            ("White", IconBackgroundGlyph(Colors.White)), ("Black", IconBackgroundGlyph(Colors.Black)),
            ("Blue", IconBackgroundGlyph(Colors.DodgerBlue)), ("Red", IconBackgroundGlyph(Colors.Crimson)),
            ("Green", IconBackgroundGlyph(Colors.LimeGreen)),
            ("Explorer", IconService.Resolve("files", "explorer.exe", style: DockIconStyle.Windows).Source),
            ("Editor", IconService.Resolve("notes", "notepad.exe", style: DockIconStyle.Windows).Source)
        };
        var canvas = new StackPanel { Width = 960, Background = (Brush)Application.Current.FindResource("WindowSurface"), Margin = new Thickness(0) };
        canvas.Children.Add(new TextBlock { Text = "Adaptive icon backgrounds · " + (dark ? "dark" : "light") + " · off / on", Foreground = (Brush)Application.Current.FindResource("TextPrimary"), FontSize = 20, Margin = new Thickness(24) });
        foreach (var enabled in new[] { false, true })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 10, 16, 18) };
            foreach (var sample in samples)
            {
                var item = new StackPanel { Width = 130 };
                var icon = new AppIconSurface(sample.Source, 82); icon.SetAdaptiveBackground(enabled);
                item.Children.Add(icon);
                item.Children.Add(new TextBlock { Text = sample.Label, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Foreground = (Brush)Application.Current.FindResource("TextPrimary") });
                row.Children.Add(item);
            }
            canvas.Children.Add(row);
        }
        canvas.Measure(new Size(960, double.PositiveInfinity)); canvas.Arrange(new Rect(new Point(), canvas.DesiredSize)); canvas.UpdateLayout();
        SaveIconBackgroundRender(canvas, Path.Combine(output, "icons-" + (dark ? "dark" : "light") + ".png"), 960, (int)Math.Ceiling(canvas.ActualHeight));
    }

    private static void SaveIconBackgroundRender(FrameworkElement content, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
