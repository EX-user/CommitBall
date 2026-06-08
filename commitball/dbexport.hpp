#pragma once
#include "sqlite3.h"
#include <string>
#include <cstring>

inline std::string DbToText(sqlite3* db) {
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

    auto flushRecord = [&]() {
        if (curRecordId < 0) return;
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
        } else if (ts) {
            lastTs = ts;
        }

        if (type && strcmp(type, "auto-analyse") == 0) continue;

        if (content) {
            if (type && strncmp(type, "focus", 5) == 0) {
                if (!body.empty() && body.back() != '\n') body += "\n";
                body += std::string("[") + type + "] " + content + "\n";
            } else if (type && strcmp(type, "direct-input") == 0) {
                if (!body.empty() && body.back() != '\n') body += "\n";
                body += std::string("[direct] ") + content + "\n";
            } else if (type && strcmp(type, "click") == 0) {
                if (!body.empty() && body.back() != '\n') body += "\n";
                body += std::string("[click]") + content + "\n";
            } else if (type && strcmp(type, "timer") == 0) {
                if (!body.empty() && body.back() != '\n') body += "\n";
                std::string timerTs = ts ? ts : "";
                if (timerTs.length() >= 16) timerTs = timerTs.substr(11, 5);
                body += "[timer] " + timerTs + "\n";
            } else if (type && (strcmp(type, "paste") == 0 || strcmp(type, "paste-big") == 0 || strcmp(type, "paste-mega") == 0)) {
                if (!body.empty() && body.back() != '\n') body += "\n";
                std::string pc = content;
                size_t pos = 0;
                while ((pos = pc.find("\r\n", pos)) != std::string::npos) {
                    pc.replace(pos, 2, "\xE2\x86\xB5");
                }
                pos = 0;
                while ((pos = pc.find('\n', pos)) != std::string::npos) {
                    pc.replace(pos, 1, "\xE2\x86\xB5");
                }
                body += std::string("[") + type + "]" + pc + "\n";
            } else {
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
