#pragma once
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <cstring>

namespace VeliTrayRelay {
constexpr DWORD Magic = 0x56545231, Protocol = 1, Limit = 128;
constexpr wchar_t Prefix[] = L"Local\\VeliShell.TrayRelay.v1.";
struct Icon {
    ULONGLONG window{}, started{};
    DWORD pid{}, uid{}, callback{}, version{0xffffffff}, active{};
    GUID guid{};
};
struct Slot { volatile LONG sequence{}; Icon icon{}; };
struct Shared {
    DWORD magic{}, protocol{}, size{}, explorer{}, thread{}, owner{}, guard{};
    ULONGLONG explorerStarted{}, ownerStarted{}, guardStarted{}, trayWindow{};
    GUID session{};
    volatile LONG state{}, reason{}, hooked{}; // 1 active, 2 stopped; reason 1 normal, 2 emergency
    volatile LONG64 heartbeat{}, captured{}, rejected{};
    Slot slots[Limit]{};
};
inline ULONGLONG Started(DWORD pid) {
    const auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return 0;
    FILETIME a{}, b{}, c{}, d{}; const auto ok = GetProcessTimes(process, &a, &b, &c, &d); CloseHandle(process);
    return ok ? (static_cast<ULONGLONG>(a.dwHighDateTime) << 32) | a.dwLowDateTime : 0;
}
inline DWORD ShellPid() {
    DWORD pid{}, ownSession{}, session{}; GetWindowThreadProcessId(GetShellWindow(), &pid);
    return pid && ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) && ProcessIdToSessionId(pid, &session) &&
        session == ownSession ? pid : 0;
}
inline std::wstring Name(DWORD pid) { return Prefix + std::to_wstring(pid); }
inline bool SameGuid(const GUID& a, const GUID& b) { return std::memcmp(&a, &b, sizeof(GUID)) == 0; }
inline bool HasGuid(const GUID& guid) { const GUID empty{}; return !SameGuid(guid, empty); }
inline bool Valid(const Shared* shared, DWORD explorer) {
    return shared && shared->magic == Magic && shared->protocol == Protocol && shared->size == sizeof(Shared) &&
        shared->explorer == explorer && shared->explorerStarted == Started(explorer);
}
inline bool Alive(const Shared* shared) {
    const auto now = GetTickCount64(), heartbeat = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        const_cast<volatile LONG64*>(&shared->heartbeat), 0, 0));
    return shared->state == 1 && heartbeat <= now && now - heartbeat < 4000;
}
struct Mapping {
    HANDLE handle{}; Shared* shared{};
    ~Mapping() { Close(); }
    void Close() { if (shared) UnmapViewOfFile(shared); shared = nullptr; if (handle) CloseHandle(handle); handle = nullptr; }
    bool Open(DWORD explorer) {
        Close(); handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, Name(explorer).c_str());
        if (handle) shared = static_cast<Shared*>(MapViewOfFile(handle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Shared)));
        if (!Valid(shared, explorer)) { Close(); return false; }
        return true;
    }
};
inline bool ReadSlot(const Slot& slot, Icon& icon) {
    for (int attempt = 0; attempt < 3; ++attempt) {
        const LONG before = InterlockedCompareExchange(const_cast<volatile LONG*>(&slot.sequence), 0, 0);
        if (before & 1) continue;
        std::memcpy(&icon, &slot.icon, sizeof(icon)); MemoryBarrier();
        if (before == InterlockedCompareExchange(const_cast<volatile LONG*>(&slot.sequence), 0, 0)) return true;
    }
    return false;
}
inline bool SameIdentity(const Icon& a, const Icon& b) {
    return HasGuid(a.guid) && HasGuid(b.guid) ? SameGuid(a.guid, b.guid) : a.window == b.window && a.uid == b.uid;
}
inline bool LiveOwner(const Icon& icon) {
    DWORD pid{}; GetWindowThreadProcessId(reinterpret_cast<HWND>(icon.window), &pid);
    return icon.active && icon.pid && icon.started && pid == icon.pid && Started(pid) == icon.started;
}
struct Update { DWORD operation{}, flags{}, state{}, stateMask{}; Icon icon{}; };
// Explorer's WM_COPYDATA wire format uses a fixed 32-bit NOTIFYICONDATA
// (including on x64). Accept native x64 packets too, without trusting cbSize
// or reading notification text. Unknown/truncated packets are ignored.
inline bool Decode(const BYTE* bytes, DWORD length, Update& update) {
    if (!bytes || length < 32 || length > 4096) return false;
    update = {};
    auto get = [&](DWORD offset, void* output, size_t size) {
        if (offset > length || size > length - offset) return false;
        std::memcpy(output, bytes + offset, size); return true;
    };
    DWORD signature{}, size{};
    if (!get(0, &signature, 4) || signature != 0x34753423 || !get(4, &update.operation, 4) ||
        !get(8, &size, 4) || size > length - 8 ||
        (update.operation != NIM_ADD && update.operation != NIM_MODIFY && update.operation != NIM_DELETE && update.operation != NIM_SETVERSION)) return false;
    const bool wide = size == sizeof(NOTIFYICONDATAW);
    if (!wide && size != 956 && size != 952 && size != 936 && size != 152) return false;
    const DWORD base = 8;
    DWORD window32{};
    if (wide) {
        if (!get(base + 8, &update.icon.window, 8) || !get(base + 16, &update.icon.uid, 4) ||
            !get(base + 20, &update.flags, 4) || !get(base + 24, &update.icon.callback, 4)) return false;
    } else {
        if (!get(base + 4, &window32, 4) || !get(base + 8, &update.icon.uid, 4) ||
            !get(base + 12, &update.flags, 4) || !get(base + 16, &update.icon.callback, 4)) return false;
        update.icon.window = window32;
    }
    if (update.flags & NIF_GUID) {
        const auto offset = wide ? static_cast<DWORD>(offsetof(NOTIFYICONDATAW, guidItem)) : 936U;
        if (offset + sizeof(GUID) > size || !get(base + offset, &update.icon.guid, sizeof(GUID))) return false;
    }
    if (update.operation == NIM_SETVERSION) {
        const auto offset = wide ? static_cast<DWORD>(offsetof(NOTIFYICONDATAW, uVersion)) : 800U;
        if (offset + 4 > size || !get(base + offset, &update.icon.version, 4) || update.icon.version > 4) return false;
    }
    if ((update.flags & NIF_STATE) && size > 152) {
        const auto offset = wide ? static_cast<DWORD>(offsetof(NOTIFYICONDATAW, dwState)) : 280U;
        if (!get(base + offset, &update.state, 4) || !get(base + offset + 4, &update.stateMask, 4)) return false;
    }
    return update.icon.window || HasGuid(update.icon.guid);
}
inline bool Apply(Shared& shared, const Update& update) {
    Slot* found{}; Slot* available{};
    for (auto& slot : shared.slots) {
        if (!slot.icon.active && !available) available = &slot;
        if (slot.icon.active && SameIdentity(slot.icon, update.icon)) { found = &slot; break; }
    }
    if (!found && (update.operation == NIM_DELETE || update.operation == NIM_SETVERSION)) return false;
    const bool added = !found;
    if (!found) found = available;
    if (!found) return false;
    auto icon = added ? Icon{} : found->icon;
    if (update.operation == NIM_DELETE) icon.active = 0;
    else {
        if (update.operation == NIM_ADD) { icon = {}; icon.version = 0; }
        if (update.icon.window) icon.window = update.icon.window;
        if (added || update.operation == NIM_ADD) icon.uid = update.icon.uid;
        if (HasGuid(update.icon.guid)) icon.guid = update.icon.guid;
        if (update.flags & NIF_MESSAGE) icon.callback = update.icon.callback;
        if (update.operation == NIM_SETVERSION) icon.version = update.icon.version;
        icon.active = 1;
        DWORD pid{}; GetWindowThreadProcessId(reinterpret_cast<HWND>(icon.window), &pid);
        if (!pid) return false;
        if (pid != icon.pid || !icon.started) { icon.pid = pid; icon.started = Started(pid); }
        if (!icon.started) return false;
    }
    InterlockedIncrement(&found->sequence); found->icon = icon; MemoryBarrier(); InterlockedIncrement(&found->sequence);
    InterlockedIncrement64(&shared.captured);
    return true;
}
}
