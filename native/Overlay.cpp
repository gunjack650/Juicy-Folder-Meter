#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <new>

// This DLL is native only. No filesystem scans, managed runtime, IPC waits or hooks.
static HMODULE module;
static volatile LONG objects = 0;
static const CLSID orange = {0x7b8ef830,0x13ba,0x4a69,{0xb1,0x58,0x29,0x67,0x14,0x43,0x10,0x01}};
static const CLSID red = {0x7b8ef830,0x13ba,0x4a69,{0xb1,0x58,0x29,0x67,0x14,0x43,0x10,0x02}};
static constexpr SIZE_T mapSize = 32 + 16384ULL * 2056;
static SRWLOCK mapLock = SRWLOCK_INIT;
static HANDLE mapping = nullptr;
static const BYTE* cache = nullptr;
static ULONGLONG nextOpen = 0;

static int Lookup(LPCWSTR path, DWORD attributes) noexcept {
    if (!path || !(attributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return 0;
    // Try-lock only; another caller initializing the mapping never stalls Explorer.
    if (!TryAcquireSRWLockExclusive(&mapLock)) return 0;
    if (!cache && GetTickCount64() >= nextOpen) {
        nextOpen = GetTickCount64() + 5000;
        mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\FolderSizeMeter.Cache.v1");
        if (mapping) {
            cache = static_cast<const BYTE*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, mapSize));
            if (!cache) { CloseHandle(mapping); mapping = nullptr; }
        }
    }
    const BYTE* data = cache;
    ReleaseSRWLockExclusive(&mapLock);
    if (!data) return 0;
    const volatile LONG* sequence = reinterpret_cast<const volatile LONG*>(data);
    LONG before = *sequence;
    MemoryBarrier();
    if (before & 1) return 0;
    int count = *reinterpret_cast<const int*>(data + 8);
    ULONGLONG tick = *reinterpret_cast<const ULONGLONG*>(data + 16);
    if (*reinterpret_cast<const DWORD*>(data + 4) != 0x314D5346 || count < 0 || count > 16384 || GetTickCount64() - tick > 600000) return 0;
    WCHAR key[1024]; size_t len = wcsnlen_s(path, 1024);
    if (!len || len >= 1024) return 0;
    memcpy(key, path, (len + 1) * sizeof(WCHAR));
    if (len > 3 && key[len - 1] == L'\\') key[--len] = 0;
    CharUpperBuffW(key, static_cast<DWORD>(len));
    int lo = 0, hi = count - 1, result = 0;
    while (lo <= hi) {
        int mid = lo + (hi - lo) / 2;
        const BYTE* row = data + 32 + static_cast<SIZE_T>(mid) * 2056;
        int comparison = wcsncmp(key, reinterpret_cast<const WCHAR*>(row), 1024);
        if (!comparison) { result = *reinterpret_cast<const int*>(row + 2048); break; }
        if (comparison < 0) hi = mid - 1; else lo = mid + 1;
    }
    MemoryBarrier();
    return *sequence == before && result >= 0 && result <= 2 ? result : 0;
}

class Overlay final : public IShellIconOverlayIdentifier {
    volatile LONG refs = 1;
    int kind;
public:
    explicit Overlay(int value) : kind(value) { InterlockedIncrement(&objects); }
    ~Overlay() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id == IID_IUnknown || id == IID_IShellIconOverlayIdentifier) { *out = static_cast<IShellIconOverlayIdentifier*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { LONG n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE IsMemberOf(LPCWSTR path, DWORD attributes) override { return Lookup(path, attributes) == kind ? S_OK : S_FALSE; }
    HRESULT STDMETHODCALLTYPE GetPriority(int* priority) override { if (!priority) return E_POINTER; *priority = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetOverlayInfo(LPWSTR path, int capacity, int* index, DWORD* flags) override {
        if (!path || !index || !flags || capacity <= 0) return E_INVALIDARG;
        DWORD n = GetModuleFileNameW(module, path, capacity);
        if (!n || n >= static_cast<DWORD>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        *index = kind - 1; *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX; return S_OK;
    }
};
class Factory final : public IClassFactory {
    volatile LONG refs = 1; int kind;
public:
    explicit Factory(int value) : kind(value) { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id == IID_IUnknown || id == IID_IClassFactory) { *out = static_cast<IClassFactory*>(this); AddRef(); return S_OK; } return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { LONG n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto object = new(std::nothrow) Overlay(kind); if (!object) return E_OUTOFMEMORY;
        HRESULT result = object->QueryInterface(id, out); object->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&objects); else InterlockedDecrement(&objects); return S_OK; }
};
STDAPI DllGetClassObject(REFCLSID id, REFIID iid, void** out) {
    if (!out) return E_POINTER; *out = nullptr;
    int kind = id == orange ? 1 : id == red ? 2 : 0;
    if (!kind) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory(kind); if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(iid, out); factory->Release(); return hr;
}
STDAPI DllCanUnloadNow() { return objects == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    if (reason == DLL_PROCESS_DETACH) { if (cache) UnmapViewOfFile(cache); if (mapping) CloseHandle(mapping); }
    return TRUE;
}
