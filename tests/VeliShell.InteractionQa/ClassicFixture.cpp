#define UNICODE
#define _UNICODE
#include <windows.h>
// Ordinary standalone Win32 app: no VeliShell dependency, opt-in property or DLL loading code.
LRESULT CALLBACK WindowProc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    if(m==WM_CREATE) {
        auto edit=CreateWindowW(L"EDIT",L"Original editable app content - preserved through rollback.",WS_CHILD|WS_VISIBLE|WS_BORDER|ES_MULTILINE,20,65,600,210,w,(HMENU)101,nullptr,nullptr);
        auto label=CreateWindowW(L"STATIC",L"Independent classic Win32 application",WS_CHILD|WS_VISIBLE,20,20,500,25,w,nullptr,nullptr,nullptr);
        auto font=(HFONT)GetStockObject(DEFAULT_GUI_FONT); SendMessageW(edit,WM_SETFONT,(WPARAM)font,TRUE); SendMessageW(label,WM_SETFONT,(WPARAM)font,TRUE);
        return 0;
    }
    if(m==WM_COMMAND && LOWORD(wp)==201) { PostMessageW(w,WM_CLOSE,0,0); return 0; }
    if(m==WM_COMMAND && LOWORD(wp)==202) { Sleep(5000); return 0; }
    if(m==WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(w,m,wp,lp);
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR arguments,int show) {
    WNDCLASSW c{}; c.lpfnWndProc=WindowProc; c.hInstance=instance; c.lpszClassName=L"VeliShellClassicFixture";
    c.hCursor=LoadCursorW(nullptr,IDC_ARROW); c.hbrBackground=(HBRUSH)(COLOR_WINDOW+1); RegisterClassW(&c);
    HMENU menu=CreateMenu(), file=CreatePopupMenu(); AppendMenuW(file,MF_STRING,202,L"Pause UI for recovery test"); AppendMenuW(file,MF_STRING,201,L"Exit"); AppendMenuW(menu,MF_POPUP,(UINT_PTR)file,L"File");
    // Keep disposable QA windows out of an older installed adapter's eligibility
    // set, without disabling or changing that user's active VeliShell instance.
    DWORD extended=wcsstr(arguments,L"--isolated")?WS_EX_TOPMOST:0;
    HWND window=CreateWindowExW(extended,c.lpszClassName,L"Independent Classic Fixture",WS_OVERLAPPEDWINDOW,120,160,800,480,nullptr,menu,instance,nullptr);
    ShowWindow(window,show); MSG message{};
    while(GetMessageW(&message,nullptr,0,0)>0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
