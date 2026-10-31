// In-process UI Automation client only. No foreign process memory, hooks,
// injection, simulated input, taskbar visibility changes or menu-item copying.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <ole2.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <algorithm>
#include <cwchar>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
constexpr int MaxElements = 256, MaxRuntimeIds = 64, MaxName = 512;
struct Apartment {
    HRESULT result{ CoInitializeEx(nullptr, COINIT_MULTITHREADED) };
    ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};
struct Target {
    HWND root{}; DWORD pid{}; ULONGLONG started{};
    std::wstring name;
    std::vector<int> runtime;
    ComPtr<IUIAutomationElement> element;
};

ULONGLONG Started(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return 0;
    FILETIME created{}, exited{}, kernel{}, user{};
    const BOOL ok = GetProcessTimes(process, &created, &exited, &kernel, &user);
    CloseHandle(process);
    return ok ? (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime : 0;
}

DWORD ShellPid() {
    DWORD pid{}, ownSession{}, shellSession{};
    GetWindowThreadProcessId(GetShellWindow(), &pid);
    return pid && ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) &&
        ProcessIdToSessionId(pid, &shellSession) && ownSession == shellSession ? pid : 0;
}

std::wstring Property(IUIAutomationElement* element, bool automationId) {
    BSTR text{};
    const HRESULT result = automationId ? element->get_CurrentAutomationId(&text) : element->get_CurrentClassName(&text);
    std::wstring value;
    if (SUCCEEDED(result) && text) value.assign(text, std::min<UINT>(SysStringLen(text), MaxName));
    SysFreeString(text);
    return value;
}

bool TrayAncestor(IUIAutomation* automation, IUIAutomationElement* element, ULONGLONG deadline) {
    ComPtr<IUIAutomationTreeWalker> walker;
    if (FAILED(automation->get_RawViewWalker(&walker))) return false;
    ComPtr<IUIAutomationElement> current = element;
    for (int depth = 0; current && depth < 16 && GetTickCount64() < deadline; ++depth) {
        const auto className = Property(current.Get(), false);
        const auto id = Property(current.Get(), true);
        if (className == L"TrayNotifyWnd" || className == L"NotifyIconOverflowWindow" ||
            className == L"TopLevelWindowForOverflowXamlIsland" || className.rfind(L"SystemTray.", 0) == 0 ||
            id.rfind(L"SystemTray.", 0) == 0) return true;
        ComPtr<IUIAutomationElement> parent;
        if (FAILED(walker->GetParentElement(current.Get(), &parent))) break;
        current = parent;
    }
    return false;
}

HRESULT Client(ComPtr<IUIAutomation>& client) {
    HRESULT result = CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&client));
    if (FAILED(result)) return result;
    ComPtr<IUIAutomation2> bounded;
    if (SUCCEEDED(client.As(&bounded))) {
        bounded->put_ConnectionTimeout(200);
        bounded->put_TransactionTimeout(200);
        bounded->put_AutoSetFocus(FALSE);
    }
    return S_OK;
}

std::vector<int> RuntimeId(IUIAutomationElement* element) {
    SAFEARRAY* ids{};
    std::vector<int> result;
    if (FAILED(element->GetRuntimeId(&ids)) || !ids) { if (ids) SafeArrayDestroy(ids); return result; }
    LONG first{}, last{};
    if (SafeArrayGetDim(ids) == 1 && SUCCEEDED(SafeArrayGetLBound(ids, 1, &first)) &&
        SUCCEEDED(SafeArrayGetUBound(ids, 1, &last)) && last >= first && last - first < MaxRuntimeIds) {
        for (LONG index = first; index <= last; ++index) {
            int value{};
            if (FAILED(SafeArrayGetElement(ids, &index, &value))) { result.clear(); break; }
            result.push_back(value);
        }
    }
    SafeArrayDestroy(ids);
    return result;
}

std::vector<HWND> ProviderRoots(HWND root, DWORD expectedPid) {
    DWORD pid{}; GetWindowThreadProcessId(root, &pid);
    if (!expectedPid || pid != expectedPid) return {};
    struct Search { DWORD pid; int visited{}; std::vector<HWND> roots; } search{ expectedPid, 0, {root} };
    // A hidden Shell_TrayWnd exposes no UIA descendants on recent Windows 11
    // builds. Its inner XAML HWNDs still expose their own provider roots.
    // Reading those does not require showing, moving or activating the taskbar.
    EnumChildWindows(root, [](HWND child, LPARAM parameter) -> BOOL {
        auto& state = *reinterpret_cast<Search*>(parameter);
        if (++state.visited > 256 || state.roots.size() >= 17) return FALSE;
        DWORD childPid{}; GetWindowThreadProcessId(child, &childPid);
        if (childPid != state.pid) return TRUE;
        wchar_t name[128]{}; GetClassNameW(child, name, 128);
        if (wcscmp(name, L"Windows.UI.Composition.DesktopWindowContentBridge") == 0 ||
            wcscmp(name, L"Windows.UI.Input.InputSite.WindowClass") == 0 ||
            wcscmp(name, L"DesktopWindowXamlSource") == 0) state.roots.push_back(child);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.roots;
}

std::vector<Target> Read(IUIAutomation* client) {
    std::vector<Target> targets;
    const auto deadline = GetTickCount64() + 1400;
    const DWORD shell = ShellPid();
    const auto started = Started(shell);
    if (!shell || !started) return targets;
    ComPtr<IUIAutomationCondition> buttons, menuItems, condition;
    VARIANT value{}; value.vt = VT_I4;
    value.lVal = UIA_ButtonControlTypeId;
    if (FAILED(client->CreatePropertyCondition(UIA_ControlTypePropertyId, value, &buttons))) return targets;
    value.lVal = UIA_MenuItemControlTypeId;
    if (FAILED(client->CreatePropertyCondition(UIA_ControlTypePropertyId, value, &menuItems))) return targets;
    // Decorative Image children often repeat their parent button's caption.
    // Enumerating both would make every original icon look ambiguous.
    if (FAILED(client->CreateOrCondition(buttons.Get(), menuItems.Get(), &condition))) return targets;

    const wchar_t* rootClasses[] = { L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd", L"NotifyIconOverflowWindow",
        L"TopLevelWindowForOverflowXamlIsland" };
    for (const auto rootClass : rootClasses) {
        HWND root{};
        for (int rootIndex = 0; rootIndex < 16 && GetTickCount64() < deadline &&
            (root = FindWindowExW(nullptr, root, rootClass, nullptr)); ++rootIndex) {
            DWORD pid{}; GetWindowThreadProcessId(root, &pid);
            if (pid != shell) continue;
            for (const auto providerRoot : ProviderRoots(root, shell)) {
                if (GetTickCount64() >= deadline || targets.size() >= MaxElements) break;
                ComPtr<IUIAutomationElement> parent;
                ComPtr<IUIAutomationElementArray> elements;
                if (FAILED(client->ElementFromHandle(providerRoot, &parent)) || !parent ||
                    FAILED(parent->FindAll(TreeScope_Descendants, condition.Get(), &elements)) || !elements) continue;
                int count{}; elements->get_Length(&count);
                for (int index = 0; index < std::min(count, MaxElements) && targets.size() < MaxElements && GetTickCount64() < deadline; ++index) {
                    ComPtr<IUIAutomationElement> element;
                    if (FAILED(elements->GetElement(index, &element)) || !element || !TrayAncestor(client, element.Get(), deadline)) continue;
                    BSTR text{};
                    if (FAILED(element->get_CurrentName(&text)) || !text) { SysFreeString(text); continue; }
                    std::wstring name(text, std::min<UINT>(SysStringLen(text), MaxName)); SysFreeString(text);
                    auto runtime = RuntimeId(element.Get());
                    if (name.empty() || runtime.empty()) continue;
                    if (std::any_of(targets.begin(), targets.end(), [&](const Target& item) { return item.runtime == runtime; })) continue;
                    targets.push_back({ root, shell, started, name, runtime, element });
                }
            }
        }
    }
    return targets;
}

void AppendString(std::wstring& json, const std::wstring& text) {
    json += L'"';
    for (const wchar_t character : text) {
        if (character == L'"' || character == L'\\') { json += L'\\'; json += character; }
        else if (character < 32) {
            wchar_t escaped[8]{}; swprintf_s(escaped, L"\\u%04x", static_cast<unsigned>(character)); json += escaped;
        } else json += character;
    }
    json += L'"';
}
}

extern "C" __declspec(dllexport) int WINAPI VeliShellTrayProtocol() { return 1; }

extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellReadTrayElements(wchar_t* output, int capacity) {
    if (!output || capacity < 2 || capacity > 262144) return E_INVALIDARG;
    output[0] = 0;
    try {
        Apartment apartment;
        ComPtr<IUIAutomation> client;
        const HRESULT created = Client(client);
        if (FAILED(created)) return created;
        std::wstring json = L"[";
        for (const auto& target : Read(client.Get())) {
            if (json.size() > 1) json += L",";
            json += L"{\"ExplorerPid\":" + std::to_wstring(target.pid) + L",\"ExplorerStarted\":" + std::to_wstring(target.started) + L",\"Name\":";
            AppendString(json, target.name);
            json += L",\"RuntimeId\":[";
            for (size_t i = 0; i < target.runtime.size(); ++i) {
                if (i) json += L",";
                json += std::to_wstring(target.runtime[i]);
            }
            json += L"]}";
        }
        json += L"]";
        if (json.size() + 1 > static_cast<size_t>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        wcscpy_s(output, static_cast<size_t>(capacity), json.c_str());
        return S_OK;
    } catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) HRESULT WINAPI VeliShellShowTrayContextMenu(DWORD pid, ULONGLONG started,
    const int* runtimeId, int count, const wchar_t* expectedName) {
    if (!pid || !started || !runtimeId || count < 1 || count > MaxRuntimeIds || !expectedName || !*expectedName ||
        wcsnlen_s(expectedName, MaxName + 1) > MaxName) return E_INVALIDARG;
    try {
        // Explorer restarts invalidate every UIA runtime ID. Never reuse a stale
        // target or forward a message guessed from another app's process name.
        if (pid != ShellPid() || started != Started(pid)) return UIA_E_ELEMENTNOTAVAILABLE;
        Apartment apartment;
        ComPtr<IUIAutomation> client;
        const HRESULT created = Client(client);
        if (FAILED(created)) return created;
        const std::vector<int> expected(runtimeId, runtimeId + count);
        for (const auto& target : Read(client.Get())) {
            if (target.pid != pid || target.started != started || target.runtime != expected || target.name != expectedName) continue;
            ComPtr<IUIAutomationElement3> context;
            if (FAILED(target.element.As(&context))) return UIA_E_NOTSUPPORTED;
            // Executed only following a user's explicit context-menu action.
            return context->ShowContextMenu();
        }
        return UIA_E_ELEMENTNOTAVAILABLE;
    } catch (...) { return E_FAIL; }
}
