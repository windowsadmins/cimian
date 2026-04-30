#include "credential.h"
#include "progress_bitmap.h"
#include "debug_log.h"

#include <shlwapi.h>
#include <strsafe.h>
#include <fstream>
#include <deque>
#include <string>

#pragma comment(lib, "Shlwapi.lib")

namespace CimianStatus {

namespace {

constexpr wchar_t kLogPath[] =
    L"C:\\ProgramData\\ManagedInstalls\\Logs\\managedsoftwareupdate.log";
constexpr size_t kLogTailLines = 40;

HRESULT DupCoString(const std::wstring& s, LPWSTR* out) {
    if (!out) return E_POINTER;
    return SHStrDupW(s.c_str(), out);
}

// Read the last N lines of a file. UTF-8 in, UTF-16 out. Returns empty on
// failure — the log may be locked while managedsoftwareupdate writes to it.
std::wstring ReadLogTail() {
    std::ifstream f(kLogPath, std::ios::in | std::ios::binary);
    if (!f) return L"(log not yet available)";

    std::deque<std::string> lines;
    std::string line;
    while (std::getline(f, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        lines.push_back(std::move(line));
        if (lines.size() > kLogTailLines) lines.pop_front();
    }

    std::string joined;
    for (auto& l : lines) { joined.append(l); joined.push_back('\n'); }
    if (joined.empty()) return L"";

    int n = MultiByteToWideChar(CP_UTF8, 0, joined.data(), (int)joined.size(),
                                nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, joined.data(), (int)joined.size(),
                        w.data(), n);
    return w;
}

} // namespace

CCimianCredential::CCimianCredential() {
    PLAPLOG(L"CCimianCredential ctor this=%p", this);
    for (DWORD i = 0; i < FIELD_COUNT; ++i) {
        if (g_FieldInitialStrings[i]) m_fields[i] = g_FieldInitialStrings[i];
    }
    // Best-effort: start the TCP listener. If it fails (port in use, no
    // winsock) we still render the static "preparing" tile.
    bool started = m_listener.Start(this);
    PLAPLOG(L"CCimianCredential listener Start -> %s", started ? L"OK" : L"FAILED");
}

CCimianCredential::~CCimianCredential() {
    PLAPLOG(L"CCimianCredential dtor this=%p", this);
    m_listener.Stop();
}

// IUnknown ------------------------------------------------------------------

IFACEMETHODIMP_(ULONG) CCimianCredential::AddRef() {
    return ++m_ref;
}

IFACEMETHODIMP_(ULONG) CCimianCredential::Release() {
    LONG r = --m_ref;
    if (r == 0) delete this;
    return r;
}

IFACEMETHODIMP CCimianCredential::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown ||
        riid == IID_ICredentialProviderCredential ||
        riid == IID_ICredentialProviderCredential2) {
        *ppv = static_cast<ICredentialProviderCredential2*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

// ICredentialProviderCredential --------------------------------------------

IFACEMETHODIMP CCimianCredential::Advise(ICredentialProviderCredentialEvents* pcpce) {
    PLAPLOG(L"Credential Advise pcpce=%p", pcpce);
    std::lock_guard<std::mutex> lk(m_mutex);
    if (m_events) m_events->Release();
    m_events = pcpce;
    if (m_events) m_events->AddRef();
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::UnAdvise() {
    std::lock_guard<std::mutex> lk(m_mutex);
    if (m_events) { m_events->Release(); m_events = nullptr; }
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::SetSelected(BOOL* pbAutoLogon) {
    PLAPLOG(L"Credential SetSelected");
    if (pbAutoLogon) *pbAutoLogon = FALSE;
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::SetDeselected() {
    PLAPLOG(L"Credential SetDeselected");
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::GetFieldState(DWORD dwFieldID,
                                                CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                                CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) {
    if (dwFieldID >= FIELD_COUNT) return E_INVALIDARG;
    if (pcpfs) {
        if (dwFieldID == FIELD_LOG_TEXT) {
            *pcpfs = m_logVisible ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN;
        } else {
            *pcpfs = g_FieldState[dwFieldID];
        }
    }
    if (pcpfis) *pcpfis = g_FieldInteractiveState[dwFieldID];
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::GetStringValue(DWORD dwFieldID, LPWSTR* ppwsz) {
    if (dwFieldID >= FIELD_COUNT) return E_INVALIDARG;
    std::lock_guard<std::mutex> lk(m_mutex);
    return DupCoString(m_fields[dwFieldID], ppwsz);
}

IFACEMETHODIMP CCimianCredential::GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp) {
    if (!phbmp) return E_POINTER;
    if (dwFieldID == FIELD_PROGRESS_IMAGE) {
        *phbmp = CurrentProgressBitmap();
        return *phbmp ? S_OK : E_OUTOFMEMORY;
    }
    if (dwFieldID == FIELD_TILE_IMAGE) {
        // No embedded logo bitmap yet — return a 1x1 transparent placeholder
        // so LogonUI gets a valid handle. A real logo can be loaded from a
        // resource later via LoadImageW.
        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = 1;
        bmi.bmiHeader.biHeight = -1;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        void* px = nullptr;
        *phbmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &px, nullptr, 0);
        return *phbmp ? S_OK : E_OUTOFMEMORY;
    }
    return E_INVALIDARG;
}

IFACEMETHODIMP CCimianCredential::GetCheckboxValue(DWORD, BOOL*, LPWSTR*) {
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianCredential::GetSubmitButtonValue(DWORD, DWORD*) {
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*) {
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianCredential::GetComboBoxValueAt(DWORD, DWORD, LPWSTR*) {
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianCredential::SetStringValue(DWORD, LPCWSTR) {
    // No editable fields in our tile.
    return E_NOTIMPL;
}

IFACEMETHODIMP CCimianCredential::SetCheckboxValue(DWORD, BOOL) { return E_NOTIMPL; }
IFACEMETHODIMP CCimianCredential::SetComboBoxSelectedValue(DWORD, DWORD) { return E_NOTIMPL; }

IFACEMETHODIMP CCimianCredential::CommandLinkClicked(DWORD dwFieldID) {
    if (dwFieldID != FIELD_VIEW_LOG_LINK) return S_OK;

    std::lock_guard<std::mutex> lk(m_mutex);
    m_logVisible = !m_logVisible;
    if (m_logVisible) {
        m_fields[FIELD_LOG_TEXT] = ReadLogTail();
        m_fields[FIELD_VIEW_LOG_LINK] = L"Hide log";
    } else {
        m_fields[FIELD_LOG_TEXT].clear();
        m_fields[FIELD_VIEW_LOG_LINK] = L"View log";
    }

    if (m_events) {
        m_events->SetFieldState(this, FIELD_LOG_TEXT,
            m_logVisible ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN);
        m_events->SetFieldString(this, FIELD_LOG_TEXT, m_fields[FIELD_LOG_TEXT].c_str());
        m_events->SetFieldString(this, FIELD_VIEW_LOG_LINK, m_fields[FIELD_VIEW_LOG_LINK].c_str());
    }
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* /*pcpcs*/,
    LPWSTR* /*ppwszOptionalStatusText*/,
    CREDENTIAL_PROVIDER_STATUS_ICON* /*pcpsiOptionalStatusIcon*/) {
    // PLAPs are non-authenticating — Winlogon still expects a serialization
    // response. CPGSR_NO_CREDENTIAL_NOT_FINISHED tells LogonUI we have no
    // credential to hand to LSA and to leave the tile in place.
    if (pcpgsr) *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
    return S_OK;
}

IFACEMETHODIMP CCimianCredential::ReportResult(NTSTATUS, NTSTATUS, LPWSTR*,
                                               CREDENTIAL_PROVIDER_STATUS_ICON*) {
    return S_OK;
}

// ICredentialProviderCredential2 ------------------------------------------

IFACEMETHODIMP CCimianCredential::GetUserSid(LPWSTR* sid) {
    if (!sid) return E_POINTER;
    *sid = nullptr;
    // No backing user — return S_FALSE so LogonUI treats this as a generic
    // credential with no associated SID.
    return S_FALSE;
}

// IStatusSink --------------------------------------------------------------

void CCimianCredential::OnStatusMessage(const std::wstring& text) {
    std::lock_guard<std::mutex> lk(m_mutex);
    m_fields[FIELD_LARGE_TEXT] = text.empty() ? L"Cimian is preparing this device" : text;
    if (m_events) {
        m_events->SetFieldString(this, FIELD_LARGE_TEXT, m_fields[FIELD_LARGE_TEXT].c_str());
    }
}

void CCimianCredential::OnDetailMessage(const std::wstring& text) {
    std::lock_guard<std::mutex> lk(m_mutex);
    m_fields[FIELD_SMALL_TEXT] = text;
    if (m_events) {
        m_events->SetFieldString(this, FIELD_SMALL_TEXT, m_fields[FIELD_SMALL_TEXT].c_str());
    }
}

void CCimianCredential::OnPercentProgress(int percent) {
    std::lock_guard<std::mutex> lk(m_mutex);
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;
    m_percent = percent;

    wchar_t buf[16];
    StringCchPrintfW(buf, ARRAYSIZE(buf), L"%d%%", percent);
    m_fields[FIELD_PERCENT_TEXT] = buf;

    if (m_events) {
        m_events->SetFieldString(this, FIELD_PERCENT_TEXT, m_fields[FIELD_PERCENT_TEXT].c_str());
        HBITMAP bmp = RenderProgressBitmap(percent);
        if (bmp) {
            // LogonUI takes ownership of the HBITMAP we hand to SetFieldBitmap.
            m_events->SetFieldBitmap(this, FIELD_PROGRESS_IMAGE, bmp);
        }
    }
}

void CCimianCredential::OnQuit() {
    std::lock_guard<std::mutex> lk(m_mutex);
    m_fields[FIELD_LARGE_TEXT] = L"Cimian setup complete";
    m_fields[FIELD_SMALL_TEXT] = L"This device is ready.";
    m_percent = 100;
    m_fields[FIELD_PERCENT_TEXT] = L"100%";
    if (m_events) {
        m_events->SetFieldString(this, FIELD_LARGE_TEXT, m_fields[FIELD_LARGE_TEXT].c_str());
        m_events->SetFieldString(this, FIELD_SMALL_TEXT, m_fields[FIELD_SMALL_TEXT].c_str());
        m_events->SetFieldString(this, FIELD_PERCENT_TEXT, m_fields[FIELD_PERCENT_TEXT].c_str());
        HBITMAP bmp = RenderProgressBitmap(100);
        if (bmp) m_events->SetFieldBitmap(this, FIELD_PROGRESS_IMAGE, bmp);
    }
}

HBITMAP CCimianCredential::CurrentProgressBitmap() {
    return RenderProgressBitmap(m_percent);
}

void CCimianCredential::RefreshLogTail() {
    m_fields[FIELD_LOG_TEXT] = ReadLogTail();
}

} // namespace CimianStatus
