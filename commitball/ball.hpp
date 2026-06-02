#pragma once
#include <windows.h>
#include <gdiplus.h>
#include <string>
#include <cstdio>

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "shell32.lib")

inline float GetDpiScale() {
    HDC hdc = GetDC(NULL);
    int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
    ReleaseDC(NULL, hdc);
    return dpi / 96.0f;
}

const int BALL_SIZE = 80;
const int BALL_RADIUS = 36;
const int BALL_CX = BALL_SIZE / 2;
const int BALL_CY = BALL_SIZE / 2;
const int SNAP_THRESHOLD = 20;

#define IDT_OUTPUT 1
#define IDT_COLOR_ANIM 2
#define IDM_STATUS    1000
#define IDM_WRITE_TXT 1001
#define IDM_EXIT      1002
#define IDM_OPEN_DIR  1003

enum Edge { EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM };
extern Edge g_snappedEdge;
extern HWND g_hWnd;
extern ULONG_PTR g_gdiplusToken;

inline int g_curR = 59, g_curG = 130, g_curB = 246;
inline int g_tgtR = 59, g_tgtG = 130, g_tgtB = 246;
inline int g_curPenR = 255, g_curPenG = 255, g_curPenB = 255;
inline int g_tgtPenR = 255, g_tgtPenG = 255, g_tgtPenB = 255;
inline bool g_noAdmin = false;

inline const wchar_t* GetStatusText() {
    if (g_noAdmin) return L"\x72B6\x6001: \x65E0\x6743\x9650, \x7A0D\x540E\x9000\x51FA...";
    extern State g_state;
    return (g_state == RECORDING)
        ? L"\x72B6\x6001: \x8BB0\x5F55"
        : L"\x72B6\x6001: \x5C31\x7EEA";
}

const wchar_t BALL_CLASS_NAME[] = L"CommitBallClass";
const wchar_t BALL_POS_FILE[] = L"commitball.pos";

bool BallInit(HINSTANCE hInstance);
void BallShutdown();
void RedrawBall();
void AnimateColor();
void SavePosition();
void LoadPosition();
void ApplySnappedEdge();
void UnsnapForRecording();
LRESULT CALLBACK BallWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

inline bool BallInit(HINSTANCE hInstance) {
    Gdiplus::GdiplusStartupInput gdiplusStartupInput;
    Gdiplus::GdiplusStartup(&g_gdiplusToken, &gdiplusStartupInput, NULL);

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = BallWndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.lpszClassName = BALL_CLASS_NAME;
    RegisterClassExW(&wc);

    LoadPosition();
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    extern int g_savedX, g_savedY;
    if (g_savedX == 0 && g_savedY == 0) {
        g_savedX = screenW - BALL_SIZE - 40;
        g_savedY = screenH - BALL_SIZE - 80;
    }

    g_hWnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        BALL_CLASS_NAME, L"CommitBall",
        WS_POPUP,
        g_savedX, g_savedY, BALL_SIZE, BALL_SIZE,
        NULL, NULL, hInstance, NULL);

    if (!g_hWnd) {
        Gdiplus::GdiplusShutdown(g_gdiplusToken);
        return false;
    }

    ShowWindow(g_hWnd, SW_SHOWNOACTIVATE);
    RedrawBall();
    return true;
}

inline void BallShutdown() {
    SavePosition();
    Gdiplus::GdiplusShutdown(g_gdiplusToken);
}

inline void AnimateColor() {
    g_curR += (g_tgtR - g_curR) / 5;
    g_curG += (g_tgtG - g_curG) / 5;
    g_curB += (g_tgtB - g_curB) / 5;
    g_curPenR += (g_tgtPenR - g_curPenR) / 5;
    g_curPenG += (g_tgtPenG - g_curPenG) / 5;
    g_curPenB += (g_tgtPenB - g_curPenB) / 5;

    if (abs(g_tgtR - g_curR) <= 1 && abs(g_tgtG - g_curG) <= 1 && abs(g_tgtB - g_curB) <= 1 &&
        abs(g_tgtPenR - g_curPenR) <= 1 && abs(g_tgtPenG - g_curPenG) <= 1 && abs(g_tgtPenB - g_curPenB) <= 1) {
        g_curR = g_tgtR; g_curG = g_tgtG; g_curB = g_tgtB;
        g_curPenR = g_tgtPenR; g_curPenG = g_tgtPenG; g_curPenB = g_tgtPenB;
        KillTimer(g_hWnd, IDT_COLOR_ANIM);
    }

    RedrawBall();
}

inline void RedrawBall() {
    extern State g_state;

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
    graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHighQuality);
    graphics.Clear(Gdiplus::Color(0, 0, 0, 0));

    Gdiplus::Color ballColor(255, g_curR, g_curG, g_curB);

    Gdiplus::SolidBrush brush(ballColor);
    graphics.FillEllipse(&brush,
        BALL_CX - BALL_RADIUS, BALL_CY - BALL_RADIUS,
        BALL_RADIUS * 2, BALL_RADIUS * 2);

    Gdiplus::Pen pen(Gdiplus::Color(255, g_curPenR, g_curPenG, g_curPenB), 2.5f);
    graphics.DrawEllipse(&pen,
        BALL_CX - BALL_RADIUS + 1, BALL_CY - BALL_RADIUS + 1,
        (BALL_RADIUS - 1) * 2, (BALL_RADIUS - 1) * 2);

    const wchar_t* symbol = g_noAdmin ? L"?" : (g_state == RECORDING) ? L"\x25B6" : L"\x23F8";

    Gdiplus::FontFamily fontFamily(L"Segoe UI Symbol");
    if (fontFamily.IsAvailable()) {
        Gdiplus::Font font(&fontFamily, 34.0f, Gdiplus::FontStyleBold, Gdiplus::UnitPixel);
        Gdiplus::SolidBrush textBrush(Gdiplus::Color(255, 255, 255, 255));
        Gdiplus::StringFormat sf;
        sf.SetAlignment(Gdiplus::StringAlignmentCenter);
        sf.SetLineAlignment(Gdiplus::StringAlignmentCenter);
        Gdiplus::RectF rect(
            (float)(BALL_CX - BALL_RADIUS) + (g_state == RECORDING ? 4.0f : 0.0f),
            (float)(BALL_CY - BALL_RADIUS),
            (float)(BALL_RADIUS * 2), (float)(BALL_RADIUS * 2));
        graphics.DrawString(symbol, -1, &font, rect, &sf, &textBrush);
    }

    POINT ptSrc = {0, 0};
    SIZE sz = {BALL_SIZE, BALL_SIZE};
    BLENDFUNCTION blend = {};
    blend.BlendOp = AC_SRC_OVER;
    blend.SourceConstantAlpha = 255;
    blend.AlphaFormat = AC_SRC_ALPHA;

    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    POINT ptDst = {rc.left, rc.top};

    UpdateLayeredWindow(g_hWnd, hdcScreen, &ptDst, &sz, hdcMem, &ptSrc, 0, &blend, ULW_ALPHA);

    SelectObject(hdcMem, hOld);
    DeleteObject(hBmp);
    DeleteDC(hdcMem);
    ReleaseDC(NULL, hdcScreen);
}

inline void SavePosition() {
    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    FILE* f = _wfopen(BALL_POS_FILE, L"w");
    if (f) {
        fprintf(f, "%d %d %d", rc.left, rc.top, (int)g_snappedEdge);
        fclose(f);
    }
}

inline void LoadPosition() {
    extern int g_savedX, g_savedY;
    FILE* f = _wfopen(BALL_POS_FILE, L"r");
    if (f) {
        int edge = 0;
        if (fscanf(f, "%d %d %d", &g_savedX, &g_savedY, &edge) == 3) {
            g_snappedEdge = (Edge)edge;
        }
        fclose(f);
    }
}

inline void ApplySnappedEdge() {
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

inline void UnsnapForRecording() {
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

inline LRESULT CALLBACK BallWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
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
        AppendMenuW(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, IDM_STATUS, GetStatusText());
        AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
        AppendMenuW(hMenu, MF_STRING, IDM_WRITE_TXT, L"\x5199\x5165 txt");
        AppendMenuW(hMenu, MF_STRING, IDM_OPEN_DIR, L"\x6253\x5F00\x6570\x636E\x8DEF\x5F84");
        AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
        AppendMenuW(hMenu, MF_STRING, IDM_EXIT, L"\x9000\x51FA CommitBall");
        POINT pt;
        GetCursorPos(&pt);
        SetForegroundWindow(hWnd);
        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hWnd, NULL);
        DestroyMenu(hMenu);
        if (cmd == IDM_EXIT) {
            PostQuitMessage(0);
            CreateThread(NULL, 0, [](LPVOID) -> DWORD {
                Sleep(3000);
                ExitProcess(0);
                return 0;
            }, NULL, 0, NULL);
        } else if (cmd == IDM_WRITE_TXT) {
            FlushLiveBuffer();
        } else if (cmd == IDM_OPEN_DIR) {
            char dataPath[MAX_PATH];
            GetModuleFileNameA(NULL, dataPath, MAX_PATH);
            char* lastSlash = strrchr(dataPath, '\\');
            if (lastSlash) {
                strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - dataPath), "data");
                ShellExecuteA(NULL, "open", dataPath, NULL, NULL, SW_SHOWNORMAL);
            }
        }
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
            FlushLiveBuffer();
            break;
        case IDM_EXIT:
            DestroyWindow(g_hWnd);
            break;
        }
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    case WM_PIPE_MSG: {
        std::wstring* pMsg = (std::wstring*)wParam;
        ProcessMessage(*pMsg);
        delete pMsg;
        return 0;
    }

    case WM_TIMER:
        if (wParam == IDT_OUTPUT) {
            if (GetTickCount() - g_lastOutputTime >= FLUSH_INTERVAL) {
                FlushLiveBuffer();
                g_lastOutputTime = GetTickCount();
            }
            CheckFocusTimer();
        } else if (wParam == IDT_COLOR_ANIM) {
            AnimateColor();
        }
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}
