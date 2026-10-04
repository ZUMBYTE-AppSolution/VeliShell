using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

internal sealed class DockTile : Button
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly AppIconSurface _iconSurface;
    private readonly Ellipse _indicator;
    internal DockItem Item { get; set; }
    internal double IconSize { get; }

    internal DockTile(DockItem item, double size)
    {
        Item = item;
        IconSize = size;
        Width = size + 22;
        Height = size + 22;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Style = (Style)FindResource("DockButton");
        ToolTip = TooltipFor(item);
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Top);
        AutomationProperties.SetName(this, item.Name);
        UpdateAutomationStatus(item);
        var grid = new Grid { Width = size + 22, Height = size + 22, ClipToBounds = false };
        var iconSource = IconService.For(item.IconId, item.Target, item.Icon);
        _iconSurface = new AppIconSurface(iconSource, size, UsesFreeformArtwork(item))
        {
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _scale
        };
        grid.Children.Add(_iconSurface);
        _indicator = new Ellipse
        {
            Width = 4,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };
        _indicator.SetResourceReference(Shape.FillProperty, "DockIndicator");
        grid.Children.Add(_indicator);
        Content = grid;
        UpdateIndicator();
    }

    internal void UpdateIndicator() => _indicator.Visibility = Item.Windows.Count > 0 || Item.Key == "velishell" ? Visibility.Visible : Visibility.Hidden;

    internal void UpdateItem(DockItem item)
    {
        Item = item;
        ToolTip = TooltipFor(item);
        AutomationProperties.SetName(this, item.Name);
        UpdateAutomationStatus(item);
        var iconSource = IconService.For(item.IconId, item.Target, item.Icon);
        _iconSurface.UpdateSource(iconSource, UsesFreeformArtwork(item));
        UpdateIndicator();
    }

    private static bool UsesFreeformArtwork(DockItem item) => item.Key == "trash";

    private void UpdateAutomationStatus(DockItem item)
    {
        var active = item.Windows.Count > 0 || item.Key == "velishell";
        AutomationProperties.SetItemStatus(this,
            active ? LocalizationService.Current.Get("Dock.Active") : item.IsUtility ? "" : LocalizationService.Current.Get("Dock.NoActiveWindow"));
    }

    // Dock hover mirrors macOS: the floating label identifies the app only.
    // Provider/legal attribution remains available in Settings and the explicit
    // context menu, but is never mixed into the hover label.
    private static string TooltipFor(DockItem item) => item.Name;

    internal void SetScale(double value, bool animate)
    {
        if (animate)
        {
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        else
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = value;
        }
    }
}
