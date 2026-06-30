# CommitBall

停靠在屏幕表面的小球，作为用户输入的 tee，记录所有打字内容输出为结构化数据。

## 产品形态

- 始终置顶的小圆球由 `CommitBall-BallShell.exe` 渲染，主进程 `CommitBall.exe` 负责记录和生命周期管理
- 四击 `CapsLock` 激活/退出记录，不影响当前应用焦点
- 激活期间记录中文（CB-Weasel commit）、英文（CB-Weasel 转发）和特殊键（LL 钩子）
- 每 30 秒持久化到 `live.txt`，按 session 分段，含起迄时间戳
- `CommitBall-Bar.exe` 提供快速输入条和面板，`CommitBall-Agent.exe` 提供多会话 AI 分析终端

## 技术路线

- **CB-Weasel Fork**：基于 Weasel（小狼毫）改名，避免与官方安装冲突。`_Respond()` 中 `get_commit()` 捕获中文 commit；`ProcessKeyEvent()` 转发英文字符，均通过 Named Pipe 发送
- **CommitBall.exe**：Pipe Server 接收 commit/keystroke + WH_KEYBOARD_LL 捕获特殊键 + 四击 CapsLock 检测 + SQLite 存储 + 定时输出 txt，并通过 pipe 驱动 Bar、Agent、BallShell
- **CommitBall-BallShell.exe**：C# WPF 悬浮球 UI，负责皮肤、动画、菜单、气泡、拖动和吸边
- **CommitBall-Bar.exe**：C# WPF 输入条和 WebView2 面板，支持自然语言指令转交 Agent
- **CommitBall-Agent.exe**：C# WPF 多标签 AI 终端，负责分析、归档修复、长期记忆和面板生成
- **构建**：Core 使用 C++ / MSVC x64，SQLite 直接编译（公有领域）；Bar、Agent、BallShell 使用 .NET 8 WPF

## 约束

- 仅支持 CB-Weasel（Rime）输入法用户（基于 Weasel fork，可与官方 Weasel 共存）
- 中文 commit 和英文字符通过 WeaselServer 转发；特殊键（Backspace、方向键等）由 CommitBall 的 LL hook 直接捕获
- 不侵入目标应用，不修改输入法核心逻辑
- CommitBall 默认请求管理员权限；拒绝 UAC 时不会进入正常运行状态

## 快速开始

> **必须按顺序完成以下所有步骤，不可跳过。**
> 详见 [WEASEL_BUILD.md](WEASEL_BUILD.md) 和 [WEASEL_INSTALL.md](WEASEL_INSTALL.md)。

### 前置依赖

- Windows 10/11
- VS 2022 Build Tools（含 ATL/MFC 组件）
- .NET 8 SDK（用于 Bar、Agent、BallShell）
- NSIS（仅打包安装包时需要）
- Git

### 步骤

```bash
# 1. 克隆仓库（含 submodule）
git clone --recursive <repo-url>
cd commit-ball

# 2. 应用 CB-Weasel 补丁（不可跳过，含 CommitBallBridge + 改名）
.\apply-patch.ps1

# 3. 构建 CB-Weasel（按 WEASEL_BUILD.md 逐步执行）
# 4. 安装 CB-Weasel（按 WEASEL_INSTALL.md 逐步执行）

# 5. 构建 CommitBall Core
.\build-commitball.ps1

# 6. 运行 Core
# 注意：源码目录中直接运行 Core 时，CommitBall-Bar.exe、CommitBall-Agent.exe、
# CommitBall-BallShell.exe 必须已经发布到 Core 同目录。完整集成测试建议使用安装包。
.\commitball\publish\CommitBall.exe
```

### 使用

1. 启动 CommitBall.exe
2. 快速按 4 次 `CapsLock` 激活录音
3. 打字（中文/英文/特殊键）
4. 快速按 4 次 `CapsLock` 停止录音
5. 每 30 秒自动输出到 `live.txt`
6. 键入默认唤醒序列 `\ccb` 可打开输入条；右键悬浮球可打开 Agent、数据目录和帮助

## 架构

```
WeaselServer.exe
  └─ _Respond() / ProcessKeyEvent()
       └─ CommitBallBridge.cpp
            └─ Named Pipe (\\.\pipe\CommitBall)
                 └─ CommitBall.exe
                      ├─ Named Pipe Server (接收 commit + 英文字符)
                      ├─ WH_KEYBOARD_LL (特殊键 + 四击 CapsLock 检测)
                      ├─ SQLite (数据存储, record_id 分 session)
                      ├─ live.txt (定时输出, DbToText)
                      ├─ CommitBall-BallShell.exe (悬浮球 UI / 菜单 / 气泡)
                      ├─ CommitBall-Bar.exe (输入条 / WebView2 面板)
                      └─ CommitBall-Agent.exe (多会话 AI 分析 / 工具调用)
```

## 文件说明

| 文件 | 说明 |
|------|------|
| `diff_of_weasel.patch` | CB-Weasel 侧改动（CommitBallBridge + 改名，26 文件，957 行） |
| `apply-patch.ps1` | 验证并应用 patch |
| `build-commitball.ps1` | 构建 CommitBall.exe |
| `commitball/main.cpp` | CommitBall 入口（WinMain + 全局变量） |
| `commitball/recorder.hpp` | 录制逻辑（DB、hook、pipe、DbToText） |
| `commitball/ball_shared.hpp` | Core 与 BallShell 共享的 UI 状态结构 |
| `commitball/core_window.hpp` | Core 隐藏窗口、消息泵和定时器宿主 |
| `commitball-ball-shell/` | C# 悬浮球 UI、皮肤资源和菜单气泡逻辑 |
| `commitball-bar/` | C# 输入条和面板 |
| `commitball-agent/` | C# AI 终端、工具和提示词 |
| `commitball/sqlite3.*` | SQLite 源码（公有领域） |
| `installer/` | NSIS 安装包脚本 |
| `WEASEL_BUILD.md` | CB-Weasel 构建指南 |
| `WEASEL_INSTALL.md` | CB-Weasel 安装/卸载指南 |

## 许可证

- CommitBall：MIT
- SQLite：公有领域
- CB-Weasel：GPL-3.0（基于 Weasel fork，见 weasel/ 目录）
