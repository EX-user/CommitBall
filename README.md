# CommitBall

停靠在屏幕表面的小球，作为用户输入的 tee，记录所有打字内容输出为结构化数据。

## 产品形态

- 始终置顶的小圆球，三击 `[` 激活/退出，不影响焦点
- 激活期间记录中文（CB-Weasel commit）、英文（CB-Weasel 转发）和特殊键（LL 钩子）
- 每 10 秒持久化到 `commitball.txt`，按 session 分段，含起迄时间戳

## 技术路线

- **CB-Weasel Fork**：基于 Weasel（小狼毫）改名，避免与官方安装冲突。`_Respond()` 中 `get_commit()` 捕获中文 commit；`ProcessKeyEvent()` 转发英文字符，均通过 Named Pipe 发送
- **CommitBall.exe**：Pipe Server 接收 commit/keystroke + WH_KEYBOARD_LL 捕获特殊键 + 三击检测 + SQLite 存储 + 定时输出 txt
- **构建**：C++ / MSVC x64，SQLite 直接编译（公有领域）

## 约束

- 仅支持 CB-Weasel（Rime）输入法用户（基于 Weasel fork，可与官方 Weasel 共存）
- 中文 commit 和英文字符通过 WeaselServer 转发；特殊键（Backspace、方向键等）由 CommitBall 的 LL hook 直接捕获
- 不侵入目标应用，不修改输入法核心逻辑

## 快速开始

> **必须按顺序完成以下所有步骤，不可跳过。**
> 详见 [WEASEL_BUILD.md](WEASEL_BUILD.md) 和 [WEASEL_INSTALL.md](WEASEL_INSTALL.md)。

### 前置依赖

- Windows 10/11
- VS 2022 Build Tools（含 ATL/MFC 组件）
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

# 5. 构建 CommitBall
.\build-commitball.ps1

# 6. 运行
.\commitball\CommitBall.exe
```

### 使用

1. 启动 CommitBall.exe
2. 三击 `[` 激活录音
3. 打字（中文/英文/特殊键）
4. 三击 `[` 停止录音
5. 每 10 秒自动输出到 `commitball.txt`（编译开关 `ENABLE_TXT_OUTPUT` 控制）

## 架构

```
WeaselServer.exe
  └─ _Respond() / ProcessKeyEvent()
       └─ CommitBallBridge.cpp
            └─ Named Pipe (\\.\pipe\CommitBall)
                 └─ CommitBall.exe
                      ├─ Named Pipe Server (接收 commit + 英文字符)
                      ├─ WH_KEYBOARD_LL (特殊键 + 三击检测)
                      ├─ SQLite (数据存储, record_id 分 session)
                      └─ commitball.txt (定时输出, DbToText)
```

## 文件说明

| 文件 | 说明 |
|------|------|
| `diff_of_weasel.patch` | CB-Weasel 侧改动（CommitBallBridge + 改名，26 文件，957 行） |
| `apply-patch.ps1` | 验证并应用 patch |
| `build-commitball.ps1` | 构建 CommitBall.exe |
| `commitball/main.cpp` | CommitBall 入口（WinMain + 全局变量） |
| `commitball/recorder.hpp` | 录制逻辑（DB、hook、pipe、DbToText） |
| `commitball/ball.hpp` | 悬浮球 UI（绘制、拖拽、吸附、右键菜单） |
| `commitball/sqlite3.*` | SQLite 源码（公有领域） |
| `WEASEL_BUILD.md` | CB-Weasel 构建指南 |
| `WEASEL_INSTALL.md` | CB-Weasel 安装/卸载指南 |

## 许可证

- CommitBall：MIT
- SQLite：公有领域
- CB-Weasel：GPL-3.0（基于 Weasel fork，见 weasel/ 目录）
