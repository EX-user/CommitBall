#pragma once
#include <windows.h>
#include <string>

const int PASTE_CONTENT_MAX = 1000;
const int PASTE_HEAD_ONLY_THRESHOLD = 10000;

enum PasteType { PASTE_NORMAL, PASTE_BIG, PASTE_MEGA, PASTE_NONE };

struct PasteResult {
    PasteType type;
    std::string content;
};

inline std::string WideClipboardTextToUtf8(const std::wstring& text) {
    if (text.empty()) return "";
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), NULL, 0, NULL, NULL);
    if (utf8Len <= 0) return "";
    std::string result(utf8Len, 0);
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), &result[0], utf8Len, NULL, NULL);
    return result;
}

inline PasteResult ReadClipboardText() {
    if (!OpenClipboard(NULL)) return {PASTE_NONE, ""};

    HANDLE hData = GetClipboardData(CF_UNICODETEXT);
    if (!hData) { CloseClipboard(); return {PASTE_NONE, ""}; }

    wchar_t* pText = (wchar_t*)GlobalLock(hData);
    if (!pText) { CloseClipboard(); return {PASTE_NONE, ""}; }

    int wLen = lstrlenW(pText);
    std::wstring text(pText, wLen);

    GlobalUnlock(hData);
    CloseClipboard();

    if (text.empty()) return {PASTE_NONE, ""};

    if ((int)text.size() <= PASTE_CONTENT_MAX) {
        std::string content = WideClipboardTextToUtf8(text);
        return content.empty() ? PasteResult{PASTE_NONE, ""} : PasteResult{PASTE_NORMAL, content};
    }

    if ((int)text.size() > PASTE_HEAD_ONLY_THRESHOLD) {
        std::wstring clipped = text.substr(0, PASTE_CONTENT_MAX - 12) + L"...... ......";
        std::string content = WideClipboardTextToUtf8(clipped);
        return content.empty() ? PasteResult{PASTE_NONE, ""} : PasteResult{PASTE_MEGA, content};
    } else {
        int headLen = PASTE_CONTENT_MAX / 2;
        int tailLen = PASTE_CONTENT_MAX - headLen - 6;
        std::wstring clipped = text.substr(0, headLen) + L"......" + text.substr(text.size() - tailLen);
        std::string content = WideClipboardTextToUtf8(clipped);
        return content.empty() ? PasteResult{PASTE_NONE, ""} : PasteResult{PASTE_BIG, content};
    }
}
