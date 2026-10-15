using System.Windows;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>WPF's ClipToBounds is rectangular, even inside a rounded Border.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0d, RadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void RadiusChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement view) return;
        view.SizeChanged -= Update;
        if (GetRadius(view) > 0) view.SizeChanged += Update;
        SetClip(view);
    }

    private static void Update(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement view) SetClip(view);
    }

    private static void SetClip(FrameworkElement view)
    {
        var radius = GetRadius(view);
        var width = view.ActualWidth > 0 ? view.ActualWidth : view.Width;
        var height = view.ActualHeight > 0 ? view.ActualHeight : view.Height;
        view.Clip = radius > 0 && double.IsFinite(width) && double.IsFinite(height) &&
                    width > 0 && height > 0
            ? new RectangleGeometry(new Rect(0, 0, width, height), radius, radius)
            : null;
    }
}
