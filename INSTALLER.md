# CB-Weasel 安装包构建

## 构建条件

安装包构建前，以下条件必须全部满足：

### 工具

| 工具 | 安装方式 | 验证 |
|------|----------|------|
| NSIS 3.08 | `cd weasel; cmd /c install_nsis.bat` | `C:\Program Files (x86)\NSIS\Bin\makensis.exe` 存在 |
| VS 2022 Build Tools | 见 WEASEL_BUILD.md | `vcvarsall.bat` 存在 |
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` | `dotnet --version` 可用 |

### 构建产物

| 文件 | 来源 | 说明 |
|------|------|------|
| `weasel/output/cb-weaselx64.dll` | msbuild weasel.sln | TSF 输入法 DLL |
| `weasel/output/WeaselServer.exe` | msbuild weasel.sln | 算法服务（含 CommitBallBridge） |
| `weasel/output/WeaselDeployer.exe` | msbuild weasel.sln | 部署工具 |
| `weasel/output/WeaselSetup.exe` | msbuild weasel.sln /t:WeaselSetup | 输入法注册/注销 helper |
| `weasel/output/rime.dll` | get-rime.ps1 | Rime 引擎 |
| `weasel/output/data/*.yaml` | build.bat data | 输入方案源数据 |
| `weasel/output/data/opencc/` | get-rime.ps1 或官方安装包 | 繁简转换数据 |
| `weasel/output/data/essay.txt` | build.bat data | 词频数据 |

### 预编译词表

| 文件 | 来源 | 说明 |
|------|------|------|
| `%APPDATA%\Rime\build\luna_pinyin.table.bin` | WeaselServer 首次启动编译 | 共享拼音词表（13MB） |
| `%APPDATA%\Rime\build\luna_pinyin_simp.prism.bin` | WeaselServer 首次启动编译 | 简体映射（31KB） |

构建脚本会检查并暂存这两个文件，用于确认本机 CB-Weasel 数据已经可用。当前 NSIS 安装包不把这两个 `%APPDATA%` 下的预编译词表打入安装包；安装后由 WeaselServer/WeaselDeployer 在目标机器上按 Rime 机制部署或重新生成。

## 构建

```powershell
cd installer
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

脚本会依次：
1. 检查所有必需文件（缺失时报错并提示修复方法）
2. 下载或复用 `installer/redist/windowsdesktop-runtime-win-x64.exe`
3. 检查并暂存本机词表到 `installer/staging/build/`
4. 编译 `commitball/publish/CommitBall.exe`
5. 发布 `commitball-bar/publish/CommitBall-Bar.exe`
6. 发布 `commitball-agent/publish/CommitBall-Agent.exe`
7. 发布 `commitball-ball-shell/publish/CommitBall-BallShell.exe` 和运行时皮肤资源
8. 调用 NSIS 构建安装包

产物：`installer/archives/CommitBall-0.2.2.0-installer.exe`

## 安装包内容

```
$INSTDIR/                              (默认 C:\Program Files\CommitBall)
  CommitBall.exe                       # 悬浮球应用（顶层）
  CommitBall-BallShell.exe             # C# 悬浮球 UI
  CommitBall-Bar.exe                   # 快捷输入条（.NET 8 framework-dependent）
  CommitBall-Agent.exe                 # AI 终端（.NET 8 framework-dependent）
  WebView2Loader.dll                   # Bar 面板 WebView2 原生加载器
  e_sqlite3.dll                        # Agent SQLite 原生依赖
  summary_to_panel-prompt.md           # Agent 汇总提示词
  uninstall.exe                        # 卸载程序
  windowsdesktop-runtime-win-x64.exe   # 安装时临时释放到 %TEMP% 并在缺失时静默安装
  Assets/
    Skins/eye-of-commit/               # BallShell 运行时皮肤资源
  cb-weasel/                           # 输入法子目录
    cb-weaselx64.dll                   # TSF DLL
    WeaselServer.exe                   # 算法服务
    WeaselDeployer.exe                 # 部署工具
    rime.dll                           # Rime 引擎
    WinSparkle.dll                     # 自动更新库
    data/                              # 输入方案 + OpenCC + essay.txt

C:\Windows\System32\
  cb-weasel.dll                        # TSF DLL 副本
```

## 安装行为

1. 检查目标机器是否已安装 Microsoft .NET 8 Desktop Runtime (x64)，缺失时从安装包内置 redist 静默安装
2. 停止正在运行的 WeaselServer / CommitBall / CommitBall-BallShell / CommitBall-Bar / CommitBall-Agent
3. 复制 CommitBall.exe、CommitBall-Bar.exe、CommitBall-Agent.exe、CommitBall-BallShell.exe、summary_to_panel-prompt.md 到 `$INSTDIR`
4. 复制 cb-weasel 文件到 `$INSTDIR\cb-weasel\`
5. 复制 DLL 到 System32
6. 注册 TSF（regsvr32）
7. 写注册表（InstallDir、Autorun、Uninstall）
8. 启动 WeaselServer
9. 调用 WeaselDeployer 部署输入方案

## 卸载行为

1. 停止 WeaselServer / CommitBall / CommitBall-BallShell / CommitBall-Bar / CommitBall-Agent
2. 取消注册 TSF
3. 删除 System32 DLL
4. 清理注册表（Rime\CBWeasel、Autorun、Uninstall、TIP CLSID）
5. 删除 `$INSTDIR\cb-weasel\`、CommitBall.exe、CommitBall-Bar.exe、CommitBall-Agent.exe、CommitBall-BallShell.exe、summary_to_panel-prompt.md

用户数据 `%APPDATA%\Rime` 不删除（需手动清理）。

## 注意事项

- .NET 组件统一发布到各自项目目录下的 `publish/`：`commitball-bar/publish/`、`commitball-agent/publish/`、`commitball-ball-shell/publish/`。
- C++ Core 统一输出到 `commitball/publish/CommitBall.exe`，中间文件输出到 `commitball/obj/`。
- 当前 Bar、Agent、BallShell 都以 .NET 8 framework-dependent single-file 方式发布，不再把 .NET/WPF 运行时重复打入每个 exe。
- 安装包内置一份 Microsoft .NET 8 Desktop Runtime (x64) redist。目标机器已安装时不会运行；缺失时静默安装。包体会比纯 framework-dependent 包大约增加 55MB，但仍显著小于三份 self-contained 运行时重复打包。

## 目录结构

```
installer/
  build-installer.ps1      # 构建脚本（含文件检查）
  commitball.nsi             # NSIS 脚本
  .gitignore               # 忽略 archives/ 和 staging/
  archives/                # 构建产物（git 忽略）
  staging/                 # 临时文件（git 忽略）
```
