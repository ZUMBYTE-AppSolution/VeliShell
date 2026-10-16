using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using VeliShell.Desktop.Native;
using Color = System.Windows.Media.Color;

namespace VeliShell.Desktop.Services;

/// <summary>A continuously clipped, GPU-composited host backdrop, without HWND strips.</summary>
internal sealed class CompositionGlassRenderer : IDisposable
{
    private static DispatcherQueueController? _queueController;
    private readonly Compositor _compositor = null!;
    private readonly DesktopWindowTarget _target = null!;
    private readonly ContainerVisual _root = null!;
    private readonly SpriteVisual _backdrop = null!;
    private readonly SpriteVisual _tint = null!;
    private readonly CompositionColorBrush _tintBrush = null!;
    private readonly CompositionRoundedRectangleGeometry _shape = null!;
    private readonly CompositionGeometricClip _clip = null!;
    private readonly CompositionBackdropBrush _backdropBrush = null!;
    private bool _disposed;

    internal CompositionGlassRenderer(nint window)
    {
        EnsureQueue();
        var enabled = 1;
        Marshal.ThrowExceptionForHR(NativeMethods.DwmSetWindowAttribute(window, 17, ref enabled, sizeof(int)));
        try
        {
            _compositor = new Compositor();
            _compositor.As<CompositionInterop.ICompositorDesktopInterop>()
                .CreateDesktopWindowTarget(window, false, out var target);
            try { _target = MarshalInspectable<DesktopWindowTarget>.FromAbi(target); }
            finally { Marshal.Release(target); }
            _root = _compositor.CreateContainerVisual();
            _shape = _compositor.CreateRoundedRectangleGeometry();
            _clip = _compositor.CreateGeometricClip(_shape);
            _root.Clip = _clip;
            _backdropBrush = _compositor.CreateHostBackdropBrush();
            _backdrop = _compositor.CreateSpriteVisual();
            _backdrop.Brush = _backdropBrush;
            _tintBrush = _compositor.CreateColorBrush();
            _tint = _compositor.CreateSpriteVisual();
            _tint.Brush = _tintBrush;
            _root.Children.InsertAtBottom(_backdrop);
            _root.Children.InsertAtTop(_tint);
            _target.Root = _root;
        }
        catch { Dispose(); throw; }
    }

    internal void Resize(int width, int height, float radiusX, float radiusY)
    {
        var size = new Vector2(width, height);
        _root.Size = size;
        _shape.Size = size;
        _shape.CornerRadius = new Vector2(Math.Min(radiusX, width / 2f), Math.Min(radiusY, height / 2f));
        _backdrop.Size = size;
        _tint.Size = size;
    }

    internal void Apply(Color tint, double gloss, double transparency)
    {
        var glossAmount = Math.Clamp(gloss, 0, 100) / 100;
        var clearAmount = Math.Clamp(transparency, 0, 100) / 100;
        // Change transmission across the entire compositor surface. The
        // rounded stroke remains independent from this material setting.
        var alpha = (byte)Math.Clamp(Math.Round(tint.A * (1.35 - glossAmount * 0.85) *
                                                (1 - clearAmount * 0.15)), 0, 255);
        _tintBrush.Color = Windows.UI.Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        _backdrop.Opacity = 1; // Preserve blur at every setting; never reveal a sharp rectangular desktop copy.
    }

    private static void EnsureQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new CompositionInterop.DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<CompositionInterop.DispatcherQueueOptions>(),
            ThreadType = 2, ApartmentType = 0
        };
        Marshal.ThrowExceptionForHR(CompositionInterop.CreateDispatcherQueueController(options, out var pointer));
        try { _queueController = MarshalInspectable<DispatcherQueueController>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        if (Application.Current is { } app)
            app.Exit += (_, _) => { _ = _queueController?.ShutdownQueueAsync(); };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_target is not null) _target.Root = null;
            _root?.Children.RemoveAll();
        }
        catch (COMException) { } // The device/target may already be gone during shutdown.
        _backdrop?.Dispose();
        _tint?.Dispose();
        _backdropBrush?.Dispose();
        _tintBrush?.Dispose();
        _clip?.Dispose();
        _shape?.Dispose();
        _root?.Dispose();
        _target?.Dispose();
        _compositor?.Dispose();
    }
}
