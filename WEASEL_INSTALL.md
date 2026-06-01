# CB-Weasel 安装与卸载

> 基于 x64-only 构建（无 Win32），不使用 WeaselSetup.exe。
> 已改名为 CB-Weasel，可与官方 Weasel 共存（独立注册表路径、TSF CLSID、DLL 名）。

## 安装前置条件

> 以下检查全部通过后再执行安装步骤。任何一项失败都需回到 WEASEL_BUILD.md 修复。

```powershell
Test-Path weasel\output\cb-weaselx64.dll            # 构建产物（注意 cb- 前缀）
Test-Path weasel\output\data\opencc\TSCharacters.ocd2  # OpenCC（缺少→繁体）
Test-Path weasel\output\data\default.yaml         # 输入方案
```

## 安装

需要管理员权限。

### 1. 复制 DLL

```powershell
Copy-Item weasel\output\cb-weaselx64.dll C:\Windows\System32\cb-weasel.dll -Force
```

### 2. 注册 TSF 文本服务

```powershell
regsvr32 /s weasel\output\cb-weaselx64.dll
```

无弹窗，静默注册。TSF 注册路径指向构建目录（非 System32），这是预期行为——两个 DLL 内容相同。

如果需要验证注册是否成功：

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\{1DAC3806-5705-46F1-A305-7066F9663F07}\InprocServer32" -ErrorAction SilentlyContinue
```

### 3. 设置注册表

```powershell
# 替换为实际的 output 目录绝对路径
$weaselOutput = (Resolve-Path weasel\output).Path

reg add "HKLM\SOFTWARE\WOW6432Node\Rime\CBWeasel" /v WeaselRoot /t REG_SZ /d "$weaselOutput" /f
reg add "HKLM\SOFTWARE\WOW6432Node\Rime\CBWeasel" /v ServerExecutable /t REG_SZ /d "WeaselServer.exe" /f
```

### 4. 设置默认简体中文

默认方案 `luna_pinyin` 输出繁体。修改 `output/data/default.yaml`，将 `luna_pinyin_simp` 放在 `schema_list` 第一位，并移除不存在的 `quick5`：

```yaml
schema_list:
  - schema: luna_pinyin_simp
  - schema: luna_pinyin
  - schema: bopomofo
  - schema: cangjie5
  - schema: stroke
  - schema: terra_pinyin
```

### 5. 启动 WeaselServer

```powershell
Start-Process weasel\output\WeaselServer.exe
```

### 6. 等待词典编译

启动后 Rime 引擎会自动编译词典，通常需要 10-30 秒。验证：

```powershell
Start-Sleep -Seconds 15
Get-ChildItem "$env:APPDATA\Rime\build\*.table.bin" -ErrorAction SilentlyContinue
```

看到 `luna_pinyin.table.bin` 等文件即表示编译完成。

### 7. 添加输入法

Windows 设置 → 时间和语言 → 语言和区域 → 中文旁边的 ⋯ → 语言选项 → 添加键盘 → 找到"CB-Weasel" → 添加。

用 `Win+Space` 切换输入法。

## 卸载

```powershell
# 1. 停止服务
Stop-Process -Name "WeaselServer" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2. 取消注册 TSF
regsvr32 /u weasel\output\cb-weaselx64.dll

# 3. 删除 DLL
Remove-Item C:\Windows\System32\cb-weasel.dll -Force

# 4. 清理注册表
Remove-Item -Path "HKLM:\SOFTWARE\WOW6432Node\Rime\CBWeasel" -Recurse -Force -ErrorAction SilentlyContinue

# 5. 清理 TSF TIP 注册（regsvr32 /u 不会清理此项）
Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\CTF\TIP\{1DAC3806-5705-46F1-A305-7066F9663F07}" -Recurse -Force -ErrorAction SilentlyContinue

# 6. 删除用户数据（必须，否则重装后词典不会重新编译）
Remove-Item "$env:APPDATA\Rime" -Recurse -Force -ErrorAction SilentlyContinue
```

**重要：** 卸载时必须删除 `$env:APPDATA\Rime` 用户目录。否则重装后 Rime 引擎会使用旧的缓存数据，导致词典不会重新编译，输入法无法正常工作。

## 验证安装

```powershell
# WeaselServer 是否运行
Get-Process -Name "WeaselServer" -ErrorAction SilentlyContinue

# 注册表是否正确
Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Rime\CBWeasel" -ErrorAction SilentlyContinue

# DLL 是否存在
Test-Path C:\Windows\System32\cb-weasel.dll

# 词典是否编译
Get-ChildItem "$env:APPDATA\Rime\build\*.table.bin" -ErrorAction SilentlyContinue
```

## 日志

```
%TEMP%\rime.cb-weasel\*.log
```

- `*.ERROR.*` — 错误（如 OpenCC 数据缺失、方案不存在）
- `*.WARNING.*` — 警告
- `*.INFO.*` — 详细日志

排查步骤：先看 `*.ERROR.*`，再看 `*.WARNING.*`。日志文件名含进程名、时间戳和 PID，例如 `rime.cb-weasel.MECH.user.log.ERROR.20260601-122730.35372.log`。

## 常见问题

| 现象 | 原因 | 解决 |
|------|------|------|
| 看不到"CB-Weasel"键盘 | 未添加输入法 | Windows 设置中添加 |
| 输出繁体 | 步骤 4 未修改 default.yaml | 按步骤 4 设置 luna_pinyin_simp |
| 输出繁体 | 缺少 OpenCC 数据 | 见 WEASEL_BUILD.md 步骤 10 |
| 输出繁体 | 修改了 `$env:APPDATA\Rime\default.yaml` 而非 `output/data/default.yaml` | 确认修改的是 `output/data` 下的 |
| 打字无候选词 | 词典未编译或旧缓存冲突 | 卸载→删除用户目录→重装 |
| regsvr32 失败 | 未以管理员运行 | 用管理员权限的终端执行 |
| DLL 被锁定无法删除 | 有进程加载了该 DLL | 重启后再删除 |
| 重装后仍无法使用 | 旧用户目录缓存冲突 | 卸载时必须删除 `%APPDATA%\Rime` |
| 与官方 Weasel 冲突 | 不应冲突——已使用独立 CLSID 和注册表路径 | 如仍有问题，确认官方 Weasel 已卸载 |
