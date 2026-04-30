#include "json_lite.h"

#include <cctype>
#include <cstdlib>

namespace CimianStatus {

namespace {

void SkipWs(std::string_view s, size_t& i) {
    while (i < s.size() && std::isspace(static_cast<unsigned char>(s[i]))) ++i;
}

// Read a JSON string starting at the opening '"'. On return, i is one past the
// closing quote. Out is the unescaped contents (only the escapes the .NET
// serializer emits are handled — \", \\, \/, \n, \r, \t, \uXXXX where XXXX
// is in the BMP).
bool ReadString(std::string_view s, size_t& i, std::string& out) {
    if (i >= s.size() || s[i] != '"') return false;
    ++i;
    out.clear();
    while (i < s.size()) {
        char c = s[i++];
        if (c == '"') return true;
        if (c != '\\') { out.push_back(c); continue; }
        if (i >= s.size()) return false;
        char e = s[i++];
        switch (e) {
            case '"': out.push_back('"'); break;
            case '\\': out.push_back('\\'); break;
            case '/': out.push_back('/'); break;
            case 'n': out.push_back('\n'); break;
            case 'r': out.push_back('\r'); break;
            case 't': out.push_back('\t'); break;
            case 'b': out.push_back('\b'); break;
            case 'f': out.push_back('\f'); break;
            case 'u': {
                if (i + 4 > s.size()) return false;
                unsigned int code = 0;
                for (int k = 0; k < 4; ++k) {
                    char h = s[i++];
                    code <<= 4;
                    if (h >= '0' && h <= '9') code |= h - '0';
                    else if (h >= 'a' && h <= 'f') code |= h - 'a' + 10;
                    else if (h >= 'A' && h <= 'F') code |= h - 'A' + 10;
                    else return false;
                }
                // Encode as UTF-8.
                if (code < 0x80) {
                    out.push_back(static_cast<char>(code));
                } else if (code < 0x800) {
                    out.push_back(static_cast<char>(0xC0 | (code >> 6)));
                    out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                } else {
                    out.push_back(static_cast<char>(0xE0 | (code >> 12)));
                    out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                }
                break;
            }
            default:
                // Unknown escape — copy literally so we don't fail noisily.
                out.push_back(e);
                break;
        }
    }
    return false; // unterminated
}

bool ReadNumber(std::string_view s, size_t& i, int& out) {
    size_t start = i;
    if (i < s.size() && (s[i] == '-' || s[i] == '+')) ++i;
    while (i < s.size() && std::isdigit(static_cast<unsigned char>(s[i]))) ++i;
    if (i == start) return false;
    out = std::atoi(std::string(s.substr(start, i - start)).c_str());
    return true;
}

bool ReadKeyword(std::string_view s, size_t& i, std::string_view kw) {
    if (i + kw.size() > s.size()) return false;
    if (s.substr(i, kw.size()) != kw) return false;
    i += kw.size();
    return true;
}

// Skip an arbitrary JSON value we don't care about. Handles strings, numbers,
// booleans, null, and nested objects/arrays. Returns true on a successful skip.
bool SkipValue(std::string_view s, size_t& i) {
    SkipWs(s, i);
    if (i >= s.size()) return false;
    char c = s[i];
    if (c == '"') {
        std::string ignored;
        return ReadString(s, i, ignored);
    }
    if (c == '{' || c == '[') {
        char open = c, close = (c == '{') ? '}' : ']';
        ++i;
        int depth = 1;
        while (i < s.size() && depth > 0) {
            char ch = s[i];
            if (ch == '"') {
                std::string ignored;
                if (!ReadString(s, i, ignored)) return false;
            } else if (ch == open) { ++depth; ++i; }
            else if (ch == close) { --depth; ++i; }
            else { ++i; }
        }
        return depth == 0;
    }
    if (c == 't') return ReadKeyword(s, i, "true");
    if (c == 'f') return ReadKeyword(s, i, "false");
    if (c == 'n') return ReadKeyword(s, i, "null");
    int ignored;
    return ReadNumber(s, i, ignored);
}

} // namespace

bool ParseStatusMessage(std::string_view s, StatusMessage& out) {
    size_t i = 0;
    SkipWs(s, i);
    if (i >= s.size() || s[i] != '{') return false;
    ++i;

    while (i < s.size()) {
        SkipWs(s, i);
        if (i < s.size() && s[i] == '}') { ++i; return true; }

        std::string key;
        if (!ReadString(s, i, key)) return false;
        SkipWs(s, i);
        if (i >= s.size() || s[i] != ':') return false;
        ++i;
        SkipWs(s, i);

        if (key == "type") {
            if (!ReadString(s, i, out.type)) return false;
        } else if (key == "data") {
            if (i < s.size() && s[i] == 'n') {
                if (!ReadKeyword(s, i, "null")) return false;
                out.data.clear();
            } else if (!ReadString(s, i, out.data)) {
                return false;
            }
        } else if (key == "percent") {
            if (!ReadNumber(s, i, out.percent)) return false;
        } else if (key == "error") {
            if (ReadKeyword(s, i, "true")) out.errorFlag = true;
            else if (ReadKeyword(s, i, "false")) out.errorFlag = false;
            else return false;
        } else {
            if (!SkipValue(s, i)) return false;
        }

        SkipWs(s, i);
        if (i < s.size() && s[i] == ',') { ++i; continue; }
        SkipWs(s, i);
        if (i < s.size() && s[i] == '}') { ++i; return true; }
    }
    return false;
}

} // namespace CimianStatus
