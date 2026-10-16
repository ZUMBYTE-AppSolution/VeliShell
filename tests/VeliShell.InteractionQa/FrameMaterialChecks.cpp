#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dwmapi.h>
#include <unknwn.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <cstdio>
#include <vector>
#include <stdexcept>
#include "../../native/VeliShell.NativeFrame/ClassicGlass.h"

// Background-only checks. No visible windows, desktop capture, target processes,
// hooks, hotkeys, input simulation, foreground activation or saved preferences.
namespace {
int checks=0;
void Check(bool pass,const char* message) { if(!pass)throw std::runtime_error(message);++checks; }
void Hr(HRESULT value) { winrt::check_hresult(value); }
struct Pixels { double contrast=0,red=0,blue=0; };
struct Offscreen {
    winrt::com_ptr<ID3D11Device> gpu;
    winrt::com_ptr<ID2D1Factory1> factory;
    winrt::com_ptr<ID2D1Device> device;
    winrt::com_ptr<ID2D1DeviceContext> context;
    Offscreen() {
        D3D_FEATURE_LEVEL level{};
        Hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,gpu.put(),&level,nullptr));
        D2D1_FACTORY_OPTIONS options{};
        Hr(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),&options,factory.put_void()));
        auto dxgi=gpu.as<IDXGIDevice>();Hr(factory->CreateDevice(dxgi.get(),device.put()));
        Hr(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.put()));
    }
    Pixels Render(int gloss,int transparency,bool dark,int solid=0) {
        constexpr UINT width=384,height=96;
        std::vector<UINT32> input(width*height);
        for(UINT y=0;y<height;++y)for(UINT x=0;x<width;++x)
            input[y*width+x]=solid==1?0xFFFF240C:solid==2?0xFF0C24FF:((x/32)%2?0xFF1010F0:0xFFF01010);
        auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));
        winrt::com_ptr<ID2D1Bitmap1> source,target,readback;
        Hr(context->CreateBitmap(D2D1::SizeU(width,height),input.data(),width*4,&props,source.put()));
        props.bitmapOptions=D2D1_BITMAP_OPTIONS_TARGET;
        Hr(context->CreateBitmap(D2D1::SizeU(width,height),nullptr,0,&props,target.put()));
        props.bitmapOptions=static_cast<D2D1_BITMAP_OPTIONS>(D2D1_BITMAP_OPTIONS_CPU_READ|D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
        Hr(context->CreateBitmap(D2D1::SizeU(width,height),nullptr,0,&props,readback.put()));
        winrt::com_ptr<ID2D1Effect> blur;
        Hr(context->CreateEffect(CLSID_D2D1GaussianBlur,blur.put()));blur->SetInput(0,source.get());
        const auto material=ClassicMaterial(gloss,transparency,dark,1);
        Hr(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,material.blurPixels));
        Hr(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE,D2D1_BORDER_MODE_HARD));
        Hr(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION,D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED));
        winrt::com_ptr<ID2D1SolidColorBrush> tint;
        Hr(context->CreateSolidColorBrush(dark?D2D1::ColorF(.08f,.13f,.22f,material.tintOpacity):D2D1::ColorF(.89f,.94f,1,material.tintOpacity),tint.put()));
        context->SetTarget(target.get());context->BeginDraw();context->Clear(D2D1::ColorF(0,0,0,0));
        context->DrawImage(blur.get());context->FillRectangle(D2D1::RectF(0,0,static_cast<float>(width),static_cast<float>(height)),tint.get());
        Hr(context->EndDraw());context->SetTarget(nullptr);
        Hr(readback->CopyFromBitmap(nullptr,target.get(),nullptr));
        D2D1_MAPPED_RECT data{};Hr(readback->Map(D2D1_MAP_OPTIONS_READ,&data));
        Pixels result;int count=0;
        for(UINT y=24;y<72;++y)for(UINT x=64;x<320;++x) {
            const BYTE* pixel=data.bits+y*data.pitch+x*4;
            result.blue+=pixel[0];result.red+=pixel[2];result.contrast+=std::abs(static_cast<int>(pixel[2])-pixel[0]);++count;
        }
        Hr(readback->Unmap());result.blue/=count;result.red/=count;result.contrast/=count;return result;
    }
};
void VerifyMaterial(const std::shared_ptr<ClassicGraphics>& shared) {
    for(bool dark:{false,true})for(float dpi:{.5f,1.0f,1.25f,1.5f,2.0f,4.0f})for(int gloss:{0,25,50,100})for(int transparency:{0,25,75,100}) {
        const auto value=ClassicMaterial(gloss,transparency,dark,dpi);
        Check(value.blurPixels>=12*dpi && value.blurPixels<=28*dpi,"Blur range / DPI scaling");
        Check(value.tintOpacity>=.21f && value.tintOpacity<=.67f,"Tint range / readability");
        Check(value.strokeOpacity<=.101f && value.strokeOpacity>0,"No bright substitute border");
    }
    Check(ClassicMaterial(-10,120,true,1).tintOpacity==ClassicMaterial(0,100,true,1).tintOpacity,"Input clamping");
    Check(ClassicMaterial(100,100,true,NAN).blurPixels==12,"Invalid DPI fallback");
    auto brush=shared->blurFactory.CreateBrush();
    brush.SetSourceParameter(L"Backdrop",shared->compositor.CreateHostBackdropBrush());
    for(float blur:{12.0f,28.0f,56.0f}) {
        brush.Properties().InsertScalar(L"FrameBlur.BlurAmount",blur);
        float actual{};
        Check(brush.Properties().TryGetScalar(L"FrameBlur.BlurAmount",actual)==winrt::Windows::UI::Composition::CompositionGetValueStatus::Succeeded && actual==blur,"Real Composition effect property roundtrip");
    }
    brush.Close();brush=nullptr;
    Offscreen renderer;
    for(bool dark:{false,true}) {
        const auto matte=renderer.Render(0,50,dark),gloss=renderer.Render(100,50,dark);
        const auto opaque=renderer.Render(100,0,dark),clear=renderer.Render(100,100,dark);
        const auto red=renderer.Render(100,100,dark,1),blue=renderer.Render(100,100,dark,2);
        Check(gloss.contrast>matte.contrast+2,"Gloss must transmit more colour detail than matte in actual D2D pixels");
        Check(clear.contrast>opaque.contrast+2,"Transparency must affect the entire material, not only its stroke");
        Check(red.red>blue.red+100 && blue.blue>red.blue+100,"Changing the backdrop input must change rendered colour");
        std::printf("PASS %s D2D/WARP pixels: matte %.2f, gloss %.2f; transparency 0 %.2f, 100 %.2f\n",dark?"dark":"light",matte.contrast,gloss.contrast,opaque.contrast,clear.contrast);
    }
}
void VerifyHiddenHost() {
    WNDCLASSW type{};type.lpfnWndProc=DefWindowProcW;type.hInstance=GetModuleHandleW(nullptr);type.lpszClassName=L"VeliShell.Headless.BorderCheck";
    Check(RegisterClassW(&type)!=0,"Register own hidden check class");
    HWND window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_NOREDIRECTIONBITMAP,type.lpszClassName,L"Headless caption",WS_POPUP,-30000,-30000,320,180,nullptr,nullptr,type.hInstance,nullptr);
    Check(window!=nullptr && !IsWindowVisible(window),"Check window must remain invisible");
    try {
        ClassicGlass glass;Check(glass.Init(window),"Initialize actual Composition host on own hidden HWND");
        VerifyMaterial(glass.shared);
        const UINT dpi=GetDpiForWindow(window);const float scale=dpi/96.0f;
        for(bool dark:{false,true})for(int gloss:{0,100})for(int transparency:{0,100}) {
            Hr(glass.Paint(window,640,360,40,dark,gloss,transparency,dark?0xFF142138:0xFFE3F0FF,-1,false));
            const auto material=ClassicMaterial(gloss,transparency,dark,scale);
            auto colour=glass.tintLayer.Brush().as<winrt::Windows::UI::Composition::CompositionColorBrush>().Color();
            Check(colour.A==std::lround(material.tintOpacity*255),"Actual host tint alpha follows full material policy");
            Check(glass.shape.Size().x==640 && glass.shape.Size().y==360,"Rounded clip covers entire frame, not a title-sized rectangle");
            Check(glass.shape.CornerRadius().x==8*scale,"Normal host has antialiased rounded clip");
        }
        Hr(glass.Paint(window,640,360,40,true,100,100,0xFF142138,-1,true));
        Check(glass.shape.CornerRadius().x==0,"Maximized state invalidates same-size geometry cache");
        Hr(glass.Paint(window,640,360,40,true,100,100,0xFF142138,-1,false));
        Check(glass.shape.CornerRadius().x==8*scale,"Restored state returns rounded geometry");
        Check(!IsWindowVisible(window),"No check may display a window");
    } catch(...) { DestroyWindow(window);UnregisterClassW(type.lpszClassName,type.hInstance);throw; }
    DestroyWindow(window);UnregisterClassW(type.lpszClassName,type.hInstance);
    std::puts("PASS: actual Composition host/effect, caption drawing, full-size rounded clip and maximize/restore cache on an own never-shown HWND.");
}
}
int main() {
    std::setvbuf(stdout,nullptr,_IONBF,0);
    try { std::puts("Checking never-shown Composition host...");VerifyHiddenHost();std::printf("PASS: %d native material checks. No foreign windows changed.\n",checks);return 0; }
    catch(const winrt::hresult_error& error) { std::printf("FAIL native material HRESULT 0x%08X: %ls\n",static_cast<unsigned>(error.code()),error.message().c_str()); }
    catch(const std::exception& error) { std::printf("FAIL native material: %s\n",error.what()); }
    return 1;
}
