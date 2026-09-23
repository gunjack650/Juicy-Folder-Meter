#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <stdio.h>
int wmain(int argc, wchar_t** argv) {
    if (argc != 4) return 2;
    HMODULE dll = LoadLibraryW(argv[1]); if (!dll) return 3;
    auto get = reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));
    if (!get) return 4;
    int expected = _wtoi(argv[3]), actual = 0;
    for (int kind = 1; kind <= 2; kind++) {
        CLSID clsid = {0x7b8ef830,0x13ba,0x4a69,{0xb1,0x58,0x29,0x67,0x14,0x43,0x10,static_cast<BYTE>(kind)}};
        IClassFactory* factory = nullptr; IShellIconOverlayIdentifier* overlay = nullptr;
        if (FAILED(get(clsid,IID_IClassFactory,reinterpret_cast<void**>(&factory)))) return 5;
        HRESULT hr = factory->CreateInstance(nullptr,IID_IShellIconOverlayIdentifier,reinterpret_cast<void**>(&overlay)); factory->Release();
        if (FAILED(hr)) return 6;
        WCHAR icon[MAX_PATH]; int index; DWORD flags;
        if (FAILED(overlay->GetOverlayInfo(icon,MAX_PATH,&index,&flags)) || index != kind - 1) return 7;
        HICON extracted = nullptr;
        if (ExtractIconExW(icon,index,&extracted,nullptr,1) != 1) return 8;
        DestroyIcon(extracted);
        if (overlay->IsMemberOf(argv[2], FILE_ATTRIBUTE_DIRECTORY) == S_OK) actual = kind;
        if (overlay->IsMemberOf(argv[2], 0) != S_FALSE) return 9;
        if (overlay->IsMemberOf(nullptr, FILE_ATTRIBUTE_DIRECTORY) != S_FALSE) return 10;
        LARGE_INTEGER begin,end,freq; QueryPerformanceFrequency(&freq); QueryPerformanceCounter(&begin);
        for(int i=0;i<10000;i++) overlay->IsMemberOf(argv[2],FILE_ATTRIBUTE_DIRECTORY);
        QueryPerformanceCounter(&end);
        wprintf(L"Handler %d: %.3f us/lookup\n",kind,(end.QuadPart-begin.QuadPart)*1000000.0/freq.QuadPart/10000);
        overlay->Release();
    }
    auto unload = reinterpret_cast<HRESULT(__stdcall*)()>(GetProcAddress(dll,"DllCanUnloadNow"));
    if (!unload || unload() != S_OK) return 11;
    FreeLibrary(dll);
    wprintf(L"Expected %d, actual %d\n", expected, actual); return actual == expected ? 0 : 1;
}
