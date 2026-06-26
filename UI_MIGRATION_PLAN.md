# CommitBall UI 迁移准备方案

本文档记录将 `CommitBall-BallUiLab` 的 C# 悬浮球 UI 迁移到 CommitBall 主程序的准备方案。目标不是重写底层，而是先把悬浮球窗口、皮肤、动画、气泡和菜单从 C++ GDI 代码中剥离出来，由独立 C# `BallShell` 进程承载。

## 当前判断

可以迁移，但不建议一次性把 `CommitBall.exe` 全部改成 C#。

当前 C++ 主进程已经稳定承担这些底层职责：

- 全局键鼠 hook 和记录状态切换。
- SQLite 写入、live 文本刷新、session split、归档触发。
- Bar 和 Agent 进程启动、状态读取、pipe 通信。
- 退出保护、数据 flush、job object、兜底终止。
- Weasel 桥接 pipe 和直达指令 pipe。

这些能力属于 Core，不应该在 UI 迁移第一阶段重写。第一阶段只替换悬浮球 UI。

## 现有耦合点

当前悬浮球 UI 主要集中在 `commitball/ball.hpp`：

- `BallInit` 创建 layered/topmost/noactivate 悬浮球窗口。
- `RedrawBall` 使用 GDI+ 绘制球、图标和眼睛动画。
- `ShowBallBubble` 绘制气泡窗口。
- `BallWndProc` 处理右键菜单、拖动吸边、pipe 消息和 timer。
- `OnStateChanged` 从 `main.cpp` 直接修改球颜色、动画 timer、吸边状态。

当前 Core 状态和命令主要集中在 `commitball/main.cpp` 与 `commitball/recorder.hpp`：

- `g_state`：记录/停止。
- `g_noAdmin`：权限状态。
- `g_eyeModeEnabled`：眼睛模式。
- `GetDbInfoText`、`GetBarStatusText`、`GetAgentStatusText`：菜单状态。
- `SendShowLockedToBar`、`SendShowToAgent`、`InvokeAgentAnalyse`、`FastCommitBallExit`：菜单命令。
- `ProcessDirectCommand`：Bar/Agent 发来的控制命令和气泡。

这说明迁移边界已经比较清楚：UI 层要拿状态、显示状态、发命令；Core 继续执行真实动作。

## 推荐目标结构

```text
CommitBall.exe
  C++ Core
  负责 hook、记录、SQLite、Bar/Agent、归档、退出保护
  不再负责悬浮球绘制、皮肤、气泡样式和菜单布局

CommitBall-BallShell.exe
  C# WPF UI
  负责悬浮球窗口、动画、皮肤、气泡、右键菜单、拖动吸边
  不直接写数据库，不直接管理 Bar/Agent 进程，不安装 hook

CommitBall-BallUiLab
  继续作为 UI 实验台和虚拟后端测试程序
```

为了复用原型，建议后续把 `CommitBall-BallUiLab` 拆成两个项目：

```text
CommitBall.BallUi/
  BallContracts.cs
  BallSkins.cs
  BasicBallRenderer.cs
  SpringAnimator.cs
  皮肤资源

CommitBall-BallShell/
  BallWindow.xaml
  NamedPipeBallBackend.cs
  BallHostCommandsPipe.cs
  本地皮肤选择配置

CommitBall-BallUiLab/
  只保留实验窗口、虚拟后端和 smoke test
  引用 CommitBall.BallUi
```

如果想减少第一步工程量，也可以先不拆库，直接从实验项目复制一份 `CommitBall-BallShell`，跑通后再整理为共享库。

## Core 到 BallShell 的协议

第一版建议使用 UTF-8 JSON 行协议。C++ Core 创建 `CommitBall-BallShell`，然后通过 pipe 推送状态。

Core 推给 UI：

```text
STATE {"mode":"recording","eyeEnabled":true,"isMouseIdle":false}
BUBBLE {"text":"归档分析完成"}
CLEAR_BUBBLE {}
STATUS {"recording":"记录","db":"128KB / 512KB","bar":"后台就绪","agent":"空闲"}
SHUTDOWN {}
```

说明：

- `STATE` 只传运行状态，不传皮肤。
- `BUBBLE` 只传文本，不传颜色和位置。
- `STATUS` 用于右键菜单打开时显示状态，也可以由 UI 主动请求。
- `SHUTDOWN` 用于 Core 退出前通知 UI 保存窗口位置并退出。

## BallShell 到 Core 的协议

UI 发给 Core：

```text
COMMAND {"name":"open_data_directory"}
COMMAND {"name":"open_live_text"}
COMMAND {"name":"open_bar_locked"}
COMMAND {"name":"open_agent"}
COMMAND {"name":"invoke_agent_analysis"}
COMMAND {"name":"exit_commitball"}
REQUEST_STATUS {}
WINDOW_STATE {"x":1280,"y":860,"edge":"right","visible":true}
```

说明：

- 菜单动作仍由 Core 执行。
- 皮肤切换不进协议，只存在于 BallShell 本地。
- 记录开始/停止不进协议，仍由 Core 的输入识别逻辑负责。
- 眼睛模式不由 UI 设置，UI 只接收 `eyeEnabled`。

## 第一阶段迁移步骤

1. 新增 `CommitBall-BallShell` WPF 项目。
   - 复用实验项目里的 contracts、renderer、skins、animator 和 assets。
   - 删除实验台按钮、虚拟后端控制区和调试网格。
   - 实现真实透明悬浮窗：topmost、toolwindow、noactivate、透明背景。

2. 在 C++ Core 中新增 BallShell 进程管理。
   - 类似 `LaunchBar` / `LaunchAgent`，新增 `LaunchBallShell`。
   - 加入 job object，保证 Core 退出时 BallShell 一起退出。
   - 启动参数带 `--parent-pid`，BallShell 也监听父进程退出。

3. 新增 Core 和 BallShell pipe。
   - C++ Core 保留状态源，提供 `SendBallState`、`SendBallBubble`、`SendBallStatus`、`SendBallShutdown`。
   - BallShell 实现 `NamedPipeBallBackend`，接收状态并更新窗口。
   - BallShell 菜单通过 pipe 发回 `COMMAND`。

4. 做旁路运行开关。
   - 第一版保留 C++ GDI 悬浮球。
   - 加一个编译开关或配置 `use_ball_shell`。
   - 开启时隐藏或不创建 C++ 悬浮球窗口，只启动 BallShell。
   - 关闭时维持当前逻辑，便于回退。

5. 转移 UI 行为。
   - 右键菜单迁到 BallShell。
   - 气泡显示迁到 BallShell。
   - 拖动、吸边、位置保存迁到 BallShell。
   - Core 只接收 `WINDOW_STATE`，是否写入配置由 Core 决定。

6. 更新构建和安装包。
   - `build-installer.ps1` 发布 `CommitBall-BallShell.exe`。
   - NSIS 安装 `CommitBall-BallShell.exe` 和 `Assets/Skins/**`。
   - 卸载时删除 BallShell 和资源目录。

## 必须保留在 Core 的逻辑

这些逻辑不迁到 UI：

- CapsLock 四连击记录开关。
- Bar 唤醒序列识别。
- 全局键鼠监听。
- away/back 记录。
- SQLite、live.txt、session split、exports、meta 生成。
- Agent/Bar 启动和真实命令执行。
- 退出前 flush、checkpoint、job object 和兜底 taskkill。
- 管理员权限判断。

## 第一阶段验收标准

- 启动 CommitBall 后只看到 C# 悬浮球，C++ GDI 球不显示。
- 记录开始/停止后，C# 球状态正确变化。
- 眼睛模式开关后，C# 球动画正确启停。
- 鼠标空闲随机游走、追踪鼠标、点击半眯眼、吸边半隐藏都正常。
- 右键菜单状态能显示记录、db、Bar、Agent。
- 右键菜单命令能打开数据目录、live 文本、Bar、Agent，并能触发 Agent 分析和退出。
- Agent `show_ball_bubble` 触发的气泡显示在 C# 球旁边，且不越出屏幕。
- 退出 CommitBall 后 BallShell、Bar、Agent 都退出。
- 关闭 `use_ball_shell` 后能回到当前 C++ 悬浮球，便于排错。

## 风险点

- WPF layered/noactivate/topmost 窗口的鼠标命中和拖动行为需要实机验证。
- 多显示器和 DPI 缩放下的吸边坐标需要单独测试。
- C# UI 的启动速度可能比 GDI 球慢，Core 启动时要允许短暂未连接。
- pipe 重连要可靠，BallShell 崩溃后 Core 应能重启它。
- 安装包需要复制皮肤资源，特别是 GIF/PNG 不能漏。
- 退出路径必须保持当前强保护逻辑，不能因为等待 UI 正常退出导致 Core 卡住。

## 建议下一步

下一步先做“旁路 BallShell”：

1. 新建 `CommitBall/commitball-ball-shell` 项目。
2. 从实验项目复制 UI 层代码和素材。
3. 实现一个最小 pipe 后端，只支持 `STATE`、`BUBBLE`、`COMMAND`。
4. Core 启动 BallShell，但默认仍保留 C++ 球。
5. 用配置或编译开关切换到 BallShell。

这样可以先验证窗口、pipe、菜单和退出链路，再决定是否删除 C++ GDI 悬浮球代码。
