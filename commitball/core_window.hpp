#pragma once
#include <windows.h>
#include <string>

extern HWND g_hWnd;
extern DWORD g_lastOutputTime;
extern bool g_running;

const wchar_t CORE_WINDOW_CLASS_NAME[] = L"CommitBallCoreWindowClass";

inline bool CoreWindowInit(HINSTANCE hInstance);
inline void CoreWindowShutdown();
inline LRESULT CALLBACK CoreWindowWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

inline bool CoreWindowInit(HINSTANCE hInstance) {
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = CoreWindowWndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = CORE_WINDOW_CLASS_NAME;
    RegisterClassExW(&wc);

    g_hWnd = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        CORE_WINDOW_CLASS_NAME, L"CommitBallCore",
        WS_POPUP,
        0, 0, 1, 1,
        NULL, NULL, hInstance, NULL);

    return g_hWnd != nullptr;
}

inline void CoreWindowShutdown() {
    if (g_hWnd) {
        DestroyWindow(g_hWnd);
        g_hWnd = nullptr;
    }
}

inline void HandleCorePipeMessage(WPARAM wParam, LPARAM lParam) {
    if (lParam == 1) {
        std::string* pText = (std::string*)wParam;
        InsertDirectInput(*pText);
        delete pText;
    } else if (lParam == 2) {
        std::wstring* pText = (std::wstring*)wParam;
        extern void SendBallShellBubble(const wchar_t* text);
        SendBallShellBubble(pText->c_str());
        delete pText;
    } else if (lParam == 3) {
        extern void OnStateChanged();
        OnStateChanged();
    } else if (lParam == 4) {
        extern void CheckBallShellHealth();
        CheckBallShellHealth();
    } else {
        std::wstring* pMsg = (std::wstring*)wParam;
        ProcessMessage(*pMsg);
        delete pMsg;
    }
}

inline void HandleCoreOutputTimer() {
    if (GetTickCount() - g_lastOutputTime >= FLUSH_INTERVAL) {
        FlushLiveBuffer();
        g_lastOutputTime = GetTickCount();
    }
    CheckFocusTimer();
    CheckAwayEvent();
    CheckTimerEvent();
    CheckSessionTimeout();
    extern void CheckAutoAnalyse();
    CheckAutoAnalyse();

    static DWORD lastBallShellStatusAt = 0;
    if (GetTickCount() - lastBallShellStatusAt >= 2000) {
        extern void CheckBallShellHealth();
        CheckBallShellHealth();
        lastBallShellStatusAt = GetTickCount();
    }
}

inline LRESULT CALLBACK CoreWindowWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    case WM_PIPE_MSG:
        HandleCorePipeMessage(wParam, lParam);
        return 0;

    case WM_TIMER:
        if (wParam == IDT_OUTPUT) {
            HandleCoreOutputTimer();
        }
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}
