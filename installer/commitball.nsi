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
!ifndef WEASEL_BUILD
  !define WEASEL_BUILD "0"
!endif
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "${WEASEL_VERSION}.${WEASEL_BUILD}"
!endif

Name "CommitBall ${WEASEL_VERSION}"
OutFile "archives\CommitBall-${PRODUCT_VERSION}-installer.exe"
InstallDir "$PROGRAMFILES64\CommitBall"
InstallDirRegKey HKLM "SOFTWARE\Rime\CBWeasel" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

; MUI settings
!define MUI_ICON "..\tools\commitball.ico"
!define MUI_UNICON "..\tools\commitball.ico"
!define MUI_ABORTWARNING

; Pages
!insertmacro MUI_PAGE_LICENSE "..\weasel\output\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\CommitBall.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 CommitBall"
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

  ; Stop existing processes
  DetailPrint "停止旧进程..."
  nsExec::ExecToLog 'taskkill /F /IM WeaselServer.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall.exe'
  Sleep 1000

  ; Set output path
  SetOutPath "$INSTDIR"

  ; CommitBall
  File "..\commitball\CommitBall.exe"

  ; Core executables (cb-weasel subdirectory)
  SetOutPath "$INSTDIR\cb-weasel"
  File "..\weasel\output\cb-weaselx64.dll"
  File "..\weasel\output\WeaselServer.exe"
  File "..\weasel\output\WeaselDeployer.exe"
  File "..\weasel\output\rime.dll"
  File "..\weasel\output\WinSparkle.dll"

  ; Data files
  SetOutPath "$INSTDIR\cb-weasel\data"
  File /r "..\weasel\output\data\*.yaml"
  File /r "..\weasel\output\data\*.txt"

  SetOutPath "$INSTDIR\cb-weasel\data\opencc"
  File /r "..\weasel\output\data\opencc\*.*"

  SetOutPath "$INSTDIR\cb-weasel\data\preview"
  File "..\weasel\output\data\preview\*.*"

  ; Copy DLL to System32
  DetailPrint "安装 DLL 到 System32..."
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

Section "Uninstall"
  ; Stop processes
  nsExec::ExecToLog 'taskkill /F /IM WeaselServer.exe'
  nsExec::ExecToLog 'taskkill /F /IM CommitBall.exe'
  Sleep 1000

  ; Unregister TSF
  nsExec::ExecToLog 'regsvr32 /u /s "$INSTDIR\cb-weasel\cb-weaselx64.dll"'

  ; Remove DLL from System32
  Delete "$SYSDIR\cb-weasel.dll"

  ; Remove TSF TIP registration
  DeleteRegKey HKLM "SOFTWARE\Microsoft\CTF\TIP\${REG_TIP_CLSID}"

  ; Remove registry
  DeleteRegKey HKLM "${REG_RIME_KEY}"
  DeleteRegKey HKLM "${REG_UNINST_KEY}"
  DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "CBWeaselServer"

  ; Remove files
  RMDir /r "$INSTDIR\cb-weasel"
  Delete "$INSTDIR\CommitBall.exe"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
