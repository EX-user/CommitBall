#pragma once
#include "recorder.hpp"
#include <UIAutomation.h>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "uiautomationcore.lib")

inline const char* ClickTypeName(CONTROLTYPEID id) {
    switch (id) {
        case 50000: return "Button";
        case 50002: return "CheckBox";
        case 50003: return "ComboBox";
        case 50004: return "Edit";
        case 50005: return "Hyperlink";
        case 50007: return "ListItem";
        case 50008: return "List";
        case 50009: return "Menu";
        case 50010: return "MenuBar";
        case 50011: return "MenuItem";
        case 50012: return "ProgressBar";
        case 50013: return "RadioButton";
        case 50014: return "ScrollBar";
        case 50015: return "Slider";
        case 50018: return "Tab";
        case 50019: return "TabItem";
        case 50020: return "Text";
        case 50021: return "ToolBar";
        case 50022: return "ToolTip";
        case 50023: return "Tree";
        case 50024: return "TreeItem";
        case 50025: return "Group";
        case 50027: return "DataGrid";
        case 50028: return "DataItem";
        case 50029: return "Document";
        case 50030: return "SplitButton";
        case 50031: return "Window";
        case 50032: return "Pane";
        case 50033: return "Header";
        case 50035: return "Table";
        case 50037: return "Separator";
        default:    return "Item";
    }
}

inline LRESULT CALLBACK LLMouseProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && wParam == WM_LBUTTONUP && g_state == RECORDING && g_pUIAutomation) {
        MSLLHOOKSTRUCT* p = (MSLLHOOKSTRUCT*)lParam;
        POINT pt = p->pt;

        IUIAutomationElement* pElement = nullptr;
        HRESULT hr = g_pUIAutomation->ElementFromPoint(pt, &pElement);
        if (SUCCEEDED(hr) && pElement) {
            BSTR bstrName = nullptr;
            pElement->get_CurrentName(&bstrName);

            if (bstrName && SysStringLen(bstrName) > 0) {
                CONTROLTYPEID ctrlType = 0;
                pElement->get_CurrentControlType(&ctrlType);
                const char* typeName = ClickTypeName(ctrlType);

                std::wstring name(bstrName, SysStringLen(bstrName));
                std::string utf8 = WideToUtf8(name);

                char contentBuf[512];
                snprintf(contentBuf, sizeof(contentBuf), "%s|%s", typeName, utf8.c_str());

                CheckFocusChange();
                std::string ts = GetTimestamp();
                sqlite3_reset(g_insertStmt);
                sqlite3_bind_int(g_insertStmt, 1, g_recordId);
                sqlite3_bind_text(g_insertStmt, 2, ts.c_str(), -1, SQLITE_TRANSIENT);
                sqlite3_bind_text(g_insertStmt, 3, "click", -1, SQLITE_STATIC);
                sqlite3_bind_text(g_insertStmt, 4, contentBuf, -1, SQLITE_TRANSIENT);
                sqlite3_step(g_insertStmt);
                CheckSessionSplit();
            }
            if (bstrName) SysFreeString(bstrName);
            pElement->Release();
        }
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}
