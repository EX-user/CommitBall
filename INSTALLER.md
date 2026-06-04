# CB-Weasel 安装包构建

## 构建条件

安装包构建前，以下条件必须全部满足：

### 工具

| 工具 | 安装方式 | 验证 |
|------|----------|------|
| NSIS 3.08 | `cd weasel; cmd /c install_nsis.bat` | `C:\Program Files (x86)\NSIS\Bin\makensis.exe` 存在 |
| VS 2022 Build Tools | 见 WEASEL_BUILD.md | `vcvarsall.bat` 存在 |

### 构建产物

| 文件 | 来源 | 说明 |
|------|------|------|
| `weasel/output/cb-weaselx64.dll` | msbuild weasel.sln | TSF 输入法 DLL |
| `weasel/output/WeaselServer.exe` | msbuild weasel.sln | 算法服务（含 CommitBallBridge） |
| `weasel/output/WeaselDeployer.exe` | msbuild weasel.sln | 部署工具 |
| `weasel/output/rime.dll` | get-rime.ps1 | Rime 引擎 |
| `weasel/output/data/*.yaml` | build.bat data | 输入方案源数据 |
| `weasel/output/data/opencc/` | get-rime.ps1 或官方安装包 | 繁简转换数据 |
| `weasel/output/data/essay.txt` | build.bat data | 词频数据 |

### 预编译词表

| 文件 | 来源 | 说明 |
|------|------|------|
| `%APPDATA%\Rime\build\luna_pinyin.table.bin` | WeaselServer 首次启动编译 | 共享拼音词表（13MB） |
| `%APPDATA%\Rime\build\luna_pinyin_simp.prism.bin` | WeaselServer 首次启动编译 | 简体映射（31KB） |

如缺少，先启动 WeaselServer 一次，等待词典编译完成。

## 构建

```powershell
cd installer
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

脚本会依次：
1. 检查所有必需文件（缺失时报错并提示修复方法）
2. 复制词表到 `staging/build/`
3. 编译 CommitBall.exe
4. 调用 NSIS 构建安装包

产物：`installer/archives/CommitBall-0.1.2.0-installer.exe`

## 安装包内容

```
$INSTDIR/                              (默认 C:\Program Files\CommitBall)
  CommitBall.exe                       # 悬浮球应用（顶层）
  CommitBall-Bar.exe                   # 快捷输入条（.NET 8 self-contained）
  CommitBall-Agent.exe                 # AI 终端（.NET 8 self-contained）
  analyse-prompt.md                    # Agent 分析提示词
  uninstall.exe                        # 卸载程序
  cb-weasel/                           # 输入法子目录
    cb-weaselx64.dll                   # TSF DLL
    WeaselServer.exe                   # 算法服务
    WeaselDeployer.exe                 # 部署工具
    rime.dll                           # Rime 引擎
    WinSparkle.dll                     # 自动更新库
    data/                              # 输入方案 + OpenCC + essay.txt

$APPDATA\Rime\build\                   # 安装时复制
  luna_pinyin.table.bin
  luna_pinyin_simp.prism.bin

C:\Windows\System32\
  cb-weasel.dll                        # TSF DLL 副本
```

## 安装行为

1. 停止旧版 WeaselServer / CommitBall / CommitBall-Bar / CommitBall-Agent
2. 复制 CommitBall.exe、CommitBall-Bar.exe、CommitBall-Agent.exe、analyse-prompt.md 到 `$INSTDIR`
3. 复制 cb-weasel 文件到 `$INSTDIR\cb-weasel\`
4. 复制 DLL 到 System32
5. 注册 TSF（regsvr32）
6. 写注册表（InstallDir、Autorun、Uninstall）
7. 复制词表到 `%APPDATA%\Rime\build\`
8. 启动 WeaselServer

## 卸载行为

1. 停止 WeaselServer / CommitBall / CommitBall-Bar / CommitBall-Agent
2. 取消注册 TSF
3. 删除 System32 DLL
4. 清理注册表（Rime\CBWeasel、Autorun、Uninstall、TIP CLSID）
5. 删除 `$INSTDIR\cb-weasel\`、CommitBall.exe、CommitBall-Bar.exe、CommitBall-Agent.exe、analyse-prompt.md

用户数据 `%APPDATA%\Rime` 不删除（需手动清理）。

## 注意事项

- `dotnet publish` 不会将 `Content` 标记的文件（如 `analyse-prompt.md`）复制到 publish 输出目录，只复制到 `bin/`。NSIS 中需要从源码目录取这些文件，而非 publish 目录。
- CommitBall-Agent.exe 约 154MB（self-contained），LZMA 压缩后安装包约 96MB。

## 目录结构

```
installer/
  build-installer.ps1      # 构建脚本（含文件检查）
  commitball.nsi             # NSIS 脚本
  .gitignore               # 忽略 archives/ 和 staging/
  archives/                # 构建产物（git 忽略）
  staging/                 # 临时文件（git 忽略）
```
