param(
    [ValidateSet('Build', 'Launch')][string]$Action = 'Build',
    [string]$TestRoot = $env:SHAREDINVENTORY_TEST_ROOT,
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    # All worktrees share the main checkout's existing isolated test installation.
    $commonGit = git rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the main checkout.' }
    $mainCheckout = Split-Path $commonGit -Parent
    if (!$TestRoot) { $TestRoot = Join-Path $mainCheckout 'SharedInventory-Test-20261023' }
    $root = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
    $game = Join-Path $root 'game'
    $exe = Join-Path $game 'valheim.exe'
    $marker = Join-Path $root '.sharedinventory-test'
    if (!(Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker).Trim() -ne 'sharedinventory-test-v1') {
        throw "Expected the marked isolated test installation: $root"
    }
    foreach ($required in @($exe, (Join-Path $root 'saves'),
        (Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll'),
        (Join-Path $game 'BepInEx/plugins/SharedInventory.TestIsolation.dll'))) {
        if (!(Test-Path -LiteralPath $required)) { throw "Missing isolated test input: $required" }
    }
    function Assert-GameClosed {
        foreach ($process in @(Get-Process valheim -ErrorAction SilentlyContinue)) {
            if (!$process.Path -or [string]::Equals($process.Path, $exe, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close the shared isolated game normally, then run just launch again.'
            }
        }
    }
    if ($Action -eq 'Launch') { Assert-GameClosed }
    if (!$Dotnet) {
        $sharedSdk = Join-Path $mainCheckout '.work/dotnet/dotnet.exe'
        if (Test-Path -LiteralPath $sharedSdk) { $Dotnet = $sharedSdk }
    }
    & (Join-Path $repo 'build.ps1') -ValheimDir $game -BepInExDir (Join-Path $game 'BepInEx') -Dotnet $Dotnet -EnableTestHarness
    if ($Action -eq 'Build') { return }

    Assert-GameClosed
    $source = Join-Path $repo 'bin/Release/testing/netstandard2.1/SharedInventory.dll'
    $target = Join-Path $game 'BepInEx/plugins/SharedInventory.dll'
    $backupDirectory = Join-Path $repo '.work/test-plugin-backups'
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    if (Test-Path -LiteralPath $target) {
        Copy-Item -LiteralPath $target -Destination (Join-Path $backupDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '-SharedInventory.dll'))
    }
    Copy-Item -LiteralPath $source -Destination $target -Force
    function Get-ModHash([string]$Path) {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '') }
        finally { $hasher.Dispose() }
    }
    $hash = Get-ModHash $source
    if ((Get-ModHash $target) -ne $hash) { throw 'Installed mod checksum does not match this worktree.' }
    # Do not call Launch-Test.cmd: it rebuilds the main checkout over our DLL.
    $arguments = '--doorstop-enabled true --doorstop-target-assembly "' + (Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll') +
        '" -sharedinventory-test -savedir "' + (Join-Path $root 'saves') + '" -logFile "' + (Join-Path $root 'Player.log') +
        '" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -console'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Normal -PassThru
    Write-Host "Launched shared isolated game (PID $($process.Id)) using $source"
    Write-Host "Installed SHA256: $hash"
}
finally { Pop-Location }
