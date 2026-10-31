#include "../../native/VeliShell.TrayBridge/TrayBridge.cpp"
#include "../../native/VeliShell.TrayBridge/TrayRelayProtocol.h"
#include <array>
#include <iostream>
#include <stdexcept>

int main() {
    using namespace VeliTrayRelay;
    int checks{};
    auto check = [&](bool pass) { if (!pass) throw std::runtime_error("Tray protocol assertion failed at " + std::to_string(checks + 1)); ++checks; };
    const auto own = GetModuleHandleW(nullptr);
    const auto window = CreateWindowExW(0, L"STATIC", L"VeliShell hidden protocol fixture", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, own, nullptr);
    try {
        check(window != nullptr); check(!IsWindowVisible(window)); check(sizeof(Icon) == 56);
        std::array<BYTE, 964> packet{};
        auto write = [&](size_t offset, DWORD value) { std::memcpy(packet.data() + offset, &value, 4); };
        write(0, 0x34753423); write(4, NIM_ADD); write(8, 956);
        write(12, static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(window))); write(16, 0x1234);
        write(20, NIF_MESSAGE | NIF_GUID); write(24, WM_APP + 91);
        GUID guid{}; guid.Data1 = 12; std::memcpy(packet.data() + 944, &guid, sizeof(guid));
        Update update;
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update));
        check(update.icon.window == reinterpret_cast<ULONGLONG>(window) && update.icon.uid == 0x1234 && update.icon.callback == WM_APP + 91);
        for (DWORD length = 0; length < packet.size(); ++length) { Update shortPacket; check(!Decode(packet.data(), length, shortPacket)); }
        Shared shared{};
        check(Apply(shared, update));
        Icon icon; check(ReadSlot(shared.slots[0], icon) && icon.version == 0 && LiveOwner(icon));
        write(4, NIM_MODIFY); write(16, 0); write(20, NIF_GUID | NIF_TIP); write(24, 0);
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && Apply(shared, update));
        check(ReadSlot(shared.slots[0], icon) && icon.uid == 0x1234 && icon.callback == WM_APP + 91);
        write(4, NIM_SETVERSION); write(808, 4);
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && Apply(shared, update));
        check(ReadSlot(shared.slots[0], icon) && icon.version == 4 && icon.uid == 0x1234);
        write(808, 5); check(!Decode(packet.data(), static_cast<DWORD>(packet.size()), update)); write(808, 4);
        write(4, NIM_DELETE);
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && Apply(shared, update));
        check(ReadSlot(shared.slots[0], icon) && !icon.active);
        write(4, NIM_MODIFY); write(20, NIF_MESSAGE | NIF_GUID); write(24, WM_APP + 91);
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && Apply(shared, update));
        check(ReadSlot(shared.slots[0], icon) && icon.version == 0xffffffff);
        shared = {};
        write(4, NIM_ADD);
        for (DWORD i = 1; i <= Limit; ++i) {
            guid.Data1 = i; std::memcpy(packet.data() + 944, &guid, sizeof(guid));
            check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && Apply(shared, update));
        }
        guid.Data1 = Limit + 1; std::memcpy(packet.data() + 944, &guid, sizeof(guid));
        check(Decode(packet.data(), static_cast<DWORD>(packet.size()), update) && !Apply(shared, update));
        shared.slots[0].sequence = 1; check(!ReadSlot(shared.slots[0], icon));
        write(0, 0); check(!Decode(packet.data(), static_cast<DWORD>(packet.size()), update)); write(0, 0x34753423);
        write(8, 4096); check(!Decode(packet.data(), static_cast<DWORD>(packet.size()), update));
        write(8, 956); write(4, 0xffffffff); check(!Decode(packet.data(), static_cast<DWORD>(packet.size()), update));
        // Deterministic malformed packet corpus, no Explorer hook or foreign input.
        DWORD random = 12345;
        for (int i = 0; i < 4000; ++i) {
            random = random * 1664525U + 1013904223U;
            packet[random % packet.size()] ^= static_cast<BYTE>(random >> 24);
            Update malformed; Decode(packet.data(), random % static_cast<DWORD>(packet.size()), malformed);
        }
        check(true);
        WNDCLASSW klass{}; klass.lpfnWndProc = DefWindowProcW; klass.hInstance = own;
        klass.lpszClassName = L"Windows.UI.Input.InputSite.WindowClass";
        const auto registered = RegisterClassW(&klass); check(registered != 0);
        const auto child = CreateWindowExW(0, klass.lpszClassName, L"", WS_CHILD, 0, 0, 0, 0, window, nullptr, own, nullptr);
        check(child != nullptr);
        const auto roots = ProviderRoots(window, GetCurrentProcessId());
        check(roots.size() == 2 && roots[0] == window && roots[1] == child);
        check(ProviderRoots(window, 0).empty());
        check(!IsWindowVisible(window) && !IsWindowVisible(child));
        DestroyWindow(child); UnregisterClassW(klass.lpszClassName, own); DestroyWindow(window);
        std::cout << "PASS: " << checks << " native tray parser, capacity, owner and hidden XAML-host checks. No Explorer hooks, input or visible windows.\n";
        return 0;
    } catch (const std::exception& error) { DestroyWindow(window); std::cerr << error.what() << '\n'; return 1; }
}
