param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [string]$BepInExDir = "",
    [switch]$SkipTests,
    [switch]$EnableTestHarness
)

$ErrorActionPreference = "Stop"
if (-not $BepInExDir) { $BepInExDir = Join-Path $ValheimDir "BepInEx" }

if (-not $SkipTests) {
    & dotnet run --project (Join-Path $PSScriptRoot "tests/SharedInventory.Tests.csproj") -c Release "-p:ValheimDir=$ValheimDir"
    if ($LASTEXITCODE -ne 0) { throw "Regression tests failed." }
    & dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir"
    if ($LASTEXITCODE -ne 0) { throw "Protocol tests failed." }
    & dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- --lab-tests
    if ($LASTEXITCODE -ne 0) { throw "Load/fault harness checks failed." }
}

& dotnet build (Join-Path $PSScriptRoot "SharedInventory.csproj") -c Release `
    "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" "-p:EnableTestHarness=$($EnableTestHarness.IsPresent.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw "Mod build failed." }
$binary = Join-Path $PSScriptRoot 'bin/Release/netstandard2.1/SharedInventory.dll'
if ($EnableTestHarness) { $binary = Join-Path $PSScriptRoot 'bin/Release/testing/netstandard2.1/SharedInventory.dll' }

if (-not $SkipTests) {
    & dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- --verify-harness $binary $EnableTestHarness.IsPresent.ToString()
    if ($LASTEXITCODE -ne 0) { throw "Test harness build isolation check failed." }
    & dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- `
        $binary `
        (Join-Path $BepInExDir "core") (Join-Path $ValheimDir "valheim_Data/Managed")
    if ($LASTEXITCODE -ne 0) { throw "Game resource hook checks failed." }
}

$packageName = 'EinherjarChest'
if ($EnableTestHarness) { $packageName = 'EinherjarChest-Testing' }
$package = Join-Path $PSScriptRoot "dist/$packageName"
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item $binary $package
Copy-Item (Join-Path $PSScriptRoot "README.md") $package
Copy-Item (Join-Path $PSScriptRoot "API.md") $package
Copy-Item (Join-Path $PSScriptRoot "TESTING.md") $package
Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-HANDOFF.md") $package
Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-PHASE2-RESULTS.md") $package
New-Item -ItemType Directory -Force -Path (Join-Path $package 'assets') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'assets/README.md') (Join-Path $package 'assets/README.md')
Compress-Archive -Path (Join-Path $package "*") `
    -DestinationPath (Join-Path $PSScriptRoot "dist/$packageName.zip") -Force
Write-Host "Built dist/$packageName.zip. Copy SharedInventory.dll into BepInEx/plugins on the server and every client."
