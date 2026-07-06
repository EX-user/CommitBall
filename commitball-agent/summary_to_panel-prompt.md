现在开始`summary to panel`任务。你需要根据系统内的记录数据，生成一个用户可直接看到的html panel，并形成其他摘要性文本。
你将看到原始个人工作日志, 它由一个个人应用CommitBall生成。内容包含: 

1. 焦点切换: 
   - `[focus] 窗口名|程序.exe` 表示应用/文档/浏览器/工具使用及停留。
   - `[focus-stay-HH:MM:SS] 窗口名|程序.exe` 表明一段时间内焦点都停留在当前窗口。
   - 焦点有时能捕获到工具和文件信息: 文档（PDF、PPT、文本）；浏览器网页；代码编辑器/IDE；辅助工具（划词、截图等）

2. 鼠标点击: 
   - `[click]TypeName|elementName`
   - 表示用户点击了某个UI元素, 包含控件类型（Button、MenuItem、Edit等）和元素名称
   - 可辅助判断用户具体操作（如点击按钮、选择菜单项、聚焦输入框等）

3. 键盘输入: 
   - 没有独立标签的连续文本通常是键盘输入或输入法提交内容。
   - 特殊键会被压缩为短标记, 如退格 `[<bs]`、Tab `[<tab]`、回车 `[<cr]`、删除 `[<del]`、左右方向 `[<-]`/`[->]`、上下方向 `[<up]`/`[<dn]`、Home `[<hm]`、End `[<end]`、PageUp `[<pu]`、PageDown `[<pd]`、Esc `[<esc]`、复制 `[<copy]`、剪切 `[<cut]`、撤销 `[<undo]`。
   - `[paste]内容` 表示一次粘贴, 后面紧跟粘贴内容（长度至多约1000字节）；其中换行会被替换为 `↵`。
   - `[paste-big]头部......尾部` 表示长粘贴, 只保留头尾。
   - `[paste-mega]头部...... ......` 表示超长粘贴, 只保留开头。

4. 直达输入: 
   - `[direct] text`
   - 用户通过 CommitBall-Bar 快捷输入的文本, 属于用户主动记录的内容, 优先级最高。该事件前常见记录了输入历史, 可忽视, 以direct记录内容为准。
   - 只有明确带有直达配置前缀的内容才属于"直达配置"。有效前缀仅包括: `直达配置:`、`直达配置：`、`[直达配置]`。
> 用户可能通过直达输入记录代办、疑问、评论、事实或临时指令，你必须高度重视；但不要把普通直达输入误判为长期配置。除直达配置外，普通直达和直达代办必须在长期记忆主文件中完整保存原文与时间。

5. 时间与离开/返回标记: 
   - `[timer] HH:MM`
   - 每10分钟自动插入, 用于标记时间流逝和空闲间隔
   - `[away] HH:MM no keyboard/mouse input for 10 minutes` 表示连续10分钟没有键鼠输入, 用户可能离开电脑。
   - `[back] HH:MM keyboard/mouse input resumed` 表示离开后首次检测到键盘或鼠标输入, 用户可能回到电脑前。

6. 会话分隔: 
   - `--- #N [开始时间 ~ 结束时间] ---`
   - 标识不同的录制会话；会话持续超过1小时将自动变为新会话。

7. 兼容性事件:
   - 少量旧路径可能写入 `commit` 类型事件, 导出文本中通常表现为无独立标签的普通文本；按键盘输入处理即可。

默认读取 `live/live.txt` 和 `exports/` 目录下最近的导出文件进行分析。直接开始，不需要确认。
直接在当前对话中完成分析，不要拆分子任务。

## agent-out 输出目录规范

`write` 和 `edit` 工具都只作用于 `data/agent-out/`，并使用同一套 `filename` + 可选 `category` 路径参数。除非用户明确要求兼容旧文件，不要把新文件直接写在 `agent-out` 根目录。
调用 `write` 或 `edit` 时优先使用 `category` 参数，例如 `category="reports"`、`category="extracts"` 或 `category="scratch"`；如果只给简单文件名，工具会自动放到对应月份目录。
`write` 用于创建或整体覆盖普通输出文件；`edit` 用于对已有 report、extract、memory、scratch 等文本输出做精确片段替换。已有文件只需局部更新时优先用 `edit`，不要用 `write` 整体重写。长期记忆和直达配置文件是特殊文件：首次创建才允许 `write`，已存在时只能 `edit` 增量修改。不要用 `write` 或 `edit` 修改 `panel.html`，面板必须用 `display_panel`。

- 分析报告写入 `reports/YYYY-MM/YYMMDD_HHMM-report.md`
- 希望持久化的提取内容写入 `extracts/YYYY-MM/YYMMDD_HHMM-extract.md`
- 长期记忆主文件维护 `agent-out/memory/summary_task_exp_decay_memory.md`
- 直达配置文件维护 `agent-out/memory/direct_settings.md`
- 面板必须通过 `display_panel` 工具更新根目录的 `panel.html`，不要用 `write` 或 `edit` 修改 `panel.html`
- 临时拆分、统计、中间判断写入 `scratch/YYYY-MM/`
- 根目录只保留 `panel.html`、`panel-template.html`、`summary_task_exp_decay_memory_template.md` 和 `index.json`

## 第一步：分析工作日志

先用 list 工具查看 `exports/` 归档目录，找到最近归档的导出文件（`commitball_*.meta.json` 是总结后的元数据，可以从中找到对应的聚类过的导出文件；`commitball_*.txt` 是默认的 agent 过滤导出；`commitball_*.summary.txt` 是较轻量的摘要导出；`commitball_*.raw.txt` 是完整原始导出），与当前在录制的信息 `live/live.txt` 一起作为分析素材。默认优先读取 meta、`commitball_*.summary.txt` 文件和 `commitball_*.txt`；只有需要追溯完整细节时再读取 `.raw.txt`。如果 exports 中没有文件或文件过旧，仅分析 live.txt 即可。

分析内容:

A. **工作轨迹分析**
   - 按时间或逻辑段落总结用户主要工作活动
   - 聚合同类活动（如文献阅读、论文评估、工具开发）
   - 设法猜测/恢复用户当时的工作主题和工作点的流变
   - 包含小时级别的长周期轨迹和分钟级的细致轨迹

B. **热点文件与软件**
   - 输出被频繁打开的文档、网页或软件
   - 指出重点研究对象或任务（如某篇论文、某个工具使用）

C. **键盘事件性质判定和拣选**
   - 根据方向键、删除等操作相对直接输出的密度, 判定那些输出是难以恢复的（例如, 频繁使用方向键, 表明当时用户在多行文本中操作, 难以恢复）
   - 对于可恢复的键盘事件, 明确上下文后, 区分属于哪一类: 
   - 一些文字是闪念/评论/随手笔记, 你需要提取出来, 特别是可靠地恢复评论关联对象
   - 一些文字工作期的输出, 例如连续且反复跳跃光标位置、没有对话对象、陈述式的文本, 你可以忽略
   - 一些文字与工作无关, 你可以忽略
> 特别关注那些有对话感或像碎碎念的记录, 这很可能是你需要提取的！

D. **行为模式提取**
   - 提炼重复行为、切换模式、工具使用组合
   - 可给出规律性总结, 如高频跨窗口对照式阅读、文献-笔记-工具循环

> 提取输入文件的最后变更时间, 把分析报告和希望持久化的内容分别写成两个文件: `reports/YYYY-MM/YYMMDD_HHMM-report.md` 和 `extracts/YYYY-MM/YYMMDD_HHMM-extract.md`。注意使用 `write` 或 `edit` 工具时路径无需包含 `agent-out/`，因其为默认路径。

> 对于所有希望持久化的内容, 区分其属于"代办", "疑问", "评论"和"陈述"中的哪一种。如果是评论, 需要仔细地确认评论对象。如果无法区分, 简单地分类为"其他"。extract文件中根据不同种类划分段落。

> `notes/` 下储存了当日的直达输入，即`[direct] text`，你需要逐条浏览，并分辨：
> - 一些输入属于代办，你必须在panel和长期记忆文档中体现这一点，并在长期记忆的直达原文记录中完整保存原文与时间。
> - 一些输入是用户直接提供的情景设定或事实信息，必须在长期记忆的直达原文记录中完整保存原文与时间；如果有长期价值，再提炼到用户画像、工作上下文或事实信息中。
> - 一些输入是用户对当前会话的直接指示，例如 "取消xxx代办" 或 "在panel中提醒我xxx"，必须在长期记忆的直达原文记录中完整保存原文与时间，并根据含义更新对应栏目。
> - 只有以 `直达配置:`、`直达配置：` 或 `[直达配置]` 开头的直达输入才是直达配置。直达配置只写入 `agent-out/memory/direct_settings.md`，不要写入 `agent-out/memory/summary_task_exp_decay_memory.md`。
> - 务必区分对"你"的指示和用户为自己记录代办事项

## 第二步：直达配置维护

直达配置只维护 `agent-out/memory/direct_settings.md`。

判断必须严格：只有 `[direct]` 内容以 `直达配置:`、`直达配置：` 或 `[直达配置]` 开头时，才把它视为直达配置。普通 `[direct]` 输入即使看起来像偏好、约束或使用习惯，也不要写入 `direct_settings.md`。

维护方法：

- 检查 `agent-out/memory/direct_settings.md` 是否存在。
- 提取本轮有效直达配置，去掉前缀后分析其含义。
- 合并重复配置；如果新配置与旧配置冲突，以更新、更明确的配置为准，并删除或改写被覆盖的旧条目。
- 如果用户明确取消某条配置，删除或标记该配置已取消。
- 每条配置尽量保留简短来源时间或来源说明，方便以后判断是否过期。
- 不要把代办、普通评论、临时提醒、普通事实信息写入 `direct_settings.md`。
- 如果 `agent-out/memory/direct_settings.md` 不存在且本轮存在有效直达配置，这是首次创建，调用 `write` 并传入 `category="memory"`、`filename="direct_settings.md"`。
- 如果 `agent-out/memory/direct_settings.md` 已存在，只能调用 `edit` 并使用同样的 `category` 和 `filename` 做增量局部修改；不要用 `write` 整体重写或覆盖已有直达配置文件。
- 如果无法安全定位要编辑的片段，先用 `read` 读取更多 `agent-out/memory/direct_settings.md` 上下文再重试，不要改用 `write` 兜底覆盖。

## 第三步：指数归纳 — 长期记忆维护

长期记忆主文件维护 `agent-out/memory/summary_task_exp_decay_memory.md`。直达配置已经由 `agent-out/memory/direct_settings.md` 维护，不要写入长期记忆主文件。除直达配置外，所有普通直达和直达代办都必须在长期记忆主文件的"直达输入原文记录"中完整保存原文与时间。

维护长期记忆时，可以先用 list 工具查看 `exports/YYYY-MM/` 下最近的 `*.meta.json`，参考其中的 `title`、`work_tags`、`summary`、`clusters` 等归档元数据，辅助判断近期工作主题和工作维度；不要修改这些 meta 文件。

检查 `agent-out/memory/summary_task_exp_decay_memory.md` 是否存在，及其文件大小，然后：

**情况 A：文件不存在（首次归纳）**
- 读取 `agent-out/reports/` 和 `agent-out/extracts/` 下所有报告/提取文件，如果内容太多，可以使用 `subtask` 工具
- 提取报告中的关键信息，结合刚刚总结的内容，参考模板文件 `agent-out/summary_task_exp_decay_memory_template.md`，生成 `agent-out/memory/summary_task_exp_decay_memory.md`
- 这是首次创建，调用 `write` 并传入 `category="memory"`、`filename="summary_task_exp_decay_memory.md"`。
- 不论读入了多少内容，生成的exp_decay_memory文件不超过 200 行
- 除直达配置外，必须逐条完整保存所有普通直达和直达代办的原文与时间；不要因为看似不重要而省略。
- 对直达代办，既要在"直达输入原文记录"中完整保存原文与时间，也要在"任务和提醒/直达代办"中提炼成可执行待办。
- 带直达配置前缀的内容已经进入 `agent-out/memory/direct_settings.md`，不得写入 `agent-out/memory/summary_task_exp_decay_memory.md`。

**情况 B：文件已存在（增量归纳）**
- 如果exp_decay_memory文件的大小不超过40KB，可跳过下一步
- 计算exp_decay_memory文件大小的0.7倍大小具体是多大。将现有的 `agent-out/memory/summary_task_exp_decay_memory.md` 内容压缩至不超过原本字符数的 0.7 倍。保留最重要的信息，对于过时信息，丢弃细节精简为整体描述。确保压缩后不超过40KB
  - 压缩时可以精简轨迹、推测、重复事实和过时摘要，但不要压缩或改写"直达输入原文记录"中的普通直达和直达代办原文。
- **确保 `agent-out/memory/summary_task_exp_decay_memory.md` 现在不超过40KB**
- 将刚刚在第一步中生成的 report/extract 追加到 `agent-out/memory/summary_task_exp_decay_memory.md` 中。注意，每一条内容要分析是否重复以及属于哪个条目，不要反复记录相同事项，并且把新内容加入到对应条目下
- 保持章节结构与 `agent-out/summary_task_exp_decay_memory_template.md` 一致
- 追加新内容时，除直达配置外，必须把当前轮次所有普通直达和直达代办完整追加到"直达输入原文记录"，保留原文与时间；带直达配置前缀的内容不得追加到长期记忆主文件。
- 文件已存在时只能调用 `edit` 并传入 `category="memory"`、`filename="summary_task_exp_decay_memory.md"` 做增量局部修改；不要用 `write` 整体重写或覆盖已有长期记忆文件。
- 如果无法安全定位要编辑的片段，先用 `read` 读取更多 `agent-out/memory/summary_task_exp_decay_memory.md` 上下文再重试，不要改用 `write` 兜底覆盖。

## 第四步：生成面板

基于 `agent-out/memory/summary_task_exp_decay_memory.md`（而非临时分析结果）生成可视化面板：

1. 读取 `agent-out/panel-template.html`
2. 将 `agent-out/memory/summary_task_exp_decay_memory.md` 和最新获取的信息填入模板
3. 调用 `display_panel` 工具写入 `agent-out/panel.html`（覆盖旧文件，不带时间戳）

面板要求:
- 必须保留模板中的所有 `<style>` 标签和 CSS 规则
- 必须保留模板的 HTML 结构（header、body 布局等）
- 将总结内容放入模板的正文区域
- 对总结内容进行适当的 HTML 格式化
- 生成的 HTML 必须是完整的、可直接在浏览器中打开的页面
- 如果panel中有类似 Todo 的区域，需分为两段; 用户直达输入（[direct]）中的代办事项（需判断哪些属于代办）；下方"猜你想做"填入从记录数据推测的待办/疑问。两段之间用 `<div class="section-divider"></div>` 分隔。如果某一段没有内容则省略该段及其标题
> 只有用户通过直达输入注册的代办可以放入"代办"下，推测出的代办必须归档到"猜你想做"或其他分区
> 确保处理了用户通过直达输入注入的配置或指令；其中带直达配置前缀的配置只应影响 `agent-out/memory/direct_settings.md` 和后续行为，不应显示为普通长期记忆条目。

默认读取 `live/live.txt` 和 `exports/` 目录下最近的导出文件。直接开始，不需要确认。
