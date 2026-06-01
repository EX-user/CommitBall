#include <windows.h>
#include <gdiplus.h>
#include <string>
#include <ctime>
#include <cstdarg>
#include "sqlite3.h"

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "gdiplus.lib")

#define IDT_OUTPUT 1
#define IDM_WRITE_TXT 1001
#define IDM_EXIT      1002

const int BALL_SIZE = 48;
const int BALL_RADIUS = 20;
const int BALL_CX = BALL_SIZE / 2;
const int BALL_CY = BALL_SIZE / 2;
const int SNAP_THRESHOLD = 20;

enum Edge { EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM };
enum State { STOPPED, RECORDING };
State g_state = STOPPED;
Edge g_snappedEdge = EDGE_NONE;
int g_savedX = 0, g_savedY = 0;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
sqlite3* g_db = nullptr;
sqlite3_stmt* g_insertStmt = nullptr;
int g_recordId = 0;
HWND g_hWnd = nullptr;

const int TRIPLE_PRESS_WINDOW = 600;
int g_pressCount = 0;
DWORD g_lastPressTime = 0;

#define ENABLE_TXT_OUTPUT 1
const int OUTPUT_INTERVAL = 10000;
const int MAX_LOG_LINES = 1000;
DWORD g_lastOutputTime = 0;

ULONG_PTR g_gdiplusToken = 0;

LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);
LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam);
void CreatePipeServer();
void ProcessMessage(const std::wstring& msg);
std::string WideToUtf8(const std::wstring& wide);
std::string GetTimestamp();
std::string DbToText();
void WriteTxtNow();
void RedrawBall();
void SavePosition();
void LoadPosition();
void ApplySnappedEdge();
void UnsnapForRecording();
void Log(const char* fmt, ...);

const wchar_t CLASS_NAME[] = L"CommitBallClass";
const wchar_t MUTEX_NAME[] = L"CommitBallMutex";
const wchar_t POS_FILE[] = L"commitball.pos";

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    HANDLE hMutex = CreateMutexW(NULL, TRUE, MUTEX_NAME);
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    if (sqlite3_open("commitball.db", &g_db) != SQLITE_OK) {
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 1;
    }

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
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 1;
    }

    sqlite3_stmt* maxStmt;
    if (sqlite3_prepare_v2(g_db, "SELECT COALESCE(MAX(record_id), 0) FROM log", -1, &maxStmt, nullptr) == SQLITE_OK) {
        if (sqlite3_step(maxStmt) == SQLITE_ROW)
            g_recordId = sqlite3_column_int(maxStmt, 0);
        sqlite3_finalize(maxStmt);
    }

    Log("CommitBall started, record_id=%d", g_recordId);

    Gdiplus::GdiplusStartupInput gdiplusStartupInput;
    Gdiplus::GdiplusStartup(&g_gdiplusToken, &gdiplusStartupInput, NULL);

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.lpszClassName = CLASS_NAME;
    RegisterClassExW(&wc);

    LoadPosition();
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    if (g_savedX == 0 && g_savedY == 0) {
        g_savedX = screenW - BALL_SIZE - 40;
        g_savedY = screenH - BALL_SIZE - 80;
    }

    g_hWnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        CLASS_NAME, L"CommitBall",
        WS_POPUP,
        g_savedX, g_savedY, BALL_SIZE, BALL_SIZE,
        NULL, NULL, hInstance, NULL);

    if (!g_hWnd) {
        Gdiplus::GdiplusShutdown(g_gdiplusToken);
        sqlite3_finalize(g_insertStmt);
        sqlite3_close(g_db);
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 1;
    }

    ShowWindow(g_hWnd, SW_SHOWNOACTIVATE);
    RedrawBall();

    HHOOK hook = SetWindowsHookEx(WH_KEYBOARD_LL, LLKeyboardProc, NULL, 0);
    Log("CommitBall started, record_id=%d", g_recordId);

    CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        CreatePipeServer();
        return 0;
    }, NULL, 0, NULL);

    g_lastOutputTime = GetTickCount();
    SetTimer(g_hWnd, IDT_OUTPUT, 1000, NULL);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    UnhookWindowsHookEx(hook);
    SavePosition();
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    sqlite3_finalize(g_insertStmt);
    sqlite3_close(g_db);
    Gdiplus::GdiplusShutdown(g_gdiplusToken);

    if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
    return 0;
}

void RedrawBall() {
    HDC hdcScreen = GetDC(NULL);
    HDC hdcMem = CreateCompatibleDC(hdcScreen);

    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = BALL_SIZE;
    bmi.bmiHeader.biHeight = -BALL_SIZE;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* pvBits = nullptr;
    HBITMAP hBmp = CreateDIBSection(hdcScreen, &bmi, DIB_RGB_COLORS, &pvBits, NULL, 0);
    HBITMAP hOld = (HBITMAP)SelectObject(hdcMem, hBmp);

    Gdiplus::Graphics graphics(hdcMem);
    graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintAntiAlias);
    graphics.Clear(Gdiplus::Color(0, 0, 0, 0));

    Gdiplus::Color ballColor = (g_state == RECORDING)
        ? Gdiplus::Color(255, 239, 68, 68)
        : Gdiplus::Color(255, 59, 130, 246);

    Gdiplus::SolidBrush brush(ballColor);
    graphics.FillEllipse(&brush,
        BALL_CX - BALL_RADIUS, BALL_CY - BALL_RADIUS,
        BALL_RADIUS * 2, BALL_RADIUS * 2);

    Gdiplus::Pen pen(Gdiplus::Color(255, 255, 255, 255), 1.5f);
    graphics.DrawEllipse(&pen,
        BALL_CX - BALL_RADIUS + 1, BALL_CY - BALL_RADIUS + 1,
        (BALL_RADIUS - 1) * 2, (BALL_RADIUS - 1) * 2);

    const wchar_t* symbol = (g_state == RECORDING) ? L"\x25B6" : L"\x23F8";

    Gdiplus::FontFamily fontFamily(L"Segoe UI Symbol");
    if (fontFamily.IsAvailable()) {
        Gdiplus::Font font(&fontFamily, 16.0f, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
        Gdiplus::SolidBrush textBrush(Gdiplus::Color(255, 255, 255, 255));
        Gdiplus::StringFormat sf;
        sf.SetAlignment(Gdiplus::StringAlignmentCenter);
        sf.SetLineAlignment(Gdiplus::StringAlignmentCenter);
        Gdiplus::RectF rect(
            (float)(BALL_CX - BALL_RADIUS) + (g_state == RECORDING ? 2.0f : 0.0f),
            (float)(BALL_CY - BALL_RADIUS),
            (float)(BALL_RADIUS * 2), (float)(BALL_RADIUS * 2));
        graphics.DrawString(symbol, -1, &font, rect, &sf, &textBrush);
    }

    POINT ptSrc = {0, 0};
    POINT ptDst = {0, 0};
    SIZE sz = {BALL_SIZE, BALL_SIZE};
    BLENDFUNCTION blend = {};
    blend.BlendOp = AC_SRC_OVER;
    blend.SourceConstantAlpha = 255;
    blend.AlphaFormat = AC_SRC_ALPHA;

    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    ptDst.x = rc.left;
    ptDst.y = rc.top;

    UpdateLayeredWindow(g_hWnd, hdcScreen, &ptDst, &sz, hdcMem, &ptSrc, 0, &blend, ULW_ALPHA);

    SelectObject(hdcMem, hOld);
    DeleteObject(hBmp);
    DeleteDC(hdcMem);
    ReleaseDC(NULL, hdcScreen);
}

LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_PAINT:
        RedrawBall();
        ValidateRect(hWnd, NULL);
        return 0;

    case WM_NCHITTEST: {
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(hWnd, &pt);
        int dx = pt.x - BALL_CX;
        int dy = pt.y - BALL_CY;
        if (dx * dx + dy * dy <= BALL_RADIUS * BALL_RADIUS) {
            return HTCAPTION;
        }
        return HTNOWHERE;
    }

    case WM_NCRBUTTONUP: {
        HMENU hMenu = CreatePopupMenu();
        AppendMenuW(hMenu, MF_STRING, IDM_WRITE_TXT, L"\x5199\x5165 txt");
        AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
        AppendMenuW(hMenu, MF_STRING, IDM_EXIT, L"\x9000\x51FA CommitBall");
        POINT pt;
        GetCursorPos(&pt);
        SetForegroundWindow(hWnd);
        TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, hWnd, NULL);
        PostMessage(hWnd, WM_NULL, 0, 0);
        DestroyMenu(hMenu);
        return 0;
    }

    case WM_EXITSIZEMOVE: {
        RECT rc;
        GetWindowRect(hWnd, &rc);
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        int x = rc.left, y = rc.top;
        g_snappedEdge = EDGE_NONE;
        if (x < SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_LEFT; x = -BALL_RADIUS;
        } else if (x + BALL_SIZE > screenW - SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_RIGHT; x = screenW - BALL_RADIUS;
        } else if (y < SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_TOP; y = -BALL_RADIUS;
        } else if (y + BALL_SIZE > screenH - SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_BOTTOM; y = screenH - BALL_RADIUS;
        }
        SetWindowPos(hWnd, NULL, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }

    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case IDM_WRITE_TXT:
            WriteTxtNow();
            break;
        case IDM_EXIT:
            PostMessage(g_hWnd, WM_CLOSE, 0, 0);
            break;
        }
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    case WM_TIMER:
        if (wParam == IDT_OUTPUT) {
#if ENABLE_TXT_OUTPUT
            if (GetTickCount() - g_lastOutputTime >= OUTPUT_INTERVAL) {
                WriteTxtNow();
                g_lastOutputTime = GetTickCount();
            }
#endif
        }
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}

void WriteTxtNow() {
    std::string text = DbToText();
    if (!text.empty()) {
        FILE* f = fopen("commitball.txt", "w");
        if (f) { fprintf(f, "%s", text.c_str()); fclose(f); }
    }
}

void Log(const char* fmt, ...) {
    static int logLineCount = 0;
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

void SavePosition() {
    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    FILE* f = _wfopen(POS_FILE, L"w");
    if (f) {
        fprintf(f, "%d %d %d", rc.left, rc.top, (int)g_snappedEdge);
        fclose(f);
    }
}

void LoadPosition() {
    FILE* f = _wfopen(POS_FILE, L"r");
    if (f) {
        int edge = 0;
        if (fscanf(f, "%d %d %d", &g_savedX, &g_savedY, &edge) == 3) {
            g_snappedEdge = (Edge)edge;
        }
        fclose(f);
    }
}

void ApplySnappedEdge() {
    if (g_snappedEdge == EDGE_NONE) return;
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    int x = rc.left, y = rc.top;
    switch (g_snappedEdge) {
        case EDGE_LEFT:   x = -BALL_RADIUS; break;
        case EDGE_RIGHT:  x = screenW - BALL_RADIUS; break;
        case EDGE_TOP:    y = -BALL_RADIUS; break;
        case EDGE_BOTTOM: y = screenH - BALL_RADIUS; break;
        default: break;
    }
    SetWindowPos(g_hWnd, NULL, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}

void UnsnapForRecording() {
    if (g_snappedEdge == EDGE_NONE) return;
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    int x = rc.left, y = rc.top;
    switch (g_snappedEdge) {
        case EDGE_LEFT:   x += BALL_RADIUS; break;
        case EDGE_RIGHT:  x -= BALL_RADIUS; break;
        case EDGE_TOP:    y += BALL_RADIUS; break;
        case EDGE_BOTTOM: y -= BALL_RADIUS; break;
        default: break;
    }
    x = max(0, min(x, screenW - BALL_SIZE));
    y = max(0, min(y, screenH - BALL_SIZE));
    SetWindowPos(g_hWnd, NULL, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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
                    UnsnapForRecording();
                    Log("State: RECORDING (record #%d)", g_recordId);
                } else {
                    g_state = STOPPED;
                    ApplySnappedEdge();
                    Log("State: STOPPED");
                }
                RedrawBall();
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
        sqlite3_step(g_insertStmt);
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
            sqlite3_step(g_insertStmt);
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
