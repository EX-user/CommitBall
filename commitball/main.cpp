#include "recorder.hpp"
#include "ball.hpp"
#include <shellscalingapi.h>
#pragma comment(lib, "shcore.lib")

State g_state = STOPPED;
Edge g_snappedEdge = EDGE_NONE;
int g_savedX = 0, g_savedY = 0;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
sqlite3* g_db = nullptr;
sqlite3_stmt* g_insertStmt = nullptr;
int g_recordId = 0;
HWND g_hWnd = nullptr;
DWORD g_lastOutputTime = 0;
ULONG_PTR g_gdiplusToken = 0;
bool g_running = true;

const wchar_t MUTEX_NAME[] = L"CommitBallMutex";

void OnStateChanged() {
    if (g_state == RECORDING)
        UnsnapForRecording();
    else
        ApplySnappedEdge();
    RedrawBall();
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    HANDLE hMutex = CreateMutexW(NULL, TRUE, MUTEX_NAME);
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    if (!RecorderInit()) {
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 1;
    }

    if (!BallInit(hInstance)) {
        RecorderCleanup();
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 1;
    }

    HHOOK hook = SetWindowsHookEx(WH_KEYBOARD_LL, LLKeyboardProc, NULL, 0);

    HANDLE hPipeThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
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

    g_running = false;
    if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
    WaitForSingleObject(hPipeThread, 2000);
    CloseHandle(hPipeThread);

    UnhookWindowsHookEx(hook);
    BallShutdown();
    RecorderCleanup();

    if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
    return 0;
}
