if (!(Test-Path "weasel/.git")) {
    Write-Error "weasel 目录不存在或不是 git 仓库。请先执行: git submodule update --init"
    exit 1
}

git -C weasel apply --check "$PSScriptRoot/weasel.patch"
if ($LASTEXITCODE -ne 0) {
    Write-Error "patch 不适用，weasel 源码可能已被修改"
    exit 1
}

git -C weasel apply "$PSScriptRoot/weasel.patch"
Write-Host "patch 已应用到 weasel/"
