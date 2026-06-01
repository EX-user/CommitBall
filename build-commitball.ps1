$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -products * -latest -property installationPath
$vcvarsall = "$vsPath\VC\Auxiliary\Build\vcvarsall.bat"

if (!(Test-Path $vcvarsall)) {
    Write-Error "找不到 vcvarsall.bat，请安装 VS Build Tools"
    exit 1
}

cmd /c "`"$vcvarsall`" x64 >nul 2>&1 && cd /d $PSScriptRoot\commitball && cl /EHsc /std:c++17 /Fe:CommitBall.exe main.cpp sqlite3.c /link user32.lib gdi32.lib gdiplus.lib shcore.lib /SUBSYSTEM:WINDOWS"
