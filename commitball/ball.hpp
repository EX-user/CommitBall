#pragma once
#include <windows.h>
#include <gdiplus.h>
#include <string>
#include <cstdio>
#include <cmath>

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
#define IDT_BLINK 3
#define IDT_BUBBLE_HIDE 4
#define IDM_STATUS      1000
#define IDM_EXIT        1002
#define IDM_OPEN_DIR    1003
#define IDM_COPY_PATH   1004
#define IDM_DB_INFO     1005
#define IDM_HELP        1006
#define IDM_OPEN_LIVE   1007
#define IDM_AGENT       1008
#define IDM_INVOKE_AGENT 1009
#define IDM_BAR_SHOW    1010

enum Edge { EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM };
extern Edge g_snappedEdge;
extern HWND g_hWnd;
extern ULONG_PTR g_gdiplusToken;

inline int g_curR = 59, g_curG = 130, g_curB = 246;
inline int g_tgtR = 59, g_tgtG = 130, g_tgtB = 246;
inline int g_curPenR = 255, g_curPenG = 255, g_curPenB = 255;
inline int g_tgtPenR = 255, g_tgtPenG = 255, g_tgtPenB = 255;
inline bool g_noAdmin = false;
inline bool g_blinkDim = false;
inline HWND g_bubbleWnd = nullptr;
inline float g_pupilAngle = 0.0f;
inline float g_pupilTargetAngle = 0.0f;
inline float g_eyeYaw = 0.0f;
inline float g_eyePitch = 0.0f;
inline float g_eyeTargetYaw = 0.0f;
inline float g_eyeTargetPitch = 0.0f;
inline float g_eyelidProgress = 0.0f;
inline bool g_blinkActive = false;
inline bool g_blinkClosing = true;
inline DWORD g_blinkPhaseStart = 0;
inline DWORD g_nextBlinkAt = 0;
inline DWORD g_lastEyeTick = 0;
inline DWORD g_lastMouseMoveAt = 0;
inline DWORD g_nextIdleLookAt = 0;
inline bool g_hasLastCursor = false;
inline int g_lastCursorX = 0;
inline int g_lastCursorY = 0;

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
void ToggleBlink();
void UpdateEyeAnimation();
void ShowBallBubble(const wchar_t* text);
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
    extern int g_savedX, g_savedY;
    if (g_savedX == 0 && g_savedY == 0) {
        RECT workArea;
        SystemParametersInfo(SPI_GETWORKAREA, 0, &workArea, 0);
        g_savedX = screenW - BALL_SIZE - 40;
        g_savedY = workArea.bottom - BALL_SIZE - 40;
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

inline void ToggleBlink() {
    extern State g_state;
    if (g_state != RECORDING || !g_eyeModeEnabled) {
        g_blinkDim = false;
        g_eyelidProgress = 0.0f;
        g_blinkActive = false;
        return;
    }
    UpdateEyeAnimation();
    RedrawBall();
}

inline float ClampFloat(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

inline float EaseInOut(float t) {
    t = ClampFloat(t, 0.0f, 1.0f);
    return 0.5f - 0.5f * cosf(t * 3.14159265f);
}

inline void ScheduleNextBlink(DWORD now) {
    g_nextBlinkAt = now + 1100 + (DWORD)(rand() % 3300);
}

inline void UpdateEyeAnimation() {
    DWORD now = GetTickCount();
    if (g_lastEyeTick == 0) {
        g_lastEyeTick = now;
        g_lastMouseMoveAt = now;
        ScheduleNextBlink(now);
    }

    POINT cursor;
    RECT rc;
    if (GetCursorPos(&cursor) && g_hWnd && GetWindowRect(g_hWnd, &rc)) {
        if (!g_hasLastCursor) {
            g_lastCursorX = cursor.x;
            g_lastCursorY = cursor.y;
            g_hasLastCursor = true;
            g_lastMouseMoveAt = now;
        }

        int cursorDx = cursor.x - g_lastCursorX;
        int cursorDy = cursor.y - g_lastCursorY;
        if (cursorDx * cursorDx + cursorDy * cursorDy >= 4) {
            g_lastCursorX = cursor.x;
            g_lastCursorY = cursor.y;
            g_lastMouseMoveAt = now;
            g_nextIdleLookAt = 0;
        }

        bool idleLook = (now - g_lastMouseMoveAt) >= 6000;
        if (idleLook) {
            if (g_nextIdleLookAt == 0 || now >= g_nextIdleLookAt ||
                (fabsf(g_eyeTargetYaw - g_eyeYaw) < 0.035f && fabsf(g_eyeTargetPitch - g_eyePitch) < 0.035f)) {
                g_eyeTargetYaw = ((float)((rand() % 101) - 50) / 100.0f) * 0.95f;
                g_eyeTargetPitch = ((float)((rand() % 81) - 40) / 100.0f) * 0.72f;
                g_nextIdleLookAt = now + 900 + (DWORD)(rand() % 1700);
            }
        } else {
            float centerX = (float)(rc.left + BALL_CX);
            float centerY = (float)(rc.top + BALL_CY);
            float dx = (float)cursor.x - centerX;
            float dy = (float)cursor.y - centerY;
            g_eyeTargetYaw = ClampFloat(dx / 190.0f, -1.0f, 1.0f) * 0.78f;
            g_eyeTargetPitch = ClampFloat(dy / 165.0f, -1.0f, 1.0f) * 0.58f;
        }
    } else {
        g_eyeTargetYaw = 0.0f;
        g_eyeTargetPitch = 0.0f;
    }
    g_pupilTargetAngle = 0.0f;
    float trackingEase = (now - g_lastMouseMoveAt) >= 6000 ? 0.025f : 0.045f;
    g_eyeYaw += (g_eyeTargetYaw - g_eyeYaw) * trackingEase;
    g_eyePitch += (g_eyeTargetPitch - g_eyePitch) * trackingEase;
    g_pupilAngle = 0.0f;

    if (!g_blinkActive && now >= g_nextBlinkAt) {
        g_blinkActive = true;
        g_blinkClosing = true;
        g_blinkPhaseStart = now;
    }

    if (g_blinkActive) {
        const DWORD closeMs = 120;
        const DWORD openMs = 190;
        DWORD elapsed = now - g_blinkPhaseStart;
        if (g_blinkClosing) {
            float t = EaseInOut((float)elapsed / (float)closeMs);
            g_eyelidProgress = t;
            if (elapsed >= closeMs) {
                g_blinkClosing = false;
                g_blinkPhaseStart = now;
            }
        } else {
            float t = EaseInOut((float)(now - g_blinkPhaseStart) / (float)openMs);
            g_eyelidProgress = 1.0f - t;
            if (now - g_blinkPhaseStart >= openMs) {
                g_blinkActive = false;
                g_eyelidProgress = 0.0f;
                ScheduleNextBlink(now);
            }
        }
    }
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

    BYTE penAlpha = (g_state == RECORDING && g_blinkDim) ? 90 : 255;
    Gdiplus::Pen pen(Gdiplus::Color(penAlpha, g_curPenR, g_curPenG, g_curPenB), 2.5f);
    graphics.DrawEllipse(&pen,
        BALL_CX - BALL_RADIUS + 1, BALL_CY - BALL_RADIUS + 1,
        (BALL_RADIUS - 1) * 2, (BALL_RADIUS - 1) * 2);

    const wchar_t* symbol = g_noAdmin ? L"?" : (g_state == RECORDING) ? L"\x25B6" : L"\x23F8";

    Gdiplus::FontFamily fontFamily(L"Segoe UI Symbol");
    if (fontFamily.IsAvailable()) {
        Gdiplus::Font font(&fontFamily, 34.0f, Gdiplus::FontStyleBold, Gdiplus::UnitPixel);
        bool eyeModeActive = (g_state == RECORDING && g_eyeModeEnabled);
        BYTE textAlpha = eyeModeActive ? (BYTE)(255 - 130 * ClampFloat(g_eyelidProgress, 0.0f, 1.0f)) : 255;
        Gdiplus::SolidBrush textBrush(Gdiplus::Color(textAlpha, 255, 255, 255));
        Gdiplus::StringFormat sf;
        sf.SetAlignment(Gdiplus::StringAlignmentCenter);
        sf.SetLineAlignment(Gdiplus::StringAlignmentCenter);
        Gdiplus::RectF rect(
            (float)(BALL_CX - BALL_RADIUS) + (eyeModeActive ? 4.0f : 0.0f),
            (float)(BALL_CY - BALL_RADIUS),
            (float)(BALL_RADIUS * 2), (float)(BALL_RADIUS * 2));
        if (eyeModeActive && !g_noAdmin) {
            float yaw = ClampFloat(g_eyeYaw, -0.78f, 0.78f);
            float pitch = ClampFloat(g_eyePitch, -0.58f, 0.58f);
            float eyeX = sinf(yaw) * 16.0f;
            float eyeY = sinf(pitch) * 11.0f;
            float yawDepth = cosf(yaw);
            float pitchDepth = cosf(pitch);
            float pupilW = 23.0f * (0.62f + 0.38f * yawDepth);
            float pupilH = 29.0f * (0.72f + 0.28f * pitchDepth);
            float cx = (float)BALL_CX + eyeX + 1.0f;
            float cy = (float)BALL_CY + eyeY;
            BYTE pupilAlpha = (BYTE)(245 - 90 * ClampFloat(g_eyelidProgress, 0.0f, 1.0f));
            BYTE shade = (BYTE)(235 + 20 * ClampFloat((yawDepth + pitchDepth) * 0.5f, 0.0f, 1.0f));
            Gdiplus::SolidBrush pupilBrush(Gdiplus::Color(pupilAlpha, shade, shade, 255));
            Gdiplus::SolidBrush glintBrush(Gdiplus::Color(85, 255, 255, 255));
            Gdiplus::GraphicsPath pupilPath;
            Gdiplus::PointF pupil[3] = {
                Gdiplus::PointF(cx + pupilW * 0.55f, cy),
                Gdiplus::PointF(cx - pupilW * 0.45f, cy - pupilH * 0.50f),
                Gdiplus::PointF(cx - pupilW * 0.45f, cy + pupilH * 0.50f)
            };
            pupilPath.AddPolygon(pupil, 3);
            graphics.FillPath(&pupilBrush, &pupilPath);
            graphics.FillEllipse(&glintBrush, cx - pupilW * 0.24f, cy - pupilH * 0.28f, 4.6f, 4.0f);

            float cover = ClampFloat(g_eyelidProgress, 0.0f, 1.0f);
            if (cover > 0.01f) {
                Gdiplus::SolidBrush lidBrush(Gdiplus::Color(248, g_curR, g_curG, g_curB));
                Gdiplus::Pen lidPen(Gdiplus::Color(120, 255, 255, 255), 1.2f);
                Gdiplus::GraphicsState lidState = graphics.Save();
                Gdiplus::GraphicsPath clipPath;
                clipPath.AddEllipse(
                    (float)(BALL_CX - BALL_RADIUS + 2),
                    (float)(BALL_CY - BALL_RADIUS + 2),
                    (float)((BALL_RADIUS - 2) * 2),
                    (float)((BALL_RADIUS - 2) * 2));
                graphics.SetClip(&clipPath, Gdiplus::CombineModeReplace);

                float left = (float)(BALL_CX - BALL_RADIUS + 6);
                float right = (float)(BALL_CX + BALL_RADIUS - 6);
                float topEdge = (float)(BALL_CY - BALL_RADIUS + 6);
                float bottomEdge = (float)(BALL_CY + BALL_RADIUS - 6);
                float topLidY = topEdge + 31.0f * cover;
                float bottomLidY = bottomEdge - 31.0f * cover;

                Gdiplus::GraphicsPath topLid;
                topLid.StartFigure();
                topLid.AddLine(left, topEdge - 8.0f, right, topEdge - 8.0f);
                topLid.AddLine(right, topEdge - 8.0f, right, topLidY);
                topLid.AddBezier(right, topLidY, right - 18.0f, topLidY + 7.0f, left + 18.0f, topLidY + 7.0f, left, topLidY);
                topLid.AddLine(left, topLidY, left, topEdge - 8.0f);
                topLid.CloseFigure();
                graphics.FillPath(&lidBrush, &topLid);

                Gdiplus::GraphicsPath bottomLid;
                bottomLid.StartFigure();
                bottomLid.AddLine(left, bottomEdge + 8.0f, right, bottomEdge + 8.0f);
                bottomLid.AddLine(right, bottomEdge + 8.0f, right, bottomLidY);
                bottomLid.AddBezier(right, bottomLidY, right - 18.0f, bottomLidY - 7.0f, left + 18.0f, bottomLidY - 7.0f, left, bottomLidY);
                bottomLid.AddLine(left, bottomLidY, left, bottomEdge + 8.0f);
                bottomLid.CloseFigure();
                graphics.FillPath(&lidBrush, &bottomLid);

                graphics.DrawBezier(&lidPen, right, topLidY, right - 18.0f, topLidY + 7.0f, left + 18.0f, topLidY + 7.0f, left, topLidY);
                graphics.DrawBezier(&lidPen, right, bottomLidY, right - 18.0f, bottomLidY - 7.0f, left + 18.0f, bottomLidY - 7.0f, left, bottomLidY);
                graphics.Restore(lidState);
            }
        } else {
            graphics.DrawString(symbol, -1, &font, rect, &sf, &textBrush);
        }
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

inline void ShowBallBubble(const wchar_t* text) {
    if (!g_hWnd || !text || !text[0]) return;
    if (g_bubbleWnd) {
        DestroyWindow(g_bubbleWnd);
        g_bubbleWnd = nullptr;
    }

    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    int width = 286;
    int height = 64;
    int tailH = 10;
    int x = rc.left + BALL_SIZE / 2 - width / 2;
    int y = rc.top - height - 8;
    bool above = true;
    RECT workArea;
    SystemParametersInfo(SPI_GETWORKAREA, 0, &workArea, 0);
    x = max(workArea.left + 8, min(x, workArea.right - width - 8));
    if (y < workArea.top + 8) {
        y = rc.bottom + 8;
        above = false;
    }

    g_bubbleWnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        L"STATIC",
        L"",
        WS_POPUP,
        x, y, width, height,
        NULL, NULL, GetModuleHandleW(NULL), NULL);
    if (!g_bubbleWnd) return;

    HDC hdcScreen = GetDC(NULL);
    HDC hdcMem = CreateCompatibleDC(hdcScreen);
    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = -height;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    void* pvBits = nullptr;
    HBITMAP hBmp = CreateDIBSection(hdcScreen, &bmi, DIB_RGB_COLORS, &pvBits, NULL, 0);
    HBITMAP hOld = (HBITMAP)SelectObject(hdcMem, hBmp);

    Gdiplus::Graphics graphics(hdcMem);
    graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
    graphics.Clear(Gdiplus::Color(0, 0, 0, 0));

    float bodyTop = above ? 3.0f : (float)tailH;
    float bodyH = (float)height - (float)tailH - 6.0f;
    Gdiplus::RectF body(6.0f, bodyTop, (float)width - 12.0f, bodyH);
    float radius = 12.0f;

    Gdiplus::GraphicsPath shadowPath;
    Gdiplus::RectF shadow = body;
    shadow.X += 0.0f;
    shadow.Y += 2.0f;
    shadowPath.AddArc(shadow.X, shadow.Y, radius, radius, 180, 90);
    shadowPath.AddArc(shadow.GetRight() - radius, shadow.Y, radius, radius, 270, 90);
    shadowPath.AddArc(shadow.GetRight() - radius, shadow.GetBottom() - radius, radius, radius, 0, 90);
    shadowPath.AddArc(shadow.X, shadow.GetBottom() - radius, radius, radius, 90, 90);
    shadowPath.CloseFigure();
    Gdiplus::SolidBrush shadowBrush(Gdiplus::Color(34, 30, 36, 52));
    graphics.FillPath(&shadowBrush, &shadowPath);

    Gdiplus::GraphicsPath path;
    path.AddArc(body.X, body.Y, radius, radius, 180, 90);
    path.AddArc(body.GetRight() - radius, body.Y, radius, radius, 270, 90);
    path.AddArc(body.GetRight() - radius, body.GetBottom() - radius, radius, radius, 0, 90);
    path.AddArc(body.X, body.GetBottom() - radius, radius, radius, 90, 90);
    path.CloseFigure();

    float tailCenter = (float)(rc.left + BALL_SIZE / 2 - x);
    tailCenter = ClampFloat(tailCenter, 28.0f, (float)width - 28.0f);
    Gdiplus::PointF tail[3];
    if (above) {
        tail[0] = Gdiplus::PointF(tailCenter - 9.0f, body.GetBottom() - 1.0f);
        tail[1] = Gdiplus::PointF(tailCenter + 9.0f, body.GetBottom() - 1.0f);
        tail[2] = Gdiplus::PointF(tailCenter, (float)height - 4.0f);
    } else {
        tail[0] = Gdiplus::PointF(tailCenter - 9.0f, body.Y + 1.0f);
        tail[1] = Gdiplus::PointF(tailCenter + 9.0f, body.Y + 1.0f);
        tail[2] = Gdiplus::PointF(tailCenter, 3.0f);
    }

    Gdiplus::SolidBrush fillBrush(Gdiplus::Color(248, 255, 255, 255));
    Gdiplus::Pen borderPen(Gdiplus::Color(255, 210, 210, 220), 1.0f);
    Gdiplus::SolidBrush accentBrush(Gdiplus::Color(255, 59, 130, 246));
    Gdiplus::SolidBrush textBrush(Gdiplus::Color(255, 38, 45, 62));
    graphics.FillPath(&fillBrush, &path);
    graphics.FillPolygon(&fillBrush, tail, 3);
    graphics.DrawPath(&borderPen, &path);
    graphics.DrawLines(&borderPen, tail, 3);

    Gdiplus::RectF accent(body.X + 14.0f, body.Y + 16.0f, 6.0f, 20.0f);
    Gdiplus::GraphicsPath accentPath;
    accentPath.AddArc(accent.X, accent.Y, 6.0f, 6.0f, 180, 90);
    accentPath.AddArc(accent.GetRight() - 6.0f, accent.Y, 6.0f, 6.0f, 270, 90);
    accentPath.AddArc(accent.GetRight() - 6.0f, accent.GetBottom() - 6.0f, 6.0f, 6.0f, 0, 90);
    accentPath.AddArc(accent.X, accent.GetBottom() - 6.0f, 6.0f, 6.0f, 90, 90);
    accentPath.CloseFigure();
    graphics.FillPath(&accentBrush, &accentPath);

    Gdiplus::FontFamily fontFamily(L"Segoe UI");
    Gdiplus::Font font(&fontFamily, 13.5f, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
    Gdiplus::StringFormat fmt;
    fmt.SetTrimming(Gdiplus::StringTrimmingEllipsisCharacter);
    fmt.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap);
    fmt.SetAlignment(Gdiplus::StringAlignmentNear);
    fmt.SetLineAlignment(Gdiplus::StringAlignmentCenter);
    Gdiplus::RectF textRect(body.X + 30.0f, body.Y + 6.0f, body.Width - 44.0f, body.Height - 12.0f);
    graphics.DrawString(text, -1, &font, textRect, &fmt, &textBrush);

    POINT ptDst = { x, y };
    POINT ptSrc = { 0, 0 };
    SIZE size = { width, height };
    BLENDFUNCTION blend = {};
    blend.BlendOp = AC_SRC_OVER;
    blend.SourceConstantAlpha = 255;
    blend.AlphaFormat = AC_SRC_ALPHA;
    UpdateLayeredWindow(g_bubbleWnd, hdcScreen, &ptDst, &size, hdcMem, &ptSrc, 0, &blend, ULW_ALPHA);

    SelectObject(hdcMem, hOld);
    DeleteObject(hBmp);
    DeleteDC(hdcMem);
    ReleaseDC(NULL, hdcScreen);

    ShowWindow(g_bubbleWnd, SW_SHOWNOACTIVATE);
    SetTimer(g_hWnd, IDT_BUBBLE_HIDE, 2600, NULL);
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
    RECT workArea;
    SystemParametersInfo(SPI_GETWORKAREA, 0, &workArea, 0);
    RECT rc;
    GetWindowRect(g_hWnd, &rc);
    int x = rc.left, y = rc.top;
    switch (g_snappedEdge) {
        case EDGE_LEFT:   x = -BALL_RADIUS; break;
        case EDGE_RIGHT:  x = screenW - BALL_RADIUS; break;
        case EDGE_TOP:    y = -BALL_RADIUS; break;
        case EDGE_BOTTOM: y = workArea.bottom - BALL_SIZE; break;
        default: break;
    }
    if (g_snappedEdge == EDGE_LEFT || g_snappedEdge == EDGE_RIGHT) {
        y = max(-BALL_RADIUS, min((int)y, (int)(workArea.bottom - BALL_SIZE)));
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
        AppendMenuW(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, IDM_DB_INFO, GetDbInfoText().c_str());
        {
            extern bool IsBarRunning();
            extern std::wstring GetBarStatusText();
            AppendMenuW(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, 0,
                IsBarRunning() ? GetBarStatusText().c_str() : L"Bar: \x672A\x8FD0\x884C");
        }
        {
            extern bool IsAgentRunning();
            extern std::wstring GetAgentStatusText();
            if (IsAgentRunning()) {
                AppendMenuW(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, 0, GetAgentStatusText().c_str());
            }
        }
        AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
        AppendMenuW(hMenu, MF_STRING, IDM_OPEN_DIR, L"\x6253\x5F00\x6570\x636E\x8DEF\x5F84");
        AppendMenuW(hMenu, MF_STRING, IDM_OPEN_LIVE, L"\x67E5\x770B\x5F53\x524D\x8BB0\x5F55\x6587\x672C");
        AppendMenuW(hMenu, MF_STRING, IDM_BAR_SHOW, L"\x6253\x5F00 Bar");
        AppendMenuW(hMenu, MF_STRING, IDM_AGENT, L"\x6253\x5F00 Agent \x7EC8\x7AEF");
        {
            extern bool IsAgentBusy();
            UINT invokeFlags = IsAgentBusy() ? (MF_STRING | MF_DISABLED | MF_GRAYED) : MF_STRING;
            AppendMenuW(hMenu, invokeFlags, IDM_INVOKE_AGENT, L"\x542F\x52A8 Agent \x5206\x6790");
        }
        AppendMenuW(hMenu, MF_SEPARATOR, 0, NULL);
        AppendMenuW(hMenu, MF_STRING, IDM_HELP, L"\x5E2E\x52A9");
        AppendMenuW(hMenu, MF_STRING, IDM_EXIT, L"\x9000\x51FA CommitBall");
        POINT pt;
        GetCursorPos(&pt);
        SetForegroundWindow(hWnd);
        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hWnd, NULL);
        DestroyMenu(hMenu);
        if (cmd == IDM_EXIT) {
            extern void FastCommitBallExit();
            FastCommitBallExit();
        } else if (cmd == IDM_OPEN_DIR) {
            char dataPath[MAX_PATH];
            GetModuleFileNameA(NULL, dataPath, MAX_PATH);
            char* lastSlash = strrchr(dataPath, '\\');
            if (lastSlash) {
                strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - dataPath), "data");
                ShellExecuteA(NULL, "open", dataPath, NULL, NULL, SW_SHOWNORMAL);
            }
        } else if (cmd == IDM_OPEN_LIVE) {
            FlushLiveBuffer();
            char livePath[MAX_PATH];
            GetModuleFileNameA(NULL, livePath, MAX_PATH);
            char* lastSlash = strrchr(livePath, '\\');
            if (lastSlash) {
                strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - livePath), LIVE_TXT);
                HINSTANCE hr = ShellExecuteA(NULL, "open", "notepad", livePath, NULL, SW_SHOWNORMAL);
                if ((uintptr_t)hr <= 32) {
                    ShellExecuteA(NULL, NULL, livePath, NULL, NULL, SW_SHOWNORMAL);
                }
            }
        } else if (cmd == IDM_BAR_SHOW) {
            extern void SendShowLockedToBar();
            SendShowLockedToBar();
        } else if (cmd == IDM_AGENT) {
            extern void SendShowToAgent();
            SendShowToAgent();
        } else if (cmd == IDM_INVOKE_AGENT) {
            extern void InvokeAgentAnalyse();
            InvokeAgentAnalyse();
        } else if (cmd == IDM_HELP) {
            MessageBoxW(hWnd,
                L"CommitBall \x8BB0\x5F55\x60A8\x7684\x952E\x76D8\x6D3B\x52A8\x3001\x7C98\x8D34\x5185\x5BB9\x3001\x9F20\x6807\x70B9\x51FB\x548C\x7126\x70B9\x53D8\x5316\x3002\n\n"
                L"\x2022 \x6309 4 \x6B21 CapsLock \x5F00\x59CB/\x505C\x6B62\x8BB0\x5F55\n"
                L"\x2022 \x53F3\x952E\x60AC\x6D6E\x7403\x6253\x5F00\x83DC\x5355\x548C\x4E86\x89E3\x72B6\x6001\n"
                L"\x2022 \x83DC\x5355\x4E2D\x53EF\x67E5\x770B\x5F53\x524D\x8BB0\x5F55\x6587\x672C\x3001\x6253\x5F00\x6570\x636E\x76EE\x5F55\n"
                L"\x2022 \x952E\x76D8\x6D3B\x52A8\x4EC5\x5F53\x4F7F\x7528 CB-Weasel \x8F93\x5165\x6CD5\x65F6\x751F\x6548\n"
                L"\x2022 \x8BB0\x5F55\x8F93\x51FA\x4E3A\x6570\x636E\x76EE\x5F55\x4E0B\x7684 live.txt \x548C db \x6587\x4EF6",
                L"CommitBall \x5E2E\x52A9",
                MB_OK | MB_ICONINFORMATION);
        }
        return 0;
    }

    case WM_EXITSIZEMOVE: {
        RECT rc;
        RECT workArea;
        SystemParametersInfo(SPI_GETWORKAREA, 0, &workArea, 0);
        GetWindowRect(hWnd, &rc);
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int x = rc.left, y = rc.top;
        g_snappedEdge = EDGE_NONE;
        if (x < SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_LEFT; x = -BALL_RADIUS;
        } else if (x + BALL_SIZE > screenW - SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_RIGHT; x = screenW - BALL_RADIUS;
        } else if (y < SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_TOP; y = -BALL_RADIUS;
        } else if (y + BALL_SIZE > workArea.bottom - SNAP_THRESHOLD) {
            g_snappedEdge = EDGE_BOTTOM; y = workArea.bottom - BALL_SIZE;
        }
        if (g_snappedEdge == EDGE_LEFT || g_snappedEdge == EDGE_RIGHT) {
            y = min(y, (int)(workArea.bottom - BALL_SIZE));
        }
        SetWindowPos(hWnd, NULL, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }

    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case IDM_EXIT:
            {
                extern void FastCommitBallExit();
                FastCommitBallExit();
            }
            break;
        }
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    // WM_PIPE_MSG: lParam=0 keyboard msg(std::wstring*), 1 direct-input(std::string*), 2 bubble(std::wstring*), 3 refresh ball state
    case WM_PIPE_MSG: {
        if (lParam == 1) {
            std::string* pText = (std::string*)wParam;
            InsertDirectInput(*pText);
            delete pText;
        } else if (lParam == 2) {
            std::wstring* pText = (std::wstring*)wParam;
            ShowBallBubble(pText->c_str());
            delete pText;
        } else if (lParam == 3) {
            extern void OnStateChanged();
            OnStateChanged();
        } else {
            std::wstring* pMsg = (std::wstring*)wParam;
            ProcessMessage(*pMsg);
            delete pMsg;
        }
        return 0;
    }

    case WM_TIMER:
        if (wParam == IDT_OUTPUT) {
            if (GetTickCount() - g_lastOutputTime >= FLUSH_INTERVAL) {
                FlushLiveBuffer();
                g_lastOutputTime = GetTickCount();
            }
            CheckFocusTimer();
            CheckTimerEvent();
            CheckSessionTimeout();
            extern void CheckAutoAnalyse();
            CheckAutoAnalyse();
        } else if (wParam == IDT_COLOR_ANIM) {
            AnimateColor();
        } else if (wParam == IDT_BLINK) {
            ToggleBlink();
        } else if (wParam == IDT_BUBBLE_HIDE) {
            KillTimer(g_hWnd, IDT_BUBBLE_HIDE);
            if (g_bubbleWnd) {
                DestroyWindow(g_bubbleWnd);
                g_bubbleWnd = nullptr;
            }
        }
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}
