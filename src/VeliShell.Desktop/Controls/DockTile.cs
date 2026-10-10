using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VeliShell.Core;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

internal sealed class DockTile : Button
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _launchOffset = new();
    private readonly AppIconSurface _iconSurface;
    private readonly DockFolderPreview? _folderPreview;
    private readonly TextBlock? _folderName;
    private readonly Border _folderDropHalo;
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
        _folderDropHalo = new Border
        {
            Width = size + 14, Height = size + 14,
            CornerRadius = new CornerRadius(Math.Max(11, size * 0.23)),
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        _folderDropHalo.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _folderDropHalo.SetResourceReference(Border.BorderBrushProperty, "AccentStroke");
        grid.Children.Add(_folderDropHalo);
        var iconSource = IconService.For(item.IconId, item.Target, item.Icon);
        var iconTransforms = new TransformGroup();
        iconTransforms.Children.Add(_scale);
        iconTransforms.Children.Add(_launchOffset);
        var artwork = new Grid
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = iconTransforms
        };
        _iconSurface = new AppIconSurface(iconSource, size, UsesFreeformArtwork(item));
        artwork.Children.Add(_iconSurface);
        if (IsFolder(item))
        {
            _folderPreview = new DockFolderPreview(size);
            artwork.Children.Add(_folderPreview);
            artwork.VerticalAlignment = VerticalAlignment.Top;
            _folderName = new TextBlock
            {
                Width = size + 22, Height = 17,
                FontSize = Math.Clamp(size * 0.19, 9, 11),
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };
            _folderName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            grid.Children.Add(_folderName);
        }
        grid.Children.Add(artwork);
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
        UpdateFolder(item);
        UpdateIndicator();
    }

    internal void UpdateIndicator() => _indicator.Visibility = !IsFolder(Item) &&
        (Item.Windows.Count > 0 || Item.Key == "velishell") ? Visibility.Visible : Visibility.Hidden;

    internal void SetFolderDropTarget(bool active) =>
        _folderDropHalo.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    internal void UpdateItem(DockItem item)
    {
        Item = item;
        ToolTip = TooltipFor(item);
        AutomationProperties.SetName(this, item.Name);
        UpdateAutomationStatus(item);
        var iconSource = IconService.For(item.IconId, item.Target, item.Icon);
        _iconSurface.UpdateSource(iconSource, UsesFreeformArtwork(item));
        UpdateFolder(item);
        UpdateIndicator();
    }

    private void UpdateFolder(DockItem item)
    {
        if (_folderPreview is null || item.Pin is not { } pin) return;
        // A chosen folder icon still takes precedence over the live preview.
        _folderPreview.Visibility = pin.Icon is null ? Visibility.Visible : Visibility.Collapsed;
        _iconSurface.Visibility = pin.Icon is null ? Visibility.Collapsed : Visibility.Visible;
        if (_folderName is not null) _folderName.Text = item.Name;
        if (pin.Icon is null) _folderPreview.UpdatePin(pin);
    }

    private static bool IsFolder(DockItem item) =>
        item.Pin?.Kind == PinKind.VirtualFolder ||
        (item.Pin is not null && System.IO.Directory.Exists(item.Target));

    private static bool UsesFreeformArtwork(DockItem item) =>
        item.Key == "trash" || item.IconId is "folder" or "virtual-folder" ||
        (!string.IsNullOrWhiteSpace(item.Target) && System.IO.Directory.Exists(item.Target));

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

    internal void StartLaunchBounce(bool reducedMotion)
    {
        StopLaunchBounce();
        if (reducedMotion) return;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(620),
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-Math.Max(9, IconSize * 0.22), KeyTime.FromPercent(0.26),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.52),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-Math.Max(4, IconSize * 0.1), KeyTime.FromPercent(0.72),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        _launchOffset.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    internal void StopLaunchBounce()
    {
        _launchOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _launchOffset.Y = 0;
    }
}
