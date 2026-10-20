param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [Parameter(Mandatory = $true)][string]$BepInExCore,
    [string]$DotNet = 'dotnet',
    [string]$JournalPackagePlugins,
    [switch]$CachedPackages,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
function Get-TestFileHash([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$workspace = Split-Path $PSScriptRoot -Parent
if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Close Valheim before staging a test runtime' }
$run = Join-Path $workspace ('.tools/m5/run-' + [Guid]::NewGuid().ToString('N'))
$plugins = Join-Path $run 'BepInEx/plugins'
$saves = Join-Path $run 'saves'
New-Item -ItemType Directory -Force $plugins, $saves | Out-Null
# These settings keep SDK state and filesystem fault fixtures inside the workspace.
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools/cli-home'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $workspace '.tools/nuget-http'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget/packages'
$env:MSBUILDDISABLENODEREUSE = '1'
$buildOptions = @('-c','Release','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
if ($CachedPackages) { $buildOptions += '-p:NuGetAudit=false' }
& $DotNet build (Join-Path $workspace 'tools/AdapterTests') @buildOptions
if ($LASTEXITCODE -ne 0) { throw 'Adapter test build failed' }
Copy-Item -LiteralPath $BepInExCore -Destination (Join-Path $run 'BepInEx') -Recurse
$build = Join-Path $workspace 'tools/AdapterTests/bin/Release/net472'
if ($JournalPackagePlugins) {
    & python -c 'import sys; from pathlib import Path; sys.path.insert(0, sys.argv[1]); from stage_journal_runtime import verify; verify(Path(sys.argv[2]))' $PSScriptRoot $JournalPackagePlugins
    if ($LASTEXITCODE -ne 0) { throw 'Candidate journal package verification failed' }
    Get-ChildItem -LiteralPath $JournalPackagePlugins -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $plugins }
    Copy-Item -LiteralPath (Join-Path $build 'ThorsKist.AdapterTests.dll') -Destination $plugins
} else {
    foreach ($name in @('ThorsKist.dll','ThorsKist.Sync.dll','ThorsKist.Sync.Sqlite.dll','ThorsKist.AdapterTests.dll',
        'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll')) {
        Copy-Item -LiteralPath (Join-Path $build $name) -Destination $plugins
    }
    # Use Unity's own System.Memory/Unsafe; do not ship duplicate game assemblies.
    Copy-Item -LiteralPath (Join-Path $build 'runtimes/win-x64/native/e_sqlite3.dll') -Destination $plugins
}
# Back up existing local saves before any game UI is opened, verifying each copy.
$existing = Join-Path $env:USERPROFILE 'AppData/LocalLow/IronGate/Valheim'
$backup = Join-Path $run 'backups'
New-Item -ItemType Directory -Force $backup | Out-Null
$manifest = @()
foreach ($folder in @('characters','characters_local','worlds','worlds_local','ThorsKist')) {
    $source = Join-Path $existing $folder
    if (!(Test-Path -LiteralPath $source)) { continue }
    Copy-Item -LiteralPath $source -Destination $backup -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($existing.Length + 1)
        $copy = Join-Path $backup $relative
        $hash = Get-TestFileHash $file.FullName
        if ($hash -ne (Get-TestFileHash $copy)) { throw "Backup verification failed: $relative" }
        $manifest += [pscustomobject]@{ Path = $relative; Sha256 = $hash; Bytes = $file.Length }
    }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'backup-manifest.json') -Encoding utf8
Write-Output "Runtime: $run"
if ($Launch) {
    $arguments = @('--doorstop-enabled','true','--doorstop-target-assembly',
        ('"' + (Join-Path $run 'BepInEx/core/BepInEx.Preloader.dll') + '"'),
        '-savedir', ('"' + $saves + '"'), '-logFile', ('"' + (Join-Path $run 'Player.log') + '"'),
        '-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-console','-thorskist-adapter-tests')
    Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $GameDirectory -WindowStyle Hidden
    Write-Output 'Tests run at the menu when ObjectDB is ready. Inspect BepInEx/LogOutput.log for M5 PASS/FAIL.'
}
