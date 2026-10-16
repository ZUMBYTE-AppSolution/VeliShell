#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <string>
#include <cstddef>
#include <algorithm>

extern "C" __declspec(dllimport) void* __cdecl VeliClassicBegin(HWND,const wchar_t*,DWORD,ULONGLONG,int,int,int,DWORD);
extern "C" __declspec(dllimport) int __cdecl VeliClassicPoll(void*,int);
extern "C" __declspec(dllimport) int __cdecl VeliClassicEnd(void*);
extern "C" __declspec(dllimport) int __cdecl VeliClassicError(void*);
extern "C" __declspec(dllimport) int __cdecl VeliClassicBeginError();
extern "C" __declspec(dllimport) void __cdecl VeliClassicConfigure(void*,int,int,int,DWORD);
extern "C" __declspec(dllimport) int __cdecl VeliMaterialEligible(HWND);

// Pointer-free, fixed-width protocol. This helper is x86; the supervisor is x64.
struct Control {
    DWORD magic,mode;
    volatile LONG64 heartbeat;
    LONG state,stop,error,dark,gloss,transparency;
    DWORD tint,owner;
    ULONGLONG ownerStart;
    DWORD target,reserved;
    ULONGLONG targetStart,window;
    DWORD bridge,reserved2;
    ULONGLONG reserved3;
    DWORD mask;
    struct Attribute { DWORD original,applied; } attributes[6];
};
static_assert(offsetof(Control,heartbeat)==8 && offsetof(Control,ownerStart)==48 && offsetof(Control,window)==72 && offsetof(Control,mask)==96);
constexpr DWORD Magic=0x56464233;
constexpr DWORD Attributes[]{20,33,34,35,36,38};
constexpr wchar_t MaterialMarker[]=L"VeliShell.Material.Active";
ULONGLONG Started(HANDLE process) { FILETIME a{},b{},c{},d{}; if(!GetProcessTimes(process,&a,&b,&c,&d))return 0; return (static_cast<ULONGLONG>(a.dwHighDateTime)<<32)|a.dwLowDateTime; }
bool SameUser(HANDLE process) {
    HANDLE ours{},theirs{}; if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&ours))return false;
    if(!OpenProcessToken(process,TOKEN_QUERY,&theirs)) { CloseHandle(ours); return false; }
    BYTE a[1024]{},b[1024]{}; DWORD n{};
    bool okay=GetTokenInformation(ours,TokenUser,a,sizeof(a),&n) && GetTokenInformation(theirs,TokenUser,b,sizeof(b),&n) &&
        EqualSid(reinterpret_cast<TOKEN_USER*>(a)->User.Sid,reinterpret_cast<TOKEN_USER*>(b)->User.Sid);
    CloseHandle(ours); CloseHandle(theirs); return okay;
}
bool Identity(Control* c,HWND window,HANDLE target) {
    DWORD pid{}; GetWindowThreadProcessId(window,&pid);
    return pid==c->target && Started(target)==c->targetStart && WaitForSingleObject(target,0)==WAIT_TIMEOUT;
}
bool RecoverMaterial(Control* c,HWND window,HANDLE target) {
    if(!Identity(c,window,target))return true;
    bool okay=true;
    for(int i=0;i<6;++i) if(c->mask&(1u<<i)) {
        DWORD current{};
        if(FAILED(DwmGetWindowAttribute(window,Attributes[i],&current,sizeof(current)))) { okay=false; continue; }
        // Respect a newer appearance change made by the app itself.
        if(current==c->attributes[i].applied && FAILED(DwmSetWindowAttribute(window,Attributes[i],&c->attributes[i].original,sizeof(DWORD)))) okay=false;
    }
    if(okay && GetPropW(window,MaterialMarker)==reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(GetCurrentProcessId())))RemovePropW(window,MaterialMarker);
    return okay;
}
bool Material(Control* c,HWND window) {
    const DWORD rgb=c->tint&0xFFFFFF;
    DWORD values[]{static_cast<DWORD>(c->dark!=0),2,RGB((rgb>>16)&255,(rgb>>8)&255,rgb&255),RGB((rgb>>16)&255,(rgb>>8)&255,rgb&255),c->dark?RGB(240,244,252):RGB(29,41,66),c->gloss>=50?3u:2u};
    bool applied=false;
    for(int i=0;i<6;++i) {
        DWORD current{}; if(FAILED(DwmGetWindowAttribute(window,Attributes[i],&current,sizeof(current))))continue;
        if(!(c->mask&(1u<<i))) { c->attributes[i].original=current; c->attributes[i].applied=values[i]; MemoryBarrier(); c->mask|=1u<<i; }
        else if(current!=c->attributes[i].applied) { c->error=32; return false; }
        c->attributes[i].applied=values[i]; MemoryBarrier();
        if(SUCCEEDED(DwmSetWindowAttribute(window,Attributes[i],&values[i],sizeof(DWORD))))applied=true;
        else c->attributes[i].applied=current;
    }
    if(!applied)c->error=33;
    return applied;
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    int count{}; auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if(!args || count!=2 || wcslen(args[1])!=32) { if(args)LocalFree(args); return 2; }
    std::wstring id=args[1]; LocalFree(args);
    if(id.find_first_not_of(L"0123456789abcdefABCDEF")!=std::wstring::npos)return 2;
    auto mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,(L"Local\\VeliShell.FrameBridge."+id).c_str()); if(!mapping)return 2;
    auto c=static_cast<Control*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,256));
    if(!c) { CloseHandle(mapping); return 2; }
    auto owner=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,c->owner);
    auto target=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,c->target);
    wchar_t ownPath[32768]{},ownerPath[32768]{}; GetModuleFileNameW(nullptr,ownPath,32768); DWORD length=32768;
    bool valid=c->magic==Magic && c->mode<=1 && owner && target && Started(owner)==c->ownerStart && SameUser(owner) && SameUser(target) && QueryFullProcessImageNameW(owner,0,ownerPath,&length);
    auto slash=wcsrchr(ownPath,L'\\'); if(slash)*slash=0;
    std::wstring expected=std::wstring(ownPath)+L"\\VeliShell.exe";
    valid=valid && _wcsicmp(expected.c_str(),ownerPath)==0 && c->window<=0xFFFFFFFFULL;
    HWND window=reinterpret_cast<HWND>(static_cast<ULONG_PTR>(c->window));
    valid=valid && Identity(c,window,target) && !c->stop && GetTickCount64()-static_cast<ULONGLONG>(InterlockedCompareExchange64(&c->heartbeat,0,0))<3000;
    void* lease=nullptr;
    bool stopping=false,material=false;
    if(valid) {
        c->bridge=GetCurrentProcessId();
        if(c->mode==0) lease=VeliClassicBegin(window,id.c_str(),c->target,c->targetStart,c->dark,c->gloss,c->transparency,c->tint);
        else if(VeliMaterialEligible(window) && !GetPropW(window,MaterialMarker)) {
            material=SetPropW(window,MaterialMarker,reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(GetCurrentProcessId())))!=FALSE;
            if(material && !Material(c,window))stopping=true;
        }
    }
    if(!lease && !material) { c->error=!valid?30:c->mode==0?VeliClassicBeginError():31; InterlockedExchange(&c->state,-1); }
    else {
        auto lastDark=c->dark,lastGloss=c->gloss; DWORD lastTint=c->tint;
        ULONGLONG start=GetTickCount64();
        for(;;) {
            stopping|=c->stop!=0 || WaitForSingleObject(owner,0)!=WAIT_TIMEOUT || GetTickCount64()-static_cast<ULONGLONG>(InterlockedCompareExchange64(&c->heartbeat,0,0))>3000;
            HIGHCONTRASTW contrast{sizeof(contrast)};
            if(material && SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(contrast),&contrast,0) && (contrast.dwFlags&HCF_HIGHCONTRASTON))stopping=true;
            if(!Identity(c,window,target)) { if(lease)VeliClassicEnd(lease); InterlockedExchange(&c->state,3); break; }
            int state{};
            if(material) {
                if(!stopping && (lastDark!=c->dark || lastGloss!=c->gloss || lastTint!=c->tint)) {
                    if(!Material(c,window))stopping=true;
                    lastDark=c->dark; lastGloss=c->gloss; lastTint=c->tint;
                }
                if(stopping) { if(RecoverMaterial(c,window,target)) { InterlockedExchange(&c->state,2); break; } state=4; }
                else state=1;
            } else {
                VeliClassicConfigure(lease,c->dark,c->gloss,c->transparency,c->tint);
                state=VeliClassicPoll(lease,stopping?1:0); c->error=VeliClassicError(lease);
                if((state==0 || state==4) && GetTickCount64()-start>5000)stopping=true;
                if((state==2 || state==3 || state==-1 || (state==0 && stopping)) && VeliClassicEnd(lease)) { InterlockedExchange(&c->state,state==0?2:state); break; }
            }
            InterlockedExchange(&c->state,state);
            MSG message{}; while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
            MsgWaitForMultipleObjects(0,nullptr,FALSE,100,QS_ALLINPUT);
        }
    }
    if(owner)CloseHandle(owner); if(target)CloseHandle(target); UnmapViewOfFile(c); CloseHandle(mapping); return 0;
}
