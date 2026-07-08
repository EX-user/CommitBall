; CB-Weasel + CommitBall Installer
; Based on Weasel NSIS installer, simplified for x64-only + CB-Weasel naming

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

; Version info - override via /D flags
!ifndef WEASEL_VERSION
  !define WEASEL_VERSION "0.17.4"
!endif
!ifndef COMMITBALL_VERSION
  !define COMMITBALL_VERSION "0.2.3"
!endif
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "${COMMITBALL_VERSION}.0"
!endif
!define DOTNET_RUNTIME_INSTALLER "windowsdesktop-runtime-win-x64.exe"

Name "CommitBall ${COMMITBALL_VERSION}"
OutFile "archives\CommitBall-${PRODUCT_VERSION}-installer.exe"
InstallDir "$PROGRAMFILES64\CommitBall"
InstallDirRegKey HKLM "SOFTWARE\Rime\CBWeasel" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
AutoCloseWindow true

; MUI settings
!define MUI_ICON "..\tools\commitball.ico"
!define MUI_UNICON "..\tools\commitball.ico"
!define MUI_ABORTWARNING

; Pages
!insertmacro MUI_PAGE_LICENSE "..\weasel\output\LICENSE.txt"
  !insertmacro MUI_PAGE_DIRECTORY
  !insertmacro MUI_PAGE_COMPONENTS
  !insertmacro MUI_PAGE_INSTFILES

!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Language
!insertmacro MUI_LANGUAGE "SimpChinese"

; Registry paths
!define REG_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CB-Weasel"
!define REG_RIME_KEY "SOFTWARE\Rime\CBWeasel"
!define REG_TIP_CLSID "{1DAC3806-5705-46F1-A305-7066F9663F07}"

Section "CB-Weasel 输入法" SecMain
  SectionIn RO

  ; The C# UI processes are framework-dependent; install the bundled runtime if missing.
  DetailPrint "检查 .NET 8 Desktop Runtime..."
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\8.*"
  StrCmp $1 "" dotnet_runtime_missing dotnet_runtime_ok

  dotnet_runtime_missing:
    FindClose $0
    DetailPrint "安装 .NET 8 Desktop Runtime..."
    SetOutPath "$TEMP\CommitBallRedist"
    File /oname=${DOTNET_RUNTIME_INSTALLER} "redist\${DOTNET_RUNTIME_INSTALLER}"
    ExecWait '"$TEMP\CommitBallRedist\${DOTNET_RUNTIME_INSTALLER}" /install /quiet /norestart' $2
    IntCmp $2 0 dotnet_runtime_recheck 0 0
    IntCmp $2 3010 dotnet_runtime_recheck 0 0
    MessageBox MB_ICONSTOP|MB_OK \
      "Microsoft .NET 8 Desktop Runtime 安装失败，退出码：$2。$\n$\n请手动安装 .NET 8 Desktop Runtime (x64) 后重新运行安装包。"
    Abort

  dotnet_runtime_recheck:
    FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\8.*"
    StrCmp $1 "" 0 dotnet_runtime_ok_after_install
      MessageBox MB_ICONSTOP|MB_OK "未检测到 Microsoft .NET 8 Desktop Runtime，安装无法继续。"
      Abort

  dotnet_runtime_ok_after_install:
    FindClose $0
    Delete "$TEMP\CommitBallRedist\${DOTNET_RUNTIME_INSTALLER}"
    RMDir "$TEMP\CommitBallRedist"
    Goto dotnet_runtime_done

  dotnet_runtime_ok:
    FindClose $0

  dotnet_runtime_done:

  ; Stop existing processes
  DetailPrint "停止旧进程..."
  nsExec::ExecToLog 'taskkill /F /IM WeaselServer.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-BallShell.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-Bar.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-Agent.exe'
  Sleep 1000

  SetOutPath "$INSTDIR"

  ; CommitBall
  File "..\commitball\publish\CommitBall.exe"

  ; CommitBall-Bar
  File "..\commitball-bar\publish\CommitBall-Bar.exe"
  File "..\commitball-bar\publish\WebView2Loader.dll"

  ; CommitBall-Agent
  File "..\commitball-agent\publish\CommitBall-Agent.exe"
  File "..\commitball-agent\publish\e_sqlite3.dll"
  File "..\commitball-agent\summary_to_panel-prompt.md"

  ; CommitBall-BallShell
  File "..\commitball-ball-shell\publish\CommitBall-BallShell.exe"

  SetOutPath "$INSTDIR\Assets\Skins\eye-of-commit"
  File "..\commitball-ball-shell\publish\Assets\Skins\eye-of-commit\*.*"

  SetOutPath "$INSTDIR\data\agent-out"
  File "..\commitball-agent\panel-template.html"
  File "..\commitball-agent\summary_task_exp_decay_memory_template.md"

  ; Unregister old CB-Weasel through the same helper used by the uninstaller.
  DetailPrint "注销旧版本 CB-Weasel 输入法..."
  IfFileExists "$INSTDIR\cb-weasel\WeaselSetup.exe" 0 old_weasel_setup_missing
    nsExec::ExecToLog '"$INSTDIR\cb-weasel\WeaselSetup.exe" /u'
    Goto old_weasel_setup_done
  old_weasel_setup_missing:
    DetailPrint "未找到旧版 WeaselSetup.exe，跳过输入法注销 helper。"
  old_weasel_setup_done:
  Sleep 500

  ; Release old DLL if locked
  DetailPrint "检查旧版本 DLL..."
retry_delete_dll:
  ClearErrors
  Delete "$INSTDIR\cb-weasel\cb-weaselx64.dll"
  IfErrors 0 dll_delete_ok
    MessageBox MB_ABORTRETRYIGNORE|MB_ICONEXCLAMATION \
      "cb-weaselx64.dll 无法删除，可能是卸载残留或正在使用中。这通常可以忽略……$\n$\n终止 = 取消安装$\n重试 = 关闭相关程序后重试$\n忽略 = 跳过此步骤继续安装" \
      IDRETRY retry_delete_dll IDIGNORE dll_delete_skip
    Abort
  dll_delete_ok:
  dll_delete_skip:

  ; Core executables (cb-weasel subdirectory)
  SetOutPath "$INSTDIR\cb-weasel"
  SetOverwrite try
  File "..\weasel\output\cb-weaselx64.dll"
  SetOverwrite on
  File "..\weasel\output\WeaselServer.exe"
  File "..\weasel\output\WeaselDeployer.exe"
  File "..\weasel\output\WeaselSetup.exe"
  File "..\weasel\output\rime.dll"
  File "..\weasel\output\WinSparkle.dll"

  ; Data files (exclude unused schemas)
  SetOutPath "$INSTDIR\cb-weasel\data"
  File /r /x "cangjie5*" /x "terra_pinyin*" /x "bopomofo*" /x "stroke*" /x "detenele*" /x "zhuyin.yaml" /x "default.yaml" "..\weasel\output\data\*.yaml"
  File /r "..\weasel\output\data\*.txt"
  File "default.yaml"

  SetOutPath "$INSTDIR\cb-weasel\data\opencc"
  File /r "..\weasel\output\data\opencc\*.*"

  SetOutPath "$INSTDIR\cb-weasel\data\preview"
  File "..\weasel\output\data\preview\*.*"

  ; Copy DLL to System32
  DetailPrint "安装 DLL 到 System32..."
  Delete "$SYSDIR\cb-weasel.dll"
  CopyFiles "$INSTDIR\cb-weasel\cb-weaselx64.dll" "$SYSDIR\cb-weasel.dll"

  ; Register TSF
  DetailPrint "注册 TSF 文本服务..."
  nsExec::ExecToLog 'regsvr32 /s "$INSTDIR\cb-weasel\cb-weaselx64.dll"'

  ; Write registry
  DetailPrint "写入注册表..."
  WriteRegStr HKLM "${REG_RIME_KEY}" "WeaselRoot" "$INSTDIR\cb-weasel"
  WriteRegStr HKLM "${REG_RIME_KEY}" "ServerExecutable" "WeaselServer.exe"
  WriteRegStr HKLM "${REG_RIME_KEY}" "InstallDir" "$INSTDIR"

  ; Autorun
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "CBWeaselServer" '"$INSTDIR\cb-weasel\WeaselServer.exe"'

  ; Uninstall registry
  WriteRegStr HKLM "${REG_UNINST_KEY}" "DisplayName" "CommitBall"
  WriteRegStr HKLM "${REG_UNINST_KEY}" "DisplayIcon" "$INSTDIR\cb-weasel\WeaselServer.exe"
  WriteRegStr HKLM "${REG_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${REG_UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${REG_UNINST_KEY}" "Publisher" "CommitBall"
  WriteRegDWORD HKLM "${REG_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_UNINST_KEY}" "NoRepair" 1

  ; Uninstaller
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Start WeaselServer
  DetailPrint "启动 WeaselServer..."
  SetOutPath "$INSTDIR\cb-weasel"
  Exec "$INSTDIR\cb-weasel\WeaselServer.exe"

  ; Deploy input schemes
  DetailPrint "部署输入方案..."
  Sleep 2000
  nsExec::ExecToLog '"$INSTDIR\cb-weasel\WeaselDeployer.exe" /deploy'

  ; Set working directory for CommitBall launch
  SetOutPath "$INSTDIR"

  DetailPrint "安装完成！请在 Windows 设置中添加 CB-Weasel 键盘。"
SectionEnd

Section "创建桌面快捷方式" SecDesktop
  CreateShortcut "$DESKTOP\CommitBall.lnk" "$INSTDIR\CommitBall.exe"
SectionEnd

Section "Uninstall"
  SetRegView 64

  ; Stop processes
  nsExec::ExecToLog 'taskkill /F /IM WeaselServer.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-BallShell.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-Bar.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall-Agent.exe'
  Sleep 1000

  ; Unregister TSF and remove language profile through CB-Weasel setup helper.
  DetailPrint "注销 CB-Weasel 输入法..."
  IfFileExists "$INSTDIR\cb-weasel\WeaselSetup.exe" 0 unregister_tsf_missing
    nsExec::ExecToLog '"$INSTDIR\cb-weasel\WeaselSetup.exe" /u'
    Goto unregister_tsf_done
  unregister_tsf_missing:
    DetailPrint "未找到 WeaselSetup.exe，跳过输入法注销 helper。"
  unregister_tsf_done:

  ; Remove DLL from System32
  Delete /REBOOTOK "$SYSDIR\cb-weasel.dll"

  ; Remove registry
  DeleteRegKey HKLM "${REG_RIME_KEY}"
  DeleteRegKey HKCU "${REG_RIME_KEY}"
  DeleteRegKey HKLM "${REG_UNINST_KEY}"
  DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "CBWeaselServer"

  ; Remove files
  Delete /REBOOTOK "$INSTDIR\cb-weasel\cb-weaselx64.dll"
  RMDir /r /REBOOTOK "$INSTDIR\cb-weasel"
  Delete "$INSTDIR\CommitBall.exe"
  Delete "$INSTDIR\CommitBall-BallShell.exe"
  Delete "$INSTDIR\CommitBall-Bar.exe"
  Delete "$INSTDIR\CommitBall-Agent.exe"
  Delete "$INSTDIR\WebView2Loader.dll"
  Delete "$INSTDIR\e_sqlite3.dll"
  Delete "$INSTDIR\summary_to_panel-prompt.md"
  Delete "$INSTDIR\uninstall.exe"
  Delete "$DESKTOP\CommitBall.lnk"
  RMDir /r "$INSTDIR\Assets"
  RMDir "$INSTDIR"
SectionEnd
