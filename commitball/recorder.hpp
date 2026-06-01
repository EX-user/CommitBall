#pragma once
#include <windows.h>
#include <string>
#include <ctime>
#include <cstdarg>
#include <cstdio>
#include "sqlite3.h"

#pragma comment(lib, "user32.lib")

enum State { STOPPED, RECORDING };
extern State g_state;
extern int g_recordId;
extern HANDLE g_pipe;
extern sqlite3* g_db;
extern sqlite3_stmt* g_insertStmt;
extern DWORD g_lastOutputTime;
extern bool g_running;
extern HWND g_hWnd;

const int TRIPLE_PRESS_WINDOW = 600;
#define ENABLE_TXT_OUTPUT 1
const int OUTPUT_INTERVAL = 10000;
#define WM_PIPE_MSG (WM_USER + 1)

bool RecorderInit();
void RecorderCleanup();
LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam);
void CreatePipeServer();
void ProcessMessage(const std::wstring& msg);
const wchar_t* SpecialKeyName(UINT vk);
std::string WideToUtf8(const std::wstring& wide);
std::string GetTimestamp();
std::string DbToText();
void WriteTxtNow();
void Log(const char* fmt, ...);

inline bool RecorderInit() {
    if (sqlite3_open("commitball.db", &g_db) != SQLITE_OK) return false;

    char* errMsg = nullptr;
    sqlite3_exec(g_db, "PRAGMA journal_mode=WAL;", nullptr, nullptr, &errMsg);
    if (errMsg) { sqlite3_free(errMsg); errMsg = nullptr; }

    sqlite3_exec(g_db,
        "CREATE TABLE IF NOT EXISTS log ("
        "id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "record_id INTEGER NOT NULL,"
        "ts TEXT NOT NULL,"
        "type TEXT NOT NULL,"
        "content TEXT NOT NULL"
        ");", nullptr, nullptr, &errMsg);
    if (errMsg) { sqlite3_free(errMsg); errMsg = nullptr; }

    sqlite3_exec(g_db,
        "CREATE INDEX IF NOT EXISTS idx_ts ON log(ts);", nullptr, nullptr, &errMsg);
    if (errMsg) { sqlite3_free(errMsg); errMsg = nullptr; }

    const char* insertSQL = "INSERT INTO log (record_id, ts, type, content) VALUES (?, ?, ?, ?)";
    if (sqlite3_prepare_v2(g_db, insertSQL, -1, &g_insertStmt, nullptr) != SQLITE_OK) {
        sqlite3_close(g_db);
        g_db = nullptr;
        return false;
    }

    sqlite3_stmt* maxStmt;
    if (sqlite3_prepare_v2(g_db, "SELECT COALESCE(MAX(record_id), 0) FROM log", -1, &maxStmt, nullptr) == SQLITE_OK) {
        if (sqlite3_step(maxStmt) == SQLITE_ROW)
            g_recordId = sqlite3_column_int(maxStmt, 0);
        sqlite3_finalize(maxStmt);
    }

    Log("CommitBall started, record_id=%d", g_recordId);
    return true;
}

inline void RecorderCleanup() {
    if (g_insertStmt) sqlite3_finalize(g_insertStmt);
    if (g_db) sqlite3_close(g_db);
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
}

inline void WriteTxtNow() {
    std::string text = DbToText();
    if (!text.empty()) {
        FILE* f = fopen("commitball.txt", "w");
        if (f) { fprintf(f, "%s", text.c_str()); fclose(f); }
    }
}

inline void Log(const char* fmt, ...) {
    static int logLineCount = 0;
    const int MAX_LOG_LINES = 1000;
    if (logLineCount >= MAX_LOG_LINES) {
        FILE* f = fopen("commitball.log", "w");
        if (f) fclose(f);
        logLineCount = 0;
    }
    FILE* f = fopen("commitball.log", "a");
    if (!f) return;
    char ts[64];
    time_t now = time(NULL);
    struct tm ti;
    localtime_s(&ti, &now);
    strftime(ts, 64, "%H:%M:%S", &ti);
    fprintf(f, "[%s] ", ts);
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fprintf(f, "\n");
    fclose(f);
    logLineCount++;
}

inline const wchar_t* SpecialKeyName(UINT vk) {
    switch (vk) {
        case VK_BACK:     return L"[Backspace]";
        case VK_TAB:      return L"[Tab]";
        case VK_RETURN:   return L"[Enter]";
        case VK_DELETE:   return L"[Delete]";
        case VK_LEFT:     return L"[Left]";
        case VK_RIGHT:    return L"[Right]";
        case VK_UP:       return L"[Up]";
        case VK_DOWN:     return L"[Down]";
        case VK_HOME:     return L"[Home]";
        case VK_END:      return L"[End]";
        case VK_PRIOR:    return L"[PageUp]";
        case VK_NEXT:     return L"[PageDown]";
        case VK_ESCAPE:   return L"[Esc]";
        default:          return nullptr;
    }
}

inline LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && wParam == WM_KEYDOWN) {
        KBDLLHOOKSTRUCT* p = (KBDLLHOOKSTRUCT*)lParam;
        UINT vk = p->vkCode;

        if (vk == VK_OEM_4) {
            static int pressCount = 0;
            static DWORD lastPressTime = 0;
            DWORD now = GetTickCount();
            if (now - lastPressTime < TRIPLE_PRESS_WINDOW) {
                pressCount++;
            } else {
                pressCount = 1;
            }
            lastPressTime = now;

            if (pressCount >= 3) {
                pressCount = 0;
                if (g_state == STOPPED) {
                    g_state = RECORDING;
                    g_recordId++;
                    g_lastOutputTime = GetTickCount();
                    Log("State: RECORDING (record #%d)", g_recordId);
                } else {
                    g_state = STOPPED;
                    Log("State: STOPPED");
                }
                extern void OnStateChanged();
                OnStateChanged();
            }
        }

        if (g_state == RECORDING) {
            const wchar_t* name = SpecialKeyName(vk);
            if (name) {
                std::string ts = GetTimestamp();
                std::string utf8 = WideToUtf8(name);
                sqlite3_reset(g_insertStmt);
                sqlite3_bind_int(g_insertStmt, 1, g_recordId);
                sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
                sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_step(g_insertStmt);
            } else if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) && vk == 'V') {
                std::string ts = GetTimestamp();
                sqlite3_reset(g_insertStmt);
                sqlite3_bind_int(g_insertStmt, 1, g_recordId);
                sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
                sqlite3_bind_text(g_insertStmt, 4, "[Paste]", -1, SQLITE_STATIC);
                sqlite3_step(g_insertStmt);
            }
        }
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}

inline void CreatePipeServer() {
    const int BUF_SIZE = 4096;
    char readBuf[BUF_SIZE];

    while (g_running) {
        g_pipe = CreateNamedPipeW(
            L"\\\\.\\pipe\\CommitBall",
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            4096, 4096, 0, NULL);

        if (g_pipe == INVALID_HANDLE_VALUE) {
            Sleep(1000);
            continue;
        }

        if (ConnectNamedPipe(g_pipe, NULL) || GetLastError() == ERROR_PIPE_CONNECTED) {
            std::string acc;
            DWORD bytesRead;
            while (g_running && ReadFile(g_pipe, readBuf, BUF_SIZE, &bytesRead, NULL)) {
                acc.append(readBuf, bytesRead);
                while (acc.size() >= 4) {
                    uint32_t msgLen;
                    memcpy(&msgLen, acc.data(), 4);
                    if (msgLen > 1024 * 1024) { acc.clear(); break; }
                    if (acc.size() < 4 + msgLen) break;
                    std::wstring wmsg((const wchar_t*)(acc.data() + 4), msgLen / sizeof(wchar_t));
                    acc.erase(0, 4 + msgLen);
                    std::wstring* pMsg = new std::wstring(std::move(wmsg));
                    PostMessage(g_hWnd, WM_PIPE_MSG, (WPARAM)pMsg, 0);
                }
            }
        }

        DisconnectNamedPipe(g_pipe);
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

inline void ProcessMessage(const std::wstring& msg) {
    if (g_state != RECORDING) return;

    std::string timestamp = GetTimestamp();

    if (msg.find(L"COMMIT:") == 0) {
        std::wstring text = msg.substr(7);
        std::string utf8 = WideToUtf8(text);
        sqlite3_reset(g_insertStmt);
        sqlite3_bind_int(g_insertStmt, 1, g_recordId);
        sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(g_insertStmt, 3, "commit", -1, SQLITE_STATIC);
        sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_step(g_insertStmt);
    } else if (msg.find(L"KEYSTROKE:") == 0) {
        std::wstring ch = msg.substr(10);
        if (!ch.empty()) {
            std::string utf8 = WideToUtf8(ch);
            sqlite3_reset(g_insertStmt);
            sqlite3_bind_int(g_insertStmt, 1, g_recordId);
            sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
            sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_step(g_insertStmt);
        }
    }
}

inline std::string WideToUtf8(const std::wstring& wide) {
    if (wide.empty()) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), NULL, 0, NULL, NULL);
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), &result[0], size, NULL, NULL);
    return result;
}

inline std::string GetTimestamp() {
    time_t now = time(NULL);
    struct tm timeinfo;
    localtime_s(&timeinfo, &now);
    char buffer[64];
    strftime(buffer, 64, "%Y-%m-%d %H:%M:%S", &timeinfo);
    return buffer;
}

inline std::string DbToText() {
    static const std::pair<const char*, const char*> shortMap[] = {
        {"[Backspace]", "[<bs]"},
        {"[Tab]",       "[<tab]"},
        {"[Enter]",     "[<cr]"},
        {"[Delete]",    "[<del]"},
        {"[Left]",      "[<-]"},
        {"[Right]",     "[->]"},
        {"[Up]",        "[<up]"},
        {"[Down]",      "[<dn]"},
        {"[Home]",      "[<hm]"},
        {"[End]",       "[<end]"},
        {"[PageUp]",    "[<pu]"},
        {"[PageDown]",  "[<pd]"},
        {"[Esc]",       "[<esc]"},
        {"[Paste]",     "[<paste]"},
        {"[Copy]",      "[<copy]"},
        {"[Cut]",       "[<cut]"},
        {"[Undo]",      "[<undo]"},
    };

    sqlite3_stmt* stmt;
    int rc = sqlite3_prepare_v2(g_db,
        "SELECT record_id, ts, type, content FROM log ORDER BY record_id, id",
        -1, &stmt, nullptr);
    if (rc != SQLITE_OK) return "";

    std::string output;
    int curRecordId = -1;
    std::string firstTs, lastTs, body;

    auto flushRecord = [&]() {
        if (curRecordId < 0) return;
        output += "--- #" + std::to_string(curRecordId) + " [" + firstTs + " ~ " + lastTs + "] ---\n";
        output += body;
        if (!body.empty() && body.back() != '\n') output += "\n";
        output += "\n";
    };

    while (sqlite3_step(stmt) == SQLITE_ROW) {
        int recordId = sqlite3_column_int(stmt, 0);
        const char* ts = (const char*)sqlite3_column_text(stmt, 1);
        const char* content = (const char*)sqlite3_column_text(stmt, 3);

        if (recordId != curRecordId) {
            flushRecord();
            curRecordId = recordId;
            firstTs = ts ? ts : "";
            lastTs = firstTs;
            body.clear();
        } else if (ts) {
            lastTs = ts;
        }

        if (content) {
            std::string s = content;
            for (auto& [from, to] : shortMap) {
                size_t pos = 0;
                while ((pos = s.find(from, pos)) != std::string::npos) {
                    s.replace(pos, strlen(from), to);
                    pos += strlen(to);
                }
            }
            body += s;
        }
    }
    flushRecord();
    sqlite3_finalize(stmt);
    return output;
}
