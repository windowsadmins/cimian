#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <unknwn.h>
#include <atomic>
#include <mutex>
#include <string>

#include "fields.h"
#include "status_listener.h"

namespace CimianStatus {

// Implements ICredentialProviderCredential2 for the single tile this provider
// publishes. Acts as the IStatusSink for StatusListener and marshals updates
// into LogonUI via ICredentialProviderCredentialEvents.
class CCimianCredential :
    public ICredentialProviderCredential2,
    public IStatusSink {
public:
    CCimianCredential();

    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(DWORD dwFieldID,
                                 CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                 CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override;
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, LPWSTR* ppwsz) override;
    IFACEMETHODIMP GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD dwFieldID, BOOL* pbChecked, LPWSTR* ppwszLabel) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD dwFieldID, DWORD* pcItems, DWORD* pdwSelectedItem) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD dwFieldID, DWORD dwItem, LPWSTR* ppwszItem) override;
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, LPCWSTR pwz) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD dwFieldID, BOOL bChecked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelected) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD dwFieldID) override;
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
                                    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
                                    LPWSTR* ppwszOptionalStatusText,
                                    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
                                LPWSTR* ppwszOptionalStatusText,
                                CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(LPWSTR* sid) override;

    // IStatusSink
    void OnStatusMessage(const std::wstring& text) override;
    void OnDetailMessage(const std::wstring& text) override;
    void OnPercentProgress(int percent) override;
    void OnQuit() override;

private:
    ~CCimianCredential();

    void RefreshLogTail();
    HBITMAP CurrentProgressBitmap();

    std::atomic<LONG> m_ref{1};
    std::mutex m_mutex;

    ICredentialProviderCredentialEvents* m_events = nullptr;

    std::wstring m_fields[FIELD_COUNT];
    int  m_percent = 0;
    bool m_logVisible = false;

    StatusListener m_listener;
};

} // namespace CimianStatus
