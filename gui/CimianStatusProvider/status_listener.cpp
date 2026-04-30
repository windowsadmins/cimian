#include "status_listener.h"
#include "debug_log.h"

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <string>
#include <vector>

#pragma comment(lib, "Ws2_32.lib")

namespace CimianStatus {

namespace {

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

} // namespace

StatusListener::StatusListener() = default;

StatusListener::~StatusListener() {
    Stop();
}

bool StatusListener::Start(IStatusSink* sink) {
    if (m_running.load()) return true;
    m_sink = sink;

    WSADATA wsa{};
    int wsaErr = WSAStartup(MAKEWORD(2, 2), &wsa);
    if (wsaErr != 0) {
        PLAPLOG(L"StatusListener WSAStartup failed err=%d", wsaErr);
        return false;
    }

    SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) {
        PLAPLOG(L"StatusListener socket() failed wsaErr=%d", WSAGetLastError());
        WSACleanup();
        return false;
    }

    BOOL reuse = TRUE;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR,
               reinterpret_cast<const char*>(&reuse), sizeof(reuse));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(19847);
    inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

    if (bind(listener, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        int err = WSAGetLastError();
        PLAPLOG(L"StatusListener bind 127.0.0.1:19847 failed wsaErr=%d (port likely in use)", err);
        closesocket(listener);
        WSACleanup();
        return false;
    }

    if (listen(listener, 1) == SOCKET_ERROR) {
        PLAPLOG(L"StatusListener listen() failed wsaErr=%d", WSAGetLastError());
        closesocket(listener);
        WSACleanup();
        return false;
    }

    m_listenSocket = static_cast<unsigned long long>(listener);
    m_running.store(true);
    m_thread = std::thread(&StatusListener::RunLoop, this);
    PLAPLOG(L"StatusListener listening on 127.0.0.1:19847");
    return true;
}

void StatusListener::Stop() {
    if (!m_running.exchange(false)) return;
    SOCKET listener = static_cast<SOCKET>(m_listenSocket);
    if (listener != INVALID_SOCKET && listener != 0) {
        closesocket(listener);
    }
    if (m_thread.joinable()) m_thread.join();
    WSACleanup();
    m_listenSocket = 0;
}

void StatusListener::RunLoop() {
    SOCKET listener = static_cast<SOCKET>(m_listenSocket);
    while (m_running.load()) {
        sockaddr_in client{};
        int clientLen = sizeof(client);
        SOCKET conn = accept(listener, reinterpret_cast<sockaddr*>(&client), &clientLen);
        if (conn == INVALID_SOCKET) {
            // listener closed by Stop(), or transient failure
            if (!m_running.load()) return;
            continue;
        }
        PLAPLOG(L"StatusListener client connected from 127.0.0.1:%d", ntohs(client.sin_port));
        HandleClient(static_cast<unsigned long long>(conn));
        closesocket(conn);
        PLAPLOG(L"StatusListener client disconnected");
    }
}

void StatusListener::HandleClient(unsigned long long sockHandle) {
    SOCKET conn = static_cast<SOCKET>(sockHandle);

    std::string buffer;
    buffer.reserve(4096);
    char chunk[1024];

    while (m_running.load()) {
        int n = recv(conn, chunk, sizeof(chunk), 0);
        if (n <= 0) return;
        buffer.append(chunk, n);

        // Process all complete lines.
        for (;;) {
            auto nl = buffer.find('\n');
            if (nl == std::string::npos) break;
            std::string line = buffer.substr(0, nl);
            buffer.erase(0, nl + 1);
            // Tolerate CRLF.
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;

            StatusMessage msg;
            if (!ParseStatusMessage(line, msg)) continue;
            if (!m_sink) continue;

            if (msg.type == "statusMessage") {
                m_sink->OnStatusMessage(Utf8ToWide(msg.data));
            } else if (msg.type == "detailMessage") {
                m_sink->OnDetailMessage(Utf8ToWide(msg.data));
            } else if (msg.type == "percentProgress") {
                if (msg.percent >= 0) m_sink->OnPercentProgress(msg.percent);
            } else if (msg.type == "quit") {
                m_sink->OnQuit();
            }
            // displayLog and other types are ignored — we tail the log file
            // ourselves when the user clicks the View Log link.
        }
    }
}

} // namespace CimianStatus
