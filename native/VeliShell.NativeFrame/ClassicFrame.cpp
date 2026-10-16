#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <sddl.h>
#include <strsafe.h>
#include <string>
#include <new>
#include <cmath>
#include <algorithm>
#include <cwctype>
#include "ClassicGlass.h"
using std::min;
using std::max;

// Optional classic-window adapter. Bootstrap hooks are thread-specific and removed
// after attachment; no system DLL replacement or global hooks. Never use thread ID zero.
namespace {
constexpr wchar_t Active[] = L"VeliShell.ClassicFrame.Active";
constexpr wchar_t Consent[] = L"VeliShell.ClassicFrame.Consent";
constexpr UINT_PTR Subclass = 0x56454C43;
constexpr DWORD Magic = 0x56464332;
constexpr int Pending = 0, Attached = 1, Restored = 2, Gone = 3, Failed = -1;
constexpr int Attaching = 4;
struct Shared {
    DWORD magic, guardian, target, thread;
    ULONGLONG guardianStart, targetStart;
    HWND window;
    LONG state, stop, error;
    volatile LONG64 heartbeat;
    int dark, gloss, transparency;
    DWORD tint;
    LONG_PTR style, exStyle;
    int policy, backdrop, corner, immersive;
    unsigned attributes;
};
struct Broker { HANDLE mapping{}; Shared* data{}; HHOOK hook{}; ATOM atom{}; };
struct Frame { HANDLE mapping; Shared* data; int hover = -1, pressed = -1; HWND caption{}; ClassicGlass* glass{}; HRGN region{}; bool syncing=false,retired=false,restoring=false; int references=1; UINT_PTR timer{}; };
static thread_local std::map<UINT_PTR,HWND> recoveryTimers;
ULONGLONG Heartbeat(Shared* data) { return static_cast<ULONGLONG>(InterlockedCompareExchange64(&data->heartbeat,0,0)); }
void Release(Frame*);
struct FrameHold { Frame* frame; explicit FrameHold(Frame* value):frame(value) { ++frame->references; } ~FrameHold() { Release(frame); } };
LRESULT CALLBACK Procedure(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
LRESULT CALLBACK CaptionProcedure(HWND,UINT,WPARAM,LPARAM);
bool Sync(HWND,Frame*);
bool Restore(HWND);
void CALLBACK RecoveryTick(HWND,UINT,UINT_PTR id,DWORD) {
    auto entry=recoveryTimers.find(id); if(entry==recoveryTimers.end())return;
    HWND window=entry->second; auto f=reinterpret_cast<Frame*>(GetPropW(window,Active)); if(!f)return;
    FrameHold hold(f);
    if(f->data->stop || GetTickCount64()-Heartbeat(f->data)>3000 || !Sync(window,f)) { InterlockedExchange(&f->data->stop,1); Restore(window); }
    else if(!f->retired) { SetPropW(window,L"VeliShell.ClassicFrame.Glass",reinterpret_cast<HANDLE>(1)); InterlockedCompareExchange(&f->data->state,Attached,Attaching); }
}
bool ReadPolicy(HWND window,int& policy) {
    // Windows exposes the effective enabled state, not the original write-only
    // policy enum. Restore that effective state; never claim byte-exact policy recovery.
    BOOL enabled=FALSE;
    if(FAILED(DwmGetWindowAttribute(window,DWMWA_NCRENDERING_ENABLED,&enabled,sizeof(enabled)))) return false;
    policy=enabled?DWMNCRP_ENABLED:DWMNCRP_DISABLED; return true;
}
UINT Bootstrap() { return RegisterWindowMessageW(L"VeliShell.ClassicFrame.Bootstrap.v2"); }
ULONGLONG ProcessStarted(HANDLE process) {
    FILETIME start{}, end{}, kernel{}, user{};
    if (!GetProcessTimes(process, &start, &end, &kernel, &user)) return 0;
    return (static_cast<ULONGLONG>(start.dwHighDateTime) << 32) | start.dwLowDateTime;
}
bool SameToken(HANDLE process) {
    HANDLE ours{}, theirs{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &ours)) return false;
    if (!OpenProcessToken(process, TOKEN_QUERY, &theirs)) { CloseHandle(ours); return false; }
    BYTE a[1024]{}, b[1024]{}; DWORD length{};
    bool same = GetTokenInformation(ours, TokenUser, a, sizeof(a), &length) && GetTokenInformation(theirs, TokenUser, b, sizeof(b), &length)
        && EqualSid(reinterpret_cast<TOKEN_USER*>(a)->User.Sid, reinterpret_cast<TOKEN_USER*>(b)->User.Sid);
    if (same) {
        same = GetTokenInformation(ours, TokenIntegrityLevel, a, sizeof(a), &length) && GetTokenInformation(theirs, TokenIntegrityLevel, b, sizeof(b), &length);
        if (same) {
            auto sa = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(a)->Label.Sid;
            auto sb = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(b)->Label.Sid;
            DWORD ia = *GetSidSubAuthority(sa, *GetSidSubAuthorityCount(sa)-1), ib = *GetSidSubAuthority(sb, *GetSidSubAuthorityCount(sb)-1);
            same = ia == SECURITY_MANDATORY_MEDIUM_RID && ia == ib;
        }
    }
    CloseHandle(ours); CloseHandle(theirs); return same;
}
bool Eligible(HWND window) {
    HIGHCONTRASTW contrast{sizeof(contrast)};
    if(SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(contrast),&contrast,0) && (contrast.dwFlags&HCF_HIGHCONTRASTON)) return false;
    if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) || GetAncestor(window, GA_ROOT) != window || GetWindow(window, GW_OWNER)) return false;
    DWORD pid{}; if (!GetWindowThreadProcessId(window, &pid) || pid == GetCurrentProcessId()) return false;
    const LONG_PTR style = GetWindowLongPtrW(window, GWL_STYLE), ex = GetWindowLongPtrW(window, GWL_EXSTYLE);
    if ((style & (WS_CAPTION | WS_SYSMENU | WS_THICKFRAME)) != (WS_CAPTION | WS_SYSMENU | WS_THICKFRAME) ||
        (ex & (WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYOUTRTL)) || (style & WS_CHILD)) return false;
    HRGN region=CreateRectRgn(0,0,0,0); if(!region) return false;
    int regionType=GetWindowRgn(window,region); DeleteObject(region); if(regionType!=ERROR) return false;
    wchar_t cls[256]{}; GetClassNameW(window, cls, 256);
    // Modern custom chrome cannot be reliably replaced by a classic NC adapter.
    for (auto denied : {L"Chrome", L"Mozilla", L"HwndWrapper", L"ApplicationFrame", L"WinUIDesktop", L"CabinetWClass", L"Progman", L"WorkerW", L"Shell_", L"Windows.UI", L"UnityWndClass", L"UnrealWindow", L"SDL_app", L"GLFW", L"Qt"})
        if (wcsstr(cls, denied)) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid);
    if (!process) return false;
    USHORT machine{}, native{}; DWORD ourSession{}, session{};
    wchar_t path[32768]{}, system[32768]{}; DWORD size = 32768;
    bool okay = SameToken(process) && IsWow64Process2(process, &machine, &native) && machine == IMAGE_FILE_MACHINE_UNKNOWN && native == IMAGE_FILE_MACHINE_AMD64 &&
        ProcessIdToSessionId(pid, &session) && ProcessIdToSessionId(GetCurrentProcessId(), &ourSession) && session == ourSession &&
        QueryFullProcessImageNameW(process, 0, path, &size);
    GetWindowsDirectoryW(system, 32768);
    if (okay && (_wcsnicmp(path, system, wcslen(system)) == 0 || wcsstr(path, L"\\WindowsApps\\"))) okay = false;
    int cloaked{}; DwmGetWindowAttribute(window, 14, &cloaked, sizeof(cloaked));
    CloseHandle(process); return okay && !cloaked;
}
int Dip(HWND w, int value) { return MulDiv(value, static_cast<int>(GetDpiForWindow(w)), 96); }
RECT Caption(HWND w) {
    RECT r{}; GetWindowRect(w, &r); r.right -= r.left; r.bottom -= r.top; r.left = r.top = 0;
    int border = IsZoomed(w) ? GetSystemMetricsForDpi(SM_CYSIZEFRAME, GetDpiForWindow(w)) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, GetDpiForWindow(w)) : Dip(w, 1);
    r.left = border; r.top = border; r.right -= border;
    // Preserve Windows' exact client/menu geometry; only replace its existing caption.
    r.bottom = GetSystemMetricsForDpi(SM_CYSIZEFRAME, GetDpiForWindow(w)) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, GetDpiForWindow(w)) + GetSystemMetricsForDpi(SM_CYCAPTION, GetDpiForWindow(w));
    return r;
}
int ButtonAt(HWND w, LPARAM point) {
    RECT wr{}; GetWindowRect(w, &wr); RECT r = Caption(w);
    int x = GET_X_LPARAM(point)-wr.left, y = GET_Y_LPARAM(point)-wr.top;
    if (y < r.top || y >= r.bottom) return -1;
    for (int i = 0; i < 3; ++i) if (abs(x - Dip(w, 23 + i*22)) <= Dip(w, 9)) return i;
    return -1;
}
void Paint(HWND w, Frame* frame) {
    HDC dc = GetWindowDC(w); if (!dc) return;
    RECT bounds{}, client{}; POINT origin{}; GetWindowRect(w,&bounds); GetClientRect(w,&client); ClientToScreen(w,&origin);
    const int widthWindow=bounds.right-bounds.left, heightWindow=bounds.bottom-bounds.top;
    const int left=origin.x-bounds.left, right=left+client.right, bottom=origin.y-bounds.top+client.bottom;
    HBRUSH border=CreateSolidBrush(frame->data->dark?RGB(30,37,52):RGB(226,233,244));
    RECT sides[]{ {0,0,left,heightWindow},{right,0,widthWindow,heightWindow},{0,bottom,widthWindow,heightWindow} };
    for(auto side:sides) FillRect(dc,&side,border); DeleteObject(border);
    if(frame->glass) { ReleaseDC(w,dc); return; }
    RECT rect = Caption(w); HDC buffer{}; BP_PAINTPARAMS params{sizeof(params)}; params.dwFlags = BPPF_ERASE;
    HPAINTBUFFER paint = BeginBufferedPaint(dc, &rect, BPBF_TOPDOWNDIB, &params, &buffer);
    RGBQUAD* bits{}; int stride{};
    if (paint && SUCCEEDED(GetBufferedPaintBits(paint, &bits, &stride))) {
        const int width = rect.right-rect.left, height = rect.bottom-rect.top;
        const bool dark = frame->data->dark != 0;
        // Readable matte NC fallback. HostBackdropBrush is client-clipped in foreign classic windows.
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
            const int a = 255;
            const int rr = dark ? 25 : 235, gg = dark ? 32 : 243, bb = dark ? 48 : 255;
            bits[y*stride+x] = {static_cast<BYTE>(bb*a/255), static_cast<BYTE>(gg*a/255), static_cast<BYTE>(rr*a/255), static_cast<BYTE>(a)};
        }
        constexpr DWORD colors[]{0xFF5F57,0xFEBC2E,0x28C840};
        for (int i=0; i<3; ++i) {
            double cx=static_cast<double>(Dip(w,23+i*22)-rect.left), cy=height/2.0, radius=static_cast<double>(Dip(w,6));
            for (int y=max(0,static_cast<int>(cy-radius-1)); y<min(height,static_cast<int>(cy+radius+2)); ++y)
                for (int x=max(0,static_cast<int>(cx-radius-1)); x<min(width,static_cast<int>(cx+radius+2)); ++x) {
                    double coverage = min(1.0,max(0.0,radius+.5-std::hypot(x+.5-cx,y+.5-cy)));
                    auto& p=bits[y*stride+x]; DWORD color=colors[i];
                    p.rgbRed=static_cast<BYTE>((color>>16)*coverage+p.rgbRed*(1-coverage));
                    p.rgbGreen=static_cast<BYTE>(((color>>8)&255)*coverage+p.rgbGreen*(1-coverage));
                    p.rgbBlue=static_cast<BYTE>((color&255)*coverage+p.rgbBlue*(1-coverage));
                    p.rgbReserved=static_cast<BYTE>(255*coverage+p.rgbReserved*(1-coverage));
                }
        }
        HTHEME theme=OpenThemeData(w,L"WINDOW");
        if(theme) {
            HFONT font=CreateFontW(-Dip(w,12),0,0,0,FW_MEDIUM,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
            auto oldFont=SelectObject(buffer,font);
            wchar_t title[512]{}; GetWindowTextW(w,title,512);
            RECT text=rect; text.left=rect.left+Dip(w,100); text.right-=Dip(w,100);
            DTTOPTS options{sizeof(options)}; options.dwFlags=DTT_COMPOSITED|DTT_TEXTCOLOR;
            options.crText=dark?RGB(240,244,252):RGB(29,41,66);
            DrawThemeTextEx(theme,buffer,0,0,title,-1,DT_SINGLELINE|DT_VCENTER|DT_CENTER|DT_END_ELLIPSIS,&text,&options);
            CloseThemeData(theme);
            SelectObject(buffer,oldFont); DeleteObject(font);
        }
        EndBufferedPaint(paint,TRUE);
    } else if(paint) EndBufferedPaint(paint,FALSE);
    ReleaseDC(w,dc);
}
void Release(Frame* f) {
    if(--f->references!=0)return;
    delete f->glass; f->glass=nullptr;
    if(f->caption) { SetWindowLongPtrW(f->caption,GWLP_USERDATA,0); DestroyWindow(f->caption); }
    if(f->region) DeleteObject(f->region);
    UnmapViewOfFile(f->data); CloseHandle(f->mapping); delete f;
}
bool OurRegion(HWND w,Frame* f) {
    HRGN actual=CreateRectRgn(0,0,0,0); if(!actual) return false;
    int kind=GetWindowRgn(w,actual); bool same=f->region ? kind!=ERROR && EqualRgn(actual,f->region) : kind==ERROR;
    DeleteObject(actual); return same;
}
bool Sync(HWND w,Frame* f) {
    if(f->retired)return false;
    if(f->syncing || f->restoring) return true;
    if(!IsWindowVisible(w) || IsIconic(w)) { ShowWindow(f->caption,SW_HIDE); return true; }
    f->syncing=true;
    RECT bounds{}; GetWindowRect(w,&bounds); RECT area=Caption(w);
    const int inset=IsZoomed(w)?area.top:0;
    const int width=bounds.right-bounds.left-2*inset, height=area.bottom-inset;
    if(width<200 || height<20 || !OurRegion(w,f)) { f->data->error=21; f->syncing=false; return false; }
    if(!f->caption || !f->glass || !IsWindow(f->caption)) { f->syncing=false; return false; }
    const HRESULT painted=f->glass->Paint(w,width,height,f->data->dark,f->data->gloss,f->data->transparency,f->data->tint,f->hover,IsZoomed(w)!=FALSE);
    if(FAILED(painted)) { f->data->error=static_cast<LONG>(painted); f->syncing=false; return false; }
    RECT previous{}; if(f->region) GetRgnBox(f->region,&previous);
    RECT next{0,area.bottom,bounds.right-bounds.left,bounds.bottom-bounds.top};
    if(!f->region || !EqualRect(&previous,&next)) {
        HRGN region=CreateRectRgn(next.left,next.top,next.right,next.bottom), copy=CreateRectRgn(next.left,next.top,next.right,next.bottom);
        if(!region || !copy) { if(region)DeleteObject(region); if(copy)DeleteObject(copy); f->data->error=22; f->syncing=false; return false; }
        auto old=f->region; f->region=copy;
        if(!SetWindowRgn(w,region,TRUE)) { DeleteObject(region); DeleteObject(copy); f->region=old; f->data->error=23; f->syncing=false; return false; }
        if(old)DeleteObject(old);
        if(f->retired) { f->syncing=false; return false; }
    }
    // Ownership supplies the z-order. Never lift an inactive app's title over
    // unrelated foreground windows on a periodic synchronization tick.
    SetWindowPos(f->caption,nullptr,bounds.left+inset,bounds.top+inset,width,height,SWP_NOACTIVATE|SWP_NOZORDER|SWP_SHOWWINDOW);
    f->syncing=false; return true;
}
int CaptionButton(HWND host,LPARAM point) {
    auto f=reinterpret_cast<Frame*>(GetWindowLongPtrW(host,GWLP_USERDATA)); if(!f) return -1;
    int x=GET_X_LPARAM(point), y=GET_Y_LPARAM(point); RECT r{}; GetClientRect(host,&r);
    if(y<0 || y>=r.bottom) return -1;
    for(int i=0;i<3;++i) if(abs(x-Dip(f->data->window,24+22*i))<=Dip(f->data->window,9)) return i;
    return -1;
}
LRESULT CALLBACK CaptionProcedure(HWND host,UINT message,WPARAM wp,LPARAM lp) {
    if(message==WM_NCCREATE) SetWindowLongPtrW(host,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams));
    auto f=reinterpret_cast<Frame*>(GetWindowLongPtrW(host,GWLP_USERDATA));
    if(!f)return DefWindowProcW(host,message,wp,lp);
    FrameHold hold(f); if(f->retired)return DefWindowProcW(host,message,wp,lp);
    if(message==WM_NCDESTROY) { SetWindowLongPtrW(host,GWLP_USERDATA,0); if(f->caption==host)f->caption=nullptr; return DefWindowProcW(host,message,wp,lp); }
    HWND owner=f->data->window;
    if(message==WM_ERASEBKGND)return TRUE;
    if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_NCHITTEST)return HTCLIENT;
    if(message==WM_MOUSEMOVE) { int hover=CaptionButton(host,lp); if(f->hover!=hover) { f->hover=hover; Sync(owner,f); } TRACKMOUSEEVENT t{sizeof(t),TME_LEAVE,host,0}; TrackMouseEvent(&t); return 0; }
    if(message==WM_MOUSELEAVE) { f->hover=-1; Sync(owner,f); return 0; }
    if(message==WM_LBUTTONDOWN) {
        if(!IsWindowEnabled(owner)) { SetForegroundWindow(GetLastActivePopup(owner)); return 0; }
        SetForegroundWindow(owner); f->pressed=CaptionButton(host,lp);
        if(f->pressed>=0) { SetCapture(host); return 0; }
        POINT p{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)}; ClientToScreen(host,&p); LPARAM screen=MAKELPARAM(p.x,p.y);
        LRESULT hit=SendMessageW(owner,WM_NCHITTEST,0,screen);
        PostMessageW(owner,WM_NCLBUTTONDOWN,hit>=HTLEFT && hit<=HTBOTTOMRIGHT?hit:HTCAPTION,screen); return 0;
    }
    if(message==WM_LBUTTONUP && f->pressed>=0) {
        const int pressed=f->pressed,released=CaptionButton(host,lp); f->pressed=-1; ReleaseCapture();
        auto style=GetWindowLongPtrW(owner,GWL_STYLE);
        if(pressed==released) {
            if(pressed==0)PostMessageW(owner,WM_SYSCOMMAND,SC_CLOSE,0);
            else if(pressed==1 && (style&WS_MINIMIZEBOX))PostMessageW(owner,WM_SYSCOMMAND,SC_MINIMIZE,0);
            else if(pressed==2 && (style&WS_MAXIMIZEBOX))PostMessageW(owner,WM_SYSCOMMAND,IsZoomed(owner)?SC_RESTORE:SC_MAXIMIZE,0);
        } return 0;
    }
    if(message==WM_CAPTURECHANGED || message==WM_CANCELMODE) f->pressed=-1;
    if(message==WM_LBUTTONDBLCLK && CaptionButton(host,lp)<0 && (GetWindowLongPtrW(owner,GWL_STYLE)&WS_MAXIMIZEBOX)) { PostMessageW(owner,WM_SYSCOMMAND,IsZoomed(owner)?SC_RESTORE:SC_MAXIMIZE,0); return 0; }
    if(message==WM_RBUTTONUP) {
        POINT p{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)}; ClientToScreen(host,&p); SetForegroundWindow(owner);
        UINT command=TrackPopupMenu(GetSystemMenu(owner,FALSE),TPM_RETURNCMD|TPM_RIGHTBUTTON,p.x,p.y,0,owner,nullptr);
        if(command)PostMessageW(owner,WM_SYSCOMMAND,command,0); return 0;
    }
    return DefWindowProcW(host,message,wp,lp);
}
bool Restore(HWND w) {
    auto f=reinterpret_cast<Frame*>(GetPropW(w,Active)); if(!f) return true;
    FrameHold hold(f); if(f->retired)return true; if(f->restoring)return false; f->restoring=true;
    // This adapter never changes style, placement, app client rectangle, menu or WndProc.
    bool restored=true;
    if(f->data->attributes&1) restored=SUCCEEDED(DwmSetWindowAttribute(w,2,&f->data->policy,sizeof(int))) && restored;
    if(f->data->attributes&2) restored=SUCCEEDED(DwmSetWindowAttribute(w,33,&f->data->corner,sizeof(int))) && restored;
    if(f->data->attributes&4) restored=SUCCEEDED(DwmSetWindowAttribute(w,20,&f->data->immersive,sizeof(int))) && restored;
    if(f->retired)return true;
    if(!restored || !RemoveWindowSubclass(w, Procedure, Subclass)) { f->restoring=false; return false; }
    if(f->region && OurRegion(w,f) && !SetWindowRgn(w,nullptr,TRUE)) { SetWindowSubclass(w,Procedure,Subclass,0); f->restoring=false; return false; }
    f->retired=true;
    if(f->timer) { KillTimer(nullptr,f->timer); recoveryTimers.erase(f->timer); }
    RemovePropW(w,Active); RemovePropW(w,Consent);
    RemovePropW(w,L"VeliShell.ClassicFrame.Caption"); RemovePropW(w,L"VeliShell.ClassicFrame.Glass");
    SetWindowPos(w,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
    RedrawWindow(w,nullptr,nullptr,RDW_INVALIDATE|RDW_FRAME|RDW_ALLCHILDREN);
    InterlockedExchange(&f->data->state,Restored); Release(f); return true;
}
LRESULT CALLBACK Procedure(HWND w, UINT message, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR) {
    auto f=reinterpret_cast<Frame*>(GetPropW(w,Active));
    if(!f) return DefSubclassProc(w,message,wp,lp);
    FrameHold hold(f);
    if(message==WM_NCDESTROY) {
        f->retired=true;
        if(f->timer) { KillTimer(nullptr,f->timer); recoveryTimers.erase(f->timer); }
        RemovePropW(w,Active); RemovePropW(w,Consent); RemovePropW(w,L"VeliShell.ClassicFrame.Caption"); RemovePropW(w,L"VeliShell.ClassicFrame.Glass"); RemoveWindowSubclass(w,Procedure,Subclass);
        InterlockedExchange(&f->data->state,Gone); Release(f); return DefSubclassProc(w,message,wp,lp);
    }
    if(f->restoring || f->retired)return DefSubclassProc(w,message,wp,lp);
    if(message==Bootstrap()) {
        if(f->data->stop || GetTickCount64()-Heartbeat(f->data)>3000) Restore(w);
        else if(f->glass && !Sync(w,f)) { InterlockedExchange(&f->data->stop,1); Restore(w); }
        return DefSubclassProc(w,message,wp,lp);
    }
    if(message==WM_SETTINGCHANGE) {
        HIGHCONTRASTW contrast{sizeof(contrast)};
        if(SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(contrast),&contrast,0) && (contrast.dwFlags&HCF_HIGHCONTRASTON)) { Restore(w); return DefSubclassProc(w,message,wp,lp); }
    }
    if(message==WM_NCPAINT || message==WM_NCACTIVATE || message==WM_SETTEXT || message==WM_SIZE || message==WM_THEMECHANGED || message==WM_WINDOWPOSCHANGED || message==WM_DPICHANGED) {
        LRESULT result=DefSubclassProc(w,message,wp,lp);
        if(GetPropW(w,Active)!=f)return result;
        Paint(w,f); if(f->glass && !Sync(w,f)) InterlockedExchange(&f->data->stop,1); return result;
    }
    if(message==WM_NCHITTEST) {
        auto hit=DefSubclassProc(w,message,wp,lp);
        if(hit==HTCLOSE || hit==HTMINBUTTON || hit==HTMAXBUTTON || ButtonAt(w,lp)>=0) return HTCAPTION;
        return hit;
    }
    if(message==WM_NCMOUSEMOVE) { TRACKMOUSEEVENT track{sizeof(track),TME_LEAVE|TME_NONCLIENT,w,0}; TrackMouseEvent(&track); }
    if(message==WM_NCMOUSELEAVE || message==WM_MOUSEMOVE || message==WM_CANCELMODE || message==WM_ACTIVATE && LOWORD(wp)==WA_INACTIVE) f->pressed=-1;
    // Do not steal capture from the target application's own controls.
    if(message==WM_NCLBUTTONDOWN) { f->pressed=ButtonAt(w,lp); if(f->pressed>=0) return 0; }
    if(message==WM_NCLBUTTONUP && f->pressed>=0) {
        int pressed=f->pressed; f->pressed=-1;
        const int released=ButtonAt(w,lp);
        if(released==pressed) {
            const auto style=GetWindowLongPtrW(w,GWL_STYLE);
            if(pressed==0) PostMessageW(w,WM_SYSCOMMAND,SC_CLOSE,0);
            if(pressed==1 && (style&WS_MINIMIZEBOX)) PostMessageW(w,WM_SYSCOMMAND,SC_MINIMIZE,0);
            if(pressed==2 && (style&WS_MAXIMIZEBOX)) PostMessageW(w,WM_SYSCOMMAND,IsZoomed(w)?SC_RESTORE:SC_MAXIMIZE,0);
        }
        return 0;
    }
    return DefSubclassProc(w,message,wp,lp);
}
void Attach(HANDLE mapping, Shared* data) {
    auto f=new(std::nothrow) Frame{mapping,data};
    if(!f) { InterlockedExchange(&data->state,Failed); UnmapViewOfFile(data); CloseHandle(mapping); return; }
    HWND w=data->window;
    data->style=GetWindowLongPtrW(w,GWL_STYLE); data->exStyle=GetWindowLongPtrW(w,GWL_EXSTYLE);
    data->policy=0; data->backdrop=1;
    data->attributes=0;
    if(ReadPolicy(w,data->policy)) data->attributes|=1;
    if(SUCCEEDED(DwmGetWindowAttribute(w,33,&data->corner,sizeof(int)))) data->attributes|=2;
    if(SUCCEEDED(DwmGetWindowAttribute(w,20,&data->immersive,sizeof(int)))) data->attributes|=4;
    if(!(data->attributes&1)) { data->error=10; InterlockedExchange(&data->state,Failed); Release(f); return; }
    if(!SetPropW(w,Active,f) || !SetWindowSubclass(w,Procedure,Subclass,0)) {
        RemovePropW(w,Active); InterlockedExchange(&data->state,Failed); Release(f); return;
    }
    FrameHold lifetime(f);
    HMODULE pinned{};
    // Hook callbacks can still be executing after Unhook; never forcibly unload target-side code.
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&Procedure),&pinned)) { Restore(w); return; }
    int policy=1,corner=2;
    if(FAILED(DwmSetWindowAttribute(w,2,&policy,sizeof(policy)))) { Restore(w); return; }
    if(data->attributes&2) DwmSetWindowAttribute(w,33,&corner,sizeof(corner));
    if(data->attributes&4) DwmSetWindowAttribute(w,20,&data->dark,sizeof(int));
    HMODULE module{}; GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&Procedure),&module);
    WNDCLASSEXW cls{sizeof(cls)}; cls.hInstance=module; cls.lpfnWndProc=CaptionProcedure; cls.lpszClassName=L"VeliShellNativeCaption"; cls.hCursor=LoadCursorW(nullptr,IDC_ARROW); cls.style=CS_DBLCLKS;
    if(!RegisterClassExW(&cls) && GetLastError()!=ERROR_CLASS_ALREADY_EXISTS) { data->error=11; Restore(w); return; }
    f->caption=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_NOREDIRECTIONBITMAP,cls.lpszClassName,L"VeliShell native glass caption",WS_POPUP,0,0,300,32,w,nullptr,module,f);
    f->glass=new(std::nothrow) ClassicGlass();
    if(!f->caption || !f->glass || !f->glass->Init(f->caption) || !Sync(w,f)) { if(f->glass && FAILED(f->glass->error)) data->error=f->glass->error; if(!data->error)data->error=12; Restore(w); return; }
    SetPropW(w,L"VeliShell.ClassicFrame.Caption",f->caption);
    f->timer=SetTimer(nullptr,0,250,RecoveryTick); // Unique thread timer: never replace an app's HWND timer.
    if(!f->timer) { Restore(w); return; }
    try { recoveryTimers.emplace(f->timer,w); }
    catch(...) { KillTimer(nullptr,f->timer); f->timer=0; data->error=13; Restore(w); return; }
    RedrawWindow(w,nullptr,nullptr,RDW_INVALIDATE|RDW_FRAME|RDW_ALLCHILDREN); Paint(w,f);
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK VeliClassicHook(int code, WPARAM wp, LPARAM lp) {
    if(code>=0) {
        auto message=reinterpret_cast<CWPSTRUCT*>(lp);
        if(message->message==Bootstrap() && !GetPropW(message->hwnd,Active)) {
            wchar_t name[128]{};
            if(GlobalGetAtomNameW(static_cast<ATOM>(message->wParam),name,128) && wcsncmp(name,L"Local\\VeliShell.Classic.",23)==0) {
                HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
                auto data=mapping?static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared))):nullptr;
                bool valid=data && data->magic==Magic && data->target==GetCurrentProcessId() && data->thread==GetCurrentThreadId() &&
                    data->window==message->hwnd && GetPropW(message->hwnd,Consent)==reinterpret_cast<HANDLE>(message->wParam) &&
                    data->targetStart==ProcessStarted(GetCurrentProcess()) && !data->stop && GetTickCount64()-Heartbeat(data)<3000;
                HANDLE guardian=valid?OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,data->guardian):nullptr;
                valid=valid && guardian && ProcessStarted(guardian)==data->guardianStart && SameToken(guardian) && WaitForSingleObject(guardian,0)==WAIT_TIMEOUT;
                if(guardian) CloseHandle(guardian);
                if(valid && InterlockedCompareExchange(&data->state,Attaching,Pending)==Pending) Attach(mapping,data);
                else { if(data) { data->error=20; UnmapViewOfFile(data); } if(mapping) CloseHandle(mapping); }
            }
        }
    }
    return CallNextHookEx(nullptr,code,wp,lp);
}
extern "C" __declspec(dllexport) int __cdecl VeliClassicEligible(HWND window) { return Eligible(window)?1:0; }
extern "C" __declspec(dllexport) void* __cdecl VeliClassicBegin(HWND window, const wchar_t* session, DWORD expectedPid, ULONGLONG expectedStart, int dark, int gloss, int transparency,DWORD tint) {
    if(!Eligible(window) || GetPropW(window,Active) || !session || wcslen(session)!=32) return nullptr;
    RECT bounds{}; GetWindowRect(window,&bounds); DWORD_PTR hit{};
    const int x=(bounds.left+bounds.right)/2,y=bounds.top+Dip(window,16);
    if(!SendMessageTimeoutW(window,WM_NCHITTEST,0,MAKELPARAM(x,y),SMTO_ABORTIFHUNG|SMTO_BLOCK,75,&hit) || hit!=HTCAPTION) return nullptr;
    for(size_t i=0;i<32;++i) if(!iswxdigit(session[i])) return nullptr;
    auto b=new(std::nothrow) Broker{}; if(!b) return nullptr;
    wchar_t name[128]{}; StringCchPrintfW(name,128,L"Local\\VeliShell.Classic.%s",session);
    // Mapping is accessible only to the current logon user, not Everyone or other sessions.
    HANDLE token{}; BYTE info[1024]{}; DWORD length{}; LPWSTR sid{}; PSECURITY_DESCRIPTOR descriptor{};
    bool secure=OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token) && GetTokenInformation(token,TokenUser,info,sizeof(info),&length) &&
        ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(info)->User.Sid,&sid);
    if(secure) { std::wstring sddl=L"D:P(A;;GA;;;"; sddl+=sid; sddl+=L")"; secure=ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr)!=FALSE; }
    if(sid) LocalFree(sid); if(token) CloseHandle(token);
    SECURITY_ATTRIBUTES security{sizeof(security),descriptor,FALSE};
    if(secure) b->mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,&security,PAGE_READWRITE,0,sizeof(Shared),name);
    DWORD error=GetLastError(); if(descriptor) LocalFree(descriptor);
    if(b->mapping && error!=ERROR_ALREADY_EXISTS) b->data=static_cast<Shared*>(MapViewOfFile(b->mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
    if(!b->data) { if(b->mapping) CloseHandle(b->mapping); delete b; return nullptr; }
    auto d=b->data; ZeroMemory(d,sizeof(Shared)); d->magic=Magic; d->guardian=GetCurrentProcessId(); d->guardianStart=ProcessStarted(GetCurrentProcess());
    d->thread=GetWindowThreadProcessId(window,&d->target); d->window=window;
    HANDLE target=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,d->target); d->targetStart=target?ProcessStarted(target):0; if(target) CloseHandle(target);
    InterlockedExchange64(&d->heartbeat,static_cast<LONG64>(GetTickCount64())); d->dark=dark?1:0; d->gloss=max(0,min(100,gloss)); d->transparency=max(0,min(100,transparency));
    d->tint=tint;
    if(d->target!=expectedPid || d->targetStart!=expectedStart) { UnmapViewOfFile(d); CloseHandle(b->mapping); delete b; return nullptr; }
    b->atom=GlobalAddAtomW(name); HMODULE module{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&VeliClassicHook),&module);
    if(d->thread && d->targetStart && b->atom && SetPropW(window,Consent,reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(b->atom)))) b->hook=SetWindowsHookExW(WH_CALLWNDPROC,VeliClassicHook,module,d->thread);
    if(!b->hook) { if(GetPropW(window,Consent)==reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(b->atom))) RemovePropW(window,Consent); if(b->atom) GlobalDeleteAtom(b->atom); UnmapViewOfFile(d); CloseHandle(b->mapping); delete b; return nullptr; }
    // A sent message executes WH_CALLWNDPROC on precisely this thread (never a global hook).
    DWORD_PTR result{}; SendMessageTimeoutW(window,Bootstrap(),b->atom,0,SMTO_ABORTIFHUNG|SMTO_BLOCK,1000,&result);
    return b;
}
extern "C" __declspec(dllexport) int __cdecl VeliClassicPoll(void* session, int stop) {
    auto b=static_cast<Broker*>(session); if(!b || !b->data) return Failed;
    InterlockedExchange64(&b->data->heartbeat,static_cast<LONG64>(GetTickCount64()));
    if(stop) { InterlockedExchange(&b->data->stop,1); PostMessageW(b->data->window,Bootstrap(),0,0); }
    auto state=InterlockedCompareExchange(&b->data->state,0,0);
    if(state==Attached && b->hook) { UnhookWindowsHookEx(b->hook); b->hook=nullptr; } // Bootstrap finished; only the per-window subclass remains.
    return static_cast<int>(state);
}
extern "C" __declspec(dllexport) void __cdecl VeliClassicConfigure(void* session,int dark,int gloss,int transparency,DWORD tint) {
    auto b=static_cast<Broker*>(session); if(!b)return;
    InterlockedExchange(reinterpret_cast<LONG*>(&b->data->dark),dark?1:0);
    InterlockedExchange(reinterpret_cast<LONG*>(&b->data->gloss),max(0,min(100,gloss)));
    InterlockedExchange(reinterpret_cast<LONG*>(&b->data->transparency),max(0,min(100,transparency)));
    InterlockedExchange(reinterpret_cast<LONG*>(&b->data->tint),static_cast<LONG>(tint));
}
extern "C" __declspec(dllexport) int __cdecl VeliClassicError(void* session) { auto b=static_cast<Broker*>(session); return b?b->data->error:-1; }
extern "C" __declspec(dllexport) int __cdecl VeliClassicEnd(void* session) {
    auto b=static_cast<Broker*>(session); if(!b) return 1;
    auto state=InterlockedCompareExchange(&b->data->state,0,0);
    DWORD currentPid{}; GetWindowThreadProcessId(b->data->window,&currentPid);
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,b->data->target);
    const bool gone=currentPid!=b->data->target || (process && (WaitForSingleObject(process,0)!=WAIT_TIMEOUT || ProcessStarted(process)!=b->data->targetStart));
    if(process)CloseHandle(process);
    if((state==Attached || state==Attaching) && !gone) return 0; // Never abandon a live subclass.
    InterlockedExchange(&b->data->stop,1);
    if(state==Pending && InterlockedCompareExchange(&b->data->state,Failed,Pending)!=Pending) return 0;
    if(b->hook) UnhookWindowsHookEx(b->hook);
    if(GetPropW(b->data->window,Consent)==reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(b->atom))) RemovePropW(b->data->window,Consent);
    if(b->atom) GlobalDeleteAtom(b->atom);
    UnmapViewOfFile(b->data); CloseHandle(b->mapping); delete b; return 1;
}
