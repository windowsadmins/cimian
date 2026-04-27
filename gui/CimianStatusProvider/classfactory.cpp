// Standard COM class factory + DLL entry points (DllGetClassObject,
// DllCanUnloadNow, DllRegisterServer, DllUnregisterServer).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <unknwn.h>
#include <new>
#include <atomic>
#include <string>

#include "guids.h"
#include "provider.h"

namespace {

std::atomic<LONG> g_dllRefs{0};
HMODULE g_module = nullptr;

class CClassFactory : public IClassFactory {
public:
    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++m_ref; }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = --m_ref;
        if (r == 0) delete this;
        return r;
    }

    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        auto* p = new (std::nothrow) CimianStatus::CCimianProvider();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL fLock) override {
        if (fLock) ++g_dllRefs; else --g_dllRefs;
        return S_OK;
    }

private:
    std::atomic<LONG> m_ref{1};
};

} // namespace

// Bump g_dllRefs from the provider/credential constructors/destructors so
// LogonUI doesn't unload the DLL while we still hold heap-allocated objects.
extern "C" {

BOOL APIENTRY DllMain(HINSTANCE hinst, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = hinst;
        DisableThreadLibraryCalls(hinst);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_CimianStatusProvider) return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) CClassFactory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return (g_dllRefs.load() == 0) ? S_OK : S_FALSE;
}

// Registration helpers -----------------------------------------------------

namespace {

std::wstring DllPath() {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(g_module, path, MAX_PATH);
    return std::wstring(path);
}

std::wstring GuidToString(REFGUID g) {
    wchar_t buf[64];
    StringFromGUID2(g, buf, ARRAYSIZE(buf));
    return std::wstring(buf);
}

LONG SetReg(HKEY parent, const wchar_t* sub, const wchar_t* name, const wchar_t* value) {
    HKEY k = nullptr;
    LONG l = RegCreateKeyExW(parent, sub, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr);
    if (l != ERROR_SUCCESS) return l;
    DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
    l = RegSetValueExW(k, name, 0, REG_SZ,
                      reinterpret_cast<const BYTE*>(value), bytes);
    RegCloseKey(k);
    return l;
}

} // namespace

STDAPI DllRegisterServer() {
    std::wstring guid = GuidToString(CLSID_CimianStatusProvider);
    std::wstring dll  = DllPath();

    // 1. COM CLSID registration.
    std::wstring base = L"SOFTWARE\\Classes\\CLSID\\" + guid;
    if (SetReg(HKEY_LOCAL_MACHINE, base.c_str(), nullptr, L"Cimian Status Credential Provider") != ERROR_SUCCESS)
        return SELFREG_E_CLASS;

    std::wstring inproc = base + L"\\InprocServer32";
    if (SetReg(HKEY_LOCAL_MACHINE, inproc.c_str(), nullptr, dll.c_str()) != ERROR_SUCCESS)
        return SELFREG_E_CLASS;
    if (SetReg(HKEY_LOCAL_MACHINE, inproc.c_str(), L"ThreadingModel", L"Apartment") != ERROR_SUCCESS)
        return SELFREG_E_CLASS;

    // 2. Register as a PLAP credential provider.
    std::wstring plap =
        L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\PLAP Providers\\" + guid;
    if (SetReg(HKEY_LOCAL_MACHINE, plap.c_str(), nullptr, L"Cimian Status Credential Provider") != ERROR_SUCCESS)
        return SELFREG_E_CLASS;

    return S_OK;
}

STDAPI DllUnregisterServer() {
    std::wstring guid = GuidToString(CLSID_CimianStatusProvider);

    std::wstring plap =
        L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\PLAP Providers\\" + guid;
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, plap.c_str());

    std::wstring inproc = L"SOFTWARE\\Classes\\CLSID\\" + guid + L"\\InprocServer32";
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, inproc.c_str());
    std::wstring base = L"SOFTWARE\\Classes\\CLSID\\" + guid;
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, base.c_str());

    return S_OK;
}

} // extern "C"
