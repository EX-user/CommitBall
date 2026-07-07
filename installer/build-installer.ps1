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
$runtimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
$runtimeInstaller = "$PSScriptRoot\redist\windowsdesktop-runtime-win-x64.exe"
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

# === .NET Desktop Runtime redist ===
Write-Host "Preparing .NET 8 Desktop Runtime redist..."
New-Item -ItemType Directory -Path "$PSScriptRoot\redist" -Force | Out-Null
if (!(Test-Path $runtimeInstaller) -or ((Get-Item $runtimeInstaller).Length -lt 50MB)) {
    Write-Host "Downloading .NET 8 Desktop Runtime (x64)..."
    curl.exe -L $runtimeUrl -o $runtimeInstaller
    if ($LASTEXITCODE -ne 0) {
        Write-Error ".NET runtime download failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}
if (!(Test-Path $runtimeInstaller) -or ((Get-Item $runtimeInstaller).Length -lt 50MB)) {
    Write-Error ".NET runtime redist is missing or too small: $runtimeInstaller"
    exit 1
}

# === Stage dictionary files ===
Write-Host "Staging dictionary files..."
$stagingDir = Join-Path $PSScriptRoot "staging"
$dictionaryStageDir = Join-Path $stagingDir "build"
New-Item -ItemType Directory -Path $dictionaryStageDir -Force | Out-Null

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

Copy-Item $tableBin $dictionaryStageDir -Force
Copy-Item $prismBin $dictionaryStageDir -Force

# === Build CommitBall ===
Write-Host "Building CommitBall..."
$coreDir = Join-Path $root "commitball"
New-Item -ItemType Directory -Path (Join-Path $coreDir "publish") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $coreDir "obj") -Force | Out-Null
cmd /c "call `"$vcvarsall`" x64 >nul 2>&1 && cd /d `"$coreDir`" && rc /fo obj\commitball.res commitball.rc && cl /EHsc /std:c++17 /Fepublish\CommitBall.exe /Foobj\ main.cpp sqlite3.c obj\commitball.res /link user32.lib shcore.lib advapi32.lib psapi.lib shell32.lib /SUBSYSTEM:WINDOWS"
if ($LASTEXITCODE -ne 0) {
    Write-Error "CommitBall.exe build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
if (!(Test-Path "$root\commitball\publish\CommitBall.exe")) {
    Write-Error "CommitBall.exe build failed. Check compiler errors above."
    exit 1
}

# === Publish CommitBall-Bar ===
Write-Host "Publishing CommitBall-Bar..."
Remove-Item "$root\commitball-bar\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\commitball-bar\commitball-bar.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$root\commitball-bar\publish"
if ($LASTEXITCODE -ne 0) {
    Write-Error "CommitBall-Bar publish command failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
if (!(Test-Path "$root\commitball-bar\publish\CommitBall-Bar.exe")) {
    Write-Error "CommitBall-Bar publish failed."
    exit 1
}

# === Publish CommitBall-Agent ===
Write-Host "Publishing CommitBall-Agent..."
Remove-Item "$root\commitball-agent\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\commitball-agent\commitball-agent.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$root\commitball-agent\publish"
if ($LASTEXITCODE -ne 0) {
    Write-Error "CommitBall-Agent publish command failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
if (!(Test-Path "$root\commitball-agent\publish\CommitBall-Agent.exe")) {
    Write-Error "CommitBall-Agent publish failed."
    exit 1
}

# === Publish CommitBall-BallShell ===
Write-Host "Publishing CommitBall-BallShell..."
Remove-Item "$root\commitball-ball-shell\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\commitball-ball-shell\commitball-ball-shell.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$root\commitball-ball-shell\publish"
if ($LASTEXITCODE -ne 0) {
    Write-Error "CommitBall-BallShell publish command failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
if (!(Test-Path "$root\commitball-ball-shell\publish\CommitBall-BallShell.exe")) {
    Write-Error "CommitBall-BallShell publish failed."
    exit 1
}
$ballShellAssetTarget = "$root\commitball-ball-shell\publish\Assets"
if (!(Test-Path "$ballShellAssetTarget\Skins\eye-of-commit\body.png")) {
    Write-Error "CommitBall-BallShell runtime assets missing from publish output."
    exit 1
}

# === Build installer ===
Write-Host "Building installer..."
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path archives -Force | Out-Null
    & $nsis /INPUTCHARSET UTF8 /DWEASEL_VERSION=0.17.4 /DCOMMITBALL_VERSION=0.2.2 commitball.nsi
} finally {
    Pop-Location
}

if ($LASTEXITCODE -eq 0) {
    $exe = Get-Item (Join-Path $PSScriptRoot "archives\CommitBall-0.2.2.0-installer.exe")
    $sizeMB = [math]::Round($exe.Length / 1MB, 1)
    Write-Host "`nDone! $($exe.FullName) ($sizeMB MB)" -ForegroundColor Green
} else {
    Write-Error "NSIS build failed. Check errors above."
}
