param([string]$Destination, [string]$PayloadRoot = (Join-Path $PSScriptRoot 'payload'))
$ErrorActionPreference='Stop'
function Hash-File([string]$Path) {
    $s=[IO.File]::OpenRead($Path); $h=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($h.ComputeHash($s)).Replace('-','') } finally { $h.Dispose();$s.Dispose() }
}
if (!$Destination) {
    $Destination=Join-Path ([Environment]::GetFolderPath('Desktop')) 'ThorsKist-M7-Test'
    if (!(Test-Path -LiteralPath (Join-Path $Destination 'installation-manifest.json'))) { $Destination=Read-Host 'Full path to the separate M7 test installation' }
}
$target=[IO.Path]::GetFullPath($Destination.Trim('"')).TrimEnd('\')
$payload=[IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\')
if ($target -match '[\\/]steamapps[\\/]common[\\/]' -or !(Test-Path -LiteralPath (Join-Path $target 'installation-manifest.json'))) { throw 'Refusing to update a normal game installation. Select the separate M7 test folder.' }
$launcher=Get-Content -LiteralPath (Join-Path $target 'Launch-Test-Valheim.cmd') -Raw
if ($launcher -notmatch '\-thorskist-adapter-tests' -or $launcher -notmatch '%~dp0saves') { throw 'The selected folder does not have the guarded M7 test launcher.' }
if (@(Get-Process valheim -ErrorAction SilentlyContinue).Count) { throw 'Close Valheim before updating the test payload. No processes were stopped.' }
$manifest=Get-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or (Hash-File (Join-Path $target 'game/valheim_Data/Managed/assembly_valheim.dll')) -ne $manifest.GameAssemblySha256) { throw 'Test game version/manifest mismatch.' }
$entries=@()
foreach ($entry in $manifest.Files) {
    $source=[IO.Path]::GetFullPath((Join-Path $payload $entry.Path)); $dest=[IO.Path]::GetFullPath((Join-Path $target $entry.Path))
    if (!$source.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase) -or !$dest.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escapes the selected test folder.' }
    if ($entry.Path -notmatch '^(BepInEx[\\/](core|plugins)[\\/]|game[\\/](winhttp\.dll|\.doorstop_version)$)') { throw 'Unexpected update destination.' }
    if ((Hash-File $source) -ne $entry.Sha256) { throw "Payload checksum mismatch: $($entry.Path)" }
    $walk=$dest
    while ($walk -and $walk.Length -ge $target.Length) {
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked test destination rejected.' }
        $walk=Split-Path $walk -Parent
    }
    $entries += [pscustomobject]@{ Source=$source; Target=$dest; Entry=$entry }
}
foreach ($entry in $entries) {
    New-Item -ItemType Directory -Force (Split-Path $entry.Target -Parent) | Out-Null
    Copy-Item -LiteralPath $entry.Source -Destination $entry.Target
    if ((Hash-File $entry.Target) -ne $entry.Entry.Sha256) { throw 'Updated file checksum mismatch.' }
}
Copy-Item -LiteralPath (Join-Path $payload 'payload-manifest.json') -Destination (Join-Path $target 'test-payload-manifest.json')
Write-Output "Updated only the isolated test payload in $target. Test characters/worlds were preserved."
