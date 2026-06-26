#pragma once
#include "recorder.hpp"
#include <windows.h>
#include <string>
#include <cwctype>

#define IDT_OUTPUT 1

inline bool g_noAdmin = false;

inline const wchar_t* GetStatusText() {
    if (g_noAdmin) return L"\x72B6\x6001: \x65E0\x6743\x9650, \x7A0D\x540E\x9000\x51FA...";
    extern State g_state;
    return (g_state == RECORDING)
        ? L"\x72B6\x6001: \x8BB0\x5F55"
        : L"\x72B6\x6001: \x5C31\x7EEA";
}

inline std::wstring GetDbInfoText() {
    extern int64_t GetDbSize();
    extern const int64_t SESSION_SPLIT_SIZE;
    int64_t size = GetDbSize();
    int pct = (int)(size * 100 / SESSION_SPLIT_SIZE);
    wchar_t buf[64];
    swprintf_s(buf, L"%lldKB / 512KB (%d%%)", (long long)(size / 1024), pct);
    return buf;
}

inline bool IsUnsupportedBubbleChar(wchar_t ch) {
    unsigned int c = (unsigned int)ch;
    if (c == 0x200D) return true;                // zero width joiner
    if (c >= 0xFE00 && c <= 0xFE0F) return true; // variation selectors
    if (c >= 0x20E0 && c <= 0x20FF) return true; // combining symbol marks
    if (c >= 0x2600 && c <= 0x27BF) return true; // most BMP emoji/dingbats
    return false;
}

inline std::wstring SanitizeBubbleText(const wchar_t* text) {
    std::wstring out;
    if (!text) return out;
    for (const wchar_t* p = text; *p; ++p) {
        wchar_t ch = *p;
        if (ch >= 0xD800 && ch <= 0xDBFF) {
            if (p[1] >= 0xDC00 && p[1] <= 0xDFFF) ++p;
            continue;
        }
        if (ch >= 0xDC00 && ch <= 0xDFFF) continue;
        if (IsUnsupportedBubbleChar(ch)) continue;
        out.push_back(ch);
    }
    while (!out.empty() && iswspace(out.front())) out.erase(out.begin());
    while (!out.empty() && iswspace(out.back())) out.pop_back();
    return out;
}
