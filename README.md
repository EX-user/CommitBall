# CommitBall

停靠在屏幕表面的小球，作为用户输入的 tee，记录所有打字内容输出为结构化数据。

## 产品形态

- 始终置顶的小圆球，三击 `[` 激活/退出，不影响焦点
- 激活期间记录中文（Weasel commit）和英文（LL 键盘钩子）
- 定时持久化为 JSON（含时间戳、类型、IME 状态）+ Markdown（纯内容）

## 技术路线

- **Weasel Fork**：`_Respond()` 中 `get_commit()` 捕获中文 commit，通过 Named Pipe 转发
- **CommitBall.exe**：Pipe Server 接收 commit + WH_KEYBOARD_LL 捕获英文 + 三击检测 + 悬浮球 + JSON 输出
- **构建**：C++ / MSVC x64，零外部依赖

## 约束

- 仅支持 Weasel（Rime）输入法用户
- 英文模式（ascii_mode）下按键通过 LL 钩子直接记录
- 不侵入目标应用，不修改输入法核心逻辑

## 快速开始

### 前置依赖

- Windows 10/11
- VS 2022 Build Tools（含 ATL/MFC 组件）
- Git

### 步骤

```bash
# 1. 克隆仓库（含 submodule）
git clone --recursive <repo-url>
cd commit-ball

# 2. 应用 Weasel patch
.\apply-patch.ps1

# 3. 构建 Weasel（按 WEASEL_BUILD.md）
# 4. 安装 Weasel（按 WEASEL_INSTALL.md）

# 5. 构建 CommitBall
.\build-commitball.ps1

# 6. 运行
.\commitball\CommitBall.exe
```

### 使用

1. 启动 CommitBall.exe
2. 三击 `[` 激活录音
3. 打字（中文/英文）
4. 三击 `[` 停止录音
5. 每 20 秒自动输出到 `commitball.txt`

## 架构

```
WeaselServer.exe
  └─ _Respond() / ProcessKeyEvent()
       └─ CommitBallBridge.cpp
            └─ Named Pipe (\\.\pipe\CommitBall)
                 └─ CommitBall.exe
                      ├─ Named Pipe Server
                      ├─ WH_KEYBOARD_LL (三击检测)
                      ├─ SQLite (数据存储)
                      └─ commitball.txt (定期输出)
```

## 文件说明

| 文件 | 说明 |
|------|------|
| `diff_of_weasel.patch` | Weasel 侧改动（4 文件，72 行） |
| `apply-patch.ps1` | 验证并应用 patch |
| `build-commitball.ps1` | 构建 CommitBall.exe |
| `commitball/main.cpp` | CommitBall 源码 |
| `commitball/sqlite3.*` | SQLite 源码（公有领域） |
| `WEASEL_BUILD.md` | Weasel 构建指南 |
| `WEASEL_INSTALL.md` | Weasel 安装/卸载指南 |

## 许可证

- CommitBall：MIT
- SQLite：公有领域
- Weasel：GPL-3.0（见 weasel/ 目录）
