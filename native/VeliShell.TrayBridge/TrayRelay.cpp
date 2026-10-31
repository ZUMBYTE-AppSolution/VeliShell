#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "TrayRelayProtocol.h"
#include <objbase.h>
#include <sddl.h>
#include <vector>
#include <cstdio>

extern "C" __declspec(dllexport) LRESULT CALLBACK VeliShellTrayRelayHook(int, WPARAM, LPARAM);

namespace {
using namespace VeliTrayRelay;
Mapping host, capture;
HHOOK installed{};
HWND ActualTray(DWORD shell, DWORD& thread) {
    HWND window{};
    while ((window = FindWindowExW(nullptr, window, L"Shell_TrayWnd", nullptr))) {
        DWORD pid{}; const auto tid = GetWindowThreadProcessId(window, &pid);
        if (pid == shell) { thread = tid; return window; }
    }
    return nullptr;
}
bool CapturePacket(const CWPSTRUCT* message, Shared* shared) {
    // WM_COPYDATA storage belongs to the sender until the hook returns. Copy
    // only the bounded protocol fields; never retain pointers or read szInfo.
    __try {
        if (!message || message->message != WM_COPYDATA || message->hwnd != reinterpret_cast<HWND>(shared->trayWindow)) return false;
        const auto data = reinterpret_cast<const COPYDATASTRUCT*>(message->lParam);
        if (!data || data->dwData != 1) return false;
        Update update;
        return Decode(static_cast<const BYTE*>(data->lpData), data->cbData, update) && Apply(*shared, update);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
LRESULT CALLBACK RelayHook(int code, WPARAM parameter, LPARAM value) {
    const auto lastError = GetLastError();
    try {
        const auto message = reinterpret_cast<const CWPSTRUCT*>(value);
        if (code >= 0 && message) {
            if (!capture.shared) {
                capture.Open(GetCurrentProcessId());
            }
            if (capture.shared && capture.shared->explorer == GetCurrentProcessId() && capture.shared->thread == GetCurrentThreadId() &&
                Alive(capture.shared) && message->hwnd == reinterpret_cast<HWND>(capture.shared->trayWindow)) {
                InterlockedExchange(&capture.shared->hooked, 1);
                if (message->message == WM_COPYDATA) CapturePacket(message, capture.shared);
            }
        }
    } catch (...) { /* An observer must never interrupt Explorer's message chain. */ }
    SetLastError(lastError);
    return CallNextHookEx(nullptr, code, parameter, value);
}
bool Current(Mapping& mapping, const GUID* session, bool alive = true) {
    const auto pid = ShellPid();
    return session && mapping.Open(pid) && SameGuid(mapping.shared->session, *session) &&
        mapping.shared->owner == GetCurrentProcessId() && mapping.shared->ownerStarted == Started(GetCurrentProcessId()) &&
        (!alive || Alive(mapping.shared));
}
std::wstring SecurityDescriptor() {
    HANDLE token{}; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    DWORD size{}; GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> storage(size);
    if (!size || !GetTokenInformation(token, TokenUser, storage.data(), size, &size)) { CloseHandle(token); return {}; }
    CloseHandle(token);
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &sid)) return {};
    const std::wstring descriptor = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")";
    LocalFree(sid); return descriptor;
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK VeliShellTrayRelayHook(int code, WPARAM parameter, LPARAM value) {
    return RelayHook(code, parameter, value);
}

extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellRelayStart(DWORD owner, ULONGLONG ownerStarted, const GUID* session) {
    using namespace VeliTrayRelay;
    if (installed || host.shared || !owner || !ownerStarted || !session || !HasGuid(*session) || Started(owner) != ownerStarted) return E_INVALIDARG;
    const auto shell = ShellPid(); DWORD thread{}; const auto tray = ActualTray(shell, thread);
    if (!shell || !tray || !thread || thread == GetCurrentThreadId()) return E_FAIL;
    RemovePropW(tray, L"VeliShell.TrayRelay.ActivationStage"); // Clean up earlier diagnostic previews.
    DWORD ownerSession{}, ownSession{};
    if (!ProcessIdToSessionId(owner, &ownerSession) || !ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || ownerSession != ownSession) return E_ACCESSDENIED;
    PSECURITY_DESCRIPTOR descriptor{};
    const auto sddl = SecurityDescriptor();
    if (sddl.empty() || !ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return E_ACCESSDENIED;
    SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };
    host.handle = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(Shared), Name(shell).c_str());
    const auto error = GetLastError(); LocalFree(descriptor);
    if (!host.handle) return HRESULT_FROM_WIN32(error);
    if (error == ERROR_ALREADY_EXISTS) { host.Close(); return HRESULT_FROM_WIN32(ERROR_BUSY); }
    host.shared = static_cast<Shared*>(MapViewOfFile(host.handle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    if (!host.shared) { host.Close(); return E_FAIL; }
    auto& shared = *host.shared;
    shared.magic = Magic; shared.protocol = Protocol; shared.size = sizeof(Shared); shared.explorer = shell;
    shared.thread = thread; shared.explorerStarted = Started(shell); shared.trayWindow = reinterpret_cast<ULONGLONG>(tray);
    shared.owner = owner; shared.ownerStarted = ownerStarted; shared.guard = GetCurrentProcessId(); shared.guardStarted = Started(shared.guard);
    shared.session = *session; InterlockedExchange64(&shared.heartbeat, static_cast<LONG64>(GetTickCount64())); shared.state = 1;
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&VeliShellTrayRelayHook), &module) || !(installed = SetWindowsHookExW(WH_CALLWNDPROC, VeliShellTrayRelayHook, module, thread))) {
        const auto failure = GetLastError(); shared.state = 2; host.Close(); return HRESULT_FROM_WIN32(failure ? failure : ERROR_NOT_SUPPORTED);
    }
    const auto readyMessage = RegisterWindowMessageW(L"VeliShell.TrayRelay.Ready.v1");
    if (!readyMessage || !SendNotifyMessageW(tray, readyMessage, 0, 0)) {
        shared.state = 2; UnhookWindowsHookEx(installed); installed = nullptr; host.Close();
        return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    }
    return S_OK;
}
extern "C" __declspec(dllexport) int WINAPI VeliShellRelayPulse() {
    if (!installed || !host.shared) return -1;
    if (host.shared->state != 1 || host.shared->reason || VeliTrayRelay::ShellPid() != host.shared->explorer ||
        VeliTrayRelay::Started(host.shared->explorer) != host.shared->explorerStarted) return 2;
    InterlockedExchange64(&host.shared->heartbeat, static_cast<LONG64>(GetTickCount64())); return host.shared->hooked == 1 ? 1 : 0;
}
extern "C" __declspec(dllexport) int WINAPI VeliShellRelayStopReason() { return host.shared ? host.shared->reason : 0; }
extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellRelayUninstall() {
    if (host.shared) InterlockedExchange(&host.shared->state, 2);
    if (installed && !UnhookWindowsHookEx(installed)) return HRESULT_FROM_WIN32(GetLastError());
    installed = nullptr; host.Close(); return S_OK;
}
extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellRelayRequestStop(const GUID* session, BOOL emergency) {
    VeliTrayRelay::Mapping mapping;
    if (!mapping.Open(VeliTrayRelay::ShellPid()) || !mapping.shared ||
        (!emergency && (!session || !VeliTrayRelay::SameGuid(*session, mapping.shared->session)))) return S_FALSE;
    if (emergency && session && !VeliTrayRelay::SameGuid(*session, mapping.shared->session)) return E_INVALIDARG;
    InterlockedExchange(&mapping.shared->reason, emergency ? 2 : 1);
    InterlockedExchange(&mapping.shared->state, 2); return S_OK;
}
extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellReadTrayCallbacks(const GUID* session, wchar_t* output, int capacity) {
    using namespace VeliTrayRelay;
    if (!output || capacity < 2 || capacity > 262144) return E_INVALIDARG;
    output[0] = 0;
    try {
        Mapping mapping; if (!Current(mapping, session)) return S_FALSE;
        std::wstring json = L"[";
        for (const auto& slot : mapping.shared->slots) {
            Icon icon;
            if (!ReadSlot(slot, icon) || icon.version > 4 || (icon.version == 4 && icon.uid > 0xffff) ||
                icon.callback < WM_USER || icon.callback > 0xffff || !LiveOwner(icon)) continue;
            if (json.size() > 1) json += L",";
            wchar_t guid[40]{}; StringFromGUID2(icon.guid, guid, 40);
            json += L"{\"Window\":" + std::to_wstring(icon.window) + L",\"Started\":" + std::to_wstring(icon.started) +
                L",\"Pid\":" + std::to_wstring(icon.pid) + L",\"Id\":" + std::to_wstring(icon.uid) +
                L",\"Message\":" + std::to_wstring(icon.callback) + L",\"Version\":" + std::to_wstring(icon.version) +
                L",\"Guid\":\"" + std::wstring(guid + 1, 36) + L"\"}";
        }
        json += L"]";
        if (json.size() + 1 > static_cast<size_t>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        wcscpy_s(output, static_cast<size_t>(capacity), json.c_str()); return S_OK;
    } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellShowTrayCallback(const GUID* session, const VeliTrayRelay::Icon* expected, int x, int y) {
    using namespace VeliTrayRelay;
    if (!expected || x < -32768 || x > 32767 || y < -32768 || y > 32767) return E_INVALIDARG;
    Mapping mapping; if (!Current(mapping, session)) return E_ACCESSDENIED;
    for (const auto& slot : mapping.shared->slots) {
        Icon icon;
        if (!ReadSlot(slot, icon) || !SameIdentity(icon, *expected) || !SameGuid(icon.guid, expected->guid) || icon.uid != expected->uid ||
            icon.version > 4 || (icon.version == 4 && icon.uid > 0xffff) || icon.window != expected->window ||
            icon.pid != expected->pid || icon.started != expected->started || icon.callback != expected->callback ||
            icon.version != expected->version || icon.callback < WM_USER || icon.callback > 0xffff || !LiveOwner(icon)) continue;
        const auto window = reinterpret_cast<HWND>(icon.window);
        AllowSetForegroundWindow(icon.pid);
        const WPARAM wparam = icon.version == 4 ? static_cast<WPARAM>(MAKELONG(static_cast<WORD>(x), static_cast<WORD>(y))) : icon.uid;
        const auto send = [&](UINT event) {
            const LPARAM lparam = icon.version == 4 ? static_cast<LPARAM>(MAKELONG(event, icon.uid)) : event;
            return SendNotifyMessageW(window, icon.callback, wparam, lparam) != FALSE;
        };
        // Legacy icons open on button down/up. Version 3+ additionally receives
        // WM_CONTEXTMENU; version 4 packs the icon ID into HIWORD(lParam).
        if (!send(WM_RBUTTONDOWN) || !send(WM_RBUTTONUP) || (icon.version >= 3 && !send(WM_CONTEXTMENU)))
        {
            const auto error = GetLastError(); return HRESULT_FROM_WIN32(error ? error : ERROR_NOT_SUPPORTED);
        }
        return S_OK;
    }
    return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}
