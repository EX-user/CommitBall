# CommitBall 用户指南

## 概述

CommitBall 是一款桌面活动记录与分析工具。它记录您的键盘输入、鼠标点击、剪贴板粘贴和窗口焦点变化，并通过 AI 生成工作日志摘要和可视化面板。

CommitBall 包含三个组件，启动后自动运行：

- **悬浮球 (CommitBall)** — 主程序，桌面上的圆形悬浮控件
- **输入条 (Bar)** — 快速笔记输入框
- **AI 终端 (Agent)** — 可选的 AI 分析助手

---

## 1. 悬浮球

### 1.1 位置与外观

- 启动后出现在屏幕右下角的蓝色圆形悬浮球
- 球上显示当前状态图标：
  - **▶** — 正在记录（红色）
  - **⏸** — 已停止（蓝色）
  - **?** — 权限不足（灰色，30 秒后自动退出）
- 可拖动到屏幕任意位置
- 靠近屏幕边缘时自动吸附（上下左右）
- 记录开始时自动从吸附位置弹出

### 1.2 开始/停止记录

**操作：快速按 4 次 CapsLock 键**（每次间隔不超过 500ms）

- 停止状态 → 开始记录：球变红，显示 ▶
- 记录中 → 停止：球变蓝，显示 ⏸

### 1.3 右键菜单

右键点击悬浮球，弹出菜单：

| 菜单项 | 说明 |
|--------|------|
| 状态: 记录 / 状态: 就绪 | 当前记录状态（仅显示） |
| xxxKB / 512KB (xx%) | 当前数据库容量（仅显示） |
| Agent: 空闲 / Agent: 繁忙 / Agent: 未运行 | Agent 运行状态（仅显示，仅 Agent 运行时出现） |
| 打开数据路径 | 用资源管理器打开 data 目录 |
| 查看当前记录文本 | 用记事本打开 live.txt（当前数据库的输出文本） |
| 打开 Agent 终端 | 显示 Agent 窗口 |
| 启动 Agent 分析 | 发送 `/new` + `/summary_to_panel` 命令给 Agent，自动分析当前记录并生成面板（Agent 繁忙时灰色不可用） |
| 帮助 | 显示帮助信息弹窗 |
| 退出 CommitBall | 退出程序（等待 3 秒后关闭所有组件） |

### 1.4 自动分析

满足以下任一条件时自动触发 Agent 分析：

1. **数据库容量达到 90%**（约 460KB）且本次数据库未分析过
2. **panel.html 超过 12 小时未更新**

触发条件：Agent 正在运行且当前不繁忙。若 Agent 繁忙，等待下次检查（每 60 秒检查一次）。

数据库轮转（split）后，新数据库的分析标志自动重置为"未分析"。

### 1.5 记录内容

记录状态下采集以下信息：

| 类型 | 说明 |
|------|------|
| keystroke | 特殊按键（Backspace、Enter、方向键等） |
| keyboard input | 键盘输入（仅当搭配特定输入法） |
| paste / paste-big / paste-mega | Ctrl+V 粘贴内容（按长度分级） |
| click | 鼠标左键点击（使用 UI Automation 识别控件名称和类型，如 `Button|保存`） |
| focus | 窗口焦点变化（记录窗口标题、进程名、窗口位置） |
| focus-stay | 同一窗口停留约 60 秒后插入一次（标记持续停留） |
| timer | 每 10 分钟插入一次时间标记 |
| direct-input | 通过输入条提交的内容 |

### 1.6 数据管理

- 数据库文件：`data/db/current.db`（SQLite）
- 容量上限 512KB，达到后自动轮转：
  - 旧数据库归档到 `data/sessions/YYYY-MM/` 目录
  - 自动导出文本到 `data/exports/YYYY-MM/`
  - 新数据库保留旧库最后 50 行
- 每次四击CapsLock关闭会话或开启新会话; 每 1 小时强制进行一次会话超时轮转
- 实时文本输出：`data/live/live.txt`（每 30 秒刷新或用户通过悬浮球按钮打开时刷新）

### 1.7 权限要求

需要管理员权限运行。若未以管理员身份运行，悬浮球显示灰色问号，30 秒后自动退出。

---

## 2. 输入条 (Bar)

### 2.1 唤出

**操作：在任意位置键入唤醒序列 `\ccb`**（默认，2 秒内完成）

输入条从屏幕下方约 3/4 处弹出，宽度为屏幕工作区的 30%（最小 480px，最大 680px）。
不论是否处于记录状态，通过输入条键入的文本都会被记录。这被视为直接向应用传递的最低噪声等级事件。

### 2.2 输入框

| 操作 | 效果 |
|------|------|
| 键入文字 + Enter | 提交内容（保存到 `data/notes/` 并写入数据库） |
| Esc | 关闭输入条 |
| 失去焦点 | 自动隐藏（150ms 后检测，若焦点仍在输入条或面板上则不隐藏） |

输入框提示文字：
- 未锁定时：`Esc 关闭 | 键入后 Enter 提交`
- 锁定时：`Esc 关闭 | Enter 提交并继续`

### 2.3 锁定按钮 🔒/🔓

| 状态 | 图标 | 行为 |
|------|------|------|
| 未锁定（默认） | 🔓 灰色 | Enter 提交后关闭输入条 |
| 锁定 | 🔵 蓝色 🔒 | Enter 提交后清空输入框但保持输入条打开 |

### 2.4 面板按钮 📊

| 状态 | 图标 | 行为 |
|------|------|------|
| 面板开启（默认） | 📊 蓝色 | 输入条显示时自动显示面板 |
| 面板关闭 | 📊 灰色 | 不显示面板 |

面板（Panel）显示在输入条上方，使用 WebView2 渲染 `data/agent-out/panel.html`。
该html通常由agent生成，被视为一个灵活的展示面板。

面板特性：
- `ShowActivated=False`、`Focusable=False`，不会抢走输入焦点
- 自动根据 HTML 内容调整高度（最大为宽度的 40%）
- 每 30 秒检测 `panel.html` 文件变化，自动刷新
- 底部有 6px 拖动条，可拖动移动面板位置
- Windows 11 上使用 DWM API 显示圆角（Windows 10 为方角）

### 2.5 直接输入

无论是否处于记录状态，输入条提交的内容都会以 `direct-input` 类型写入当前数据库。

---

## 3. AI 终端 (Agent)

### 3.1 启动与窗口

主程序启动时自动启动 Agent。可通过悬浮球右键菜单"打开 Agent 终端"显示窗口。
正常情况下，Agent在CommitBall使用期间不会被关闭。

| 窗口元素 | 说明 |
|----------|------|
| 标题栏 | 显示"CommitBall Agent Terminal"，可拖动移动窗口 |
| ▾ 按钮 | 隐藏窗口到后台（Agent 继续运行） |
| 输出区域 | 显示对话历史，支持 Ctrl+C 复制选中文字 |
| 输入框 | 键入命令或对话内容，Enter 提交 |
| > 提示符 | 输入行前缀 |

#### 快捷键

| 操作 | 效果 |
|------|------|
| Enter | 提交输入（繁忙时忽略） |
| Esc | 隐藏窗口 |
| 连按 Esc × 2（1 秒内） | 中断当前模型输出并清空待执行队列 |

### 3.2 命令

所有命令以 `/` 开头。Agent 繁忙时无法输入命令，除非事先打断模型输出。

| 命令 | 说明 |
|------|------|
| `/help` | 显示所有可用命令 |
| `/vendor` | 显示当前 API 配置（base_url、model、api_key 前 8 位） |
| `/vendor {"base_url":"...","model":"...","api_key":"..."}` | 设置并验证 API 配置。验证成功后保存到 `data/agent-config.json` |
| `/new` | 创建新的空会话 |
| `/session` | 进入会话列表菜单，可选择已有会话或创建新会话 |
| `/analyse [附加说明]` | 使用 subtask 模式分析 live.txt 工作日志，输出报告到 `data/agent-out/` |
| `/summary_to_panel` | 一步完成分析 + 面板生成：分析 live.txt 并生成 report、extract 和 panel.html |
| 任意其他文字 | 作为对话内容发送给模型 |

### 3.3 `/vendor` 命令详解

首次使用 Agent 需要先配置 API。

**查看当前配置：**
```
/vendor
```

**设置新配置：**
```
/vendor {"base_url":"https://api.example.com/v1","model":"gpt-4","api_key":"sk-xxx"}
```

三个字段均为必填：
- `base_url` — API 服务地址，用户需自行包含版本路径（如 `/v1`、`/v4/`）
- `model` — 模型名称
- `api_key` — API 密钥

设置后自动验证：请求 `{base_url}/models`，检查模型是否在可用列表中。验证失败会显示错误信息但不修改配置。

### 3.4 `/session` 菜单

进入菜单后显示所有会话列表：

```
--- Sessions ---
  a1b2c3d4  06-04 18:20 ~ 06-04 18:33  33msgs *
  e5f6g7h8  06-04 17:19 ~ 06-04 17:23  15msgs

Enter session id to switch, /new for new. Esc to cancel.
```

| 操作 | 效果 |
|------|------|
| 输入会话 ID | 切换到该会话（加载历史消息到输出区） |
| `/new` | 创建新会话 |
| `/session` | 刷新会话列表 |

`*` 标记当前所在会话。

### 3.6 `/analyse` vs `/summary_to_panel`

| | `/analyse` | `/summary_to_panel` |
|---|---|---|
| 模式 | 分发Subtask | 单次任务（更快速） |
| 输出 | `YYMMDD_HHMM-report.md` + `YYMMDD_HHMM-extract.md` | 同左 + `panel.html` |
| 面板 | 不生成面板 | 自动生成面板 |
| 用途 | 仅生成报告 | 报告 + 面板一步到位 |

主程序的"启动 Agent 分析"和自动分析均使用 `/summary_to_panel`。

### 3.7 数据目录

Agent 相关文件位于 `data/` 目录下：

| 路径 | 说明 |
|------|------|
| `data/agent-config.json` | API 配置（base_url, model, api_key） |
| `data/agent-status` | 当前状态文本（"busy" / "idle"），由 Agent 写入 |
| `data/agent-memory/` | 会话存储（每个会话一个 JSON 文件） |
| `data/agent-out/` | 分析输出目录 |
| `data/agent-out/panel.html` | 面板 HTML 文件 |
| `data/agent-out/panel-template.html` | 面板模板 |
| `data/agent-out/YYMMDD_HHMM-report.md` | 分析报告 |
| `data/agent-out/YYMMDD_HHMM-extract.md` | 提取摘要 |
| `data/log/agent.log` | Agent 运行日志 |

### 3.8 Tool 调用

Agent 使用以下工具与文件系统交互（仅限 `data/` 目录）：

| 工具 | 说明 |
|------|------|
| `list` | 列出目录内容 |
| `read` | 读取文件内容 |
| `write` | 写入文件 |
| `pwd` | 显示当前目录 |
| `subtask` | 启动子任务（仅在非 subtask 模式下可用，不可嵌套） |

Agent 的 tool 调用无硬性上限。当连续调用超过 20 次时，系统会每 10 轮插入提示信息提醒模型控制调用次数。

---

## 4. 整体工作流

```
启动 CommitBall
    │
    ├── 悬浮球出现（停止状态）
    ├── 输入条后台运行
    └── Agent 后台运行
    │
快速按 4 次 CapsLock → 开始记录
    │
    ├── 记录键盘、鼠标、粘贴、焦点变化
    ├── 数据写入 data/db/current.db + data/live/live.txt
    ├── 键入 \ccb → 弹出输入条 → 可提交快速笔记
    │
    ├── [自动] DB 达到 90% → 触发 Agent 分析
    ├── [自动] panel.html 超过 12 小时未更新 → 触发 Agent 分析
    ├── [手动] 右键 → 启动 Agent 分析
    │
    └── Agent 生成报告 + 面板 → panel.html → 输入条面板自动刷新
```

---

## 5. 安装与卸载

- 安装：运行 `CommitBall-0.1.2.0-installer.exe`，以管理员权限安装到 `C:\Program Files\CommitBall`
- 卸载：运行 `C:\Program Files\CommitBall\uninstall.exe`
- 数据保留：卸载不会删除 `data/` 目录下的用户数据
