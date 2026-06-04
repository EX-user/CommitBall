# TODO

- [x] 添加粘贴时放入实际粘贴内容的特性，粘贴内容上限为400字，超出则保留头尾。
- [x] 开发悬浮球（已完成：GDI+ 绘制、拖拽、边缘吸附、右键菜单、单例）
- [x] 设计数据记录文件夹布局（已完成：data/db, data/sessions, data/exports, data/live, data/log）
- [x] 添加焦点位置探测
- [x] 定期归档/清理数据库（已完成：512KB 阈值自动拆分 session，DbToText 导出）
- [x] 不能编译出weasel，需要确保编译出"modified-weasel"避免冲突（已完成：cb-weasel 改名，新 GUID、注册表路径、DLL 名）
- [x] 确定打包方案（已完成：NSIS 安装包，installer\commitball.nsi + build-installer.ps1）
- [x] 添加鼠标点击事件的监听，只监听按钮点击事件
- [x] 悬浮球状态添加当前db大小和分裂进度百分比
- [x] 键入激活序列弹出输入栏(bar)
- [x] 悬浮球右键菜单添加「帮助」按钮，点击弹出对话框解释功能
- [x] db-split时保留最新的一小部分
- [x] 添加timer事件，每10分钟发生一次，无额外消息
- [x] 每次session超过1h时停止，后续记录分裂为新session
- [x] 悬浮球右键菜单加一个按钮，效果是点击后用记事本打开live/live.txt；如果没有记事本就用默认txt打开方式打开。
- [ ] 给 bar 进程状态加上检测和用户端显示（崩溃重启、状态提示等）
- [ ] bar 唤醒序列配置功能（当前硬编码 `\ccb`，需改为 UI 可配置）
- [x] bar 加锁定按钮，锁定时 Enter 不关闭窗口，便于反复使用
- [x] 去掉 agent 硬编码的 API key，改为配置文件或环境变量
- [x] CommitBall 添加对 Agent 的 invoke 能力（主动发消息给 agent 执行任务）
- [x] Bar 添加显示能力（展示 agent 回复、工具结果等）
- [ ] Agent 终端添加文本超链接功能
- [x] Bar 非锁定状态下，用户点击其他应用窗口时自动隐藏
- [ ] 悬浮球右键菜单加按钮：打开 Bar 并上锁

# 大型需求
- [x] 设计commitbal的mini-agent架构，明确最小支持的tool calling集合
- [x] 用不超过600行代码完成核心的agent loop
- [x] 为其设计解耦的前端，只需要能由悬浮球拉起即可

# 待评阅需求
- [ ] 悬浮球录制状态下添加"眨眼"效果
- [ ] 用c-sharp重构commitball