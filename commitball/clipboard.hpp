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

inline PasteResult ReadClipboardText() {
    if (!OpenClipboard(NULL)) return {PASTE_NONE, ""};

    HANDLE hData = GetClipboardData(CF_UNICODETEXT);
    if (!hData) { CloseClipboard(); return {PASTE_NONE, ""}; }

    wchar_t* pText = (wchar_t*)GlobalLock(hData);
    if (!pText) { CloseClipboard(); return {PASTE_NONE, ""}; }

    int wLen = lstrlenW(pText);
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, pText, wLen, NULL, 0, NULL, NULL);
    if (utf8Len <= 0) { GlobalUnlock(hData); CloseClipboard(); return {PASTE_NONE, ""}; }

    std::string result(utf8Len, 0);
    WideCharToMultiByte(CP_UTF8, 0, pText, wLen, &result[0], utf8Len, NULL, NULL);

    GlobalUnlock(hData);
    CloseClipboard();

    if ((int)result.size() <= PASTE_CONTENT_MAX) return {PASTE_NORMAL, result};

    if ((int)result.size() > PASTE_HEAD_ONLY_THRESHOLD) {
        return {PASTE_MEGA, result.substr(0, PASTE_CONTENT_MAX - 12) + "...... ......"};
    } else {
        int headLen = PASTE_CONTENT_MAX / 2;
        int tailLen = PASTE_CONTENT_MAX - headLen - 6;
        return {PASTE_BIG, result.substr(0, headLen) + "......" + result.substr(result.size() - tailLen)};
    }
}
