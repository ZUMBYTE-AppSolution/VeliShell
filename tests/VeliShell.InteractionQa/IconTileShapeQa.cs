using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

internal static partial class Program
{
    private static void VerifyExistingIconTiles(Action<bool, string> check, string output)
    {
        var tiles = new List<(string Name, ImageSource Source)>();
        foreach (var kind in new[] { "square", "rounded", "rounder", "continuous" })
        foreach (var pixels in new[] { 16, 32, 100, 512 })
            tiles.Add(($"{kind}-{pixels}", IconTileFixture(kind, pixels)));
        tiles.Add(("asymmetric-padding", IconTileFixture("rounded", body: new Rect(4, 22, 66, 66))));
        tiles.Add(("soft-shadow", IconTileFixture("rounded", shadow: true)));
        tiles.Add(("translucent-tile", IconTileFixture("rounded", opacity: 180)));
        tiles.Add(("rectangular-canvas", IconTileFixture("wide-canvas")));
        tiles.Add(("VeliShell-artwork", IconService.Resolve("velishell", style: DockIconStyle.Mac).Source));
        tiles.Add(("authored-fallback", IconService.Resolve("qa-shaped-fallback").Source));

        foreach (var tile in tiles)
        {
            var appearance = AppIconAppearance.For(tile.Source);
            check(appearance.HasTileShape, "Existing tile was not recognized: " + tile.Name);
            check(ReferenceEquals(appearance, AppIconAppearance.For(tile.Source)), "Tile shape was not cached: " + tile.Name);
            foreach (var side in new[] { 24d, 32d, 58d, 96d })
            {
                var surface = new AppIconSurface(tile.Source, side);
                LayoutIcon(surface, side);
                var width = surface.IconImage.Width;
                var left = Canvas.GetLeft(surface.IconImage); var top = Canvas.GetTop(surface.IconImage);
                var before = new[] { 96d, 120d, 144d, 192d }.Select(dpi => RenderIconPixels(surface, side, dpi)).ToArray();
                surface.SetAdaptiveBackground(true);
                LayoutIcon(surface, side);
                check(!surface.HasAdaptiveBackground && ((Panel)surface.PlateElement).Background is null && surface.IconImage.Effect is null,
                    $"Extra plate/halo on {tile.Name} at {side} DIP.");
                check(Math.Abs(width - surface.IconImage.Width) < .001 && Math.Abs(left - Canvas.GetLeft(surface.IconImage)) < .001 &&
                    Math.Abs(top - Canvas.GetTop(surface.IconImage)) < .001, $"Existing tile shrank or moved: {tile.Name} at {side} DIP.");
                check(Math.Abs(appearance.NormalizedArtworkBounds.Width * surface.IconImage.Width - side) < .001,
                    $"Existing tile does not fill its configured dock size: {tile.Name} at {side} DIP.");
                var index = 0;
                foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
                    check(before[index++].SequenceEqual(RenderIconPixels(surface, side, dpi)),
                        $"Background setting changed existing tile pixels: {tile.Name}, {side} DIP, {dpi} DPI.");
                var ghost = (AppIconSurface)RequireMethod(typeof(DragGhostWindow), "CreateAppArtwork")
                    .Invoke(null, [tile.Source, side, false, true])!;
                check(!ghost.HasAdaptiveBackground && ghost.IconImage.Effect is null, "Drag ghost wrapped an existing tile: " + tile.Name);
            }
        }

        foreach (var kind in new[] { "circle", "circle-shadow", "diamond", "folder", "outline", "portrait", "landscape", "four-panes", "glyph" })
        {
            var source = IconTileFixture(kind);
            check(!AppIconAppearance.For(source).HasTileShape, "Freeform artwork misclassified as a tile: " + kind);
            var surface = new AppIconSurface(source, 58);
            surface.SetAdaptiveBackground(true);
            check(surface.HasAdaptiveBackground, "Freeform artwork lost its optional background: " + kind);
        }
        var glyph = IconBackgroundGlyph(Colors.DodgerBlue);
        var reused = new AppIconSurface(glyph, 58);
        reused.SetAdaptiveBackground(true);
        check(reused.HasAdaptiveBackground, "Freeform fixture must start with its optional background.");
        reused.UpdateSource(tiles[0].Source);
        check(!reused.HasAdaptiveBackground && reused.IconImage.Effect is null &&
            Math.Abs(AppIconAppearance.For(tiles[0].Source).NormalizedArtworkBounds.Width * reused.IconImage.Width - 58) < .001,
            "Changing a running app icon from glyph to tile retained its old inset or halo.");
        reused.UpdateSource(glyph);
        check(reused.HasAdaptiveBackground, "Changing a running app icon back to freeform lost the enabled setting.");

        foreach (var dark in new[] { false, true }) RenderTileShapeSheet(output, dark);
    }

    private static BitmapSource IconTileFixture(string kind, int pixels = 100, Rect? body = null, bool shadow = false, byte opacity = 255)
    {
        var wide = kind == "wide-canvas";
        var bounds = body ?? (wide ? new Rect(44, 8, 70, 70) : new Rect(10, 10, 80, 80));
        if (kind == "portrait") bounds = new Rect(26, 10, 48, 80);
        if (kind == "landscape") bounds = new Rect(10, 26, 80, 48);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(pixels / 100d, pixels / 100d));
            if (shadow || kind == "circle-shadow")
            {
                var outside = bounds; outside.Inflate(5, 5);
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), null, outside, 22, 22);
            }
            var colour = new SolidColorBrush(Color.FromArgb(opacity, 35, 135, 230));
            switch (kind)
            {
                case "circle": case "circle-shadow":
                    drawing.DrawEllipse(colour, null, new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2); break;
                case "diamond":
                    var diamond = new StreamGeometry();
                    using (var path = diamond.Open())
                    {
                        path.BeginFigure(new Point(bounds.Left + bounds.Width / 2, bounds.Top), true, true);
                        path.PolyLineTo([new(bounds.Right, bounds.Top + bounds.Height / 2), new(bounds.Left + bounds.Width / 2, bounds.Bottom), new(bounds.Left, bounds.Top + bounds.Height / 2)], true, false);
                    }
                    drawing.DrawGeometry(colour, null, diamond); break;
                case "folder":
                    drawing.DrawRoundedRectangle(colour, null, new Rect(bounds.Left, bounds.Top, bounds.Width * .46, bounds.Height * .4), 4, 4);
                    drawing.DrawRoundedRectangle(colour, null, new Rect(bounds.Left, bounds.Top + bounds.Height * .23, bounds.Width, bounds.Height * .77), 4, 4); break;
                case "outline": drawing.DrawRoundedRectangle(null, new Pen(colour, 5), bounds, 16, 16); break;
                case "continuous":
                    drawing.PushTransform(new TranslateTransform(bounds.Left, bounds.Top));
                    drawing.DrawGeometry(colour, null, AppIconMask.Create(bounds.Width)); drawing.Pop(); break;
                case "rounded": case "wide-canvas": case "rounder":
                    var radius = bounds.Width * (kind == "rounder" ? .4 : .23);
                    drawing.DrawRoundedRectangle(colour, null, bounds, radius, radius); break;
                case "four-panes":
                    foreach (var x in new[] { 0d, .55 }) foreach (var y in new[] { 0d, .55 })
                        drawing.DrawRectangle(colour, null, new Rect(bounds.Left + bounds.Width * x, bounds.Top + bounds.Height * y, bounds.Width * .45, bounds.Height * .45));
                    break;
                case "glyph": drawing.DrawGeometry(colour, null, Geometry.Parse("M18,18 L34,18 L50,59 L66,18 L82,18 L57,82 L43,82 Z")); break;
                default: drawing.DrawRectangle(colour, null, bounds); break;
            }
            drawing.Pop();
        }
        var image = new RenderTargetBitmap(wide ? (int)(pixels * 1.6) : pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static void LayoutIcon(AppIconSurface surface, double side)
    { surface.Measure(new Size(side, side)); surface.Arrange(new Rect(0, 0, side, side)); surface.UpdateLayout(); }

    private static byte[] RenderIconPixels(AppIconSurface surface, double side, double dpi)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(side * dpi / 96), (int)Math.Ceiling(side * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(surface);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }

    private static void RenderTileShapeSheet(string output, bool dark)
    {
        var samples = new[] { "square", "rounded", "continuous", "circle", "glyph", "folder", "four-panes" };
        var panel = new StackPanel { Width = 980, Background = new SolidColorBrush(dark ? Color.FromRgb(23, 28, 43) : Color.FromRgb(243, 245, 251)) };
        var ink = dark ? Brushes.White : Brushes.Black;
        panel.Children.Add(new TextBlock { Text = "Existing tiles stay full-size · backgrounds off / on", Foreground = ink, Margin = new Thickness(24), FontSize = 20 });
        foreach (var enabled in new[] { false, true })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 24) };
            foreach (var kind in samples)
            {
                var item = new StackPanel { Width = 140 };
                var surface = new AppIconSurface(IconTileFixture(kind), 82, freeform: kind == "folder"); surface.SetAdaptiveBackground(enabled);
                item.Children.Add(surface);
                item.Children.Add(new TextBlock { Text = kind, Foreground = ink, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
                row.Children.Add(item);
            }
            panel.Children.Add(row);
        }
        panel.Measure(new Size(980, double.PositiveInfinity)); panel.Arrange(new Rect(new Point(), panel.DesiredSize)); panel.UpdateLayout();
        SaveIconBackgroundRender(panel, Path.Combine(output, "tile-shapes-" + (dark ? "dark" : "light") + ".png"), 980, (int)Math.Ceiling(panel.ActualHeight));
    }
}
