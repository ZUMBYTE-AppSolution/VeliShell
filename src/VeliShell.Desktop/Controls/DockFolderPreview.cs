using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VeliShell.Core;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

/// <summary>A glass folder shape containing up to nine actual app thumbnails.</summary>
internal sealed class DockFolderPreview : Grid
{
    private readonly UniformGrid _thumbnails;
    private readonly double _thumbnailSize;
    private string? _virtualSignature;
    private string? _physicalPath;
    private DateTime _lastPhysicalRead;
    private bool _physicalReadPending;
    private int _readGeneration;
    private DockIconStyle? _renderedIconStyle;

    internal DockFolderPreview(double side)
    {
        Width = Height = side;
        IsHitTestVisible = false;
        _thumbnailSize = Math.Max(5, (side - 12) / 3 - 1);

        var shape = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(8, side * 0.22)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };
        shape.SetResourceReference(Border.BackgroundProperty, "GlassCardSurface");
        shape.SetResourceReference(Border.BorderBrushProperty, "GlassPanelStroke");
        shape.Effect = new DropShadowEffect
        {
            BlurRadius = 9, ShadowDepth = 2, Opacity = 0.24
        };
        Children.Add(shape);

        var sheen = new Border
        {
            Margin = new Thickness(2, 2, 2, side * 0.55),
            CornerRadius = new CornerRadius(Math.Max(6, side * 0.18)),
            Opacity = 0.42,
            IsHitTestVisible = false
        };
        sheen.SetResourceReference(Border.BackgroundProperty, "DockMilkOverlay");
        Children.Add(sheen);

        _thumbnails = new UniformGrid
        {
            Rows = 3, Columns = 3,
            Margin = new Thickness(5),
            IsHitTestVisible = false
        };
        Children.Add(_thumbnails);

        Unloaded += (_, _) => ++_readGeneration;
    }

    internal void UpdatePin(Pin pin)
    {
        var iconStyle = Application.Current is App { Preferences: { } preferences }
            ? preferences.IconStyle : DockIconStyle.Mac;
        if (_renderedIconStyle != iconStyle)
        {
            _virtualSignature = null;
            _lastPhysicalRead = DateTime.MinValue;
            _renderedIconStyle = iconStyle;
        }
        if (pin.Kind == PinKind.VirtualFolder)
        {
            var entries = pin.VirtualItems ?? [];
            var signature = string.Join('|', entries.Take(9).Select(entry =>
                entry.Target + ":" + entry.Icon?.ContentSha256));
            if (signature == _virtualSignature) return;
            _virtualSignature = signature;
            ++_readGeneration;
            _physicalPath = null;
            Render(entries.Take(9).Select(entry => (entry.Target, entry.Icon)));
            return;
        }

        if (string.Equals(_physicalPath, pin.Target, StringComparison.OrdinalIgnoreCase) &&
            (_physicalReadPending || DateTime.UtcNow - _lastPhysicalRead < TimeSpan.FromSeconds(30))) return;
        _virtualSignature = null;
        _physicalPath = pin.Target;
        _lastPhysicalRead = DateTime.UtcNow;
        _ = ReadPhysicalFolderAsync(pin.Target, ++_readGeneration);
    }

    private async Task ReadPhysicalFolderAsync(string path, int generation)
    {
        _physicalReadPending = true;
        try
        {
            if (!FolderBrowserService.TryCreateRoot(path, out var root))
            {
                if (generation == _readGeneration) Render([]);
                return;
            }
            var result = await FolderBrowserService.EnumerateAsync(root, root, CancellationToken.None);
            if (generation != _readGeneration) return;
            Render(result.Entries
                .Where(entry => entry.IsOpenable && !entry.IsDirectory && IsApp(entry.Path))
                .Take(9).Select(entry => (entry.Path, (IconReference?)null)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                        ArgumentException or InvalidOperationException)
        {
            App.Log("Could not read dock folder thumbnails", exception);
            if (generation == _readGeneration) Render([]);
        }
        finally
        {
            if (generation == _readGeneration) _physicalReadPending = false;
        }
    }

    private void Render(IEnumerable<(string Target, IconReference? Icon)> entries)
    {
        _thumbnails.Children.Clear();
        foreach (var (target, icon) in entries)
        {
            _thumbnails.Children.Add(new AppIconSurface(IconService.For("app", target, icon), _thumbnailSize)
            {
                Margin = new Thickness(0.5)
            });
        }
    }

    private static bool IsApp(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);
}
