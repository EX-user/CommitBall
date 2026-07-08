# Clauses

*实验性项目*
条款是一组相互联系的陈述. 条款间组成一个偏序, 包含了从最抽象的产品理念到部分关键细节.
从意图上说, 条款偏向用户视角, 而非系统内部视角. 条款是容易判定真伪的短句子.
我们期望条款成为vibe coding过程中传达完整设计意图和追踪项目演变的面板.

## 文件类型

| 后缀 | 角色 | 编辑者 |
|------|------|--------|
| `.json` | 源文件 (人类可读) | 人工编辑 |
| `.clause` | 生成文件 (哈希索引) | `gen_clauses.py` 自动生成 |
| `.py` | 脚本 | 人工编辑 |

## JSON 源文件格式 (`.json`)

每个文件是一个 JSON 数组, 每个条款包含三个字段:

```json
{
  "title": "SYS_ROOT",
  "accordance": ["SYS_MULTIPROC"],
  "content": "用中文描述一个可判定的设计预期."
}
```

- **title**: 人类可读的标识 (如 `REC_TOGGLE`). 用于跨文件引用.
- **accordance**: 本条款依赖的父条款 title 列表. 空列表表示根条款.
- **content**: 描述一个可判定的设计预期的文本. 使用英文标点.

规则:
- title 在所有 `.json` 文件中必须唯一.
- 同名 title 对应不同 content 是错误.
- accordance 引用图必须是 DAG (无环).
- 所有 accordance 引用必须指向已存在的 title (无悬空引用).

## 生成文件格式 (`.clause`)

由 `gen_clauses.py` 生成, 是源文件的逐项拓展:

```json
{
  "cuid": "27f25001",
  "accordance": ["SYS_ROOT"],
  "accordance-cuid": ["daf6da0b"],
  "content": "..."
}
```

- **cuid**: 条款的唯一不变编号. 当条款内容发生变化时, cuid也需发生变化. 当前 由内容文本通过 SHA-1 取前 8 位得到.
- **accordance**: 原始 title 列表 (从源文件保留).
- **accordance-cuid**: 解析后的 cuid 列表 (用于图验证).
- **content**: 与源文件相同.
*title和cuid都是clause的指针, 但title不随着内容的变化而变化; cuid确保指向确定性的条款内容.*

`.clause` 是 `.json` 的拓展: 源文件的所有字段原样保留, 另加 `cuid` 和 `accordance-cuid`.

## 脚本

### `clauses_gen.py`

将 title 解析为 cuid, 写入 `.clause` 文件.

```
python Clauses/clauses_gen.py                    # 处理所有文件
python Clauses/clauses_gen.py 01-core.json       # 处理指定文件
python Clauses/clauses_gen.py --check            # 仅解析, 不写入
```

行为:
1. 加载**所有** `.json` 文件 ( 从而支持跨文件 title 替换).
2. 通过 SHA-1(content)[:8] 构建 title → cuid 映射.
3. 整理源 `.json` 文件格式.
4. 写入 `.clause` 文件 (含 cuid 和 accordance-cuid).

对外暴露 `load_and_resolve()` 函数, 供其他脚本调用.

### `clauses_check.py`

验证条款图的合法性. 通过 `import clauses_gen` 获取解析数据 (无重复逻辑).

```
python Clauses/clauses_check.py                  # 仅验证
python Clauses/clauses_check.py -g               # 先生成再验证
```

检查项:
1. **DAG**: accordance 引用图无环.
2. **悬空引用**: 所有 accordance-cuid 引用指向已存在的 cuid.
3. **根/叶报告**: 列出根条款 (无父依赖) 和叶条款 (无子引用).

验证通过后自动调用 `clauses_plot.py` 生成依赖图 PNG.

### `clauses_plot.py`

以条款引用关系画图. 输出 `clauses.png` (DPI 200).

```
python Clauses/clauses_plot.py                   # 画所有条款
python Clauses/clauses_plot.py 01-core.clause    # 画指定文件
```

*需要 graphviz (`winget install graphviz`).*

### `verify_prompt.md`

条款判定提示词. 将此提示词与目标条款文件一起提供给 AI agent, 可逐条验证 content 与代码的一致性.

## 工作流

1. 编辑 `.json` 源文件 (增删改条款).
2. 运行 `clauses_check.py -g` 生成 `.clause` 文件并验证结构合法性.
3. 使用 `verify_prompt.md` 配合 AI agent 逐条验证 content 与代码的一致性.
4. 提交 `.json` 和 `clause_file/` 文件.
