# Weasel (小狼毫) 本地构建记录

> 在 Windows 11 + VS 2022 Build Tools 环境下成功构建 x64 的完整流程。
> 仅覆盖 x64 Release 构建，Win32 因 Boost 命名问题未解决。

## 1. 前置依赖

| 工具 | 安装命令 | 验证命令 |
|------|---------|---------|
| VS 2022 Build Tools | `winget install Microsoft.VisualStudio.2022.BuildTools` | `where cl.exe` |
| ATL/MFC 组件 | 见下方说明 | 文件存在（见下方） |
| 7-Zip | `winget install 7zip.7zip` | `where 7z` |
| CMake | `winget install Kitware.CMake` | `where cmake` |
| aria2c | `winget install aria2.aria2` | `where aria2c` |
| Git | `winget install Git.Git` | `where git` |
| Git Bash | 随 Git for Windows 安装 | `where bash` |

### 验证依赖

执行以下脚本检查所有工具是否可用：

```powershell
$tools = @("7z", "cmake", "aria2c", "git", "bash")
foreach ($tool in $tools) {
    $cmd = Get-Command $tool -ErrorAction SilentlyContinue
    if ($cmd) { Write-Host "OK: $tool -> $($cmd.Source)" }
    else { Write-Host "MISSING: $tool" }
}
# cl.exe 需要通过 vcvarsall.bat 设置环境，此处不检查
```

如果有 MISSING，检查是否已安装但不在 PATH 中：

```powershell
$knownPaths = @{
    "7z"     = "C:\Program Files\7-Zip\7z.exe"
    "cmake"  = "C:\Program Files\CMake\bin\cmake.exe"
    "aria2c" = "$env:LOCALAPPDATA\Microsoft\WinGet\Links\aria2c.exe"
}
foreach ($tool in $knownPaths.Keys) {
    if (Test-Path $knownPaths[$tool]) {
        Write-Host "$tool 已安装在 $($knownPaths[$tool])，请将其所在目录加入 PATH"
    }
}
```

将目录加入 PATH 的方法：系统属性 → 高级 → 环境变量 → 编辑 Path → 添加对应目录。添加后重新打开终端。

**自动化处理（无人值守构建时）：** 如果检测到工具已安装但不在 PATH 中，执行以下命令将其加入当前会话的 PATH（不影响系统设置）：

```powershell
$extraPaths = @(
    "C:\Program Files\7-Zip",
    "C:\Program Files\CMake\bin",
    "$env:LOCALAPPDATA\Microsoft\WinGet\Links"
)
$env:PATH = ($extraPaths -join ";") + ";" + $env:PATH
```

### ATL/MFC 组件安装

VS Build Tools 安装后，需手动添加 ATL/MFC：

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify `
  --installPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" `
  --add Microsoft.VisualStudio.Component.VC.ATLMFC `
  --includeOptional --quiet --norestart
```

验证：`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\*\atlmfc\include\afxwin.h` 存在。

## 2. 获取源码

```bash
git clone --recursive https://github.com/rime/weasel.git weasel
```

子模块：`librime/`（Rime 引擎）、`plum/`（输入方案包管理器）。

## 3. 应用 CommitBall 补丁

> **不可跳过。** 未应用此补丁则 Weasel 不会通过 Named Pipe 转发 commit，CommitBall 无法接收数据。

```powershell
# 在 commit-ball 仓库根目录下执行
.\apply-patch.ps1
```

验证：

```powershell
Get-Content weasel\RimeWithWeasel\RimeWithWeasel.cpp | Select-String "CommitBall"
```

应输出 `CommitBallBridge` 相关行。如需重新应用（例如重置了 weasel 子模块）：`cd weasel; git checkout -- .`，然后重新执行 `apply-patch.ps1`。

## 4. 环境配置

在 `weasel/` 目录下创建 `env.bat`（从 `env.bat.template` 复制修改）：

```bat
set BOOST_ROOT=%CD%\deps\boost_1_84_0
set BJAM_TOOLSET=msvc-14.3
set PLATFORM_TOOLSET=v143
set DEVTOOLS_PATH=C:\Program Files\7-Zip;C:\Program Files\CMake\bin;
```

- `BJAM_TOOLSET=msvc-14.3` — VS 2022（默认 msvc-14.2 对应 VS 2019）
- `PLATFORM_TOOLSET=v143` — VS 2022
- `BOOST_ROOT` 使用 `%CD%` 使其相对于 weasel 目录

## 5. b2 的 vcvarsall.bat 符号链接（一次性）

Boost 的 b2 构建工具会在 `VC\Tools\MSVC\<版本>\bin\Hostx64\` 下找 `vcvarsall.bat`，
但 VS 2022 Build Tools 把它放在 `VC\Auxiliary\Build\` 下。

**查找你的 MSVC 版本号：**

```powershell
Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\" -Directory | Select-Object Name
```

**创建符号链接（把 `<版本>` 替换为上一步输出）：**

```powershell
$msvcVer = "<版本>"  # 例如 14.44.35207
$target = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\$msvcVer\bin\Hostx64\vcvarsall.bat"
$link = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
New-Item -ItemType Directory -Path (Split-Path $target) -Force
cmd /c "mklink `"$target`" `"$link`""
```

## 6. 构建 Boost

**前提：** 步骤 5 的 vcvarsall 符号链接必须已创建，否则 b2 会报 `msvc-setup.nup` 错误。

确保 `7z`、`aria2c` 在 PATH 中（见步骤 1 的"自动化处理"）。

```powershell
cd weasel
$vcvarsall = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
cmd /c "`"$vcvarsall`" x64 && set PATH=C:\Program Files\7-Zip;%LOCALAPPDATA%\Microsoft\WinGet\Links;%PATH% && install_boost.bat"
```

流程：aria2c 下载 → 7z 解压 → b2 编译。

**如果 bootstrap.bat 报 `'cl' 不是内部或外部命令`**，原因是 b2 的子进程未继承 vcvarsall.bat 的 PATH。手动进入 boost 目录单独运行 bootstrap.bat：

```powershell
cd weasel\deps\boost_1_84_0
cmd /c "`"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat`" x64 >nul 2>&1 && bootstrap.bat"
```

之后重新运行 `build.bat boost` 即可（b2.exe 已存在，跳过 bootstrap）。

**如果 aria2c 下载速度慢或超时**，用 `Invoke-WebRequest` 替代（走系统代理）：

```powershell
cd weasel
Invoke-WebRequest -Uri "https://archives.boost.io/release/1.84.0/source/boost_1_84_0.7z" -OutFile "deps\boost_1_84_0.7z" -TimeoutSec 600
7z x deps\boost_1_84_0.7z -odeps -y
```

然后运行 `build.bat boost` 编译（源码已存在，跳过下载）。

产物在 `deps/boost_1_84_0/stage/lib/` 下。

## 7. 获取 librime

```powershell
cd weasel
powershell -ExecutionPolicy Bypass -Command "& './get-rime.ps1' -use dev -extract `$true -build_variant msvc"
```

**如果 get-rime.ps1 报语法错误**（编码问题），先修复：

```powershell
$content = [System.IO.File]::ReadAllText("$PWD\get-rime.ps1", [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText("$PWD\get-rime.ps1", $content, (New-Object System.Text.UTF8Encoding $true))
```

**如果 GitHub API 限流（403）**，手动下载：
https://github.com/rime/librime/releases/latest
选择 `rime-*-Windows-msvc-x64.7z` 和 `rime-*-Windows-msvc-x86.7z`，解压后按以下路径放置：

| 来源 | 目标 |
|------|------|
| `dist/include/rime_*.h` | `include/` |
| `dist/lib/rime.lib` (x86) | `lib/` |
| `dist/lib/rime.lib` (x64) | `lib64/` |
| `dist/lib/rime.dll` (x64) | `output/` |
| `dist/lib/rime.dll` (x86) | `output/Win32/` |

**注意：** 手动下载 librime 不会自动安装 OpenCC 数据，必须在步骤 10 手动处理。

## 8. 获取输入方案数据

```powershell
cd weasel
$vcvarsall = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
cmd /c "`"$vcvarsall`" x64 && build.bat data"
```

需要 bash（Git Bash）在 PATH 中。

## 9. 编译 Weasel x64

```powershell
cd weasel

# 设置环境变量（与 env.bat 一致）
$env:BOOST_ROOT = "$PWD\deps\boost_1_84_0"
$env:PLATFORM_TOOLSET = "v143"
$env:VERSION_MAJOR = "0"
$env:VERSION_MINOR = "17"
$env:VERSION_PATCH = "4"
$env:PRODUCT_VERSION = "0.17.4.0"
$env:FILE_VERSION = "0.17.4.0"

# 生成 weasel.props
cscript.exe render.js weasel.props BOOST_ROOT PLATFORM_TOOLSET VERSION_MAJOR VERSION_MINOR VERSION_PATCH PRODUCT_VERSION FILE_VERSION

# 编译（指定 PlatformToolset 确保使用 v143）
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild weasel.sln /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /t:Build /verbosity:minimal
```

产物在 `output/` 目录下。

## 10. OpenCC 数据（必需）

> **缺少 OpenCC 数据会导致繁简转换不生效，所有输出为繁体。**

步骤 7 的 `get-rime.ps1` 已自动将 OpenCC 数据复制到 `output/data/opencc/`。

验证：

```powershell
Test-Path output\data\opencc\TSCharacters.ocd2
```

如果缺失（例如步骤 7 手动下载了 librime），从官方安装包获取：

```powershell
Invoke-WebRequest -Uri "https://github.com/rime/weasel/releases/latest/download/weasel-0.17.4.0-installer.exe" -OutFile "$env:TEMP\weasel-official.exe" -TimeoutSec 120
& "C:\Program Files\7-Zip\7z.exe" x "$env:TEMP\weasel-official.exe" -o"$env:TEMP\weasel-official" -y
Copy-Item "$env:TEMP\weasel-official\data\opencc\*" "output\data\opencc\" -Force
```

## 11. 安装

见 `WEASEL_INSTALL.md`。

## 12. 日志

```
%TEMP%\rime.weasel\*.log
```

- `*.ERROR.*` — 错误（如 OpenCC 数据缺失、方案不存在）
- `*.WARNING.*` — 警告
- `*.INFO.*` — 详细日志

排查步骤：先看 `*.ERROR.*`，再看 `*.WARNING.*`。日志文件名含进程名、时间戳和 PID。

## 13. 已知问题

| 问题 | 原因 | 影响 |
|------|------|------|
| Win32 构建失败 | Boost x86 lib 命名带 `x32` 后缀 | 不影响 x64 |
| `build.bat opencc` 失败 | OpenCC cmake 配置问题 | 繁简自动转换不可用 |
| WeaselSetup.exe 不可用 | 需要 Win32 weasel.dll | 用 regsvr32 替代 |
| get-rime.ps1 报错 | 文件编码无 BOM | 重新保存为 UTF-8 BOM |
| `default.yaml` 含不存在的 `quick5` | plum prelude 方案包未同步 | 启动时报 ERROR 日志，安装时移除 |
