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
    private CompositionGraphicsDevice? _captionDevice;
    private CompositionDrawingSurface? _captionSurface;
    private CompositionSurfaceBrush? _captionBrush;
    private SpriteVisual? _captionVisual;
    private (int Width, int Height, float Scale, bool Dark, uint Plate, int Hover)? _captionSize;

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

    internal void Resize(int width, int height, float radiusX, float radiusY, bool squareBottom = false)
    {
        var size = new Vector2(width, height);
        _root.Size = size;
        _shape.Size = new Vector2(width, height + (squareBottom ? radiusY : 0));
        _shape.CornerRadius = new Vector2(Math.Min(radiusX, width / 2f), Math.Min(radiusY, height / 2f));
        _backdrop.Size = size;
        _tint.Size = size;
    }

    internal void PaintPrototypeCaption(int width, int height, float scale, bool dark, uint plate, int hover, string title, string restore)
    {
        if (!BuildIdentity.IsTest) throw new InvalidOperationException("Native caption is test-only.");
        if (_captionSize == (width, height, scale, dark, plate, hover)) return;
        if (_captionDevice is null)
        {
            var pointer = MarshalInspectable<Compositor>.FromManaged(_compositor);
            try
            {
                Marshal.ThrowExceptionForHR(NativePrototypeMethods.VeliFrameGraphicsDevice(pointer, out var device));
                try { _captionDevice = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(device); }
                finally { Marshal.Release(device); }
            }
            finally { Marshal.Release(pointer); }
            _captionBrush = _compositor.CreateSurfaceBrush();
            _captionVisual = _compositor.CreateSpriteVisual(); _captionVisual.Brush = _captionBrush;
            _root.Children.InsertAtTop(_captionVisual);
        }
        var surface = _captionDevice.CreateDrawingSurface(new Windows.Foundation.Size(width, height),
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        try
        {
            var pointer = MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface);
            try { Marshal.ThrowExceptionForHR(NativePrototypeMethods.VeliFramePaintCaption(pointer, width, height, scale, dark ? 1 : 0, plate, hover, title, restore)); }
            finally { Marshal.Release(pointer); }
            _captionBrush!.Surface = surface; _captionVisual!.Size = new Vector2(width, height);
            _captionSurface?.Dispose(); _captionSurface = surface;
            _captionSize = (width, height, scale, dark, plate, hover);
        }
        catch { surface.Dispose(); throw; }
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
        _captionVisual?.Dispose(); _captionBrush?.Dispose(); _captionSurface?.Dispose(); _captionDevice?.Dispose();
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
