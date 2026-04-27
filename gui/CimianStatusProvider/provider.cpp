#include "provider.h"
#include "credential.h"
#include "fields.h"

#include <shlwapi.h>
#include <new>

namespace CimianStatus {

CCimianProvider::CCimianProvider() = default;

CCimianProvider::~CCimianProvider() {
    if (m_cred) m_cred->Release();
}

// IUnknown ------------------------------------------------------------------

IFACEMETHODIMP_(ULONG) CCimianProvider::AddRef() {
    return ++m_ref;
}

IFACEMETHODIMP_(ULONG) CCimianProvider::Release() {
    LONG r = --m_ref;
    if (r == 0) delete this;
    return r;
}

IFACEMETHODIMP CCimianProvider::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider) {
        *ppv = static_cast<ICredentialProvider*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

// ICredentialProvider -------------------------------------------------------

IFACEMETHODIMP CCimianProvider::SetUsageScenario(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD /*dwFlags*/) {
    // PLAP is what we register for. Decline every other scenario so this
    // provider never appears on the regular logon, unlock, or change-password
    // surfaces.
    if (cpus == CPUS_PLAP) {
        m_isPlap = true;
        if (!m_cred) {
            m_cred = new (std::nothrow) CCimianCredential();
            if (!m_cred) return E_OUTOFMEMORY;
        }
        return S_OK;
    }
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianProvider::SetSerialization(
    const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) {
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianProvider::Advise(ICredentialProviderEvents*, UINT_PTR) {
    return S_OK;
}

IFACEMETHODIMP CCimianProvider::UnAdvise() {
    return S_OK;
}

IFACEMETHODIMP CCimianProvider::GetFieldDescriptorCount(DWORD* pdwCount) {
    if (!pdwCount) return E_POINTER;
    *pdwCount = FIELD_COUNT;
    return S_OK;
}

IFACEMETHODIMP CCimianProvider::GetFieldDescriptorAt(
    DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) {
    if (!ppcpfd) return E_POINTER;
    if (dwIndex >= FIELD_COUNT) return E_INVALIDARG;

    auto* desc = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
        CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
    if (!desc) return E_OUTOFMEMORY;

    desc->dwFieldID = g_FieldDescriptors[dwIndex].dwFieldID;
    desc->cpft      = g_FieldDescriptors[dwIndex].cpft;
    HRESULT hr = SHStrDupW(g_FieldDescriptors[dwIndex].pszLabel, &desc->pszLabel);
    if (FAILED(hr)) {
        CoTaskMemFree(desc);
        return hr;
    }
    desc->guidFieldType = GUID_NULL;
    *ppcpfd = desc;
    return S_OK;
}

IFACEMETHODIMP CCimianProvider::GetCredentialCount(
    DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault) {
    if (!pdwCount || !pdwDefault || !pbAutoLogonWithDefault) return E_POINTER;
    *pdwCount = (m_isPlap && m_cred) ? 1 : 0;
    *pdwDefault = 0;
    *pbAutoLogonWithDefault = FALSE;
    return S_OK;
}

IFACEMETHODIMP CCimianProvider::GetCredentialAt(
    DWORD dwIndex, ICredentialProviderCredential** ppcpc) {
    if (!ppcpc) return E_POINTER;
    *ppcpc = nullptr;
    if (!m_isPlap || !m_cred || dwIndex != 0) return E_INVALIDARG;
    return m_cred->QueryInterface(IID_PPV_ARGS(ppcpc));
}

} // namespace CimianStatus
