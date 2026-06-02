# CommitBall Frontend Design

## 悬浮球窗口

- 80x80px 分层窗口（WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE）
- GDI+ 绘制：36px 半径圆形，2.5px 白色边框，中心符号，PixelOffsetModeHighQuality
- 圆内左键拖拽（WM_NCHITTEST → HTCAPTION），圆外点击穿透（HTNOWHERE）
- 三角形 ▶ 绘制时右移 3px 补偿视觉偏移

## 状态显示

- STOPPED：蓝 #3B82F6 + ⏸（U+23F8）
- RECORDING：红 #EF4444 + ▶（U+25B6）
- 符号用 Segoe UI Symbol 28px + DrawString，居中对齐
- 录制开始时颜色渐变动画（AnimateColor）

## 右键菜单

- WM_NCRBUTTONUP 触发（HTCAPTION 区域的右键事件）
- SetForegroundWindow + TrackPopupMenu + PostMessage(WM_NULL) 确保菜单可关闭
- 「写入 txt」→ FlushLiveBuffer()
- 「打开数据路径」→ ShellExecuteA 打开 data/ 目录
- 「退出 CommitBall」→ PostMessage(WM_CLOSE) → WM_DESTROY → PostQuitMessage(0)
- 录制切换只能四击 CapsLock，右键菜单不提供此功能

## 边缘吸附

- WM_EXITSIZEMOVE 时检测四边距离，<20px（SNAP_THRESHOLD）则吸附
- 吸附后球半露在屏幕边缘，窗口坐标 = -BALL_RADIUS 或 screen - BALL_RADIUS
- 录制开始时 UnsnapForRecording（向内移动 BALL_RADIUS），停止时 ApplySnappedEdge（回弹）
- 吸附状态（x, y, edge）持久化到 commitball.pos

## 位置持久化

- 启动时读取 commitball.pos（文本：`x y edge`），无则默认右下角
- WM_CLOSE（退出时）写入当前坐标和吸附边

## 单例

- CreateMutexW("CommitBallMutex")，ERROR_ALREADY_EXISTS 时直接退出

## 键盘 Hook

- WH_KEYBOARD_LL 安装在主线程，需管理员权限运行
- 四击 CapsLock（VK_CAPITAL，500ms 间隔）切换 STOPPED/RECORDING
- 序列匹配：滑动窗口 8 次按键历史，匹配最近 4 次是否为 CapsLock 且间隔 ≤ 500ms

## 焦点追踪

- 每 400ms 检测焦点变化（CheckFocusTimer）
- 焦点变化时写 focus 事件（窗口标题|进程名）
- 不变时计数，150 次（60s）写哑变更
- 每次 INSERT 前也检测焦点变化（CheckFocusChange）
- 录制开始时立即写 focus 事件
- 进程名获取失败降级为 [unknown]，标题截断 128 字符

## Debug 日志

- Log() 写入 data/log/commitball.log，带时间戳，1000 行上限（超限清空）
- 记录：启动、状态切换、焦点变化

## 无控制台

- WinMain 入口，/SUBSYSTEM:WINDOWS
- 链接：user32.lib gdi32.lib gdiplus.lib shcore.lib advapi32.lib psapi.lib shell32.lib

## DPI 感知

- SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)，避免高清屏幕模糊
- 提供 GetDpiScale() 辅助函数（96dpi 基准），可按需动态缩放尺寸
