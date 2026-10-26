param(
    [Parameter(Mandatory = $true)][string]$TestRoot,
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [ValidateNotNullOrEmpty()][string]$PlayerUser = $env:USERNAME
)

# Run on the joining Windows computer, after its isolated game has exited.
# Payload: the two built plugins and CLIENT-MANIFEST.json from the host.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
$payload = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$game = Join-Path $root 'game'
$exe = Join-Path $game 'valheim.exe'
$plugins = Join-Path $game 'BepInEx\plugins'
$names = @('SharedInventory.dll', 'SharedInventory.TestIsolation.dll')

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function SaveHashes {
    @(Get-ChildItem -LiteralPath (Join-Path $root 'saves') -File -Recurse -Force |
        Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName.Substring($root.Length + 1); SHA256 = (Hash $_.FullName) }
        })
}

if (!(Test-Path -LiteralPath "$root\.sharedinventory-test") -or
    [IO.File]::ReadAllText("$root\.sharedinventory-test").Trim() -ne 'sharedinventory-test-v1') {
    throw 'Use the existing marked isolated test directory.'
}
foreach ($path in @($root, $game, $plugins, (Join-Path $root 'saves'), $payload)) {
    $item = Get-Item -LiteralPath $path -Force
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected an ordinary directory: $path"
    }
    if (@(Get-ChildItem -LiteralPath $path -Force -Recurse -Attributes ReparsePoint).Count) {
        throw "Linked files/directories beneath: $path"
    }
}
if ($payload -eq $root -or !$payload.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Stage the payload in its own directory beneath the marked test root.'
}
foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='valheim.exe'")) {
    if (!$process.ExecutablePath -or [string]::Equals($process.ExecutablePath, $exe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Exit this isolated game normally before updating. No process was stopped.'
    }
}
foreach ($path in @($exe, "$root\Apply-Update.ps1", "$root\Launch-Test.cmd", "$game\BepInEx\core\BepInEx.Preloader.dll")) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing existing input: $path" }
}
foreach ($name in $names) {
    if (!(Test-Path -LiteralPath (Join-Path $plugins $name) -PathType Leaf)) { throw "Missing existing plugin: $name" }
}
$otherPlugins = @(Get-ChildItem -LiteralPath $plugins -File -Recurse | Where-Object { $_.Extension -eq '.dll' -and $_.Name -notin $names })
if ($otherPlugins.Count -or @(Get-ChildItem -LiteralPath $plugins -Filter *.dll -Recurse).Count -ne 2) {
    throw 'Expected only the mod and isolation plugin in this test installation.'
}
$manifest = Get-Content -LiteralPath (Join-Path $payload 'CLIENT-MANIFEST.json') -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or $manifest.Build -ne 'Testing' -or !$manifest.ModVersion -or
    @($manifest.Plugins).Count -ne 2 -or @($manifest.GameFiles).Count -ne 4) { throw 'Invalid client manifest.' }
$gameFiles = @('valheim.exe', 'UnityPlayer.dll', 'valheim_Data\Managed\assembly_valheim.dll', 'valheim_Data\Managed\assembly_utils.dll')
foreach ($name in $names) {
    $record = @($manifest.Plugins | Where-Object Path -EQ $name)
    if ($record.Count -ne 1 -or (Hash (Join-Path $payload $name)) -ne $record[0].SHA256) { throw "Payload checksum mismatch: $name" }
}
foreach ($name in $gameFiles) {
    $record = @($manifest.GameFiles | Where-Object Path -EQ $name)
    if ($record.Count -ne 1 -or (Hash (Join-Path $game $name)) -ne $record[0].SHA256) { throw "Game differs from host: $name" }
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'SharedInventory.dll')).FileVersion -ne $manifest.ModVersion) {
    throw 'Mod file version differs from manifest.'
}
$configPath = Join-Path $game 'BepInEx\config\local.sharedinventory.cfg'
$isolationPath = Join-Path $game 'BepInEx\config\local.sharedinventory.testisolation.cfg'
$config = [IO.File]::ReadAllText($configPath)
$testing = [regex]::Match($config, '(?ms)^\[Testing\]\s*$(.*?)(?=^\[|\z)').Groups[1].Value
if ($testing -notmatch '(?m)^Enabled\s*=\s*true\s*$' -or $testing -notmatch '(?m)^Impairments\s*=\s*false\s*$' -or
    [IO.File]::ReadAllText($isolationPath) -notmatch '(?m)^FreeBuild\s*=\s*false\s*$') {
    throw 'Use Testing Enabled=true, Impairments=false and isolation FreeBuild=false before updating.'
}
$launcher = [IO.File]::ReadAllText("$root\Launch-Test.cmd")
if ($launcher -notmatch '-sharedinventory-test' -or $launcher -notmatch '-savedir' -or
    $launcher -notmatch 'Apply-Update.ps1' -or $launcher -notmatch [regex]::Escape($PlayerUser)) {
    throw 'Expected the existing player-only guarded launcher with staged updates.'
}

$before = @(SaveHashes)
$configBefore = @((Hash $configPath), (Hash $isolationPath))
$backup = Join-Path $root ('backups\before-client-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($relative in @('saves', 'game\BepInEx\config', 'game\BepInEx\plugins', 'pending-update',
        'Launch-Test.cmd', 'Apply-Update.ps1', 'SharedInventory.next.dll', 'Sync-Pyramid-Client.ps1', 'CLIENT-UPDATE-RESULTS.json')) {
    $source = Join-Path $root $relative
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $backup $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
    }
}
foreach ($file in @(Get-ChildItem -LiteralPath $backup -File -Recurse -Force)) {
    $relative = $file.FullName.Substring($backup.Length + 1)
    if ((Hash $file.FullName) -ne (Hash (Join-Path $root $relative))) { throw "Backup verification failed: $relative" }
}
# The old sync helper embeds the 0.4.1 hash and could reinstall its old incoming
# DLL. Preserve its task XML but disable it; the normal launch task is retained.
$legacyTask = Get-ScheduledTask -TaskName "SharedInventory-$PlayerUser-Sync-20261023" -ErrorAction SilentlyContinue
if ($legacyTask) {
    Export-ScheduledTask -TaskName $legacyTask.TaskName | Set-Content -LiteralPath (Join-Path $backup 'legacy-sync-task.xml') -Encoding UTF8
    Disable-ScheduledTask -TaskName $legacyTask.TaskName | Out-Null
}
$pending = Join-Path $root 'pending-update'
New-Item -ItemType Directory -Force -Path $pending | Out-Null
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $pending $name) -Force }
# Apply both staged DLLs now, and retain the same pair for every future launch.
& (Join-Path $root 'Apply-Update.ps1')
foreach ($name in $names) {
    $expected = ($manifest.Plugins | Where-Object Path -EQ $name).SHA256
    if ((Hash (Join-Path $plugins $name)) -ne $expected -or (Hash (Join-Path $pending $name)) -ne $expected) {
        throw "Installed/staged checksum mismatch: $name. Restore the verified backup before launching."
    }
}
$after = @(SaveHashes)
if ((ConvertTo-Json -InputObject $before -Compress) -ne (ConvertTo-Json -InputObject $after -Compress) -or
    $configBefore[0] -ne (Hash $configPath) -or $configBefore[1] -ne (Hash $isolationPath)) {
    throw 'Saves or configuration changed during the update; inspect the backup before launching.'
}
$result = [ordered]@{
    UpdatedAt = (Get-Date).ToString('o'); Computer = $env:COMPUTERNAME; TestRoot = $root
    ModVersion = $manifest.ModVersion; Commit = $manifest.Commit; Backup = $backup
    Plugins = $manifest.Plugins; GameFiles = $manifest.GameFiles
    SaveFilesVerified = $before.Count; SavesUnchanged = $true; ConfigurationUnchanged = $true
    LegacySyncTaskDisabled = [bool]$legacyTask; RuntimeStartupVerified = $false; MultiplayerGameplayVerified = $false
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$root\CLIENT-UPDATE-RESULTS.json" -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $payload 'CLIENT-MANIFEST.json') -Destination "$root\CLIENT-MANIFEST.json" -Force
Write-Output ($result | ConvertTo-Json -Depth 6)
