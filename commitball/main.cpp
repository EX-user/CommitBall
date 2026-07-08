#include "recorder.hpp"
#include "click.hpp"
#include "ball_shared.hpp"
#include "core_window.hpp"
#include <shellscalingapi.h>
#include <cstdarg>
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "advapi32.lib")

HANDLE g_barProcess = nullptr;
HANDLE g_agentProcess = nullptr;
HANDLE g_ballShellProcess = nullptr;
HANDLE g_childJob = nullptr;
HANDLE g_pipeThread = nullptr;
HANDLE g_barPipeThread = nullptr;
HANDLE g_mutex = nullptr;
HHOOK g_keyboardHook = nullptr;
HHOOK g_mouseHook = nullptr;
bool g_exiting = false;

bool EnsureAgentRunning();
void FastCommitBallExit();
bool IsBallShellEnabled();
void PushBallShellState();
void PushBallShellStatus();
void SendBallShellBubble(const wchar_t* text);
void CheckBallShellHealth();

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

void FlushBeforeExit() {
    ExitLog("FlushBeforeExit begin state=%d db=%p stmt=%p", (int)g_state, g_db, g_insertStmt);
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
    ExitLog("FlushBeforeExit end");
}

bool LaunchBar() {
    if (g_exiting) return false;
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
    if (g_exiting) {
        Log("SendBarCommand skipped during exit: %s", command ? command : "");
        return;
    }
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
    if (g_exiting) return false;
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
    Log("Launched CommitBall-Agent.exe (pid=%d)", pi.dwProcessId);
    ExitLog("LaunchAgent pid=%lu process=%p", pi.dwProcessId, g_agentProcess);

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
    if (g_exiting) {
        Log("SendShowToAgent skipped during exit");
        return;
    }
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

bool SendInvokeToAgent(const char* json, const char* verb) {
    if (g_exiting) {
        Log("SendInvokeToAgent skipped during exit");
        return false;
    }
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
    if (!verb || !verb[0]) {
        Log("SendInvokeToAgent: missing invoke verb");
        return false;
    }
    std::string msg = verb;
    msg += " ";
    msg += json;
    msg += "\r\n";
    DWORD written = 0;
    BOOL writeOk = WriteFile(hPipe, msg.c_str(), (DWORD)msg.size(), &written, NULL);
    CloseHandle(hPipe);
    Log("SendInvokeToAgent: sent %s ok=%d written=%lu/%d", verb, writeOk, written, (int)msg.size());
    return writeOk && written == msg.size();
}

DWORD g_lastAgentAnalyseSubmitFailTime = 0;

bool ShouldSkipAgentAnalyseSubmitRetry() {
    DWORD now = GetTickCount();
    return g_lastAgentAnalyseSubmitFailTime != 0 && now - g_lastAgentAnalyseSubmitFailTime < 5 * 60 * 1000;
}

bool InvokeAgentAnalyse() {
    if (ShouldSkipAgentAnalyseSubmitRetry()) {
        Log("InvokeAgentAnalyse: skipped during submit retry cooldown");
        return false;
    }
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
    bool sent = SendInvokeToAgent(json.c_str(), "INVOKE_NEW");
    if (!sent) {
        g_lastAgentAnalyseSubmitFailTime = GetTickCount();
        Log("InvokeAgentAnalyse: submit failed, retry cooldown started");
    } else {
        g_lastAgentAnalyseSubmitFailTime = 0;
    }
    return sent;
}

void InvokeAgentRepairArchives() {
    const char* command = "/repair_archives";
    std::string json = "[\"";
    json += JsonEscape(command);
    json += "\"]";
    Log("InvokeAgentRepairArchives: %s", json.c_str());
    SendInvokeToAgent(json.c_str(), "INVOKE_NEW");
}

void InvokeAgentText(const char* text) {
    if (!text || !text[0]) return;
    std::string json = "[\"";
    json += JsonEscape(text);
    json += "\"]";
    Log("InvokeAgentText: %s", json.c_str());
    SendInvokeToAgent(json.c_str(), "INVOKE_BAR");
}

bool g_ballShellActive = false;

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
    if (g_exiting) return false;
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
    json += ",\"IsMouseIdle\":false";
    json += "}";
    SendBallShellLine(json);
}

void PushBallShellStatus() {
    if (!IsBallShellEnabled()) return;
    std::string json = "HOST_STATUS {\"Recording\":\"";
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
    ExitLog("ShutdownBallShellProcess begin process=%p enabled=%d", g_ballShellProcess, IsBallShellEnabled());
    if (IsBallShellEnabled()) {
        bool sent = SendBallShellLine("SHUTDOWN {}");
        ExitLog("ShutdownBallShellProcess send SHUTDOWN ok=%d", sent);
    }
    DWORD wait = WaitForSingleObject(g_ballShellProcess, 600);
    ExitLog("ShutdownBallShellProcess wait=%lu", wait);
    if (wait == WAIT_TIMEOUT) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(g_ballShellProcess, &exitCode) && exitCode == STILL_ACTIVE) {
            ExitLog("ShutdownBallShellProcess timeout exitCode=%lu terminating", exitCode);
            BOOL termOk = TerminateProcess(g_ballShellProcess, 0);
            ExitLog("ShutdownBallShellProcess TerminateProcess ok=%d err=%lu", termOk, GetLastError());
            DWORD wait2 = WaitForSingleObject(g_ballShellProcess, 300);
            ExitLog("ShutdownBallShellProcess post-terminate wait=%lu", wait2);
        }
    }
    CloseHandle(g_ballShellProcess);
    g_ballShellProcess = nullptr;
    g_ballShellActive = false;
    ExitLog("ShutdownBallShellProcess end");
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
    if (!IsBallShellRunning() && !LaunchBallShell()) return;
    PushBallShellState();
    PushBallShellStatus();
}

void CheckBallShellHealth() {
    if (!g_running || g_exiting) return;
    if (!IsBallShellRunning()) {
        Log("BallShell not running, restarting");
        EnsureBallShellStarted();
    } else {
        PushBallShellState();
        PushBallShellStatus();
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
    if (g_exiting && strcmp(command, "exit_commitball") != 0) {
        Log("Ball UI command skipped during exit: %s", command);
        return;
    }
    if (strcmp(command, "open_data_directory") == 0) OpenDataDirectory();
    else if (strcmp(command, "open_live_text") == 0) OpenLiveText();
    else if (strcmp(command, "open_bar_locked") == 0) SendShowLockedToBar();
    else if (strcmp(command, "open_agent") == 0) SendShowToAgent();
    else if (strcmp(command, "invoke_agent_analysis") == 0) InvokeAgentAnalyse();
    else if (strcmp(command, "exit_commitball") == 0) FastCommitBallExit();
}

DWORD g_lastAutoCheckTime = 0;

bool IsAgentSummaryBusy() {
    if (!IsAgentRunning()) return false;
    FILE* f = fopen("data/agent-summary-status", "r");
    if (!f) return false;

    char state[32] = {};
    int n = fscanf(f, "%31s", state);
    fclose(f);

    return n >= 1 && strcmp(state, "busy") == 0;
}

void CheckAutoAnalyse() {
    if (g_exiting) return;
    if (GetTickCount() - g_lastAutoCheckTime < 60000) return;
    g_lastAutoCheckTime = GetTickCount();

    if (IsAgentSummaryBusy()) {
        Log("CheckAutoAnalyse: summary_to_panel already running, skip");
        return;
    }

    bool panelExpired = true;
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
        panelExpired = now - fileTime >= 4 * 3600;
    } else {
        Log("CheckAutoAnalyse: panel.html missing, treating as expired");
    }
    if (panelExpired) {
        InvokeAgentAnalyse();
    }
}

bool SendQuitToPipe(const wchar_t* pipeName, const char* label) {
    ExitLog("%s shutdown: SendQuitToPipe begin", label);
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
        BOOL ok = WriteFile(hPipe, "QUIT\r\n", 6, &written, NULL);
        CloseHandle(hPipe);
        ExitLog("%s shutdown: SendQuitToPipe write ok=%d written=%lu", label, ok, written);
        return ok && written == 6;
    } else {
        Log("%s shutdown: pipe connect failed (err=%d)", label, GetLastError());
        ExitLog("%s shutdown: pipe connect failed err=%lu", label, GetLastError());
        return false;
    }
}

void ShutdownChildProcess(HANDLE& process, const wchar_t* pipeName, const char* label) {
    if (!process) return;
    ExitLog("%s shutdown: begin process=%p", label, process);
    bool sent = SendQuitToPipe(pipeName, label);
    ExitLog("%s shutdown: quit sent=%d", label, sent);

    DWORD wait = WaitForSingleObject(process, 600);
    ExitLog("%s shutdown: wait=%lu", label, wait);
    if (wait == WAIT_TIMEOUT) {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(process, &exitCode) && exitCode == STILL_ACTIVE) {
            Log("%s shutdown: timeout, terminating", label);
            ExitLog("%s shutdown: timeout exitCode=%lu terminating", label, exitCode);
            BOOL termOk = TerminateProcess(process, 0);
            ExitLog("%s shutdown: TerminateProcess ok=%d err=%lu", label, termOk, GetLastError());
            DWORD wait2 = WaitForSingleObject(process, 300);
            ExitLog("%s shutdown: post-terminate wait=%lu", label, wait2);
        }
    }
    CloseHandle(process);
    process = nullptr;
    ExitLog("%s shutdown: end", label);
}

State g_state = STOPPED;
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
bool g_running = true;
bool g_awayLogged = false;
HWND g_lastFocusHwnd = nullptr;
int g_focusNoChangeCount = 0;

IUIAutomation* g_pUIAutomation = nullptr;

const wchar_t MUTEX_NAME[] = L"CommitBallMutex";

void CloseExitHandle(HANDLE& handle, const char* label) {
    if (handle == INVALID_HANDLE_VALUE || handle == nullptr) return;
    BOOL ok = CloseHandle(handle);
    ExitLog("CloseHandle(%s) ok=%d err=%lu", label, ok, GetLastError());
    handle = INVALID_HANDLE_VALUE;
}

void CancelExitIo(HANDLE handle, const char* label) {
    if (handle == INVALID_HANDLE_VALUE || handle == nullptr) return;
    BOOL ok = CancelIoEx(handle, NULL);
    ExitLog("CancelIoEx(%s) ok=%d err=%lu", label, ok, GetLastError());
}

void WakePipeServer(const wchar_t* pipeName, const char* label, const char* message = nullptr) {
    ExitLog("WakePipeServer(%s) begin", label);
    for (int i = 0; i < 3; ++i) {
        HANDLE hPipe = CreateFileW(pipeName, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) {
            ExitLog("WakePipeServer(%s) connected attempt=%d", label, i + 1);
            if (message && message[0]) {
                DWORD written = 0;
                BOOL ok = WriteFile(hPipe, message, (DWORD)strlen(message), &written, NULL);
                ExitLog("WakePipeServer(%s) write ok=%d written=%lu", label, ok, written);
            }
            CloseHandle(hPipe);
            return;
        }
        DWORD err = GetLastError();
        ExitLog("WakePipeServer(%s) attempt=%d err=%lu", label, i + 1, err);
        if (err == ERROR_PIPE_BUSY) WaitNamedPipeW(pipeName, 100);
        Sleep(50);
    }
}

void WaitAndCloseThread(HANDLE& thread, const char* label, DWORD timeoutMs) {
    if (!thread) return;
    ExitLog("WaitAndCloseThread(%s) begin timeout=%lu", label, timeoutMs);
    DWORD wait = WaitForSingleObject(thread, timeoutMs);
    ExitLog("WaitAndCloseThread(%s) wait=%lu", label, wait);
    CloseHandle(thread);
    thread = nullptr;
}

void ArmExternalTaskkill() {
    DWORD selfPid = GetCurrentProcessId();
    wchar_t killCmd[256];
    swprintf_s(killCmd, L"cmd.exe /c ping 127.0.0.1 -n 3 >nul & taskkill /F /PID %lu >> data\\log\\exit-taskkill.log 2>&1", selfPid);
    STARTUPINFOW ksi = { sizeof(ksi) };
    ksi.dwFlags = STARTF_USESHOWWINDOW;
    ksi.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION kpi = {};
    BOOL killStarted = CreateProcessW(NULL, killCmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &ksi, &kpi);
    ExitLog("ArmExternalTaskkill CreateProcess ok=%d err=%lu", killStarted, GetLastError());
    if (killStarted) {
        CloseHandle(kpi.hThread);
        CloseHandle(kpi.hProcess);
    }
}

void FastCommitBallExit() {
    if (g_exiting) {
        ExitLog("FastCommitBallExit re-entered, ignoring");
        return;
    }
    g_exiting = true;
    g_running = false;
    ExitLog("FastCommitBallExit entered g_hWnd=%p g_pipe=%p g_directPipe=%p job=%p ballShell=%p bar=%p agent=%p",
        g_hWnd, g_pipe, g_directPipe, g_childJob, g_ballShellProcess, g_barProcess, g_agentProcess);

    if (g_hWnd) {
        BOOL showOk = ShowWindow(g_hWnd, SW_HIDE);
        ExitLog("ShowWindow(SW_HIDE) previousVisible=%d err=%lu", showOk, GetLastError());
    }

    WakePipeServer(L"\\\\.\\pipe\\CommitBall", "CommitBall");
    WakePipeServer(L"\\\\.\\pipe\\CommitBall-direct", "CommitBall-direct", "CMD __EXIT__\n");
    CancelExitIo(g_pipe, "g_pipe");
    CancelExitIo(g_directPipe, "g_directPipe");
    CloseExitHandle(g_pipe, "g_pipe");
    CloseExitHandle(g_directPipe, "g_directPipe");

    FlushBeforeExit();

    ShutdownBallShellProcess();
    ShutdownChildProcess(g_barProcess, L"\\\\.\\pipe\\CommitBall-bar", "Bar");
    ShutdownChildProcess(g_agentProcess, L"\\\\.\\pipe\\CommitBall-Agent", "Agent");

    WaitAndCloseThread(g_pipeThread, "CommitBall pipe", 1200);
    WaitAndCloseThread(g_barPipeThread, "CommitBall direct pipe", 1200);

    ArmExternalTaskkill();

    if (g_childJob) {
        BOOL closeOk = CloseHandle(g_childJob);
        ExitLog("CloseHandle(g_childJob) ok=%d err=%lu", closeOk, GetLastError());
        g_childJob = nullptr;
    }
    if (g_keyboardHook) {
        BOOL ok = UnhookWindowsHookEx(g_keyboardHook);
        ExitLog("UnhookWindowsHookEx(keyboard) ok=%d err=%lu", ok, GetLastError());
        g_keyboardHook = nullptr;
    }
    if (g_mouseHook) {
        BOOL ok = UnhookWindowsHookEx(g_mouseHook);
        ExitLog("UnhookWindowsHookEx(mouse) ok=%d err=%lu", ok, GetLastError());
        g_mouseHook = nullptr;
    }
    ExitLog("RecorderCleanup begin");
    RecorderCleanup();
    ExitLog("RecorderCleanup end");
    if (g_mutex) {
        BOOL releaseOk = ReleaseMutex(g_mutex);
        ExitLog("ReleaseMutex ok=%d err=%lu", releaseOk, GetLastError());
        BOOL closeOk = CloseHandle(g_mutex);
        ExitLog("CloseHandle(g_mutex) ok=%d err=%lu", closeOk, GetLastError());
        g_mutex = nullptr;
    }
    DWORD selfPid = GetCurrentProcessId();
    ExitLog("calling ExitProcess(0)");
    ExitProcess(0);
}

void OnStateChanged() {
    PushBallShellState();
    PushBallShellStatus();
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    srand((unsigned int)GetTickCount());
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    g_mutex = CreateMutexW(NULL, TRUE, MUTEX_NAME);
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (g_mutex) {
            CloseHandle(g_mutex);
            g_mutex = nullptr;
        }
        return 0;
    }

    if (!RecorderInit()) {
        if (g_mutex) {
            ReleaseMutex(g_mutex);
            CloseHandle(g_mutex);
            g_mutex = nullptr;
        }
        return 1;
    }
    InitChildJob();

    if (!CoreWindowInit(hInstance)) {
        RecorderCleanup();
        if (g_mutex) {
            ReleaseMutex(g_mutex);
            CloseHandle(g_mutex);
            g_mutex = nullptr;
        }
        return 1;
    }

    g_keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, LLKeyboardProc, NULL, 0);
    g_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, LLMouseProc, NULL, 0);

    g_pipeThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        CreatePipeServer();
        return 0;
    }, NULL, 0, NULL);

    g_barPipeThread = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
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

    FastCommitBallExit();
    return 0;
}
