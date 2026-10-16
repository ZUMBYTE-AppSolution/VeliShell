#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <windowsx.h>
#include <new>
#include <wrl/client.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <windows.ui.composition.interop.h>
#include <cwchar>

// These exports serve only the cooperative own-window demo. ClassicFrame.cpp
// contains the separate, explicitly approved single-thread foreign-window adapter.
namespace {
constexpr UINT RestoreMessage = WM_APP + 0x531;
constexpr UINT_PTR SubclassId = 0x56454C49;
constexpr wchar_t OptIn[] = L"VeliShell.NativePrototype.OptIn";
constexpr wchar_t Active[] = L"VeliShell.NativePrototype.Active";
struct Frame { LONG_PTR style; int backdrop = 1; int corner = 0; int dark = 0; int hostBackdrop = 0; };

bool Owned(HWND window) {
    DWORD pid = 0;
    const DWORD thread = GetWindowThreadProcessId(window, &pid);
    return pid == GetCurrentProcessId() && thread == GetCurrentThreadId() && GetPropW(window, OptIn) == reinterpret_cast<HANDLE>(1);
}
int Dip(HWND window, int pixels) { return MulDiv(pixels, static_cast<int>(GetDpiForWindow(window)), 96); }
bool Restore(HWND window);
LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR) {
    if (message == RestoreMessage) { Restore(window); return DefSubclassProc(window, message, wp, lp); }
    if (message == WM_NCDESTROY) {
        auto frame = reinterpret_cast<Frame*>(GetPropW(window, Active));
        RemovePropW(window, Active); RemoveWindowSubclass(window, Procedure, SubclassId); delete frame;
        return DefSubclassProc(window, message, wp, lp);
    }
    if (message == WM_NCCALCSIZE) {
        if (wp && IsZoomed(window)) {
            MONITORINFO monitor{sizeof(MONITORINFO)};
            if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor))
                reinterpret_cast<NCCALCSIZE_PARAMS*>(lp)->rgrc[0] = monitor.rcWork;
        }
        return 0;
    }
    if (message == WM_NCPAINT) return 0;
    if (message == WM_NCACTIVATE) return TRUE;
    if (message == WM_NCHITTEST) {
        RECT rect{}; GetWindowRect(window, &rect);
        const int x = GET_X_LPARAM(lp) - rect.left, y = GET_Y_LPARAM(lp) - rect.top;
        const int width = rect.right - rect.left, height = rect.bottom - rect.top;
        const int edge = Dip(window, 6), corner = Dip(window, 12);
        if (!IsZoomed(window)) {
            if (y < edge) return x < corner ? HTTOPLEFT : x >= width - corner ? HTTOPRIGHT : HTTOP;
            if (y >= height - edge) return x < corner ? HTBOTTOMLEFT : x >= width - corner ? HTBOTTOMRIGHT : HTBOTTOM;
            if (x < edge) return HTLEFT;
            if (x >= width - edge) return HTRIGHT;
        }
        if (y < Dip(window, 38) && x > Dip(window, 102) && x < width - Dip(window, 155)) return HTCAPTION;
        return HTCLIENT;
    }
    return DefSubclassProc(window, message, wp, lp);
}
bool Restore(HWND window) {
    if (!Owned(window)) return false;
    auto frame = reinterpret_cast<Frame*>(GetPropW(window, Active));
    if (!frame) return true;
    // Keep the DLL loaded until the test process ends; no dangling code pointers.
    if (!RemoveWindowSubclass(window, Procedure, SubclassId)) return false;
    RemovePropW(window, Active);
    const auto current = GetWindowLongPtrW(window, GWL_STYLE);
    SetWindowLongPtrW(window, GWL_STYLE, (current & ~static_cast<LONG_PTR>(WS_CAPTION)) | (frame->style & WS_CAPTION));
    DwmSetWindowAttribute(window, 38, &frame->backdrop, sizeof(int));
    DwmSetWindowAttribute(window, 33, &frame->corner, sizeof(int));
    DwmSetWindowAttribute(window, 20, &frame->dark, sizeof(int));
    DwmSetWindowAttribute(window, 17, &frame->hostBackdrop, sizeof(int));
    MARGINS normal{}; DwmExtendFrameIntoClientArea(window, &normal);
    SetWindowPos(window, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
    delete frame; return true;
}
}

extern "C" __declspec(dllexport) int __cdecl VeliFrameApiVersion() { return 1; }
extern "C" __declspec(dllexport) int __cdecl VeliFrameAttach(HWND window, int dark, int highContrast) {
    if (!Owned(window) || GetPropW(window, Active)) return 0;
    auto frame = new(std::nothrow) Frame{};
    if (!frame) return 0;
    frame->style = GetWindowLongPtrW(window, GWL_STYLE);
    DwmGetWindowAttribute(window, 38, &frame->backdrop, sizeof(int));
    DwmGetWindowAttribute(window, 33, &frame->corner, sizeof(int));
    DwmGetWindowAttribute(window, 20, &frame->dark, sizeof(int));
    DwmGetWindowAttribute(window, 17, &frame->hostBackdrop, sizeof(int));
    if (!SetPropW(window, Active, frame)) { delete frame; return 0; }
    if (!SetWindowSubclass(window, Procedure, SubclassId, 0)) { RemovePropW(window, Active); delete frame; return 0; }
    SetWindowLongPtrW(window, GWL_STYLE, frame->style & ~static_cast<LONG_PTR>(WS_CAPTION));
    const int backdrop = highContrast ? 1 : 3; // DWMSBT_TRANSIENTWINDOW: native Desktop Acrylic.
    const int corner = 2;
    DwmSetWindowAttribute(window, 20, &dark, sizeof(int));
    DwmSetWindowAttribute(window, 33, &corner, sizeof(int));
    const HRESULT material = DwmSetWindowAttribute(window, 38, &backdrop, sizeof(int));
    MARGINS glass{-1, -1, -1, -1}; DwmExtendFrameIntoClientArea(window, &glass);
    SetWindowPos(window, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    return SUCCEEDED(material) && !highContrast ? 2 : 1; // 1 = safe, readable non-glass fallback.
}
extern "C" __declspec(dllexport) int __cdecl VeliFrameRestore(HWND window) { return Restore(window) ? 1 : 0; }

extern "C" __declspec(dllexport) HRESULT __cdecl VeliFrameGraphicsDevice(IUnknown* compositor, IUnknown** output) {
    if (!compositor || !output) return E_INVALIDARG;
    *output = nullptr;
    using Microsoft::WRL::ComPtr;
    ComPtr<ID3D11Device> d3d;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr);
    if (FAILED(hr)) hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr);
    if (FAILED(hr)) return hr;
    ComPtr<IDXGIDevice> dxgi; hr = d3d.As(&dxgi); if (FAILED(hr)) return hr;
    ComPtr<ID2D1Device> d2d; hr = D2D1CreateDevice(dxgi.Get(), nullptr, &d2d); if (FAILED(hr)) return hr;
    ComPtr<ABI::Windows::UI::Composition::ICompositorInterop> interop; hr = compositor->QueryInterface(IID_PPV_ARGS(&interop)); if (FAILED(hr)) return hr;
    ComPtr<ABI::Windows::UI::Composition::ICompositionGraphicsDevice> graphics;
    hr = interop->CreateGraphicsDevice(d2d.Get(), &graphics); if (FAILED(hr)) return hr;
    return graphics.CopyTo(output);
}
extern "C" __declspec(dllexport) HRESULT __cdecl VeliFramePaintCaption(IUnknown* surface, int width, int height, float scale, int dark, UINT32 plate, int hover, const wchar_t* title, const wchar_t* restore) {
    if (!surface || !title || !restore || width < 100 || height < 20 || scale <= 0) return E_INVALIDARG;
    using Microsoft::WRL::ComPtr;
    ComPtr<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop> interop;
    HRESULT hr = surface->QueryInterface(IID_PPV_ARGS(&interop)); if (FAILED(hr)) return hr;
    ComPtr<ID2D1DeviceContext> context; POINT offset{};
    hr = interop->BeginDraw(nullptr, IID_PPV_ARGS(&context), &offset); if (FAILED(hr)) return hr;
    context->SetTransform(D2D1::Matrix3x2F::Translation(static_cast<float>(offset.x), static_cast<float>(offset.y)));
    context->Clear(D2D1::ColorF(0, 0.0f));
    ComPtr<ID2D1SolidColorBrush> brush;
    hr = context->CreateSolidColorBrush(D2D1::ColorF(0, 0.0f), &brush);
    if (SUCCEEDED(hr)) {
        const bool cooperative = *restore != L'\0';
        const float centerY = cooperative ? 19 * scale : height / 2.0f;
        brush->SetColor(D2D1::ColorF(plate & 0xFFFFFF, static_cast<float>(plate >> 24) / 255.0f));
        context->FillRectangle(D2D1::RectF(0, 0, static_cast<float>(width), static_cast<float>(height)), brush.Get());
        constexpr UINT32 colors[] = { 0xFF5F57, 0xFEBC2E, 0x28C840 };
        for (int index = 0; index < 3; ++index) {
            brush->SetColor(D2D1::ColorF(colors[index]));
            context->FillEllipse(D2D1::Ellipse(D2D1::Point2F((24.0f + 22.0f * index) * scale, centerY), 6 * scale, 6 * scale), brush.Get());
            if (index == hover) {
                brush->SetColor(D2D1::ColorF(index == 0 ? 0x71231E : index == 1 ? 0x805400 : 0x0E6220));
                const float x = (24.0f + 22.0f * index) * scale, y = centerY, r = 2.5f * scale;
                if (index == 0) {
                    context->DrawLine(D2D1::Point2F(x-r,y-r), D2D1::Point2F(x+r,y+r), brush.Get(), scale);
                    context->DrawLine(D2D1::Point2F(x-r,y+r), D2D1::Point2F(x+r,y-r), brush.Get(), scale);
                } else if (index == 1) context->DrawLine(D2D1::Point2F(x-r,y), D2D1::Point2F(x+r,y), brush.Get(), scale);
                else {
                    context->DrawLine(D2D1::Point2F(x-r,y), D2D1::Point2F(x-r,y-r), brush.Get(), scale);
                    context->DrawLine(D2D1::Point2F(x-r,y-r), D2D1::Point2F(x,y-r), brush.Get(), scale);
                    context->DrawLine(D2D1::Point2F(x,y+r), D2D1::Point2F(x+r,y+r), brush.Get(), scale);
                    context->DrawLine(D2D1::Point2F(x+r,y+r), D2D1::Point2F(x+r,y), brush.Get(), scale);
                }
            }
        }
        ComPtr<IDWriteFactory> factory;
        hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(factory.GetAddressOf()));
        if (SUCCEEDED(hr)) {
            ComPtr<IDWriteTextFormat> format;
            hr = factory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12 * scale, L"en-US", &format);
            if (SUCCEEDED(hr)) {
                format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER); format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                brush->SetColor(D2D1::ColorF(dark ? 0xF0F4FC : 0x1D2942));
                context->DrawTextW(title, static_cast<UINT32>(wcslen(title)), format.Get(), D2D1::RectF(100 * scale, 0, width - (cooperative ? 155 : 100) * scale, static_cast<float>(height)), brush.Get());
                if (cooperative) {
                brush->SetColor(D2D1::ColorF(dark ? 0xECF2FF : 0xFFFFFF, dark ? .10f : .38f));
                context->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(width - 140 * scale, 6 * scale, width - 12 * scale, 32 * scale), 13 * scale, 13 * scale), brush.Get());
                brush->SetColor(D2D1::ColorF(dark ? 0xF0F4FC : 0x1D2942));
                context->DrawTextW(restore, static_cast<UINT32>(wcslen(restore)), format.Get(), D2D1::RectF(width - 140 * scale, 6 * scale, width - 12 * scale, 32 * scale), brush.Get());
                }
            }
        }
    }
    const HRESULT end = interop->EndDraw(); return FAILED(hr) ? hr : end;
}
