#pragma once
#include "sqlite3.h"
#include <string>
#include <cstring>
#include <utility>
#include <vector>

inline bool DbExportEnsureLine(std::string& body) {
    if (!body.empty() && body.back() != '\n') {
        body += "\n";
        return true;
    }
    return false;
}

inline std::string DbExportFocusProcess(const std::string& focus) {
    size_t bar = focus.rfind('|');
    if (bar == std::string::npos) return focus;
    if (bar + 1 >= focus.size()) return "";
    size_t start = bar + 1;
    while (start < focus.size() && (unsigned char)focus[start] <= 32) start++;
    size_t end = focus.size();
    while (end > start && (unsigned char)focus[end - 1] <= 32) end--;
    return focus.substr(start, end - start);
}

inline std::string DbExportFocusTitle(const std::string& focus) {
    size_t bar = focus.rfind('|');
    std::string title = (bar == std::string::npos) ? focus : focus.substr(0, bar);
    size_t start = 0;
    while (start < title.size() && (unsigned char)title[start] <= 32) start++;
    size_t end = title.size();
    while (end > start && (unsigned char)title[end - 1] <= 32) end--;
    return title.substr(start, end - start);
}

inline bool DbExportExcludedFocus(const std::string& focus) {
    std::string proc = DbExportFocusProcess(focus);
    for (char& c : proc) {
        if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
    }
    if (proc.empty() || DbExportFocusTitle(focus).empty()) return true;
    return proc == "TEXTINPUTHOST.EXE" ||
           proc == "SHELLEXPERIENCEHOST.EXE" ||
           proc == "STARTMENUEXPERIENCEHOST.EXE" ||
           proc == "SEARCHAPP.EXE" ||
           proc == "LOCKAPP.EXE" ||
           proc == "COMMITBALL-BAR.EXE" ||
           proc == "COMMITBALL-AGENT.EXE" ||
           proc == "COMMITBALL-BALLSHELL.EXE" ||
           proc == "GIT-CREDENTIAL-MANAGER.EXE" ||
           proc.find("INSTALLER") != std::string::npos;
}

inline std::string DbExportFlattenText(std::string text) {
    size_t pos = 0;
    while ((pos = text.find("\r\n", pos)) != std::string::npos) {
        text.replace(pos, 2, "\xE2\x86\xB5");
    }
    pos = 0;
    while ((pos = text.find('\n', pos)) != std::string::npos) {
        text.replace(pos, 1, "\xE2\x86\xB5");
    }
    return text;
}

inline void DbExportFlushInput(std::string& body, std::string& input, std::string& inputStart, const std::string& inputEnd) {
    if (input.empty()) return;
    DbExportEnsureLine(body);
    body += "[" + inputStart;
    if (!inputEnd.empty() && inputEnd != inputStart) body += "~" + inputEnd;
    body += "] [input] " + input + "\n";
    input.clear();
    inputStart.clear();
}

inline std::string DbExportKeyText(std::string s) {
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
        {"[Copy]",      "[<copy]"},
        {"[Cut]",       "[<cut]"},
        {"[Undo]",      "[<undo]"},
        {"[Paste]",     "[<paste]"},
    };
    for (auto& [from, to] : shortMap) {
        size_t pos = 0;
        while ((pos = s.find(from, pos)) != std::string::npos) {
            s.replace(pos, strlen(from), to);
            pos += strlen(to);
        }
    }
    return s;
}

inline void DbExportAppendEventLine(std::string& body, const std::string& ts, const std::string& tag, const std::string& content) {
    DbExportEnsureLine(body);
    body += "[" + ts + "] [" + tag + "]";
    if (!content.empty()) body += " " + content;
    body += "\n";
}

inline std::string DbToText(sqlite3* db) {
    if (!db) return "";

    sqlite3_stmt* stmt;
    int rc = sqlite3_prepare_v2(db,
        "SELECT record_id, ts, type, content FROM log ORDER BY record_id, id",
        -1, &stmt, nullptr);
    if (rc != SQLITE_OK) return "";

    std::string output;
    int curRecordId = -1;
    std::string firstTs, lastTs, body;
    std::string lastFocus;
    std::string pendingInput;
    std::string pendingInputStart;
    std::string pendingInputEnd;
    int skippedFocusRepeats = 0;
    bool awayActive = false;

    auto flushRecord = [&]() {
        if (curRecordId < 0) return;
        DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
        if (body.empty()) return;
        output += "--- #" + std::to_string(curRecordId) + " [" + firstTs + " ~ " + lastTs + "] ---\n";
        output += body;
        if (!body.empty() && body.back() != '\n') output += "\n";
        output += "\n";
    };

    while (sqlite3_step(stmt) == SQLITE_ROW) {
        int recordId = sqlite3_column_int(stmt, 0);
        const char* ts = (const char*)sqlite3_column_text(stmt, 1);
        const char* type = (const char*)sqlite3_column_text(stmt, 2);
        const char* content = (const char*)sqlite3_column_text(stmt, 3);

        if (recordId != curRecordId) {
            flushRecord();
            curRecordId = recordId;
            firstTs = ts ? ts : "";
            lastTs = firstTs;
            body.clear();
            pendingInput.clear();
            pendingInputStart.clear();
            pendingInputEnd.clear();
            skippedFocusRepeats = 0;
        } else if (ts) {
            lastTs = ts;
        }

        if (content) {
            std::string tsStr = ts ? ts : "";
            std::string typeStr = type ? type : "";
            std::string contentStr = content ? content : "";
            if (type && strncmp(type, "focus", 5) == 0) {
                std::string focus = contentStr;
                if (DbExportExcludedFocus(focus)) continue;
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                if (focus == lastFocus) {
                    skippedFocusRepeats++;
                    continue;
                }
                skippedFocusRepeats = 0;
                lastFocus = focus;
                DbExportAppendEventLine(body, tsStr, "focus", focus);
            } else if (type && strcmp(type, "direct-input") == 0) {
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                DbExportAppendEventLine(body, tsStr, "direct", contentStr);
            } else if (type && strcmp(type, "commit") == 0) {
                if (pendingInput.empty()) pendingInputStart = tsStr;
                pendingInputEnd = tsStr;
                pendingInput += contentStr;
            } else if (type && strcmp(type, "click") == 0) {
                if (pendingInput.empty()) continue;
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                DbExportAppendEventLine(body, tsStr, "click", contentStr);
            } else if (type && strcmp(type, "timer") == 0) {
                continue;
            } else if (type && strcmp(type, "away") == 0) {
                if (awayActive) continue;
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                awayActive = true;
                DbExportAppendEventLine(body, tsStr, "away", contentStr);
            } else if (type && strcmp(type, "back") == 0) {
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                awayActive = false;
                DbExportAppendEventLine(body, tsStr, "back", contentStr);
            } else if (type && (strcmp(type, "paste") == 0 || strcmp(type, "paste-big") == 0 || strcmp(type, "paste-mega") == 0)) {
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                DbExportAppendEventLine(body, tsStr, typeStr, DbExportFlattenText(contentStr));
            } else if (type && strcmp(type, "keystroke") == 0) {
                DbExportFlushInput(body, pendingInput, pendingInputStart, pendingInputEnd);
                DbExportAppendEventLine(body, tsStr, "key", DbExportKeyText(contentStr));
            } else {
                continue;
            }
        }
    }
    flushRecord();
    sqlite3_finalize(stmt);
    return output;
}
