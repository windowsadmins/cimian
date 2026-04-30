// Header-only debug logger for the CimianStatusProvider PLAP.
//
// Writes UTC-timestamped lines to C:\ProgramData\ManagedInstalls\Logs\plap_debug.log.
// Designed to be safe from LogonUI's restricted process context: uses only
// kernel32/advapi32 surface, opens with FILE_SHARE_READ|FILE_SHARE_WRITE so
// multiple processes (regsvr32, LogonUI, cimistatus.exe) can append without
// stomping each other.
//
// Temporary diagnostic — remove or guard behind a build flag once the PLAP
// is verified working in the field.
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

namespace CimianStatus {

inline void PlapLogV(const wchar_t* fmt, va_list args) {
    SYSTEMTIME st;
    GetSystemTime(&st);

    wchar_t prefix[160];
    _snwprintf_s(prefix, _countof(prefix), _TRUNCATE,
        L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ pid=%lu tid=%lu | ",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
        GetCurrentProcessId(), GetCurrentThreadId());

    wchar_t body[1024];
    _vsnwprintf_s(body, _countof(body), _TRUNCATE, fmt, args);

    wchar_t line[1280];
    _snwprintf_s(line, _countof(line), _TRUNCATE, L"%s%s\r\n", prefix, body);

    // Best-effort: ensure the Logs directory exists before opening the file.
    CreateDirectoryW(L"C:\\ProgramData\\ManagedInstalls", nullptr);
    CreateDirectoryW(L"C:\\ProgramData\\ManagedInstalls\\Logs", nullptr);

    HANDLE h = CreateFileW(
        L"C:\\ProgramData\\ManagedInstalls\\Logs\\plap_debug.log",
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    int u8len = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (u8len > 1) {
        char* buf = static_cast<char*>(HeapAlloc(GetProcessHeap(), 0, u8len));
        if (buf) {
            WideCharToMultiByte(CP_UTF8, 0, line, -1, buf, u8len, nullptr, nullptr);
            DWORD written = 0;
            WriteFile(h, buf, static_cast<DWORD>(u8len - 1), &written, nullptr);
            HeapFree(GetProcessHeap(), 0, buf);
        }
    }
    CloseHandle(h);
}

inline void PlapLog(const wchar_t* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    PlapLogV(fmt, args);
    va_end(args);
}

} // namespace CimianStatus

#define PLAPLOG(...) ::CimianStatus::PlapLog(__VA_ARGS__)
