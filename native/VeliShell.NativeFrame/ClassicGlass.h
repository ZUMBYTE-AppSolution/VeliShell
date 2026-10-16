#pragma once
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <windows.ui.composition.interop.h>
#include <dispatcherqueue.h>
#include <roapi.h>
#include <memory>
#include <map>

extern "C" HRESULT __cdecl VeliFrameGraphicsDevice(IUnknown*, IUnknown**);
extern "C" HRESULT __cdecl VeliFramePaintCaption(IUnknown*,int,int,float,int,UINT32,int,const wchar_t*,const wchar_t*);

// Shared per UI thread: do not create a D3D device for every window of an app.
struct ClassicGraphics {
    winrt::Windows::System::DispatcherQueueController queue{nullptr};
    winrt::Windows::UI::Composition::Compositor compositor{nullptr};
    winrt::Windows::UI::Composition::CompositionGraphicsDevice graphics{nullptr};
    bool apartment=false;
    ClassicGraphics() {
        apartment=SUCCEEDED(RoInitialize(RO_INIT_SINGLETHREADED));
        try {
            if(!winrt::Windows::System::DispatcherQueue::GetForCurrentThread()) {
                DispatcherQueueOptions options{sizeof(options),DQTYPE_THREAD_CURRENT,DQTAT_COM_NONE};
                winrt::check_hresult(CreateDispatcherQueueController(options,reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(queue))));
            }
            compositor=winrt::Windows::UI::Composition::Compositor();
            winrt::com_ptr<IUnknown> device;
            winrt::check_hresult(VeliFrameGraphicsDevice(reinterpret_cast<IUnknown*>(winrt::get_abi(compositor)),device.put()));
            graphics=device.as<winrt::Windows::UI::Composition::CompositionGraphicsDevice>();
        } catch(...) { if(apartment) { RoUninitialize(); apartment=false; } throw; }
    }
    ~ClassicGraphics() noexcept {
        graphics=nullptr; compositor=nullptr;
        try { if(queue) queue.ShutdownQueueAsync(); } catch(...) { }
        queue=nullptr; if(apartment) RoUninitialize();
    }
};
struct ClassicGlass {
    std::shared_ptr<ClassicGraphics> shared;
    winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget target{nullptr};
    winrt::Windows::UI::Composition::ContainerVisual root{nullptr};
    winrt::Windows::UI::Composition::SpriteVisual backdrop{nullptr},tintLayer{nullptr},label{nullptr};
    winrt::Windows::UI::Composition::ShapeVisual outline{nullptr};
    winrt::Windows::UI::Composition::CompositionSpriteShape stroke{nullptr};
    winrt::Windows::UI::Composition::CompositionRoundedRectangleGeometry strokeShape{nullptr};
    winrt::Windows::UI::Composition::CompositionRoundedRectangleGeometry shape{nullptr};
    winrt::Windows::UI::Composition::CompositionDrawingSurface surface{nullptr};
    int lastWidth=0,lastHeight=0,lastCaption=0,lastDark=-1,lastHover=-2,lastGloss=-1,lastTransparency=-1;
    UINT32 lastTint=0;
    std::wstring lastTitle;
    HRESULT error=S_OK;
    bool Init(HWND window) noexcept {
        try {
            // A thread_local weak reference is never a live COM owner at DLL/thread teardown.
            static thread_local std::weak_ptr<ClassicGraphics> cache;
            shared=cache.lock(); if(!shared) { shared=std::make_shared<ClassicGraphics>(); cache=shared; }
            int enabled=1;
            winrt::check_hresult(DwmSetWindowAttribute(window,17,&enabled,sizeof(enabled)));
            int disabled=1; DwmSetWindowAttribute(window,2,&disabled,sizeof(disabled));
            auto compositor=shared->compositor;
            auto interop=compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
            winrt::check_hresult(interop->CreateDesktopWindowTarget(window,FALSE,reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(winrt::put_abi(target))));
            root=compositor.CreateContainerVisual();
            shape=compositor.CreateRoundedRectangleGeometry(); root.Clip(compositor.CreateGeometricClip(shape));
            backdrop=compositor.CreateSpriteVisual(); backdrop.Brush(compositor.CreateHostBackdropBrush());
            tintLayer=compositor.CreateSpriteVisual();
            label=compositor.CreateSpriteVisual();
            outline=compositor.CreateShapeVisual(); strokeShape=compositor.CreateRoundedRectangleGeometry();
            stroke=compositor.CreateSpriteShape(strokeShape); stroke.StrokeThickness(1); outline.Shapes().Append(stroke);
            root.Children().InsertAtBottom(backdrop); root.Children().InsertAtTop(tintLayer); root.Children().InsertAtTop(label); root.Children().InsertAtTop(outline); target.Root(root);
            return true;
        } catch(...) { error=winrt::to_hresult(); return false; }
    }
    HRESULT Paint(HWND owner,int width,int height,int captionHeight,int dark,int gloss,int transparency,UINT32 tint,int hover,bool maximized) noexcept {
        try {
            using namespace winrt::Windows::Graphics::DirectX;
            wchar_t title[512]{}; GetWindowTextW(owner,title,512);
            if(width==lastWidth && height==lastHeight && captionHeight==lastCaption && dark==lastDark && hover==lastHover && gloss==lastGloss && transparency==lastTransparency && tint==lastTint && lastTitle==title) return S_OK;
            root.Size({static_cast<float>(width),static_cast<float>(height)});
            backdrop.Size(root.Size()); tintLayer.Size(root.Size()); outline.Size(root.Size()); label.Size({static_cast<float>(width),static_cast<float>(captionHeight)});
            const float scale=GetDpiForWindow(owner)/96.0f,radius=maximized?0.0f:8.0f*scale;
            shape.Size({static_cast<float>(width),static_cast<float>(height)}); shape.CornerRadius({radius,radius});
            strokeShape.Offset({.5f,.5f}); strokeShape.Size({width-1.0f,height-1.0f}); strokeShape.CornerRadius({radius,radius});
            auto next=shared->graphics.CreateDrawingSurface({static_cast<float>(width),static_cast<float>(captionHeight)},DirectXPixelFormat::B8G8R8A8UIntNormalized,DirectXAlphaMode::Premultiplied);
            const double glossAmount=std::clamp(gloss,0,100)/100.0,clearAmount=std::clamp(transparency,0,100)/100.0;
            // Title text needs a contrast floor even over a white/black desktop.
            const auto alpha=static_cast<UINT32>(std::clamp(static_cast<double>(tint>>24)-45*glossAmount-30*clearAmount,160.0,245.0));
            winrt::Windows::UI::Color colour{static_cast<BYTE>(alpha),static_cast<BYTE>(tint>>16),static_cast<BYTE>(tint>>8),static_cast<BYTE>(tint)};
            tintLayer.Brush(shared->compositor.CreateColorBrush(colour));
            auto edge=colour; edge.A=90; edge.R=static_cast<BYTE>(std::clamp(static_cast<int>(edge.R)+(dark?70:-40),0,255)); edge.G=static_cast<BYTE>(std::clamp(static_cast<int>(edge.G)+(dark?70:-40),0,255)); edge.B=static_cast<BYTE>(std::clamp(static_cast<int>(edge.B)+(dark?70:-40),0,255));
            stroke.StrokeBrush(shared->compositor.CreateColorBrush(edge));
            HRESULT hr=VeliFramePaintCaption(reinterpret_cast<IUnknown*>(winrt::get_abi(next)),width,captionHeight,scale,dark,0,hover,title,L"");
            if(FAILED(hr)) return hr;
            label.Brush(shared->compositor.CreateSurfaceBrush(next)); surface=next;
            lastWidth=width; lastHeight=height; lastCaption=captionHeight; lastDark=dark; lastHover=hover; lastGloss=gloss; lastTransparency=transparency; lastTint=tint; lastTitle=title;
            return S_OK;
        } catch(...) { return winrt::to_hresult(); }
    }
    ~ClassicGlass() noexcept {
        try { if(target) target.Root(nullptr); if(root) root.Children().RemoveAll(); } catch(...) { }
        surface=nullptr; label=nullptr; tintLayer=nullptr; outline=nullptr; stroke=nullptr; strokeShape=nullptr; backdrop=nullptr; shape=nullptr; root=nullptr; target=nullptr; shared.reset();
    }
};
