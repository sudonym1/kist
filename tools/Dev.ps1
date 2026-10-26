param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Create', 'Build', 'Run', 'Clean', 'CleanAll')][string]$Action,
    [string]$ValheimDir = $env:VALHEIM_DIR,
    [string]$BepInExPack = $env:BEPINEX_PACK,
    [string]$Dotnet = $env:DOTNET,
    [string]$TestDataDir = $env:TEST_DATA_DIR,
    [switch]$EnableTestHarness
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo '.work/isolated'
$game = Join-Path $root 'game'
$executable = Join-Path $game 'valheim.exe'
$marker = Join-Path $root '.sharedinventory-test'
$markerValue = 'sharedinventory-test-v1'
$projects = @('', 'tests', 'tools/ProtocolTests', 'tools/TestIsolation')

# Every deletion is confined to this worktree and rejects links/junctions.
function Assert-LocalTree([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside this worktree: $full"
    }
    $current = $full
    while ($current -ne $repo) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked path: $current"
            }
        }
        $current = Split-Path $current -Parent
    }
    if ((Test-Path -LiteralPath $full) -and
        @(Get-ChildItem -LiteralPath $full -Force -Recurse -Attributes ReparsePoint).Count) {
        throw "Linked files beneath: $full"
    }
}

function Assert-MarkedEnvironment {
    Assert-LocalTree $root
    if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or
        [IO.File]::ReadAllText($marker).Trim() -ne $markerValue) {
        throw "Refusing to use an unmarked environment: $root"
    }
}

function Assert-ReadyEnvironment {
    Assert-MarkedEnvironment
    foreach ($relative in @('game/valheim.exe', 'game/UnityPlayer.dll',
            'game/valheim_Data/Managed/assembly_valheim.dll', 'game/MonoBleedingEdge',
            'game/BepInEx/core/BepInEx.dll', 'game/BepInEx/core/BepInEx.Preloader.dll',
            'game/winhttp.dll', 'game/doorstop_config.ini', 'saves', '.ready')) {
        if (!(Test-Path -LiteralPath (Join-Path $root $relative))) {
            throw "Incomplete environment ($relative). Run 'just clean-all', then retry."
        }
    }
}

function Assert-GameStopped {
    foreach ($process in @(Get-Process valheim -ErrorAction SilentlyContinue)) {
        if (!$process.Path -or [string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Exit this isolated game normally before updating, deleting, or relaunching it.'
        }
    }
}

function Get-TestData {
    if (!$TestDataDir) {
        $commonGit = & git -C $repo rev-parse --path-format=absolute --git-common-dir
        if ($LASTEXITCODE -ne 0) { throw 'Cannot locate shared test data. Set TEST_DATA_DIR.' }
        $TestDataDir = Join-Path (Split-Path $commonGit -Parent) '.work/test-data'
    }
    $directory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TestDataDir)
    $manifestPath = Join-Path $directory 'manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Missing preserved test data: $manifestPath. Set TEST_DATA_DIR to a snapshot containing manifest.json, saves, and game/BepInEx/config."
    }
    if (((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $directory -Force -Recurse -Attributes ReparsePoint).Count) {
        throw "Linked test data: $directory"
    }
    $snapshot = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($snapshot.Schema -ne 1 -or !@($snapshot.Files).Count) { throw "Invalid test data manifest: $manifestPath" }
    foreach ($file in $snapshot.Files) {
        if ($file.Path -notmatch '^(saves/|game/BepInEx/config/)' -or
            $file.Path -match '(^|[/\\])\.\.([/\\]|$)' -or $file.Path -match '[:\\]') {
            throw "Invalid test data path: $($file.Path)"
        }
        $source = Join-Path $directory $file.Path
        if (!(Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.SHA256) {
            throw "Preserved test data checksum mismatch: $source"
        }
    }
    return [pscustomobject]@{ Directory = $directory; Files = @($snapshot.Files) }
}

if ($Action -in @('Clean', 'CleanAll')) {
    $outputs = @()
    foreach ($project in $projects) {
        foreach ($name in @('bin', 'obj')) {
            $outputs += Join-Path (Join-Path $repo $project) $name
        }
    }
    $outputs += Join-Path $repo 'dist'
    if ($Action -eq 'CleanAll' -and (Test-Path -LiteralPath $root)) {
        Assert-MarkedEnvironment
        Assert-GameStopped
        $outputs += $root
    }
    # Validate every target before removing anything.
    foreach ($output in $outputs) { Assert-LocalTree $output }
    foreach ($output in $outputs) {
        if (Test-Path -LiteralPath $output) {
            Remove-Item -LiteralPath $output -Recurse -Force
            Write-Host "Removed $output"
        }
    }
    return
}

if ($Action -eq 'Create') {
    Assert-LocalTree $root
    if (Test-Path -LiteralPath $root) {
        Assert-ReadyEnvironment
        Write-Host "Using existing isolated environment: $root"
        return
    }
    # Shared across worktrees, outside the disposable environment and clean-all.
    # Validate the complete snapshot before copying the game or restoring saves.
    $testData = Get-TestData
    if (!$ValheimDir) { $ValheimDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim' }
    if (!$BepInExPack) {
        $BepInExPack = Join-Path $env:APPDATA 'com.kesomannen.gale/cache/denikson-BepInExPack_Valheim/5.4.2351'
    }
    $ValheimDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ValheimDir)
    $BepInExPack = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BepInExPack)
    $gameEntries = @('valheim.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe',
        'steam_appid.txt', 'MonoBleedingEdge', 'valheim_Data', 'D3D12')
    $bootstrap = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
    foreach ($required in @('valheim.exe', 'UnityPlayer.dll', 'MonoBleedingEdge',
            'valheim_Data/Managed/assembly_valheim.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $ValheimDir $required))) {
            throw "Missing Valheim input: $required. Set VALHEIM_DIR to your vanilla game installation."
        }
    }
    foreach ($required in @('BepInEx/core/BepInEx.dll', 'BepInEx/core/BepInEx.Preloader.dll') + $bootstrap) {
        if (!(Test-Path -LiteralPath (Join-Path $BepInExPack $required))) {
            throw "Missing BepInEx input: $required. Set BEPINEX_PACK to an extracted clean BepInEx 5 pack."
        }
    }
    # Copy only game data and clean loader files, never existing plugins or saves.
    $copies = @{}
    foreach ($name in $gameEntries) {
        $source = Join-Path $ValheimDir $name
        if (Test-Path -LiteralPath $source) { $copies[$source] = Join-Path $game $name }
    }
    $copies[(Join-Path $BepInExPack 'BepInEx/core')] = Join-Path $game 'BepInEx/core'
    foreach ($name in $bootstrap) { $copies[(Join-Path $BepInExPack $name)] = Join-Path $game $name }
    foreach ($source in $copies.Keys) {
        $item = Get-Item -LiteralPath $source -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $source -Recurse -Force -Attributes ReparsePoint).Count)) {
            throw "Linked input: $source"
        }
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [IO.File]::WriteAllText($marker, $markerValue)
    New-Item -ItemType Directory -Path $game, (Join-Path $root 'saves'),
        (Join-Path $game 'BepInEx/plugins') -Force | Out-Null
    foreach ($source in $copies.Keys) {
        Copy-Item -LiteralPath $source -Destination $copies[$source] -Recurse
    }
    foreach ($file in $testData.Files) {
        $destination = Join-Path $root $file.Path
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $testData.Directory $file.Path) -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.SHA256) {
            throw "Restored test data checksum mismatch: $destination"
        }
    }
    [IO.File]::WriteAllText((Join-Path $root '.ready'), 'Created by tools/Dev.ps1')
    Write-Host "Created isolated environment: $root (restored $($testData.Files.Count) test data files)"
    return
}

Assert-ReadyEnvironment
if ($Action -eq 'Build') {
    $Dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -Dotnet $Dotnet
    & (Join-Path $repo 'build.ps1') -ValheimDir $game -BepInExDir (Join-Path $game 'BepInEx') -Dotnet $Dotnet
    & $Dotnet build (Join-Path $PSScriptRoot 'TestIsolation/TestIsolation.csproj') -c Release `
        "-p:ValheimDir=$game" "-p:BepInExDir=$(Join-Path $game 'BepInEx')"
    if ($LASTEXITCODE -ne 0) { throw 'Isolation plugin compilation failed.' }
    $manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.json') -Raw | ConvertFrom-Json
    $package = Join-Path $repo ("dist/{0}-{1}.zip" -f $manifest.name, $manifest.version_number)
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing production package: $package" }
    Write-Host "Hexium upload (production; testing menu excluded): $package"
    return
}

Assert-GameStopped
$modBinary = 'bin/Release/netstandard2.1/SharedInventory.dll'
if ($EnableTestHarness) { $modBinary = 'bin/Release/testing/netstandard2.1/SharedInventory.dll' }
foreach ($relative in @($modBinary,
        'tools/TestIsolation/bin/Release/netstandard2.1/SharedInventory.TestIsolation.dll')) {
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination (Join-Path $game 'BepInEx/plugins') -Force
}
# The isolation plugin also disables cloud save enumeration; -savedir alone is insufficient.
$arguments = '--doorstop-enabled true --doorstop-target-assembly "' +
    (Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll') + '" -sharedinventory-test -savedir "' +
    (Join-Path $root 'saves') + '" -logFile "' + (Join-Path $root 'Player.log') +
    '" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -console'
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Normal -PassThru
Write-Host "Launched isolated Valheim (PID $($process.Id)). Test saves: $(Join-Path $root 'saves')"
