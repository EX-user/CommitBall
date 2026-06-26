#include "recorder.hpp"
#include "click.hpp"
#include "ball.hpp"
#include "core_window.hpp"
#include <shellscalingapi.h>
#include <cstdarg>
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "advapi32.lib")

HANDLE g_barProcess = nullptr;
HANDLE g_agentProcess = nullptr;
HANDLE g_ballShellProcess = nullptr;
HANDLE g_childJob = nullptr;

bool EnsureAgentRunning();
void RequestCommitBallExit();
void FastCommitBallExit();
bool IsBallShellEnabled();
void PushBallShellState();
void PushBallShellStatus();
void SendBallShellBubble(const wchar_t* text);
void CheckBallShellHealth();
bool IsCoreWindowOnly();

void ExitLog(const char* fmt, ...) {
    CreateDirectoryA("data", NULL);
    CreateDirectoryA("data\\log", NULL);
    HANDLE f = CreateFileA(
        "data\\log\\exit.log",
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (f == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1024];
    int prefix = snprintf(line, sizeof(line),
        "[%02u:%02u:%02u.%03u pid=%lu] ",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId());
    if (prefix < 0) prefix = 0;
    if (prefix >= (int)sizeof(line)) prefix = (int)sizeof(line) - 1;

    va_list args;
    va_start(args, fmt);
    int body = vsnprintf(line + prefix, sizeof(line) - prefix, fmt, args);
    va_end(args);
    if (body < 0) body = 0;

    size_t len = strnlen(line, sizeof(line));
    if (len + 2 < sizeof(line)) {
        line[len++] = '\r';
        line[len++] = '\n';
        line[len] = '\0';
    }
    DWORD written = 0;
    WriteFile(f, line, (DWORD)len, &written, NULL);
    FlushFileBuffers(f);
    CloseHandle(f);
}

void InitChildJob() {
    if (g_childJob) return;
    g_childJob = CreateJobObjectW(NULL, NULL);
    if (!g_childJob) {
        Log("CreateJobObject failed (err=%d)", GetLastError());
        return;
    }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = {};
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(g_childJob, JobObjectExtendedLimitInformation, &info, sizeof(info))) {
        Log("SetInformationJobObject failed (err=%d)", GetLastError());
        CloseHandle(g_childJob);
        g_childJob = nullptr;
    }
}

void AssignChildToJob(HANDLE process, const char* label) {
    if (!g_childJob || !process) return;
    if (!AssignProcessToJobObject(g_childJob, process))
        Log("AssignProcessToJobObject(%s) failed (err=%d)", label, GetLastError());
}

void FlushBeforeHardExit() {
    ExitLog("FlushBeforeHardExit begin state=%d db=%p stmt=%p", (int)g_state, g_db, g_insertStmt);
    g_state = STOPPED;

    if (g_db) {
        FlushLiveBuffer();
        ExitLog("FlushLiveBuffer done");
    }

    if (g_insertStmt) {
        int rc = sqlite3_finalize(g_insertStmt);
        ExitLog("sqlite3_finalize insert stmt rc=%d", rc);
        g_insertStmt = nullptr;
    }

    if (g_db) {
        int logFrames = 0;
        int checkpointed = 0;
        int rc = sqlite3_wal_checkpoint_v2(g_db, NULL, SQLITE_CHECKPOINT_PASSIVE, &logFrames, &checkpointed);
        ExitLog("sqlite3_wal_checkpoint_v2 rc=%d logFrames=%d checkpointed=%d", rc, logFrames, checkpointed);
        rc = sqlite3_close(g_db);
        ExitLog("sqlite3_close rc=%d", rc);
        g_db = nullptr;
    }
    ExitLog("FlushBeforeHardExit end");
}

bool LaunchBar() {
    if (g_barProcess) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(g_barProcess, &exitCode) && exitCode == STILL_ACTIVE)
            return true;
        CloseHandle(g_barProcess);
        g_barProcess = nullptr;
    }

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
    wchar_t cmdLine[MAX_PATH + 64];
    swprintf_s(cmdLine, L"\"%ls\" --parent-pid %lu", exePath, GetCurrentProcessId());
    if (!CreateProcessW(exePath, cmdLine, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) {
        Log("CreateProcessW failed (err=%d)", GetLastError());
        return false;
    }
    CloseHandle(pi.hThread);
    g_barProcess = pi.hProcess;
    AssignChildToJob(g_barProcess, "Bar");
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

bool IsBarRunning() {
    if (!g_barProcess) return false;
    DWORD exitCode = 0;
    if (!GetExitCodeProcess(g_barProcess, &exitCode)) return false;
    if (exitCode != STILL_ACTIVE) {
        CloseHandle(g_barProcess);
        g_barProcess = nullptr;
        return false;
    }
    return true;
}

bool EnsureBarRunning() {
    return IsBarRunning() || LaunchBar();
}

std::wstring GetBarStatusText() {
    if (!IsBarRunning()) return L"Bar: \x672A\x8FD0\x884C";
    char buf[64] = {};
    FILE* f = fopen("data/bar-status", "r");
    if (f) { fgets(buf, sizeof(buf), f); fclose(f); }
    std::string status = buf;
    if (status.find("locked") != std::string::npos)
        return L"Bar: \x9501\x5B9A\x663E\x793A";
    if (status.find("visible") != std::string::npos)
        return L"Bar: \x663E\x793A\x4E2D";
    return L"Bar: \x540E\x53F0\x5C31\x7EEA";
}

void SendBarCommand(const char* command) {
    if (!EnsureBarRunning()) {
        Log("SendBarCommand: bar not running");
        return;
    }
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 12; ++i) {
        hPipe = CreateFileW(
            L"\\\\.\\pipe\\CommitBall-bar",
            GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        DWORD err = GetLastError();
        if (err == ERROR_PIPE_BUSY) {
            WaitNamedPipeW(L"\\\\.\\pipe\\CommitBall-bar", 300);
        } else {
            Sleep(100);
        }
    }
    if (hPipe == INVALID_HANDLE_VALUE) {
        Log("SendBarCommand: pipe connect failed (err=%d)", GetLastError());
        return;
    }
    DWORD written;
    std::string msg = command;
    msg += "\r\n";
    WriteFile(hPipe, msg.c_str(), (DWORD)msg.size(), &written, NULL);
    CloseHandle(hPipe);
}

void SendShowToBar() {
    SendBarCommand("SHOW");
}

void SendShowLockedToBar() {
    SendBarCommand("SHOW_LOCKED");
}

bool LaunchAgent() {
    if (g_agentProcess) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(g_agentProcess, &exitCode) && exitCode == STILL_ACTIVE)
            return true;
        CloseHandle(g_agentProcess);
        g_agentProcess = nullptr;
    }

    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    wchar_t* lastSlash = wcsrchr(exePath, L'\\');
    if (!lastSlash) return false;
    wcscpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - exePath), L"CommitBall-Agent.exe");

    DWORD attrs = GetFileAttributesW(exePath);
    if (attrs == INVALID_FILE_ATTRIBUTES) return false;

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};
    wchar_t cmdLine[MAX_PATH + 64];
    swprintf_s(cmdLine, L"\"%ls\" --parent-pid %lu", exePath, GetCurrentProcessId());
    if (!CreateProcessW(exePath, cmdLine, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
        return false;
    CloseHandle(pi.hThread);
    g_agentProcess = pi.hProcess;
    AssignChildToJob(g_agentProcess, "Agent");

    WaitForInputIdle(pi.hProcess, 3000);

    DWORD exitCode = 0;
    if (GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode != STILL_ACTIVE) {
        CloseHandle(pi.hProcess);
        g_agentProcess = nullptr;
        return false;
    }
    return true;
}

void SendShowToAgent() {
    if (!EnsureAgentRunning()) {
        Log("SendShowToAgent: agent not running");
        return;
    }
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 24; ++i) {
        hPipe = CreateFileW(
            L"\\\\.\\pipe\\CommitBall-Agent",
            GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        DWORD err = GetLastError();
        if (err == ERROR_PIPE_BUSY) {
            WaitNamedPipeW(L"\\\\.\\pipe\\CommitBall-Agent", 300);
        } else {
            Sleep(125);
        }
    }
    if (hPipe == INVALID_HANDLE_VALUE) {
        Log("SendShowToAgent: pipe connect failed (err=%d)", GetLastError());
        return;
    }
    DWORD written;
    WriteFile(hPipe, "SHOW\r\n", 6, &written, NULL);
    CloseHandle(hPipe);
}

bool IsAgentRunning() {
    if (!g_agentProcess) return false;
    DWORD exitCode = 0;
    if (!GetExitCodeProcess(g_agentProcess, &exitCode)) return false;
    return exitCode == STILL_ACTIVE;
}

bool EnsureAgentRunning() {
    return IsAgentRunning() || LaunchAgent();
}

bool IsAgentBusy() {
    if (!IsAgentRunning()) return false;
    char buf[64] = {};
    FILE* f = fopen("data/agent-status", "r");
    if (f) { fgets(buf, sizeof(buf), f); fclose(f); }
    return std::string(buf).find("busy") != std::string::npos;
}

std::wstring GetAgentStatusText() {
    if (!IsAgentRunning()) return L"Agent: \x672a\x8fd0\x884c";
    char buf[64] = {};
    FILE* f = fopen("data/agent-status", "r");
    if (f) { fgets(buf, sizeof(buf), f); fclose(f); }
    std::string status = buf;
    if (status.find("busy") != std::string::npos)
        return L"Agent: \x7e41\x5fd9";
    return L"Agent: \x7a7a\x95f2";
}

bool SendInvokeToAgent(const char* json) {
    if (!EnsureAgentRunning()) {
        Log("SendInvokeToAgent: agent not running");
        return false;
    }
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 24; ++i) {
        hPipe = CreateFileW(
            L"\\\\.\\pipe\\CommitBall-Agent",
            GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        DWORD err = GetLastError();
        if (err == ERROR_PIPE_BUSY) {
            WaitNamedPipeW(L"\\\\.\\pipe\\CommitBall-Agent", 300);
        } else {
            Sleep(125);
        }
    }
    if (hPipe == INVALID_HANDLE_VALUE) {
        Log("SendInvokeToAgent: pipe connect failed (err=%d)", GetLastError());
        return false;
    }
    std::string msg = "INVOKE ";
    msg += json;
    msg += "\r\n";
    DWORD written;
    WriteFile(hPipe, msg.c_str(), (DWORD)msg.size(), &written, NULL);
    CloseHandle(hPipe);
    Log("SendInvokeToAgent: sent %d bytes", (int)msg.size());
    return true;
}

void InvokeAgentAnalyse() {
    time_t now = time(NULL);
    struct tm ti;
    localtime_s(&ti, &now);
    const char* weekdays[] = {"\xe6\x98\x9f\xe6\x9c\x9f\xe6\x97\xa5","\xe6\x98\x9f\xe6\x9c\x9f\xe4\xb8\x80","\xe6\x98\x9f\xe6\x9c\x9f\xe4\xba\x8c","\xe6\x98\x9f\xe6\x9c\x9f\xe4\xb8\x89","\xe6\x98\x9f\xe6\x9c\x9f\xe5\x9b\x9b","\xe6\x98\x9f\xe6\x9c\x9f\xe4\xba\x94","\xe6\x98\x9f\xe6\x9c\x9f\xe5\x85\xad"};
    char timeBuf[128];
    snprintf(timeBuf, sizeof(timeBuf),
        "\xe5\xbd\x93\xe5\x89\x8d\xe6\x97\xb6\xe9\x97\xb4\xe6\x98\xaf %04d-%02d-%02d %s %02d:%02d",
        1900 + ti.tm_year, 1 + ti.tm_mon, ti.tm_mday,
        weekdays[ti.tm_wday], ti.tm_hour, ti.tm_min);
    std::string json = "[\"";
    json += timeBuf;
    json += "\",\"/summary_to_panel\"]";
    Log("InvokeAgentAnalyse: %s", json.c_str());
    SendInvokeToAgent(json.c_str());
}

void InvokeAgentText(const char* text) {
    if (!text || !text[0]) return;
    std::string json = "[\"";
    json += JsonEscape(text);
    json += "\"]";
    Log("InvokeAgentText: %s", json.c_str());
    SendInvokeToAgent(json.c_str());
}

bool g_ballShellActive = false;
bool g_coreWindowOnly = false;

bool ShouldUseBallShell() {
    char env[8] = {};
    DWORD len = GetEnvironmentVariableA("COMMITBALL_USE_BALL_SHELL", env, sizeof(env));
    if (len > 0) {
        if (env[0] == '0' || env[0] == 'f' || env[0] == 'F' || env[0] == 'n' || env[0] == 'N')
            return false;
        if (env[0] == '1' || env[0] == 't' || env[0] == 'T' || env[0] == 'y' || env[0] == 'Y')
            return true;
    }
    if (GetFileAttributesA("data\\disable-ball-shell.flag") != INVALID_FILE_ATTRIBUTES)
        return false;
    if (GetFileAttributesA("data\\use-ball-shell.flag") != INVALID_FILE_ATTRIBUTES)
        return true;
    return GetConfigBool("use_ball_shell");
}

bool IsBallShellRunning() {
    if (!g_ballShellProcess) return false;
    DWORD exitCode = 0;
    if (!GetExitCodeProcess(g_ballShellProcess, &exitCode)) return false;
    if (exitCode != STILL_ACTIVE) {
        CloseHandle(g_ballShellProcess);
        g_ballShellProcess = nullptr;
        g_ballShellActive = false;
        return false;
    }
    return true;
}

bool IsBallShellEnabled() {
    return g_ballShellActive && IsBallShellRunning();
}

bool IsCoreWindowOnly() {
    return g_coreWindowOnly;
}

bool SendBallShellLine(const std::string& line) {
    if (!IsBallShellEnabled()) return false;
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 8; ++i) {
        hPipe = CreateFileW(
            L"\\\\.\\pipe\\CommitBall-BallShell",
            GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        DWORD err = GetLastError();
        if (err == ERROR_PIPE_BUSY) {
            WaitNamedPipeW(L"\\\\.\\pipe\\CommitBall-BallShell", 200);
        } else {
            Sleep(80);
        }
    }
    if (hPipe == INVALID_HANDLE_VALUE) {
        Log("SendBallShellLine: pipe connect failed (err=%d)", GetLastError());
        return false;
    }

    std::string msg = line;
    msg += "\n";
    DWORD written = 0;
    BOOL ok = WriteFile(hPipe, msg.c_str(), (DWORD)msg.size(), &written, NULL);
    CloseHandle(hPipe);
    return ok && written == msg.size();
}

std::string JsonWide(const std::wstring& text) {
    return JsonEscape(WideToUtf8(text));
}

bool LaunchBallShell() {
    if (IsBallShellRunning()) return true;

    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    wchar_t* lastSlash = wcsrchr(exePath, L'\\');
    if (!lastSlash) return false;
    wcscpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - exePath), L"CommitBall-BallShell.exe");

    DWORD attrs = GetFileAttributesW(exePath);
    if (attrs == INVALID_FILE_ATTRIBUTES) {
        Log("BallShell exe NOT found (err=%d)", GetLastError());
        return false;
    }

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};
    wchar_t cmdLine[MAX_PATH + 64];
    swprintf_s(cmdLine, L"\"%ls\" --parent-pid %lu", exePath, GetCurrentProcessId());
    if (!CreateProcessW(exePath, cmdLine, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) {
        Log("CreateProcessW BallShell failed (err=%d)", GetLastError());
        return false;
    }

    CloseHandle(pi.hThread);
    g_ballShellProcess = pi.hProcess;
    g_ballShellActive = true;
    AssignChildToJob(g_ballShellProcess, "BallShell");
    Log("Launched CommitBall-BallShell.exe (pid=%d)", pi.dwProcessId);
    WaitForInputIdle(pi.hProcess, 3000);
    return IsBallShellRunning();
}

void PushBallShellState() {
    if (!IsBallShellEnabled()) return;
    std::string mode = g_state == RECORDING ? "recording" : "idle";
    std::string json = "STATE {\"Mode\":\"";
    json += mode;
    json += "\",\"EyeEnabled\":";
    json += g_eyeModeEnabled ? "true" : "false";
    json += ",\"IsMouseIdle\":false,\"NoAdmin\":";
    json += g_noAdmin ? "true" : "false";
    json += "}";
    SendBallShellLine(json);
}

void PushBallShellStatus() {
    if (!IsBallShellEnabled()) return;
    std::string json = "STATUS {\"Recording\":\"";
    json += JsonWide(GetStatusText());
    json += "\",\"Db\":\"";
    json += JsonWide(GetDbInfoText());
    json += "\",\"Bar\":\"";
    json += JsonWide(GetBarStatusText());
    json += "\",\"Agent\":\"";
    json += JsonWide(GetAgentStatusText());
    json += "\"}";
    SendBallShellLine(json);
}

void SendBallShellBubble(const wchar_t* text) {
    if (!text || !text[0] || !IsBallShellEnabled()) return;
    std::wstring safeText = SanitizeBubbleText(text);
    if (safeText.empty()) safeText = L"...";
    std::string json = "BUBBLE {\"Text\":\"";
    json += JsonWide(safeText);
    json += "\"}";
    SendBallShellLine(json);
}

void ShutdownBallShellProcess() {
    if (!g_ballShellProcess) return;
    if (IsBallShellEnabled()) {
        SendBallShellLine("SHUTDOWN {}");
    }
    DWORD wait = WaitForSingleObject(g_ballShellProcess, 600);
    if (wait == WAIT_TIMEOUT) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(g_ballShellProcess, &exitCode) && exitCode == STILL_ACTIVE) {
            Log("BallShell shutdown: timeout, terminating");
            TerminateProcess(g_ballShellProcess, 0);
            WaitForSingleObject(g_ballShellProcess, 300);
        }
    }
    CloseHandle(g_ballShellProcess);
    g_ballShellProcess = nullptr;
    g_ballShellActive = false;
}

void SaveBallShellWindowState(const char* json) {
    if (!json || !json[0]) return;
    EnsureDir("data");
    FILE* f = fopen("data\\ball-shell-state.json", "wb");
    if (!f) {
        Log("BallShell window state save failed");
        return;
    }
    fwrite(json, 1, strlen(json), f);
    fclose(f);
    Log("BallShell window state saved");
}

void EnsureBallShellStarted() {
    if (!ShouldUseBallShell()) return;
    if (!IsBallShellRunning() && !LaunchBallShell()) return;
    if (g_hWnd && !IsCoreWindowOnly()) ShowWindow(g_hWnd, SW_HIDE);
    PushBallShellState();
    PushBallShellStatus();
}

void CheckBallShellHealth() {
    if (!g_running) return;
    if (ShouldUseBallShell()) {
        if (!IsBallShellRunning()) {
            Log("BallShell not running, restarting");
            EnsureBallShellStarted();
        } else {
            PushBallShellState();
            PushBallShellStatus();
        }
    } else if (g_ballShellProcess) {
        Log("BallShell disabled by config, shutting down");
        ShutdownBallShellProcess();
        if (IsCoreWindowOnly()) {
            Log("BallShell disabled while CoreWindow-only mode is active; legacy UI will be restored after restart");
            return;
        }
        if (g_hWnd) {
            ShowWindow(g_hWnd, SW_SHOWNOACTIVATE);
            RedrawBall();
        }
    }
}

void OpenDataDirectory() {
    char dataPath[MAX_PATH];
    GetModuleFileNameA(NULL, dataPath, MAX_PATH);
    char* lastSlash = strrchr(dataPath, '\\');
    if (lastSlash) {
        strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - dataPath), "data");
        ShellExecuteA(NULL, "open", dataPath, NULL, NULL, SW_SHOWNORMAL);
    }
}

void OpenLiveText() {
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
}

void HandleBallUiCommand(const char* command) {
    if (!command || !command[0]) return;
    Log("Ball UI command: %s", command);
    if (strcmp(command, "open_data_directory") == 0) OpenDataDirectory();
    else if (strcmp(command, "open_live_text") == 0) OpenLiveText();
    else if (strcmp(command, "open_bar_locked") == 0) SendShowLockedToBar();
    else if (strcmp(command, "open_agent") == 0) SendShowToAgent();
    else if (strcmp(command, "invoke_agent_analysis") == 0) InvokeAgentAnalyse();
    else if (strcmp(command, "exit_commitball") == 0) FastCommitBallExit();
}

static std::string DataRelativePath(const std::string& path) {
    std::string p = path;
    std::replace(p.begin(), p.end(), '\\', '/');
    const std::string prefix = "data/";
    if (p.rfind(prefix, 0) == 0)
        return p.substr(prefix.size());
    return p;
}

void InvokeAgentNameArchive(const char* txtPath, const char* metaPath) {
    if (!txtPath || !metaPath) return;
    std::string txtRel = DataRelativePath(txtPath);
    std::string metaRel = DataRelativePath(metaPath);
    std::string command = "/name_archive ";
    command += txtRel;
    command += " ";
    command += metaRel;

    std::string json = "[\"";
    json += JsonEscape(command);
    json += "\"]";
    Log("InvokeAgentNameArchive: %s", json.c_str());
    SendInvokeToAgent(json.c_str());
}

DWORD g_lastAutoCheckTime = 0;

void CheckAutoAnalyse() {
    if (GetTickCount() - g_lastAutoCheckTime < 60000) return;
    g_lastAutoCheckTime = GetTickCount();

    if (!IsAgentRunning()) return;

    WIN32_FILE_ATTRIBUTE_DATA fileInfo;
    if (GetFileAttributesExA("data/agent-out/panel.html", GetFileExInfoStandard, &fileInfo)) {
        SYSTEMTIME stUtc, stLocal;
        FileTimeToSystemTime(&fileInfo.ftLastWriteTime, &stUtc);
        SystemTimeToTzSpecificLocalTime(NULL, &stUtc, &stLocal);
        struct tm tmFile = {};
        tmFile.tm_year = stLocal.wYear - 1900;
        tmFile.tm_mon = stLocal.wMonth - 1;
        tmFile.tm_mday = stLocal.wDay;
        tmFile.tm_hour = stLocal.wHour;
        tmFile.tm_min = stLocal.wMinute;
        tmFile.tm_sec = stLocal.wSecond;
        tmFile.tm_isdst = -1;
        time_t fileTime = mktime(&tmFile);
        time_t now = time(NULL);
        if (now - fileTime >= 4 * 3600) {
            InvokeAgentAnalyse();
        }
    }
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

bool SendQuitToPipe(const wchar_t* pipeName, const char* label) {
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 8; ++i) {
        hPipe = CreateFileW(pipeName, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        DWORD err = GetLastError();
        if (err == ERROR_PIPE_BUSY) {
            WaitNamedPipeW(pipeName, 250);
        } else {
            Sleep(80);
        }
    }
    if (hPipe != INVALID_HANDLE_VALUE) {
        DWORD written;
        WriteFile(hPipe, "QUIT\r\n", 6, &written, NULL);
        CloseHandle(hPipe);
        return true;
    } else {
        Log("%s shutdown: pipe connect failed (err=%d)", label, GetLastError());
        return false;
    }
}

void ShutdownChildProcess(HANDLE& process, const wchar_t* pipeName, const char* label) {
    if (!process) return;
    SendQuitToPipe(pipeName, label);

    DWORD wait = WaitForSingleObject(process, 600);
    if (wait == WAIT_TIMEOUT) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(process, &exitCode) && exitCode == STILL_ACTIVE) {
            Log("%s shutdown: timeout, terminating", label);
            TerminateProcess(process, 0);
            WaitForSingleObject(process, 300);
        }
    }
    CloseHandle(process);
    process = nullptr;
}

State g_state = STOPPED;
Edge g_snappedEdge = EDGE_NONE;
int g_savedX = 0, g_savedY = 0;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
HANDLE g_directPipe = INVALID_HANDLE_VALUE;
sqlite3* g_db = nullptr;
sqlite3_stmt* g_insertStmt = nullptr;
int g_recordId = 0;
HWND g_hWnd = nullptr;
DWORD g_lastOutputTime = 0;
DWORD g_lastTimerEvent = 0;
DWORD g_lastUserInputTime = 0;
DWORD g_recordingStartTime = 0;
ULONG_PTR g_gdiplusToken = 0;
bool g_running = true;
bool g_awayLogged = false;
HWND g_lastFocusHwnd = nullptr;
int g_focusNoChangeCount = 0;

IUIAutomation* g_pUIAutomation = nullptr;

const wchar_t MUTEX_NAME[] = L"CommitBallMutex";

void RequestCommitBallExit() {
    Log("Exit requested from menu");
    g_running = false;
    if (g_pipe != INVALID_HANDLE_VALUE) {
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
    if (g_directPipe != INVALID_HANDLE_VALUE) {
        CloseHandle(g_directPipe);
        g_directPipe = INVALID_HANDLE_VALUE;
    }
    if (g_hWnd) DestroyWindow(g_hWnd);
    PostQuitMessage(0);
}

void FastCommitBallExit() {
    ExitLog("FastCommitBallExit entered g_hWnd=%p g_pipe=%p g_directPipe=%p job=%p ballShell=%p bar=%p agent=%p",
        g_hWnd, g_pipe, g_directPipe, g_childJob, g_ballShellProcess, g_barProcess, g_agentProcess);
    g_running = false;
    if (g_hWnd) {
        BOOL showOk = ShowWindow(g_hWnd, SW_HIDE);
        ExitLog("ShowWindow(SW_HIDE) previousVisible=%d err=%lu", showOk, GetLastError());
    }

    FlushBeforeHardExit();

    DWORD selfPid = GetCurrentProcessId();
    wchar_t killCmd[256];
    swprintf_s(killCmd, L"cmd.exe /c ping 127.0.0.1 -n 3 >nul & taskkill /F /PID %lu >> data\\log\\exit-taskkill.log 2>&1", selfPid);
    STARTUPINFOW ksi = { sizeof(ksi) };
    ksi.dwFlags = STARTF_USESHOWWINDOW;
    ksi.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION kpi = {};
    BOOL killStarted = CreateProcessW(NULL, killCmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &ksi, &kpi);
    ExitLog("external taskkill CreateProcess ok=%d err=%lu", killStarted, GetLastError());
    if (killStarted) {
        CloseHandle(kpi.hThread);
        CloseHandle(kpi.hProcess);
    }
    if (g_pipe != INVALID_HANDLE_VALUE) {
        BOOL closeOk = CloseHandle(g_pipe);
        ExitLog("CloseHandle(g_pipe) ok=%d err=%lu", closeOk, GetLastError());
        g_pipe = INVALID_HANDLE_VALUE;
    }
    if (g_directPipe != INVALID_HANDLE_VALUE) {
        BOOL closeOk = CloseHandle(g_directPipe);
        ExitLog("CloseHandle(g_directPipe) ok=%d err=%lu", closeOk, GetLastError());
        g_directPipe = INVALID_HANDLE_VALUE;
    }
    if (g_childJob) {
        BOOL jobOk = TerminateJobObject(g_childJob, 0);
        ExitLog("TerminateJobObject ok=%d err=%lu", jobOk, GetLastError());
        BOOL closeOk = CloseHandle(g_childJob);
        ExitLog("CloseHandle(g_childJob) ok=%d err=%lu", closeOk, GetLastError());
        g_childJob = nullptr;
    }
    if (g_ballShellProcess) {
        BOOL termOk = TerminateProcess(g_ballShellProcess, 0);
        ExitLog("TerminateProcess(ballShell) ok=%d err=%lu", termOk, GetLastError());
    }
    if (g_barProcess) {
        BOOL termOk = TerminateProcess(g_barProcess, 0);
        ExitLog("TerminateProcess(bar) ok=%d err=%lu", termOk, GetLastError());
    }
    if (g_agentProcess) {
        BOOL termOk = TerminateProcess(g_agentProcess, 0);
        ExitLog("TerminateProcess(agent) ok=%d err=%lu", termOk, GetLastError());
    }
    HANDLE self = OpenProcess(PROCESS_TERMINATE, FALSE, selfPid);
    ExitLog("OpenProcess(self) handle=%p err=%lu", self, GetLastError());
    if (self) {
        BOOL selfOk = TerminateProcess(self, 0);
        ExitLog("TerminateProcess(self handle) ok=%d err=%lu", selfOk, GetLastError());
        CloseHandle(self);
    }
    ExitLog("calling TerminateProcess(GetCurrentProcess)");
    TerminateProcess(GetCurrentProcess(), 0);
    ExitLog("returned from TerminateProcess(GetCurrentProcess), calling ExitProcess");
    ExitProcess(0);
}

void OnStateChanged() {
    if (IsCoreWindowOnly()) {
        PushBallShellState();
        PushBallShellStatus();
        return;
    }

    ApplyLegacyBallStateChange();
    PushBallShellState();
    PushBallShellStatus();
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    srand((unsigned int)GetTickCount());
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
    InitChildJob();

    g_coreWindowOnly = ShouldUseBallShell();
    bool uiInitOk = g_coreWindowOnly ? CoreWindowInit(hInstance) : BallInit(hInstance);
    if (!uiInitOk) {
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
        if (!IsCoreWindowOnly()) RedrawBall();
        EnsureBallShellStarted();
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
    EnsureBallShellStarted();

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    g_running = false;
    if (g_pipe != INVALID_HANDLE_VALUE) { CloseHandle(g_pipe); g_pipe = INVALID_HANDLE_VALUE; }
    if (g_directPipe != INVALID_HANDLE_VALUE) { CloseHandle(g_directPipe); g_directPipe = INVALID_HANDLE_VALUE; }
    ShutdownBallShellProcess();
    ShutdownChildProcess(g_barProcess, L"\\\\.\\pipe\\CommitBall-bar", "Bar");
    ShutdownChildProcess(g_agentProcess, L"\\\\.\\pipe\\CommitBall-Agent", "Agent");
    WaitForSingleObject(hPipeThread, 2000);
    CloseHandle(hPipeThread);
    WaitForSingleObject(hBarPipeThread, 500);
    CloseHandle(hBarPipeThread);

    UnhookWindowsHookEx(hook);
    UnhookWindowsHookEx(mouseHook);
    if (IsCoreWindowOnly()) CoreWindowShutdown();
    else BallShutdown();
    RecorderCleanup();

    if (hMutex) { ReleaseMutex(hMutex); CloseHandle(hMutex); }
    if (g_childJob) { CloseHandle(g_childJob); g_childJob = nullptr; }
    return 0;
}
