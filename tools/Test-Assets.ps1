param(
    [Parameter(Mandatory = $true)][string]$TestRoot,
    [ValidateSet('Review', 'Capture', 'Headless')][string]$Mode = 'Review',
    [ValidateSet('Assets', 'Access', 'Ritual', 'Outcomes')][string]$Suite = 'Assets',
    [switch]$ExitAfter,
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'
if ($Suite -ne 'Assets' -and $Mode -eq 'Capture') { throw 'Gameplay tests support Review and Headless modes.' }
$root = [IO.Path]::GetFullPath($TestRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$game = Join-Path $root 'game'
$repo = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $game 'valheim.exe'
$marker = Join-Path $root '.sharedinventory-test'
if (!(Test-Path -LiteralPath $marker) -or ([IO.File]::ReadAllText($marker).Trim() -ne 'sharedinventory-test-v1')) {
    throw 'Use an existing marked isolated test copy, prepared with WINDOWS-CODEX-SETUP.md.'
}
foreach ($required in @($executable, (Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll'),
        (Join-Path $game 'BepInEx/plugins/SharedInventory.TestIsolation.dll'), (Join-Path $root 'saves'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing guarded test input: $required" }
}
foreach ($process in @(Get-Process valheim -ErrorAction SilentlyContinue)) {
    if (!$process.Path -or [string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Close this isolated game normally before updating or relaunching it.'
    }
}
$config = [IO.File]::ReadAllText((Join-Path $game 'BepInEx/config/local.sharedinventory.cfg'))
$testing = [regex]::Match($config, '(?ms)^\[Testing\]\s*$(.*?)(?=^\[|\z)').Groups[1].Value
if ($testing -notmatch '(?m)^Enabled\s*=\s*true\s*$' -or $testing -match '(?m)^Impairments\s*=\s*true\s*$') {
    throw 'Asset tests require [Testing] Enabled = true and Impairments = false in the isolated config.'
}
$isolation = [IO.File]::ReadAllText((Join-Path $game 'BepInEx/config/local.sharedinventory.testisolation.cfg'))
if ($isolation -notmatch '(?m)^FreeBuild\s*=\s*false\s*$') {
    throw 'Set the isolation helper Testing / FreeBuild = false to exercise real construction costs.'
}
$Dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -Dotnet $Dotnet
Push-Location $repo
try {
    & $Dotnet build (Join-Path $repo 'SharedInventory.csproj') -c Release -p:EnableTestHarness=true "-p:ValheimDir=$game"
    if ($LASTEXITCODE -ne 0) { throw 'Asset test build failed.' }
}
finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $repo 'bin/Release/testing/netstandard2.1/SharedInventory.dll') `
    -Destination (Join-Path $game 'BepInEx/plugins/SharedInventory.dll') -Force
$arguments = '--doorstop-enabled true --doorstop-target-assembly "' + (Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll') +
    '" -sharedinventory-test -savedir "' + (Join-Path $root 'saves') + '" -logFile "' + (Join-Path $root "asset-$Mode.log") + '" -console'
switch ($Mode) {
    'Review' {
        $arguments += ' -screen-fullscreen 0 -screen-width 1280 -screen-height 720'
        if ($Suite -eq 'Outcomes') { $arguments += ' -einherjar-outcome-tests' }
        elseif ($Suite -eq 'Ritual') { $arguments += ' -einherjar-ritual-tests' }
        elseif ($Suite -eq 'Access') { $arguments += ' -einherjar-access-tests' }
        else { $arguments += ' -einherjar-asset-tests -einherjar-capture-assets' }
    }
    'Capture' { $arguments += ' -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -einherjar-capture-assets -einherjar-exit-after-capture' }
    'Headless' {
        $arguments += ' -batchmode -nographics'
        if ($Suite -eq 'Outcomes') { $arguments += ' -einherjar-outcome-tests -einherjar-exit-after-outcomes' }
        elseif ($Suite -eq 'Ritual') { $arguments += ' -einherjar-ritual-tests -einherjar-exit-after-ritual' }
        elseif ($Suite -eq 'Access') { $arguments += ' -einherjar-access-tests -einherjar-exit-after-access' }
        else { $arguments += ' -einherjar-asset-tests -einherjar-exit-after-assets' }
    }
}
# The isolation helper independently validates these arguments and redirects all
if ($ExitAfter) {
    if ($Suite -eq 'Outcomes') { $arguments += ' -einherjar-exit-after-outcomes' }
    elseif ($Suite -eq 'Ritual') { $arguments += ' -einherjar-exit-after-ritual' }
    elseif ($Suite -eq 'Access') { $arguments += ' -einherjar-exit-after-access' }
    else { $arguments += ' -einherjar-exit-after-assets' }
}
# saves. Review mode can be brought forward through its returned game window.
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $game -WindowStyle Hidden -PassThru
Write-Output "Started guarded $Mode mode, PID $($process.Id). Reports and captures: $root"
