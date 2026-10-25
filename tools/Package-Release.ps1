param([Parameter(Mandatory = $true)][string]$Binary)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $repo 'manifest.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
foreach ($field in @('name', 'description', 'version_number', 'website_url', 'dependencies')) {
    if ($manifest.PSObject.Properties.Name -notcontains $field) { throw "Missing manifest field: $field" }
}
if ($manifest.name -cne 'ThorsKist') { throw 'Keep the existing ThorsKist package identity when updating its listing.' }
if ($manifest.version_number -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be Major.Minor.Patch.' }
if ($manifest.description -isnot [string] -or $manifest.description.Length -gt 256) { throw 'Invalid Hexium description.' }
if ($manifest.website_url -isnot [string] -or $manifest.dependencies -isnot [Array]) { throw 'Invalid manifest URL or dependencies.' }
foreach ($dependency in $manifest.dependencies) {
    if ($dependency -isnot [string] -or $dependency -notmatch '^.+-[A-Za-z0-9_]+-\d+\.\d+\.\d+$') {
        throw "Invalid dependency: $dependency"
    }
}
$icon = [IO.File]::ReadAllBytes((Join-Path $repo 'icon.png'))
if ($icon.Length -lt 24 -or [BitConverter]::ToString($icon, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
    throw 'Package icon must be a PNG.'
}
$width = $icon[16] * 16777216 + $icon[17] * 65536 + $icon[18] * 256 + $icon[19]
$height = $icon[20] * 16777216 + $icon[21] * 65536 + $icon[22] * 256 + $icon[23]
if ($width -ne 256 -or $height -ne 256) { throw 'Hexium requires an exactly 256x256 icon.' }

# Construct the archive from an allowlist, never an existing staging directory.
# Old packages and developer/test files cannot leak into a new release.
$files = [ordered]@{
    'manifest.json' = $manifestPath
    'icon.png' = (Join-Path $repo 'icon.png')
    'README.md' = (Join-Path $repo 'README.md')
    'CHANGELOG.md' = (Join-Path $repo 'CHANGELOG.md')
    'LICENSE' = (Join-Path $repo 'LICENSE')
    'plugins/SharedInventory.dll' = $Binary
}
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$archivePath = Join-Path $dist ("ThorsKist-" + $manifest.version_number + '.zip')
$temporaryPath = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
function Get-StreamHash([IO.Stream]$InputStream) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($InputStream)).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }

    # Read the actual upload and compare every decompressed entry to its source.
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryPath)
    try {
        if ($archive.Entries.Count -ne $files.Count) { throw 'Unexpected release ZIP entries.' }
        foreach ($entry in $archive.Entries) {
            if (!$files.Contains($entry.FullName)) { throw "Unexpected release entry: $($entry.FullName)" }
            $packed = $entry.Open()
            $source = [IO.File]::OpenRead($files[$entry.FullName])
            try {
                if ((Get-StreamHash $packed) -ne (Get-StreamHash $source)) { throw "Release ZIP mismatch: $($entry.FullName)" }
            }
            finally { $packed.Dispose(); $source.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    if ((Get-Item -LiteralPath $temporaryPath).Length -gt 512MB) { throw 'Hexium package exceeds 512 MB.' }
    Move-Item -LiteralPath $temporaryPath -Destination $archivePath -Force
    Copy-Item -LiteralPath $archivePath -Destination (Join-Path $dist 'EinherjarChest.zip') -Force
    $stream = [IO.File]::OpenRead($archivePath)
    try { $digest = Get-StreamHash $stream }
    finally { $stream.Dispose() }
    [IO.File]::WriteAllText($archivePath + '.sha256', "$digest  $([IO.Path]::GetFileName($archivePath))`n", [Text.Encoding]::ASCII)
    Write-Host "PASS Hexium manifest, 256x256 icon, exact release contents and packaged DLL hashes"
    Write-Host "Upload: $archivePath"
    Write-Host "SHA256: $digest"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}
