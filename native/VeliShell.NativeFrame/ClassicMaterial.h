#pragma once
#include <unknwn.h>
#include <algorithm>
#include <cmath>
#include <d2d1effects.h>
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.Composition.h>

struct ClassicMaterialValues {
    float blurPixels,tintOpacity,strokeOpacity;
};
inline ClassicMaterialValues ClassicMaterial(int gloss,int transparency,bool dark,float dpiScale) {
    const float g=std::clamp(gloss,0,100)/100.0f,t=std::clamp(transparency,0,100)/100.0f;
    const float scale=std::isfinite(dpiScale)?std::clamp(dpiScale,.5f,4.0f):1.0f;
    // Matte scatters more; gloss stays softly blurred while transmitting colour.
    // Tint and blur are independent. No opaque title-sized readability rectangle.
    return {(28.0f-16.0f*g)*scale,(dark?.62f:.66f)-.16f*g-.24f*t,(dark?.075f:.065f)+.025f*g};
}

// Describe the supported Windows Composition Gaussian effect directly through
// the SDK interop interface; no Win2D package, bitmap desktop capture or shaders
// loaded from another app. The real host backdrop remains the effect input.
struct ClassicBlurEffect : winrt::implements<ClassicBlurEffect,
    winrt::Windows::Graphics::Effects::IGraphicsEffect,
    winrt::Windows::Graphics::Effects::IGraphicsEffectSource,
    ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop> {
    winrt::hstring name=L"FrameBlur";
    winrt::Windows::Graphics::Effects::IGraphicsEffectSource source{winrt::Windows::UI::Composition::CompositionEffectSourceParameter(L"Backdrop")};
    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& value) { name=value; }
    HRESULT __stdcall GetEffectId(GUID* id) noexcept final { if(!id)return E_POINTER;*id=CLSID_D2D1GaussianBlur;return S_OK; }
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR property,UINT* index,ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept final {
        if(!property || !index || !mapping)return E_POINTER;
        if(wcscmp(property,L"BlurAmount")==0)*index=D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION;
        else if(wcscmp(property,L"Optimization")==0)*index=D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION;
        else if(wcscmp(property,L"BorderMode")==0)*index=D2D1_GAUSSIANBLUR_PROP_BORDER_MODE;
        else return E_INVALIDARG;
        *mapping=ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;return S_OK;
    }
    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept final { if(!count)return E_POINTER;*count=3;return S_OK; }
    HRESULT __stdcall GetProperty(UINT index,ABI::Windows::Foundation::IPropertyValue** value) noexcept final {
        if(!value)return E_POINTER;*value=nullptr;
        try {
            winrt::Windows::Foundation::IPropertyValue property{nullptr};
            if(index==D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION)property=winrt::box_value(12.0f).as<winrt::Windows::Foundation::IPropertyValue>();
            else if(index==D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION)property=winrt::box_value(static_cast<UINT32>(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED)).as<winrt::Windows::Foundation::IPropertyValue>();
            else if(index==D2D1_GAUSSIANBLUR_PROP_BORDER_MODE)property=winrt::box_value(static_cast<UINT32>(D2D1_BORDER_MODE_HARD)).as<winrt::Windows::Foundation::IPropertyValue>();
            else return E_INVALIDARG;
            winrt::copy_to_abi(property,reinterpret_cast<void*&>(*value));return S_OK;
        } catch(...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall GetSource(UINT index,ABI::Windows::Graphics::Effects::IGraphicsEffectSource** value) noexcept final {
        if(!value)return E_POINTER;*value=nullptr;if(index!=0)return E_INVALIDARG;
        winrt::copy_to_abi(source,reinterpret_cast<void*&>(*value));return S_OK;
    }
    HRESULT __stdcall GetSourceCount(UINT* count) noexcept final { if(!count)return E_POINTER;*count=1;return S_OK; }
};
