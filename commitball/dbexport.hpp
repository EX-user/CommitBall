#pragma once
#include "sqlite3.h"
#include <string>
#include <cstring>
#include <utility>
#include <vector>

enum class DbTextProfile {
    Raw,
    Summary,
    Agent
};

inline bool DbExportEnsureLine(std::string& body) {
    if (!body.empty() && body.back() != '\n') {
        body += "\n";
        return true;
    }
    return false;
}

inline std::string DbExportShortTime(const char* ts) {
    std::string value = ts ? ts : "";
    if (value.length() >= 16) return value.substr(11, 5);
    return value;
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

inline std::string DbExportShorten(const std::string& text, size_t maxLen) {
    if (text.size() <= maxLen) return text;
    return text.substr(0, maxLen) + "...";
}

inline void DbExportFlushInput(std::string& body, std::string& input, DbTextProfile profile) {
    if (input.empty()) return;
    DbExportEnsureLine(body);
    if (profile == DbTextProfile::Summary)
        body += "[input] " + DbExportShorten(input, 200) + "\n";
    else
        body += "[input] " + input + "\n";
    input.clear();
}

inline std::string DbToText(sqlite3* db, DbTextProfile profile = DbTextProfile::Agent) {
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
    };

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
    int skippedFocusRepeats = 0;
    bool wroteTimerInRecord = false;
    bool awayActive = false;

    auto flushRecord = [&]() {
        if (curRecordId < 0) return;
        if (profile != DbTextProfile::Raw)
            DbExportFlushInput(body, pendingInput, profile);
        if (skippedFocusRepeats > 0 && profile == DbTextProfile::Raw) {
            DbExportEnsureLine(body);
            body += "[focus-repeat] +" + std::to_string(skippedFocusRepeats) + "\n";
        }
        if (profile != DbTextProfile::Raw && body.empty()) return;
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
            skippedFocusRepeats = 0;
            wroteTimerInRecord = false;
        } else if (ts) {
            lastTs = ts;
        }

        if (content) {
            if (type && strncmp(type, "focus", 5) == 0) {
                std::string focus = content;
                if (profile != DbTextProfile::Raw && DbExportExcludedFocus(focus)) continue;
                if (profile != DbTextProfile::Raw)
                    DbExportFlushInput(body, pendingInput, profile);
                if (profile != DbTextProfile::Raw && focus == lastFocus) {
                    skippedFocusRepeats++;
                    continue;
                }
                if (skippedFocusRepeats > 0 && profile == DbTextProfile::Raw) {
                    DbExportEnsureLine(body);
                    body += "[focus-repeat] +" + std::to_string(skippedFocusRepeats) + "\n";
                }
                skippedFocusRepeats = 0;
                lastFocus = focus;
                DbExportEnsureLine(body);
                if (profile == DbTextProfile::Raw)
                    body += std::string("[") + type + "] " + focus + "\n";
                else
                    body += std::string(profile == DbTextProfile::Summary ? "[work] " : "[focus] ") + focus + "\n";
            } else if (type && strcmp(type, "direct-input") == 0) {
                if (profile != DbTextProfile::Raw)
                    DbExportFlushInput(body, pendingInput, profile);
                DbExportEnsureLine(body);
                std::string direct = content;
                if (profile == DbTextProfile::Summary)
                    direct = DbExportShorten(direct, 200);
                body += std::string("[direct] ") + direct + "\n";
            } else if (type && strcmp(type, "commit") == 0) {
                if (profile == DbTextProfile::Raw) {
                    DbExportEnsureLine(body);
                    body += std::string("[commit] ") + content + "\n";
                } else {
                    pendingInput += content;
                }
            } else if (type && strcmp(type, "click") == 0) {
                if (profile != DbTextProfile::Raw) continue;
                DbExportEnsureLine(body);
                body += std::string("[click]") + content + "\n";
            } else if (type && strcmp(type, "timer") == 0) {
                if (profile != DbTextProfile::Raw) continue;
                DbExportEnsureLine(body);
                body += "[timer] " + DbExportShortTime(ts) + "\n";
                wroteTimerInRecord = true;
            } else if (type && strcmp(type, "away") == 0) {
                if (profile != DbTextProfile::Raw) {
                    if (awayActive) continue;
                    DbExportFlushInput(body, pendingInput, profile);
                    awayActive = true;
                }
                DbExportEnsureLine(body);
                body += "[away] " + DbExportShortTime(ts) + " " + content + "\n";
            } else if (type && strcmp(type, "back") == 0) {
                if (profile != DbTextProfile::Raw) {
                    DbExportFlushInput(body, pendingInput, profile);
                    awayActive = false;
                }
                DbExportEnsureLine(body);
                body += "[back] " + DbExportShortTime(ts) + " " + content + "\n";
            } else if (type && (strcmp(type, "paste") == 0 || strcmp(type, "paste-big") == 0 || strcmp(type, "paste-mega") == 0)) {
                if (profile != DbTextProfile::Raw)
                    DbExportFlushInput(body, pendingInput, profile);
                DbExportEnsureLine(body);
                std::string pc = DbExportFlattenText(content);
                if (profile == DbTextProfile::Summary) {
                    body += std::string("[") + type + " chars=" + std::to_string(pc.size()) + "]" + DbExportShorten(pc, 160) + "\n";
                } else {
                    body += std::string("[") + type + "]" + pc + "\n";
                }
            } else {
                if (profile != DbTextProfile::Raw) continue;
                std::string s = content;
                for (auto& [from, to] : shortMap) {
                    size_t pos2 = 0;
                    while ((pos2 = s.find(from, pos2)) != std::string::npos) {
                        s.replace(pos2, strlen(from), to);
                        pos2 += strlen(to);
                    }
                }
                body += s;
            }
        }
    }
    flushRecord();
    sqlite3_finalize(stmt);
    return output;
}
