param(
    [string]$GameSource,
    [string]$Destination,
    [string]$PayloadRoot = (Join-Path $PSScriptRoot 'payload')
)
$ErrorActionPreference = 'Stop'
function Hash-File([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Is-Within([string]$Path,[string]$Root) {
    $Path.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
}
if (!$GameSource) { $GameSource = Read-Host 'Full path to this PC''s Steam Valheim folder' }
if (!$Destination) { $Destination = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ThorsKist-M7-Test' }
$sourceRoot = [IO.Path]::GetFullPath($GameSource.Trim('"')).TrimEnd('\')
$targetRoot = [IO.Path]::GetFullPath($Destination.Trim('"')).TrimEnd('\')
$payload = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\')
if ((Is-Within $targetRoot $sourceRoot) -or (Is-Within $sourceRoot $targetRoot) -or (Is-Within $targetRoot $payload) -or (Is-Within $payload $targetRoot)) {
    throw 'Game source, test destination and payload must be separate directories.'
}
if (!(Test-Path -LiteralPath (Join-Path $sourceRoot 'valheim.exe'))) { throw 'Source is not a Valheim installation.' }
if ((Test-Path -LiteralPath $targetRoot) -and @(Get-ChildItem -LiteralPath $targetRoot -Force).Count) { throw 'Test destination must be empty. Existing files will not be overwritten.' }
$manifest = Get-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1) { throw 'Unknown test-payload manifest.' }
if ((Hash-File (Join-Path $sourceRoot 'valheim_Data/Managed/assembly_valheim.dll')) -ne $manifest.GameAssemblySha256) {
    throw 'This PC has a different Valheim build. Update both PCs to the same Steam build before testing.'
}
$gameNames = @('valheim.exe','UnityPlayer.dll','UnityCrashHandler64.exe','steam_appid.txt','MonoBleedingEdge','valheim_Data','D3D12')
$copyPlan = @()
function Add-CopyTree([string]$From,[string]$Relative) {
    $item = Get-Item -LiteralPath $From -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked input: $From" }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $From -Force) { Add-CopyTree $child.FullName (Join-Path $Relative $child.Name) }
    } else {
        $script:copyPlan += [pscustomobject]@{ Source=$item.FullName; Relative=$Relative }
    }
}
foreach ($name in $gameNames) {
    $inputPath = Join-Path $sourceRoot $name
    if (Test-Path -LiteralPath $inputPath) { Add-CopyTree $inputPath (Join-Path 'game' $name) }
}
foreach ($entry in $manifest.Files) {
    $inputPath = [IO.Path]::GetFullPath((Join-Path $payload $entry.Path))
    if (!(Is-Within $inputPath $payload) -or $inputPath -eq $payload -or (Hash-File $inputPath) -ne $entry.Sha256) {
        throw "Invalid test-payload checksum/path: $($entry.Path)"
    }
    if ((Get-Item -LiteralPath $inputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload rejected.' }
    $copyPlan += [pscustomobject]@{ Source=$inputPath; Relative=$entry.Path }
}
if (@($copyPlan | Group-Object Relative | Where-Object Count -gt 1).Count) { throw 'Payload would replace a copied game file.' }
foreach ($entry in $copyPlan) {
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $entry.Relative))
    if (!(Is-Within $target $targetRoot)) { throw 'Copy plan escapes the test directory.' }
}
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
$verification = @(); $done = 0
foreach ($entry in $copyPlan) {
    $target = Join-Path $targetRoot $entry.Relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $entry.Source -Destination $target
    $wanted = Hash-File $entry.Source
    if ((Hash-File $target) -ne $wanted) { throw "Copy verification failed: $($entry.Relative)" }
    $verification += [pscustomobject]@{ Path=$entry.Relative; Sha256=$wanted }
    $done++
    if ($done % 50 -eq 0) { Write-Output "Copied and verified $done / $($copyPlan.Count) files" }
}
New-Item -ItemType Directory -Path (Join-Path $targetRoot 'saves') -Force | Out-Null
$launcher = @'
@echo off
setlocal
cd /d "%~dp0game"
start "" "%~dp0game\valheim.exe" --doorstop-enabled true --doorstop-target-assembly "%~dp0BepInEx\core\BepInEx.Preloader.dll" -savedir "%~dp0saves" -logFile "%~dp0Player.log" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -console -thorskist-adapter-tests
'@
[IO.File]::WriteAllText((Join-Path $targetRoot 'Launch-Test-Valheim.cmd'),$launcher,[Text.Encoding]::ASCII)
$verification | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $targetRoot 'installation-manifest.json') -Encoding utf8
Write-Output "READY: $targetRoot"
Write-Output 'Launch-Test-Valheim.cmd starts the separate game copy. Create a new LOCAL test character and world; no original saves were copied.'
