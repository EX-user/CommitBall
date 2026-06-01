#pragma once
#include <windows.h>
#include <gdiplus.h>
#include <string>
#include <cstdio>

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "gdiplus.lib")

const int BALL_SIZE = 48;
const int BALL_RADIUS = 20;
const int BALL_CX = BALL_SIZE / 2;
const int BALL_CY = BALL_SIZE / 2;
const int SNAP_THRESHOLD = 20;

#define IDT_OUTPUT 1
#define IDM_WRITE_TXT 1001
#define IDM_EXIT      1002

enum Edge { EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM };
extern Edge g_snappedEdge;
extern HWND g_hWnd;
extern ULONG_PTR g_gdiplusToken;

const wchar_t BALL_CLASS_NAME[] = L"CommitBallClass";
const wchar_t BALL_POS_FILE[] = L"commitball.pos";

bool BallInit(HINSTANCE hInstance);
void BallShutdown();
void RedrawBall();
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
            FlushLiveBuffer();
            break;
        case IDM_EXIT:
            PostMessage(g_hWnd, WM_CLOSE, 0, 0);
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
        }
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}
