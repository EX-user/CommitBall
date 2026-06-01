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

const int TRIPLE_PRESS_WINDOW = 600;
int g_pressCount = 0;
DWORD g_lastPressTime = 0;

const int OUTPUT_INTERVAL = 20000;
DWORD g_lastOutputTime = 0;

LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam);
void CreatePipeServer();
void ProcessMessage(const std::wstring& msg);
std::string WideToUtf8(const std::wstring& wide);
std::string GetTimestamp();
void FlushTextOutput();

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
        "ts TEXT NOT NULL,"
        "type TEXT NOT NULL,"
        "content TEXT NOT NULL"
        ");", nullptr, nullptr, &errMsg);
    if (errMsg) { printf("SQL error: %s\n", errMsg); sqlite3_free(errMsg); errMsg = nullptr; }

    sqlite3_exec(g_db,
        "CREATE INDEX IF NOT EXISTS idx_ts ON log(ts);", nullptr, nullptr, &errMsg);
    if (errMsg) { printf("SQL error: %s\n", errMsg); sqlite3_free(errMsg); errMsg = nullptr; }

    const char* insertSQL = "INSERT INTO log (ts, type, content) VALUES (?, ?, ?)";
    int rc = sqlite3_prepare_v2(g_db, insertSQL, -1, &g_insertStmt, nullptr);
    if (rc != SQLITE_OK) {
        printf("Error: Cannot prepare insert statement: %s\n", sqlite3_errmsg(g_db));
        return 1;
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

        if (msg.message == WM_TIMER) {
            if (GetTickCount() - g_lastOutputTime >= OUTPUT_INTERVAL) {
                FlushTextOutput();
                g_lastOutputTime = GetTickCount();
            }
        }
    }

    UnhookWindowsHookEx(hook);
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    sqlite3_finalize(g_insertStmt);
    sqlite3_close(g_db);
    return 0;
}

LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && wParam == WM_KEYDOWN) {
        KBDLLHOOKSTRUCT* p = (KBDLLHOOKSTRUCT*)lParam;
        if (p->vkCode == VK_OEM_4) {
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
                    g_lastOutputTime = GetTickCount();
                    printf("State: RECORDING\n");
                } else {
                    g_state = STOPPED;
                    printf("State: STOPPED\n");
                }
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
        sqlite3_bind_text(g_insertStmt, 1, timestamp.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(g_insertStmt, 2, "commit", -1, SQLITE_STATIC);
        sqlite3_bind_text(g_insertStmt, 3, utf8.c_str(), -1, SQLITE_TRANSIENT);
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
            sqlite3_bind_text(g_insertStmt, 1, timestamp.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_bind_text(g_insertStmt, 2, "keystroke", -1, SQLITE_STATIC);
            sqlite3_bind_text(g_insertStmt, 3, utf8.c_str(), -1, SQLITE_TRANSIENT);
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

void FlushTextOutput() {
    sqlite3_stmt* stmt;
    int rc = sqlite3_prepare_v2(g_db,
        "SELECT content FROM log ORDER BY id",
        -1, &stmt, nullptr);
    if (rc != SQLITE_OK) {
        printf("SQL error: %s\n", sqlite3_errmsg(g_db));
        return;
    }

    std::string output;
    int count = 0;
    while (sqlite3_step(stmt) == SQLITE_ROW) {
        const char* content = (const char*)sqlite3_column_text(stmt, 0);
        if (content) {
            output += content;
            count++;
        }
    }
    sqlite3_finalize(stmt);

    if (!output.empty()) {
        FILE* f = fopen("commitball.txt", "w");
        if (f) {
            fprintf(f, "%s", output.c_str());
            fclose(f);
        }
    }
}
