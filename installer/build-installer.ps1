$ErrorActionPreference = "Stop"

# === Tool checks ===
$nsis = "C:\Program Files (x86)\NSIS\Bin\makensis.exe"
if (!(Test-Path $nsis)) {
    Write-Error "NSIS not found.`n  Install: cd weasel; cmd /c install_nsis.bat"
    exit 1
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -products * -latest -property installationPath
$vcvarsall = "$vsPath\VC\Auxiliary\Build\vcvarsall.bat"
if (!(Test-Path $vcvarsall)) {
    Write-Error "vcvarsall.bat not found.`n  Install VS 2022 Build Tools with C++ workload."
    exit 1
}

# === Required file checks ===
$root = Resolve-Path "$PSScriptRoot\.."
$checks = @(
    @{ Path = "$root\weasel\output\cb-weaselx64.dll";       Hint = "Build cb-weasel first: cd weasel; msbuild weasel.sln /p:Configuration=Release /p:Platform=x64" },
    @{ Path = "$root\weasel\output\WeaselServer.exe";        Hint = "Build cb-weasel first (same as above)" },
    @{ Path = "$root\weasel\output\WeaselDeployer.exe";      Hint = "Build cb-weasel first (same as above)" },
    @{ Path = "$root\weasel\output\rime.dll";                Hint = "Run get-rime.ps1 to download librime" },
    @{ Path = "$root\weasel\output\WinSparkle.dll";           Hint = "Run get-rime.ps1 — WinSparkle is bundled with librime" },
    @{ Path = "$root\weasel\output\data\default.yaml";       Hint = "Run: cd weasel; build.bat data" },
    @{ Path = "$root\weasel\output\data\opencc\TSCharacters.ocd2"; Hint = "Missing OpenCC data. Copy from official installer or run get-rime.ps1" },
    @{ Path = "$root\weasel\output\data\essay.txt";          Hint = "Run: cd weasel; build.bat data" },
    @{ Path = "$root\commitball\main.cpp";                   Hint = "Source missing — check git clone" },
    @{ Path = "$root\commitball\sqlite3.c";                  Hint = "Source missing — check git clone" }
)

$failed = $false
foreach ($c in $checks) {
    if (!(Test-Path $c.Path)) {
        Write-Host "MISSING: $($c.Path)" -ForegroundColor Red
        Write-Host "  -> $($c.Hint)" -ForegroundColor Yellow
        $failed = $true
    }
}
if ($failed) {
    Write-Error "`nRequired files missing. Fix the issues above and retry."
    exit 1
}

# === Stage dictionary files ===
Write-Host "Staging dictionary files..."
New-Item -ItemType Directory -Path staging\build -Force | Out-Null

$tableBin = "$env:APPDATA\Rime\build\luna_pinyin.table.bin"
$prismBin = "$env:APPDATA\Rime\build\luna_pinyin_simp.prism.bin"

if (!(Test-Path $tableBin)) {
    Write-Error "luna_pinyin.table.bin not found.`n  Start WeaselServer once to compile dictionaries, or copy from another machine.`n  Path: $tableBin"
    exit 1
}
if (!(Test-Path $prismBin)) {
    Write-Error "luna_pinyin_simp.prism.bin not found.`n  Add luna_pinyin_simp schema and restart WeaselServer.`n  Path: $prismBin"
    exit 1
}

Copy-Item $tableBin staging\build\ -Force
Copy-Item $prismBin staging\build\ -Force

# === Build CommitBall ===
Write-Host "Building CommitBall..."
cmd /c "`"$vcvarsall`" x64 >nul 2>&1 && cd /d $root\commitball && cl /EHsc /std:c++17 /Fe:CommitBall.exe main.cpp sqlite3.c /link user32.lib gdi32.lib gdiplus.lib shcore.lib advapi32.lib psapi.lib shell32.lib /SUBSYSTEM:WINDOWS"
if (!(Test-Path "$root\commitball\CommitBall.exe")) {
    Write-Error "CommitBall.exe build failed. Check compiler errors above."
    exit 1
}

# === Build installer ===
Write-Host "Building installer..."
New-Item -ItemType Directory -Path archives -Force | Out-Null
& $nsis /INPUTCHARSET UTF8 /DWEASEL_VERSION=0.17.4 /DWEASEL_BUILD=0 /DPRODUCT_VERSION=0.17.4.0 cb-weasel.nsi

if ($LASTEXITCODE -eq 0) {
    $exe = Get-Item "archives\CommitBall-0.17.4.0-installer.exe"
    $sizeMB = [math]::Round($exe.Length / 1MB, 1)
    Write-Host "`nDone! archives\CommitBall-0.17.4.0-installer.exe ($sizeMB MB)" -ForegroundColor Green
} else {
    Write-Error "NSIS build failed. Check errors above."
}
