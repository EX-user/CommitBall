#include <windows.h>
#include <string>
#include <ctime>
#include <cstdio>
#include "sqlite3.h"

#pragma comment(lib, "user32.lib")

enum State { STOPPED, RECORDING };
State g_state = STOPPED;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
sqlite3* g_db = nullptr;
sqlite3_stmt* g_insertStmt = nullptr;
int g_recordId = 0;

const int TRIPLE_PRESS_WINDOW = 600;
int g_pressCount = 0;
DWORD g_lastPressTime = 0;

#define ENABLE_TXT_OUTPUT 1
const int OUTPUT_INTERVAL = 10000;
DWORD g_lastOutputTime = 0;

LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam);
void CreatePipeServer();
void ProcessMessage(const std::wstring& msg);
std::string WideToUtf8(const std::wstring& wide);
std::string GetTimestamp();
std::string DbToText();

int main() {
    SetConsoleOutputCP(CP_UTF8);

    if (sqlite3_open("commitball.db", &g_db) != SQLITE_OK) {
        printf("Error: Cannot open database: %s\n", sqlite3_errmsg(g_db));
        return 1;
    }

    char* errMsg = nullptr;
    sqlite3_exec(g_db, "PRAGMA journal_mode=WAL;", nullptr, nullptr, &errMsg);
    if (errMsg) { printf("SQL error: %s\n", errMsg); sqlite3_free(errMsg); errMsg = nullptr; }

    sqlite3_exec(g_db,
        "CREATE TABLE IF NOT EXISTS log ("
        "id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "record_id INTEGER NOT NULL,"
        "ts TEXT NOT NULL,"
        "type TEXT NOT NULL,"
        "content TEXT NOT NULL"
        ");", nullptr, nullptr, &errMsg);
    if (errMsg) { printf("SQL error: %s\n", errMsg); sqlite3_free(errMsg); errMsg = nullptr; }

    sqlite3_exec(g_db,
        "CREATE INDEX IF NOT EXISTS idx_ts ON log(ts);", nullptr, nullptr, &errMsg);
    if (errMsg) { printf("SQL error: %s\n", errMsg); sqlite3_free(errMsg); errMsg = nullptr; }

    const char* insertSQL = "INSERT INTO log (record_id, ts, type, content) VALUES (?, ?, ?, ?)";
    int rc = sqlite3_prepare_v2(g_db, insertSQL, -1, &g_insertStmt, nullptr);
    if (rc != SQLITE_OK) {
        printf("Error: Cannot prepare insert statement: %s\n", sqlite3_errmsg(g_db));
        return 1;
    }

    sqlite3_stmt* maxStmt;
    if (sqlite3_prepare_v2(g_db, "SELECT COALESCE(MAX(record_id), 0) FROM log", -1, &maxStmt, nullptr) == SQLITE_OK) {
        if (sqlite3_step(maxStmt) == SQLITE_ROW)
            g_recordId = sqlite3_column_int(maxStmt, 0);
        sqlite3_finalize(maxStmt);
    }

    printf("CommitBall MVP (SQLite)\n");
    printf("Triple-press [ to start/stop recording\n");
    printf("State: STOPPED\n");

    HHOOK hook = SetWindowsHookEx(WH_KEYBOARD_LL, LLKeyboardProc, NULL, 0);
    if (!hook) {
        printf("Error: Cannot install keyboard hook\n");
        return 1;
    }

    CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        CreatePipeServer();
        return 0;
    }, NULL, 0, NULL);

    g_lastOutputTime = GetTickCount();
    SetTimer(NULL, 1, 1000, NULL);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);

#if ENABLE_TXT_OUTPUT
        if (msg.message == WM_TIMER) {
            if (GetTickCount() - g_lastOutputTime >= OUTPUT_INTERVAL) {
                std::string text = DbToText();
                if (!text.empty()) {
                    FILE* f = fopen("commitball.txt", "w");
                    if (f) { fprintf(f, "%s", text.c_str()); fclose(f); }
                }
                g_lastOutputTime = GetTickCount();
            }
        }
#endif
    }

    UnhookWindowsHookEx(hook);
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    sqlite3_finalize(g_insertStmt);
    sqlite3_close(g_db);
    return 0;
}

const wchar_t* SpecialKeyName(UINT vk) {
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

LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && wParam == WM_KEYDOWN) {
        KBDLLHOOKSTRUCT* p = (KBDLLHOOKSTRUCT*)lParam;
        UINT vk = p->vkCode;

        if (vk == VK_OEM_4) {
            DWORD now = GetTickCount();
            if (now - g_lastPressTime < TRIPLE_PRESS_WINDOW) {
                g_pressCount++;
            } else {
                g_pressCount = 1;
            }
            g_lastPressTime = now;

            if (g_pressCount >= 3) {
                g_pressCount = 0;
                if (g_state == STOPPED) {
                    g_state = RECORDING;
                    g_recordId++;
                    g_lastOutputTime = GetTickCount();
                    printf("State: RECORDING (record #%d)\n", g_recordId);
                } else {
                    g_state = STOPPED;
                    printf("State: STOPPED\n");
                }
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

void CreatePipeServer() {
    while (true) {
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
            wchar_t buffer[4096];
            DWORD bytesRead;
            while (ReadFile(g_pipe, buffer, sizeof(buffer) - sizeof(wchar_t), &bytesRead, NULL)) {
                buffer[bytesRead / sizeof(wchar_t)] = L'\0';
                ProcessMessage(buffer);
            }
        }

        DisconnectNamedPipe(g_pipe);
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

void ProcessMessage(const std::wstring& msg) {
    if (g_state != RECORDING) return;

    std::string timestamp = GetTimestamp();

    if (msg.find(L"COMMIT:") == 0) {
        std::wstring text = msg.substr(7);
        if (!text.empty() && text.back() == L'\n') text.pop_back();
        std::string utf8 = WideToUtf8(text);
        sqlite3_reset(g_insertStmt);
        sqlite3_bind_int(g_insertStmt, 1, g_recordId);
        sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(g_insertStmt, 3, "commit", -1, SQLITE_STATIC);
        sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
        int rc = sqlite3_step(g_insertStmt);
        if (rc != SQLITE_DONE) {
            printf("SQLite error: %s\n", sqlite3_errmsg(g_db));
        } else {
            printf("Recorded commit: %s\n", utf8.c_str());
        }
    } else if (msg.find(L"KEYSTROKE:") == 0) {
        std::wstring ch = msg.substr(10);
        if (!ch.empty() && ch.back() == L'\n') ch.pop_back();
        if (!ch.empty()) {
            std::string utf8 = WideToUtf8(ch);
            sqlite3_reset(g_insertStmt);
            sqlite3_bind_int(g_insertStmt, 1, g_recordId);
            sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
            sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
            int rc = sqlite3_step(g_insertStmt);
            if (rc != SQLITE_DONE) {
                printf("SQLite error: %s\n", sqlite3_errmsg(g_db));
            }
        }
    }
}

std::string WideToUtf8(const std::wstring& wide) {
    if (wide.empty()) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), NULL, 0, NULL, NULL);
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), &result[0], size, NULL, NULL);
    return result;
}

std::string GetTimestamp() {
    time_t now = time(NULL);
    struct tm timeinfo;
    localtime_s(&timeinfo, &now);
    char buffer[64];
    strftime(buffer, 64, "%Y-%m-%d %H:%M:%S", &timeinfo);
    return buffer;
}

std::string DbToText() {
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
    if (rc != SQLITE_OK) {
        printf("SQL error: %s\n", sqlite3_errmsg(g_db));
        return "";
    }

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
