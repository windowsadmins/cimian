// Tiny JSON reader for the four fields we expect from the Cimian status
// protocol: type (string), data (string), percent (int), error (bool).
//
// Intentionally not a general-purpose JSON parser. Avoids pulling in a
// dependency for a credential provider where any extra surface area is
// loaded in-process by LogonUI.exe.
#pragma once

#include <string>
#include <string_view>

namespace CimianStatus {

struct StatusMessage {
    std::string type;
    std::string data;
    int  percent = -1;     // -1 = absent
    bool errorFlag = false;
};

// Parse one JSON object line. Returns true on success.
bool ParseStatusMessage(std::string_view line, StatusMessage& out);

} // namespace CimianStatus
