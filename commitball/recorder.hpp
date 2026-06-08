#pragma once
#include <windows.h>
#include <psapi.h>
#include <string>
#include <vector>
#include <tuple>
#include <ctime>
#include <cstdarg>
#include <cstdio>
#include <UIAutomation.h>
#include "sqlite3.h"
#include "clipboard.hpp"
#include "dbexport.hpp"

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "psapi.lib")

enum State { STOPPED, RECORDING };
extern State g_state;
extern int g_recordId;
extern HANDLE g_pipe;
extern sqlite3* g_db;
extern sqlite3_stmt* g_insertStmt;
extern DWORD g_lastOutputTime;
extern DWORD g_lastTimerEvent;
extern DWORD g_recordingStartTime;
extern bool g_running;
extern HWND g_hWnd;
extern HWND g_lastFocusHwnd;
extern int g_focusNoChangeCount;
extern IUIAutomation* g_pUIAutomation;

const int FLUSH_INTERVAL = 30000;
const int64_t SESSION_SPLIT_SIZE = 512 * 1024;
const int FOCUS_TITLE_MAX = 128;
#define WM_PIPE_MSG (WM_USER + 1)

const char CURRENT_DB[]   = "data/db/current.db";
const char SESSIONS_DIR[] = "data/sessions";
const char EXPORTS_DIR[]  = "data/exports";
const char LOG_DIR[]      = "data/log";
const char LIVE_TXT[]     = "data/live/live.txt";

struct TriggerKey {
    UINT vk;
    DWORD maxDelay;
};

const int TRIGGER_PATTERNS_COUNT = 1;
const int TRIGGER_PATTERNS_MAX_LEN = 4;
const int TRIGGER_HISTORY_SIZE = 8;

const char BAR_TRIGGER_DEFAULT[] = "\\ccb";
const int BAR_TRIGGER_MAX_LEN = 10;
char g_barTrigger[BAR_TRIGGER_MAX_LEN + 1] = "\\ccb";
int g_barTriggerLen = 4;
char g_barSeq[16] = {};
int g_barSeqLen = 0;
DWORD g_barSeqTime = 0;

inline bool CheckBarTrigger(UINT vk) {
    char c = 0;
    if (vk >= 'A' && vk <= 'Z') c = (char)(vk + 32);
    else if (vk >= '0' && vk <= '9') c = (char)vk;
    else if (vk == VK_OEM_5) c = '\\';
    else if (vk == VK_OEM_1) c = ';';
    else if (vk == VK_OEM_2) c = '/';
    else if (vk == VK_OEM_3) c = '`';
    else if (vk == VK_OEM_4) c = '[';
    else if (vk == VK_OEM_6) c = ']';
    else if (vk == VK_OEM_MINUS) c = '-';
    else if (vk == VK_OEM_PLUS) c = '=';
    else if (vk == VK_OEM_COMMA) c = ',';
    else if (vk == VK_OEM_PERIOD) c = '.';

    DWORD now = GetTickCount();

    if (c) {
        if (g_barSeqLen == 0 || now - g_barSeqTime > 2000) {
            g_barSeqLen = 0;
            g_barSeq[0] = '\0';
            g_barSeqTime = now;
        }

        if (g_barSeqLen < 15) {
            g_barSeq[g_barSeqLen++] = c;
            g_barSeq[g_barSeqLen] = '\0';
        } else {
            memmove(g_barSeq, g_barSeq + 1, 14);
            g_barSeq[15] = '\0';
            g_barSeqLen = 15;
            g_barSeq[14] = c;
        }
        if (g_barSeqLen >= g_barTriggerLen &&
            strcmp(g_barSeq + g_barSeqLen - g_barTriggerLen, g_barTrigger) == 0) {
            g_barSeqLen = 0;
            g_barSeq[0] = '\0';
            return true;
        }
    } else {
        g_barSeqLen = 0;
        g_barSeq[0] = '\0';
    }
    return false;
}

inline bool CheckTrigger(UINT vk) {
    static UINT histVk[TRIGGER_HISTORY_SIZE] = {};
    static DWORD histTime[TRIGGER_HISTORY_SIZE] = {};
    static int histCount = 0;
    static int histPos = 0;

    DWORD now = GetTickCount();
    histVk[histPos] = vk;
    histTime[histPos] = now;
    histPos = (histPos + 1) % TRIGGER_HISTORY_SIZE;
    if (histCount < TRIGGER_HISTORY_SIZE) histCount++;

    if (histCount < 4) return false;

    for (int i = 0; i < 4; i++) {
        int idx = (histPos - 4 + i + TRIGGER_HISTORY_SIZE) % TRIGGER_HISTORY_SIZE;
        if (histVk[idx] != VK_CAPITAL) return false;
        if (i > 0) {
            int prevIdx = (histPos - 4 + i - 1 + TRIGGER_HISTORY_SIZE) % TRIGGER_HISTORY_SIZE;
            if (histTime[idx] - histTime[prevIdx] > 500) return false;
        }
    }
    histCount = 0;
    return true;
}

inline bool GetConfigBool(const char* key) {
    sqlite3* db = nullptr;
    if (sqlite3_open(CURRENT_DB, &db) != SQLITE_OK) return false;
    sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT)", nullptr, nullptr, nullptr);
    sqlite3_stmt* stmt = nullptr;
    bool result = false;
    if (sqlite3_prepare_v2(db, "SELECT value FROM config WHERE key=?", -1, &stmt, nullptr) == SQLITE_OK) {
        sqlite3_bind_text(stmt, 1, key, -1, SQLITE_TRANSIENT);
        if (sqlite3_step(stmt) == SQLITE_ROW) {
            const char* val = (const char*)sqlite3_column_text(stmt, 0);
            result = (val && val[0] == '1');
        }
        sqlite3_finalize(stmt);
    }
    sqlite3_close(db);
    return result;
}

inline void SetConfigBool(const char* key, bool value) {
    sqlite3* db = nullptr;
    if (sqlite3_open(CURRENT_DB, &db) != SQLITE_OK) return;
    sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT)", nullptr, nullptr, nullptr);
    sqlite3_stmt* stmt = nullptr;
    if (sqlite3_prepare_v2(db, "INSERT OR REPLACE INTO config (key, value) VALUES (?, ?)", -1, &stmt, nullptr) == SQLITE_OK) {
        sqlite3_bind_text(stmt, 1, key, -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(stmt, 2, value ? "1" : "0", 1, SQLITE_TRANSIENT);
        sqlite3_step(stmt);
        sqlite3_finalize(stmt);
    }
    sqlite3_close(db);
}

inline void LoadBarTriggerConfig() {
    sqlite3* cfgDb = nullptr;
    if (sqlite3_open(CURRENT_DB, &cfgDb) != SQLITE_OK) return;
    sqlite3_exec(cfgDb, "CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT)", nullptr, nullptr, nullptr);
    sqlite3_stmt* stmt = nullptr;
    if (sqlite3_prepare_v2(cfgDb, "SELECT value FROM config WHERE key='bar_trigger'", -1, &stmt, nullptr) == SQLITE_OK) {
        if (sqlite3_step(stmt) == SQLITE_ROW) {
            const char* val = (const char*)sqlite3_column_text(stmt, 0);
            int len = (int)strlen(val);
            if (len > 0 && len <= BAR_TRIGGER_MAX_LEN) {
                memcpy(g_barTrigger, val, len + 1);
                g_barTriggerLen = len;
            }
        }
        sqlite3_finalize(stmt);
    }
    sqlite3_close(cfgDb);
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

inline void Log(const char* fmt, ...) {
    static int logLineCount = 0;
    const int MAX_LOG_LINES = 1000;
    if (logLineCount >= MAX_LOG_LINES) {
        char logPath[MAX_PATH];
        snprintf(logPath, MAX_PATH, "%s\\commitball.log", LOG_DIR);
        FILE* f = fopen(logPath, "w");
        if (f) fclose(f);
        logLineCount = 0;
    }
    char logPath[MAX_PATH];
    snprintf(logPath, MAX_PATH, "%s\\commitball.log", LOG_DIR);
    FILE* f = fopen(logPath, "a");
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

inline void EnsureDir(const char* path) {
    CreateDirectoryA(path, NULL);
}

inline void EnsureDirs() {
    EnsureDir("data");
    EnsureDir("data/db");
    EnsureDir("data/sessions");
    EnsureDir("data/exports");
    EnsureDir("data/live");
    EnsureDir("data/log");
    EnsureDir("data/notes");
}

inline std::string GetSessionTs() {
    time_t now = time(NULL);
    struct tm timeinfo;
    localtime_s(&timeinfo, &now);
    char buffer[64];
    strftime(buffer, 64, "%Y-%m-%d_%H%M%S", &timeinfo);
    return buffer;
}

inline std::string GetMonthDir() {
    time_t now = time(NULL);
    struct tm timeinfo;
    localtime_s(&timeinfo, &now);
    char buffer[32];
    strftime(buffer, 32, "%Y-%m", &timeinfo);
    return buffer;
}

inline int64_t GetDbSize() {
    WIN32_FILE_ATTRIBUTE_DATA fileInfo;
    if (!GetFileAttributesExA(CURRENT_DB, GetFileExInfoStandard, &fileInfo))
        return 0;
    return ((int64_t)fileInfo.nFileSizeHigh << 32) | fileInfo.nFileSizeLow;
}

inline bool OpenDb(const char* path) {
    if (sqlite3_open(path, &g_db) != SQLITE_OK) return false;

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

    return true;
}

inline bool RecorderInit() {
    EnsureDirs();
    if (!OpenDb(CURRENT_DB)) return false;
    LoadBarTriggerConfig();
    CoInitializeEx(NULL, COINIT_MULTITHREADED);
    CoCreateInstance(CLSID_CUIAutomation, NULL, CLSCTX_INPROC_SERVER,
        IID_IUIAutomation, (void**)&g_pUIAutomation);
    Log("CommitBall started, record_id=%d", g_recordId);
    return true;
}

inline void RecorderCleanup() {
    if (g_insertStmt) sqlite3_finalize(g_insertStmt);
    if (g_db) sqlite3_close(g_db);
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    if (g_pUIAutomation) g_pUIAutomation->Release();
    CoUninitialize();
}

inline void FlushLiveBuffer() {
    std::string text = DbToText(g_db);
    if (!text.empty()) {
        FILE* f = fopen(LIVE_TXT, "w");
        if (f) { fprintf(f, "%s", text.c_str()); fclose(f); }
    }
}

inline void ExportSessionDb(const std::string& dbPath, const std::string& txtPath) {
    sqlite3* db;
    if (sqlite3_open(dbPath.c_str(), &db) != SQLITE_OK) return;

    std::string text = DbToText(db);
    sqlite3_close(db);

    if (!text.empty()) {
        FILE* f = fopen(txtPath.c_str(), "w");
        if (f) { fprintf(f, "%s", text.c_str()); fclose(f); }
    }
}

inline void InsertAutoAnalyseMarker() {
    if (!g_insertStmt || !g_db) return;
    std::string ts = GetTimestamp();
    sqlite3_reset(g_insertStmt);
    sqlite3_bind_int(g_insertStmt, 1, g_recordId);
    sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(g_insertStmt, 3, "auto-analyse", -1, SQLITE_STATIC);
    sqlite3_bind_text(g_insertStmt, 4, "", -1, SQLITE_STATIC);
    sqlite3_step(g_insertStmt);
    Log("Inserted auto-analyse marker at record_id=%d", g_recordId);
}

inline void CheckSessionSplit() {
    if (GetDbSize() >= SESSION_SPLIT_SIZE * 9 / 10) {
        if (!GetConfigBool("auto_analysed")) {
            extern bool IsAgentRunning();
            extern bool IsAgentBusy();
            extern void InvokeAgentAnalyse();
            if (IsAgentRunning() && !IsAgentBusy()) {
                SetConfigBool("auto_analysed", true);
                InsertAutoAnalyseMarker();
                InvokeAgentAnalyse();
            }
        }
    }

    if (GetDbSize() < SESSION_SPLIT_SIZE) return;

    std::string sessionTs = GetSessionTs();
    std::string month = GetMonthDir();

    std::string sessionDir = std::string(SESSIONS_DIR) + "\\" + month;
    EnsureDir(sessionDir.c_str());
    std::string sessionPath = sessionDir + "\\" + sessionTs + ".db";

    std::string exportDir = std::string(EXPORTS_DIR) + "\\" + month;
    EnsureDir(exportDir.c_str());
    std::string exportPath = exportDir + "\\commitball_" + sessionTs + ".txt";

    Log("Session split: size=%lld, exporting...", (long long)GetDbSize());

    sqlite3_finalize(g_insertStmt);
    g_insertStmt = nullptr;
    sqlite3_close(g_db);
    g_db = nullptr;

    sqlite3* tmpDb = nullptr;
    if (sqlite3_open(CURRENT_DB, &tmpDb) == SQLITE_OK) {
        sqlite3_exec(tmpDb, "PRAGMA wal_checkpoint(TRUNCATE)", nullptr, nullptr, nullptr);
        sqlite3_close(tmpDb);
    }

    rename(CURRENT_DB, sessionPath.c_str());

    ExportSessionDb(sessionPath, exportPath);

    OpenDb(CURRENT_DB);

    sqlite3* oldDb = nullptr;
    if (sqlite3_open(sessionPath.c_str(), &oldDb) == SQLITE_OK) {
        int64_t markerRowId = 0;
        sqlite3_stmt* markerStmt = nullptr;
        if (sqlite3_prepare_v2(oldDb,
                "SELECT id FROM log WHERE type='auto-analyse' ORDER BY id DESC LIMIT 1",
                -1, &markerStmt, nullptr) == SQLITE_OK) {
            if (sqlite3_step(markerStmt) == SQLITE_ROW)
                markerRowId = sqlite3_column_int64(markerStmt, 0);
            sqlite3_finalize(markerStmt);
        }

        sqlite3_stmt* sel = nullptr;
        std::string selectSQL;
        if (markerRowId > 0) {
            char buf[256];
            snprintf(buf, sizeof(buf),
                "SELECT record_id, ts, type, content FROM log WHERE id >= %lld OR id IN (SELECT id FROM log ORDER BY id DESC LIMIT 50) ORDER BY id ASC",
                (long long)(markerRowId > 50 ? markerRowId - 50 : 1));
            selectSQL = buf;
        } else {
            selectSQL = "SELECT record_id, ts, type, content FROM log ORDER BY id DESC LIMIT 50";
        }

        if (sqlite3_prepare_v2(oldDb, selectSQL.c_str(), -1, &sel, nullptr) == SQLITE_OK) {
            std::vector<std::tuple<int, std::string, std::string, std::string>> rows;
            while (sqlite3_step(sel) == SQLITE_ROW) {
                rows.emplace_back(
                    sqlite3_column_int(sel, 0),
                    (const char*)sqlite3_column_text(sel, 1),
                    (const char*)sqlite3_column_text(sel, 2),
                    (const char*)sqlite3_column_text(sel, 3)
                );
            }
            sqlite3_finalize(sel);

            if (markerRowId > 0 && !rows.empty()) {
                std::reverse(rows.begin(), rows.end());
            }

            for (auto it = rows.begin(); it != rows.end(); ++it) {
                const std::string& rowType = std::get<2>(*it);
                if (rowType == "auto-analyse") continue;
                sqlite3_stmt* ins = nullptr;
                if (sqlite3_prepare_v2(g_db,
                        "INSERT INTO log (record_id, ts, type, content) VALUES (?, ?, ?, ?)",
                        -1, &ins, nullptr) == SQLITE_OK) {
                    sqlite3_bind_int(ins, 1, std::get<0>(*it));
                    sqlite3_bind_text(ins, 2, std::get<1>(*it).c_str(), -1, SQLITE_TRANSIENT);
                    sqlite3_bind_text(ins, 3, std::get<2>(*it).c_str(), -1, SQLITE_TRANSIENT);
                    sqlite3_bind_text(ins, 4, std::get<3>(*it).c_str(), -1, SQLITE_TRANSIENT);
                    sqlite3_step(ins);
                    sqlite3_finalize(ins);
                }
            }
        }
        sqlite3_close(oldDb);
    }

    sqlite3_finalize(g_insertStmt);
    g_insertStmt = nullptr;
    const char* insertSQL = "INSERT INTO log (record_id, ts, type, content) VALUES (?, ?, ?, ?)";
    sqlite3_prepare_v2(g_db, insertSQL, -1, &g_insertStmt, nullptr);

    sqlite3_stmt* maxStmt;
    if (sqlite3_prepare_v2(g_db, "SELECT COALESCE(MAX(record_id), 0) FROM log", -1, &maxStmt, nullptr) == SQLITE_OK) {
        if (sqlite3_step(maxStmt) == SQLITE_ROW)
            g_recordId = sqlite3_column_int(maxStmt, 0);
        sqlite3_finalize(maxStmt);
    }

    Log("Session split done: new current.db, record_id=%d", g_recordId);
}

inline std::wstring GetFocusInfo(std::wstring& outTitle, std::wstring& outProcess, RECT& outRect) {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) {
        outTitle = L"";
        outProcess = L"";
        outRect = {0, 0, 0, 0};
        return L"";
    }

    wchar_t titleBuf[256];
    int titleLen = GetWindowTextW(hwnd, titleBuf, 256);
    outTitle = titleLen > 0 ? std::wstring(titleBuf, min(titleLen, FOCUS_TITLE_MAX)) : L"";

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    outProcess = L"[unknown]";
    if (pid) {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
        if (hProc) {
            wchar_t exeBuf[MAX_PATH];
            if (GetModuleFileNameExW(hProc, NULL, exeBuf, MAX_PATH)) {
                const wchar_t* slash = wcsrchr(exeBuf, L'\\');
                outProcess = slash ? std::wstring(slash + 1) : std::wstring(exeBuf);
            }
            CloseHandle(hProc);
        }
    }

    GetWindowRect(hwnd, &outRect);

    Log("Focus: hwnd=%p title=%S process=%S rect=%ld,%ld,%ld,%ld",
        hwnd, outTitle.c_str(), outProcess.c_str(),
        outRect.left, outRect.top, outRect.right, outRect.bottom);

    return outTitle;
}

inline void InsertFocusEvent(const std::wstring& title, const std::wstring& processName, const RECT& rect, bool isDummy = false) {
    if (!g_insertStmt || !g_db) return;

    std::string ts = GetTimestamp();
    std::string titleUtf8 = WideToUtf8(title);
    std::string procUtf8 = WideToUtf8(processName);

    char timeBuf[16];
    time_t now = time(NULL);
    struct tm ti;
    localtime_s(&ti, &now);
    strftime(timeBuf, 16, "%H:%M:%S", &ti);

    char contentBuf[512];
    snprintf(contentBuf, sizeof(contentBuf), "%s|%s",
        titleUtf8.c_str(), procUtf8.c_str());

    const char* eventType = "focus";
    char focusType[64];
    if (isDummy) {
        snprintf(focusType, sizeof(focusType), "focus-stay-%s", timeBuf);
        eventType = focusType;
    }

    sqlite3_reset(g_insertStmt);
    sqlite3_bind_int(g_insertStmt, 1, g_recordId);
    sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(g_insertStmt, 3, eventType, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(g_insertStmt, 4, contentBuf, -1, SQLITE_TRANSIENT);
    sqlite3_step(g_insertStmt);
}

inline void CheckFocusChange() {
    if (g_state != RECORDING) return;
    HWND currentHwnd = GetForegroundWindow();
    if (currentHwnd != g_lastFocusHwnd) {
        std::wstring title, process;
        RECT rect;
        GetFocusInfo(title, process, rect);
        InsertFocusEvent(title, process, rect);
        g_lastFocusHwnd = currentHwnd;
        g_focusNoChangeCount = 0;
    }
}

inline void CheckFocusTimer() {
    if (g_state != RECORDING) return;
    HWND currentHwnd = GetForegroundWindow();
    if (currentHwnd != g_lastFocusHwnd) {
        std::wstring title, process;
        RECT rect;
        GetFocusInfo(title, process, rect);
        InsertFocusEvent(title, process, rect);
        g_lastFocusHwnd = currentHwnd;
        g_focusNoChangeCount = 0;
    } else {
        g_focusNoChangeCount++;
        if (g_focusNoChangeCount >= 150) {
            std::wstring title, process;
            RECT rect;
            GetFocusInfo(title, process, rect);
            InsertFocusEvent(title, process, rect, true);
            g_focusNoChangeCount = 0;
        }
    }
}

inline void CheckTimerEvent() {
    if (g_state != RECORDING) return;
    if (GetTickCount() - g_lastTimerEvent >= 600000) {
        std::string ts = GetTimestamp();
        sqlite3_reset(g_insertStmt);
        sqlite3_bind_int(g_insertStmt, 1, g_recordId);
        sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(g_insertStmt, 3, "timer", -1, SQLITE_STATIC);
        sqlite3_bind_text(g_insertStmt, 4, "", -1, SQLITE_STATIC);
        sqlite3_step(g_insertStmt);
        g_lastTimerEvent = GetTickCount();
        CheckSessionSplit();
    }
}

inline void CheckSessionTimeout() {
    if (g_state != RECORDING) return;
    if (GetTickCount() - g_recordingStartTime < 3600000) return;

    Log("Session timeout: 1h reached, splitting session");

    g_state = STOPPED;

    g_state = RECORDING;
    g_recordId++;
    g_recordingStartTime = GetTickCount();
    g_lastTimerEvent = GetTickCount();
    g_lastOutputTime = GetTickCount();
    g_focusNoChangeCount = 0;

    std::wstring title, process;
    RECT rect;
    GetFocusInfo(title, process, rect);
    InsertFocusEvent(title, process, rect);

    extern void OnStateChanged();
    OnStateChanged();

    Log("Session timeout: new record #%d", g_recordId);
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

        if (CheckBarTrigger(vk)) {
            extern void SendShowToBar();
            SendShowToBar();
        }

        if (CheckTrigger(vk)) {
            if (g_state == STOPPED) {
                g_state = RECORDING;
                g_recordId++;
                g_lastOutputTime = GetTickCount();
                g_lastTimerEvent = GetTickCount();
                g_recordingStartTime = GetTickCount();
                std::wstring title, process;
                RECT rect;
                GetFocusInfo(title, process, rect);
                InsertFocusEvent(title, process, rect);
                g_focusNoChangeCount = 0;
                Log("State: RECORDING (record #%d)", g_recordId);
            } else {
                g_state = STOPPED;
                Log("State: STOPPED");
            }
            extern void OnStateChanged();
            OnStateChanged();
        }

        if (g_state == RECORDING) {
            const wchar_t* name = SpecialKeyName(vk);
            if (name) {
                CheckFocusChange();
                std::string ts = GetTimestamp();
                std::string utf8 = WideToUtf8(name);
                sqlite3_reset(g_insertStmt);
                sqlite3_bind_int(g_insertStmt, 1, g_recordId);
                sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
                sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_step(g_insertStmt);
                CheckSessionSplit();
            } else if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) && vk == 'V') {
                CheckFocusChange();
                std::string ts = GetTimestamp();
                PasteResult pr = ReadClipboardText();
                sqlite3_reset(g_insertStmt);
                sqlite3_bind_int(g_insertStmt, 1, g_recordId);
                sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
                switch (pr.type) {
                    case PASTE_NORMAL: sqlite3_bind_text(g_insertStmt, 3, "paste", -1, SQLITE_STATIC); break;
                    case PASTE_BIG:    sqlite3_bind_text(g_insertStmt, 3, "paste-big", -1, SQLITE_STATIC); break;
                    case PASTE_MEGA:   sqlite3_bind_text(g_insertStmt, 3, "paste-mega", -1, SQLITE_STATIC); break;
                    default:           sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC); break;
                }
                if (pr.type == PASTE_NONE) {
                    sqlite3_bind_text(g_insertStmt, 4, "[Paste]", -1, SQLITE_STATIC);
                } else {
                    sqlite3_bind_text(g_insertStmt, 4, pr.content.c_str(), -1, SQLITE_TRANSIENT);
                }
                sqlite3_step(g_insertStmt);
                CheckSessionSplit();
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
                    PostMessage(g_hWnd, WM_PIPE_MSG, (WPARAM)pMsg, 0); // lParam=0: keyboard msg
                }
            }
        }

        DisconnectNamedPipe(g_pipe);
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

inline void InsertDirectInput(const std::string& text) {
    std::string ts = GetTimestamp();
    sqlite3_reset(g_insertStmt);
    sqlite3_bind_int(g_insertStmt, 1, g_recordId);
    sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(g_insertStmt, 3, "direct-input", -1, SQLITE_STATIC);
    sqlite3_bind_text(g_insertStmt, 4, text.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_step(g_insertStmt);
    CheckSessionSplit();
    Log("Direct input: %s", text.substr(0, 60).c_str());
}

inline void CreateBarPipeServer() {
    const int BUF_SIZE = 4096;
    char readBuf[BUF_SIZE];

    while (g_running) {
        HANDLE hPipe = CreateNamedPipeW(
            L"\\\\.\\pipe\\CommitBall-direct",
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, BUF_SIZE, 0, 0, NULL);

        if (hPipe == INVALID_HANDLE_VALUE) {
            Sleep(1000);
            continue;
        }

        if (ConnectNamedPipe(hPipe, NULL) || GetLastError() == ERROR_PIPE_CONNECTED) {
            std::string acc;
            DWORD bytesRead;
            while (g_running && ReadFile(hPipe, readBuf, BUF_SIZE, &bytesRead, NULL)) {
                acc.append(readBuf, bytesRead);
            }
            if (!acc.empty()) {
                std::string text = acc;
                while (!text.empty() && (text.back() == '\r' || text.back() == '\n'))
                    text.pop_back();
                if (!text.empty()) {
                    std::string* pText = new std::string(std::move(text));
                    PostMessage(g_hWnd, WM_PIPE_MSG, (WPARAM)pText, 1); // lParam=1: direct-input
                }
            }
        }

        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }
}

inline void ProcessMessage(const std::wstring& msg) {
    if (g_state != RECORDING) return;

    std::string timestamp = GetTimestamp();

    if (msg.find(L"COMMIT:") == 0) {
        CheckFocusChange();
        std::wstring text = msg.substr(7);
        std::string utf8 = WideToUtf8(text);
        sqlite3_reset(g_insertStmt);
        sqlite3_bind_int(g_insertStmt, 1, g_recordId);
        sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(g_insertStmt, 3, "commit", -1, SQLITE_STATIC);
        sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_step(g_insertStmt);
        CheckSessionSplit();
    } else if (msg.find(L"KEYSTROKE:") == 0) {
        std::wstring ch = msg.substr(10);
        if (!ch.empty()) {
            CheckFocusChange();
            std::string utf8 = WideToUtf8(ch);
            sqlite3_reset(g_insertStmt);
            sqlite3_bind_int(g_insertStmt, 1, g_recordId);
            sqlite3_bind_text(g_insertStmt, 2, timestamp.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_bind_text(g_insertStmt, 3, "keystroke", -1, SQLITE_STATIC);
            sqlite3_bind_text(g_insertStmt, 4, utf8.c_str(), -1, SQLITE_TRANSIENT);
            sqlite3_step(g_insertStmt);
            CheckSessionSplit();
        }
    }
}


