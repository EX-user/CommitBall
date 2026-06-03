#include "recorder.hpp"
#include "click.hpp"
#include "ball.hpp"
#include <shellscalingapi.h>
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "advapi32.lib")

HANDLE g_barProcess = nullptr;

bool LaunchBar() {
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    wchar_t* lastSlash = wcsrchr(exePath, L'\\');
    if (!lastSlash) return false;
    wcscpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - exePath), L"CommitBall-Bar.exe");

    Log("Bar path: %ls", exePath);

    DWORD attrs = GetFileAttributesW(exePath);
    if (attrs == INVALID_FILE_ATTRIBUTES) {
        Log("Bar exe NOT found (err=%d)", GetLastError());
        return false;
    }

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(exePath, NULL, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) {
        Log("CreateProcessW failed (err=%d)", GetLastError());
        return false;
    }
    CloseHandle(pi.hThread);
    g_barProcess = pi.hProcess;
    Log("Launched CommitBall-Bar.exe (pid=%d)", pi.dwProcessId);

    WaitForInputIdle(pi.hProcess, 3000);

    DWORD exitCode = 0;
    if (GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode != STILL_ACTIVE) {
        Log("Bar exited immediately with code %d", exitCode);
        CloseHandle(pi.hProcess);
        g_barProcess = nullptr;
        return false;
    }
    return true;
}

void SendShowToBar() {
    HANDLE hPipe = CreateFileW(
        L"\\\\.\\pipe\\CommitBall-bar",
        GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (hPipe == INVALID_HANDLE_VALUE) return;
    DWORD written;
    WriteFile(hPipe, "SHOW\r\n", 6, &written, NULL);
    CloseHandle(hPipe);
}

bool IsRunAsAdmin() {
    BOOL isAdmin = FALSE;
    PSID adminGroup = NULL;
    SID_IDENTIFIER_AUTHORITY ntAuth = SECURITY_NT_AUTHORITY;
    if (AllocateAndInitializeSid(&ntAuth, 2, SECURITY_BUILTIN_DOMAIN_RID,
        DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &adminGroup)) {
        CheckTokenMembership(NULL, adminGroup, &isAdmin);
        FreeSid(adminGroup);
    }
    return isAdmin != FALSE;
}

State g_state = STOPPED;
Edge g_snappedEdge = EDGE_NONE;
int g_savedX = 0, g_savedY = 0;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
sqlite3* g_db = nullptr;
sqlite3_stmt* g_insertStmt = nullptr;
int g_recordId = 0;
HWND g_hWnd = nullptr;
DWORD g_lastOutputTime = 0;
DWORD g_lastTimerEvent = 0;
DWORD g_recordingStartTime = 0;
ULONG_PTR g_gdiplusToken = 0;
bool g_running = true;
HWND g_lastFocusHwnd = nullptr;
int g_focusNoChangeCount = 0;

IUIAutomation* g_pUIAutomation = nullptr;

const wchar_t MUTEX_NAME[] = L"CommitBallMutex";

void OnStateChanged() {
    if (g_state == RECORDING) {
        UnsnapForRecording();
        g_tgtR = 239; g_tgtG = 68; g_tgtB = 68;
        g_tgtPenR = 180; g_tgtPenG = 30; g_tgtPenB = 30;
    } else {
        ApplySnappedEdge();
        g_tgtR = 59; g_tgtG = 130; g_tgtB = 246;
        g_tgtPenR = 255; g_tgtPenG = 255; g_tgtPenB = 255;
    }
    SetTimer(g_hWnd, IDT_COLOR_ANIM, 16, NULL);
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

    if (!IsRunAsAdmin()) {
        g_noAdmin = true;
        g_curR = 128; g_curG = 128; g_curB = 128;
        g_tgtR = 128; g_tgtG = 128; g_tgtB = 128;
        g_curPenR = 255; g_curPenG = 255; g_curPenB = 255;
        g_tgtPenR = 255; g_tgtPenG = 255; g_tgtPenB = 255;
        RedrawBall();
        CreateThread(NULL, 0, [](LPVOID) -> DWORD {
            Sleep(30000);
            ExitProcess(0);
            return 0;
        }, NULL, 0, NULL);
        MSG msg;
        while (GetMessage(&msg, NULL, 0, 0)) {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
        return 0;
    }

    HHOOK hook = SetWindowsHookEx(WH_KEYBOARD_LL, LLKeyboardProc, NULL, 0);
    HHOOK mouseHook = SetWindowsHookEx(WH_MOUSE_LL, LLMouseProc, NULL, 0);

    HANDLE hPipeThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        CreatePipeServer();
        return 0;
    }, NULL, 0, NULL);

    HANDLE hBarPipeThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        CreateBarPipeServer();
        return 0;
    }, NULL, 0, NULL);

    g_lastOutputTime = GetTickCount();
    SetTimer(g_hWnd, IDT_OUTPUT, 400, NULL);

    LaunchBar();

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    g_running = false;
    if (g_pipe != INVALID_HANDLE_VALUE) { CloseHandle(g_pipe); g_pipe = INVALID_HANDLE_VALUE; }
    if (g_barProcess) {
        HANDLE hPipe = CreateFileW(L"\\\\.\\pipe\\CommitBall-bar", GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) {
            DWORD written;
            WriteFile(hPipe, "QUIT\r\n", 6, &written, NULL);
            CloseHandle(hPipe);
            WaitForSingleObject(g_barProcess, 2000);
        }
        TerminateProcess(g_barProcess, 0);
        CloseHandle(g_barProcess);
    }
    WaitForSingleObject(hPipeThread, 2000);
    CloseHandle(hPipeThread);
    WaitForSingleObject(hBarPipeThread, 2000);
    CloseHandle(hBarPipeThread);

    UnhookWindowsHookEx(hook);
    UnhookWindowsHookEx(mouseHook);
    BallShutdown();
    RecorderCleanup();

    if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
    return 0;
}
