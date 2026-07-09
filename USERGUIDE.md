# CommitBall 用户指南

## 概述

CommitBall 是一款桌面活动记录与分析工具。它记录您的键盘输入、鼠标点击、剪贴板粘贴和窗口焦点变化，并通过 AI 生成工作日志摘要和可视化面板。

CommitBall 包含四个进程，启动后自动协同运行：

- **CommitBall.exe** — Core 主进程，负责键鼠监听、数据库、归档、进程生命周期和权限
- **悬浮球 (BallShell)** — 桌面圆形悬浮控件，负责外观、动画、右键菜单和气泡
- **输入条 (Bar)** — 快速笔记输入框和面板容器
- **AI 终端 (Agent)** — 多会话 AI 分析助手

---

## 1. 悬浮球

### 1.1 位置与外观

- 启动后出现在屏幕右下角附近的圆形悬浮球
- 悬浮球外观由 BallShell 皮肤决定；可在右键菜单中切换皮肤
- 非眼睛模式下使用固定状态图标；眼睛模式开启且处于记录状态时启用皮肤动画
  - **▶ / 动态眼睛** — 正在记录
  - **⏸ / 静态皮肤** — 已停止
- 可拖动到屏幕任意位置
- 靠近屏幕边缘时自动吸附（上下左右）

### 1.2 开始/停止记录

**操作：快速按 4 次 CapsLock 键**（每次间隔不超过 500ms）

- 停止状态 → 开始记录：状态切换为记录，BallShell 更新为记录态外观
- 记录中 → 停止：状态切换为就绪，BallShell 更新为停止态外观

### 1.3 右键菜单

右键点击悬浮球，弹出菜单：

| 菜单项 | 说明 |
|--------|------|
| 记录：记录 / 就绪 | 当前记录状态（仅显示） |
| 数据库：xxxKB / 512KB (xx%) | 当前数据库容量（仅显示） |
| Bar：后台就绪 / 显示中 / 锁定显示 / 未运行 | Bar 运行状态（仅显示） |
| Agent：空闲 / 繁忙 / 未运行 | Agent 运行状态（仅显示）。只要存在可接收输入的空闲会话即显示空闲 |
| 打开数据目录 | 用资源管理器打开 data 目录 |
| 打开当前记录文本 | 用记事本打开 live.txt（当前数据库的输出文本） |
| 打开并锁定 Bar | 显示 Bar，并进入锁定状态 |
| 打开 Agent | 显示 Agent 窗口 |
| 分析当前状态 | 向 Agent 发送当前时间提示和 `/summary_to_panel`。若有空闲会话则复用空闲会话，否则创建新会话 |
| 皮肤 | 在 BallShell 内切换悬浮球皮肤 |
| 帮助 | 显示内置帮助菜单 |
| 退出 CommitBall | 退出程序。Core 会先 flush 数据并 checkpoint SQLite，再通过 job object / 兜底 taskkill 确保 BallShell、Bar、Agent 一起退出 |

### 1.4 自动分析

满足以下任一条件时自动触发 Agent 分析：

1. **数据库容量达到 90%**（约 460KB）且本次数据库未分析过
2. **panel.html 超过 4 小时未更新**

触发条件：Agent 正在运行且至少有一个会话可接收输入。若所有会话都繁忙或上下文已满，等待后续检查。

数据库轮转（split）后，新数据库的分析标志自动重置为"未分析"。
归档 split 后会自动触发 `/repair_archives`，由 Agent 检查缺失的 txt、meta、cluster 和模型总结信息。

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
| away / back | 连续 10 分钟无键鼠输入时记录离开，回来后首次键鼠输入记录返回 |
| direct-input | 通过输入条普通 / 代办 / 配置模式提交的内容；指令模式不会写入为 direct-input |

### 1.6 数据管理

- 数据库文件：`data/db/current.db`（SQLite）
- 容量上限 512KB，达到后自动轮转：
  - 旧数据库归档到 `data/sessions/YYYY-MM/` 目录
  - 自动导出文本到 `data/exports/YYYY-MM/`
  - 新数据库保留旧库最后 50 行
- 每次四击CapsLock关闭会话或开启新会话; 每 1 小时强制进行一次会话超时轮转
- 实时文本输出：`data/live/live.txt`（每 30 秒刷新或用户通过悬浮球按钮打开时刷新）

### 1.7 权限要求

需要管理员权限运行。应用启动时会请求管理员权限；如果拒绝 UAC 授权，程序不会进入正常运行状态。

---

## 2. 输入条 (Bar)

### 2.1 唤出

**操作：在任意位置键入唤醒序列 `\ccb`**（默认，2 秒内完成）

输入条从屏幕下方约 3/4 处弹出，宽度为屏幕工作区的 30%（最小 480px，最大 680px）。
不论是否处于记录状态，通过输入条提交的普通输入都会被记录。这被视为直接向应用传递的最低噪声等级事件。

### 2.2 输入框

| 操作 | 效果 |
|------|------|
| 键入文字 + Enter | 普通模式下提交内容（保存到 `data/notes/` 并写入数据库）；指令模式下发送给 Agent |
| Tab | 在普通、代办、配置、指令几类输入模式之间切换 |
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
- 自动根据 HTML 内容调整高度（最大约为输入条宽度的 40%，且不超过工作区高度的 25%）
- 每 30 秒检测 `panel.html` 文件变化，自动刷新
- 底部有 6px 拖动条，可拖动移动面板位置
- Windows 11 上使用 DWM API 显示圆角（Windows 10 为方角）

### 2.5 直接输入

输入条有两类输入：

- **普通 / 代办 / 配置输入**：保存到 `data/notes/`，并以 `direct-input` 写入当前数据库；其中代办、配置会加上对应前缀，便于后续分析。
- **指令模式输入**：不会写入数据库为 `direct-input`，而是作为自然语言命令交给 Agent 的空闲会话处理；如果没有空闲会话则新建会话。Bar/Ball 相关控制由 Agent tool 执行，例如修改唤醒序列、开关眼睛模式、修复归档等。

---

## 3. AI 终端 (Agent)

### 3.1 启动与窗口

主程序启动时自动启动 Agent。可通过悬浮球右键菜单"打开 Agent"显示窗口。
正常情况下，Agent在CommitBall使用期间不会被关闭。Agent 支持多标签会话，每个标签页对应独立会话、输出文档、运行状态和取消令牌。

| 窗口元素 | 说明 |
|----------|------|
| 标题栏 | 显示"CommitBall Agent Terminal"，可拖动移动窗口 |
| 标签栏 | 切换多个 Agent 会话；忙碌、未读和上下文占用状态按标签显示 |
| ▾ 按钮 | 隐藏窗口到后台（Agent 继续运行） |
| 输出区域 | 显示对话历史，支持 Ctrl+C 复制选中文字 |
| 输入框 | 键入命令或对话内容，Enter 提交；当前标签忙碌或上下文已满时禁用 |
| > 提示符 | 输入行前缀 |

#### 快捷键

| 操作 | 效果 |
|------|------|
| Enter | 提交当前标签输入（当前标签繁忙或上下文已满时忽略） |
| Esc | 当前标签忙碌时取消该标签请求；当前标签空闲时隐藏窗口 |
| 连按 Esc × 2（1 秒内） | 中断当前标签模型输出并清空当前标签待执行队列 |

### 3.2 命令

所有终端内置命令以 `/` 开头。当前标签繁忙或上下文已满时无法继续输入；切换到其他空闲标签后可以继续对话。

| 命令 | 说明 |
|------|------|
| `/help` | 显示所有可用命令 |
| `/vendor` | 显示当前 API 配置（base_url、model、api_key 前 8 位） |
| `/vendor {"base_url":"...","model":"...","api_key":"..."}` | 设置并验证 API 配置。验证成功后保存到 `data/agent-config.json` |
| `/new` | 创建新的空会话 |
| `/session` | 进入会话列表菜单，可选择已有会话或创建新会话 |
| `/summary_to_panel` | 向模型显示完整分析 prompt，引导模型读取 live、近期 exports 和长期记忆，并生成 report、extract、memory 更新和 panel.html |
| `/repair_archives` | 向模型显示完整修复 prompt，引导模型调用 `repair_archives` 工具补齐归档 txt/meta/cluster，并检查缺少模型总结的 meta |
| `/organize_agent_out` | 整理 `agent-out` 根目录下的旧输出文件并重建 `index.json` |
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
  a1b2c3d4  open    项目说明文档更新  06-04 18:20 ~ 06-04 18:33  33msgs *
  e5f6g7h8  busy    归档修复分析      06-04 17:19 ~ 06-04 17:23  15msgs

Enter session id to switch, /new for new. Esc to cancel.
```

| 操作 | 效果 |
|------|------|
| 输入会话 ID | 切换到该会话；如果该会话已打开，则切换到已有标签 |
| `/new` | 创建新会话 |
| `/session` | 刷新会话列表 |

`open`、`busy`、`full`、`closed` 表示会话状态；`*` 标记当前所在会话。

### 3.6 数据目录

Agent 相关文件位于 `data/` 目录下：

| 路径 | 说明 |
|------|------|
| `data/agent-config.json` | API 配置（base_url, model, api_key） |
| `data/agent-status` | 当前状态文本（"busy" / "idle"）。只要有任一会话可接收输入即为 idle |
| `data/agent-memory/` | 会话存储（每个会话一个 JSON 文件） |
| `data/agent-out/` | 分析输出目录 |
| `data/agent-out/panel.html` | 面板 HTML 文件 |
| `data/agent-out/panel-template.html` | 面板模板 |
| `data/agent-out/reports/YYYY-MM/` | 分析报告 |
| `data/agent-out/extracts/YYYY-MM/` | 提取出的持久笔记 |
| `data/agent-out/scratch/YYYY-MM/` | 临时分析文件 |
| `data/agent-out/memory/summary_task_exp_decay_memory.md` | 长期记忆主文件 |
| `data/agent-out/index.json` | `agent-out` 文件索引 |
| `data/log/agent.log` | Agent 运行日志 |

### 3.7 Tool 调用

Agent 使用以下工具读取和整理 `data/` 目录，也可以通过少量控制类工具通知 Core / Bar / BallShell：

| 工具 | 说明 |
|------|------|
| `list` | 列出目录内容 |
| `read` | 读取文件内容 |
| `write` | 在 `data/agent-out/` 下写入完整文件 |
| `edit` | 在 `data/agent-out/` 下精确替换已有文本文件片段 |
| `grep` | 在 `data/agent-out`、`data/exports` 或指定 `data/` 子目录中搜索文本 |
| `display_panel` | 写入 `data/agent-out/panel.html` 并通知 Bar 刷新面板 |
| `update_meta` | 更新 `data/exports/**/*.meta.json` 中的标题、标签、摘要和 cluster 总结 |
| `rename_session` | 为当前 Agent 会话命名 |
| `set_bar_trigger` | 设置 Bar 唤醒序列 |
| `set_eye_mode` | 开关或切换悬浮球眼睛模式 |
| `repair_archives` | 机器修复归档缺失的 txt/meta/cluster，不覆盖已有 meta |
| `show_ball_bubble` | 让悬浮球显示短文本气泡 |
| `now` | 获取当前本地时间、UTC 时间和时区 |
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
    ├── [自动] panel.html 超过 4 小时未更新 → 触发 Agent 分析
    ├── [手动] 右键 → 启动 Agent 分析
    ├── [自动] 归档 split 后 → 触发归档修复和 meta 检查
    │
    └── Agent 生成 reports/extracts/memory + panel.html → 输入条面板自动刷新
```

---

## 5. 安装与卸载

- 安装：运行 `CommitBall-0.2.4.0-installer.exe`，以管理员权限安装到 `C:\Program Files\CommitBall`
- 运行环境：安装包内置 Microsoft .NET 8 Desktop Runtime (x64)，系统缺失时会自动静默安装
- 卸载：运行 `C:\Program Files\CommitBall\uninstall.exe`
- 数据保留：卸载不会删除 `data/` 目录下的用户数据

