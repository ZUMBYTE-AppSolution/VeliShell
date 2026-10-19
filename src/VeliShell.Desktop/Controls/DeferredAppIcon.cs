using System.Windows;
using System.Windows.Controls;
using VeliShell.Core;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

/// <summary>Names and controls can paint before any shell icon lookup starts.</summary>
public sealed class DeferredAppIcon : Grid
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(string), typeof(DeferredAppIcon), new PropertyMetadata("", TargetChanged));
    public static readonly DependencyProperty IconStyleProperty = DependencyProperty.Register(nameof(IconStyle), typeof(DockIconStyle), typeof(DeferredAppIcon), new PropertyMetadata(DockIconStyle.Mac));
    public string Target { get => (string)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public string IconId { get; set; } = "app";
    public IconReference? Icon { get; set; }
    public DockIconStyle IconStyle { get => (DockIconStyle)GetValue(IconStyleProperty); set => SetValue(IconStyleProperty, value); }
    internal StartIconLoader Loader { get; set; } = StartIconLoader.Shared;
    private readonly AppIconSurface _surface;
    private readonly VeliSymbol _placeholder;
    private readonly double _side;
    private CancellationTokenSource? _request;

    public DeferredAppIcon() : this(36) { }

    internal DeferredAppIcon(double side)
    {
        _side = Math.Clamp(side, 16, 96);
        Width = Height = _side; IsHitTestVisible = false;
        _surface = new AppIconSurface(null, _side);
        _placeholder = new VeliSymbol { Symbol = VeliSymbolKind.Apps, Width = 24, Height = 24, Opacity = .55 };
        _placeholder.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        Children.Add(_placeholder); Children.Add(_surface);
        Loaded += async (_, _) => { if (PresentationSource.FromVisual(this) is not null) await RefreshAsync(); };
        Unloaded += (_, _) => { _request?.Cancel(); _request?.Dispose(); _request = null; };
    }

    private static async void TargetChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is DeferredAppIcon { IsLoaded: true } icon && PresentationSource.FromVisual(icon) is not null) await icon.RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        _request?.Cancel(); _request?.Dispose();
        var request = _request = new CancellationTokenSource();
        var pixels = (int)Math.Clamp(Math.Ceiling(_side * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX), 48, 256);
        var result = await Loader.LoadAsync(new(IconId, Target, Icon, IconStyle, pixels), request.Token).ConfigureAwait(false);
        void ApplyResult()
        {
            if (request.IsCancellationRequested || !ReferenceEquals(_request, request) || result is null) return;
            _surface.UpdateSource(result.Value.Source, result.Value.Freeform);
            _placeholder.Visibility = Visibility.Collapsed;
        }
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) ApplyResult();
        else await Dispatcher.InvokeAsync(ApplyResult);
    }
}
