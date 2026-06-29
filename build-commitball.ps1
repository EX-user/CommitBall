$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -products * -latest -property installationPath
$vcvarsall = "$vsPath\VC\Auxiliary\Build\vcvarsall.bat"

if (!(Test-Path $vcvarsall)) {
    Write-Error "找不到 vcvarsall.bat，请安装 VS Build Tools"
    exit 1
}

cmd /c "`"$vcvarsall`" x64 >nul 2>&1 && cd /d $PSScriptRoot\commitball && if not exist publish mkdir publish && if not exist obj mkdir obj && rc /fo obj\commitball.res commitball.rc && cl /EHsc /std:c++17 /Fepublish\CommitBall.exe /Foobj\ main.cpp sqlite3.c obj\commitball.res /link user32.lib shcore.lib advapi32.lib psapi.lib shell32.lib /SUBSYSTEM:WINDOWS"
