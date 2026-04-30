// In-process TCP server replacing the role cimistatus.exe normally plays.
// Listens on 127.0.0.1:19847 and parses newline-delimited JSON status
// messages from managedsoftwareupdate.exe. Calls IStatusSink methods on a
// background thread; the sink is responsible for marshalling to UI thread.
#pragma once

#include "json_lite.h"

#include <atomic>
#include <string>
#include <thread>

namespace CimianStatus {

class IStatusSink {
public:
    virtual ~IStatusSink() = default;
    virtual void OnStatusMessage(const std::wstring& text) = 0;
    virtual void OnDetailMessage(const std::wstring& text) = 0;
    virtual void OnPercentProgress(int percent) = 0;
    virtual void OnQuit() = 0;
};

class StatusListener {
public:
    StatusListener();
    ~StatusListener();

    // Sink pointer must outlive Start/Stop. Caller owns the sink.
    bool Start(IStatusSink* sink);
    void Stop();

private:
    void RunLoop();
    void HandleClient(unsigned long long socket);

    IStatusSink* m_sink = nullptr;
    std::atomic<bool> m_running{false};
    std::thread m_thread;
    unsigned long long m_listenSocket = 0; // SOCKET as opaque
};

} // namespace CimianStatus
