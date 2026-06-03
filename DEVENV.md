# 开发环境 Checklist

> 按顺序完成后，从 `apply-patch.ps1` 到 `build-installer.ps1` 的全流程应可一次性通过。

---

## 环境

- [ ] Windows 10/11 x64

## 安装

- [ ] VS 2022 Build Tools: `winget install Microsoft.VisualStudio.2022.BuildTools`
- [ ] VS 2022 C++ workload + ATL/MFC（winget 不会自动安装，必须手动执行）：

```powershell
Start-Process -FilePath "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" `
  -ArgumentList 'modify --installPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.ATLMFC --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --includeRecommended --quiet --norestart' `
  -Verb RunAs -Wait
```

> 注意：`--quiet`/`--passive` 必须以管理员身份运行，否则返回 Exit Code 5007 且不安装任何东西。用 `Start-Process -Verb RunAs` 提权。

- [ ] 7-Zip: `winget install 7zip.7zip`
- [ ] CMake: `winget install Kitware.CMake`
- [ ] aria2c: `winget install aria2.aria2`
- [ ] Git（含 Git Bash）: `winget install Git.Git`
- [ ] NSIS（仅打包阶段）: `winget install NSIS.NSIS`
- [ ] .NET 8 SDK（CommitBall-Bar 和 CommitBall-Agent）: `winget install Microsoft.DotNet.SDK.8`

## 验证

```powershell
Test-Path "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
$msvcVer = (Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\" -Directory)[0].Name
Test-Path "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\$msvcVer\atlmfc\include\afxwin.h"
Test-Path "C:\Program Files (x86)\NSIS\Bin\makensis.exe"
```

## PATH 检查

winget 安装的 7z、cmake、aria2c 可能不在 PATH。确认可用，否则临时加入：

```powershell
$env:PATH = "C:\Program Files\7-Zip;C:\Program Files\CMake\bin;$env:LOCALAPPDATA\Microsoft\WinGet\Links;" + $env:PATH
```

> `build.bat data` 中的 `bash` 必须是 Git Bash（`C:\Program Files\Git\usr\bin\bash.exe`），而非 WSL 的 bash（`C:\WINDOWS\system32\bash.exe`）。确保 Git Bash 路径在 PATH 中靠前。

## 克隆

```bash
git clone --recursive https://github.com/EX-user/CommitBall.git
cd CommitBall
```

> `--recursive` 会初始化 weasel 及其嵌套子模块（plum、librime）。如果缺少 `--recursive`，需手动：

```bash
git submodule update --init --recursive
```

- [ ] 检查 plum 子模块指向正确的 commit（`git -C weasel/plum rev-parse HEAD` 应为 `cab9ed35289f2022b6888503750d2dfb1851dbe0`），`--recursive` 可能将其更新到 master HEAD，需手动 checkout 回来

- [ ] Git Bash 网络设置：`git config --global http.version HTTP/1.1`（HTTP/2 连 GitHub 不稳定，会导致 `build.bat data` 中 plum 下载失败）

## 一次性环境配置

- [ ] 在 `weasel/` 目录下创建 `env.bat`（从 `env.bat.template` 复制修改）：

```bat
set BOOST_ROOT=%CD%\deps\boost_1_84_0
set BJAM_TOOLSET=msvc-14.3
set PLATFORM_TOOLSET=v143
set DEVTOOLS_PATH=C:\Program Files\7-Zip;C:\Program Files\CMake\bin;
```

- [ ] 创建 vcvarsall 符号链接（以管理员身份运行，见 WEASEL_BUILD.md 步骤 5）：

```powershell
$msvcVer = (Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\" -Directory)[0].Name
$base = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
$target = "$base\VC\Tools\MSVC\$msvcVer\bin\Hostx64\vcvarsall.bat"
$link = "$base\VC\Auxiliary\Build\vcvarsall.bat"
New-Item -ItemType Directory -Path (Split-Path $target) -Force
cmd /c "mklink `"$target`" `"$link`""
```

## 构建流程

按 WEASEL_BUILD.md / README.md / INSTALLER.md 顺序执行：

1. `.\apply-patch.ps1`

   > 如果报 "patch 不适用"，确认 weasel 子模块完全干净（`git -C weasel diff --stat HEAD` 应无输出）。plum 子模块 dirty 状态会导致失败，可用 `git -C weasel apply --exclude plum ../diff_of_weasel.patch` 跳过。

2. Boost 构建（见 WEASEL_BUILD.md 步骤 6-7）

   > aria2c 下载慢时用 `Invoke-WebRequest` 替代（见 WEASEL_BUILD.md）。

3. 获取 librime（见 WEASEL_BUILD.md 步骤 7）

   > GitHub API 限流（403）时手动下载（见 WEASEL_BUILD.md）。

4. 输入方案数据：`cd weasel && build.bat data`（见 WEASEL_BUILD.md 步骤 8）

5. 编译 CB-Weasel x64（见 WEASEL_BUILD.md 步骤 9）

   > `render.js` 生成的 `weasel.props` 中值可能带尾随空格导致 MSBuild 找不到 Boost 库。如遇 LNK1104，改用 MSBuild `/p:` 直接传参：

   ```powershell
   $env:BOOST_ROOT = "$PWD\deps\boost_1_84_0"
   $env:PRODUCT_VERSION = "0.17.4.0"
   $env:FILE_VERSION = "0.17.4.0"
   & "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
     weasel.sln /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 `
     /p:BOOST_ROOT=$env:BOOST_ROOT /p:PRODUCT_VERSION=$env:PRODUCT_VERSION /p:FILE_VERSION=$env:FILE_VERSION `
     /t:Rebuild /verbosity:minimal
   ```

6. `.\build-commitball.ps1`

7. Publish CommitBall-Bar: `cd commitball-bar\commitball-bar && dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\commitball-bar\publish`

8. Publish CommitBall-Agent: `cd commitball-agent\commitball-agent && dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\publish\agent`

9. 打包：`cd installer && .\build-installer.ps1`

   > 必须从 `installer/` 目录运行，脚本内 makensis 使用相对路径。
