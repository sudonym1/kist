param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [string]$BepInExDir = "",
    [switch]$SkipTests,
    [switch]$EnableTestHarness,
    [string]$Dotnet = ''
)

$ErrorActionPreference = "Stop"
$Dotnet = & (Join-Path $PSScriptRoot 'tools/Resolve-Dotnet.ps1') -Dotnet $Dotnet
$ValheimDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ValheimDir)
if ($BepInExDir) { $BepInExDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BepInExDir) }
Push-Location $PSScriptRoot
try {
    if (-not $BepInExDir) { $BepInExDir = Join-Path $ValheimDir "BepInEx" }

    if (-not $SkipTests) {
        & $Dotnet run --project (Join-Path $PSScriptRoot "tests/SharedInventory.Tests.csproj") -c Release "-p:ValheimDir=$ValheimDir"
        if ($LASTEXITCODE -ne 0) { throw "Regression tests failed." }
        & $Dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir"
        if ($LASTEXITCODE -ne 0) { throw "Protocol tests failed." }
        & $Dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- --lab-tests
        if ($LASTEXITCODE -ne 0) { throw "Load/fault harness checks failed." }
    }

    & $Dotnet build (Join-Path $PSScriptRoot "SharedInventory.csproj") -c Release `
        "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" "-p:EnableTestHarness=$($EnableTestHarness.IsPresent.ToString().ToLowerInvariant())"
    if ($LASTEXITCODE -ne 0) { throw "Mod build failed." }
    $binary = Join-Path $PSScriptRoot 'bin/Release/netstandard2.1/SharedInventory.dll'
    if ($EnableTestHarness) { $binary = Join-Path $PSScriptRoot 'bin/Release/testing/netstandard2.1/SharedInventory.dll' }

    # Release isolation is mandatory, including when regression tests are skipped.
    $verification = @('--verify-release', $binary, (Join-Path $PSScriptRoot 'manifest.json'))
    if ($EnableTestHarness) { $verification = @('--verify-harness', $binary, 'true') }
    & $Dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- @verification
    if ($LASTEXITCODE -ne 0) { throw "Release identity or test harness isolation check failed." }

    if (-not $SkipTests) {
        & $Dotnet run --project (Join-Path $PSScriptRoot "tools/ProtocolTests/ProtocolTests.csproj") -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir" -- `
            $binary `
            (Join-Path $BepInExDir "core") (Join-Path $ValheimDir "valheim_Data/Managed")
        if ($LASTEXITCODE -ne 0) { throw "Game resource hook checks failed." }
    }

    if (-not $EnableTestHarness) {
        & (Join-Path $PSScriptRoot 'tools/Package-Release.ps1') -Binary $binary
        return
    }

    $packageName = 'EinherjarChest-Testing'
    $package = Join-Path $PSScriptRoot "dist/$packageName"
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    Copy-Item $binary $package
    Copy-Item (Join-Path $PSScriptRoot "README.md") $package
    Copy-Item (Join-Path $PSScriptRoot "API.md") $package
    Copy-Item (Join-Path $PSScriptRoot "TESTING.md") $package
    Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-HANDOFF.md") $package
    Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-PHASE2-RESULTS.md") $package
    Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-PHASE3-RESULTS.md") $package
    Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-PHASE4-RESULTS.md") $package
    Copy-Item (Join-Path $PSScriptRoot "EINHERJAR-PHASE5-RESULTS.md") $package
    New-Item -ItemType Directory -Force -Path (Join-Path $package 'assets') | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'assets/README.md') (Join-Path $package 'assets/README.md')
    Compress-Archive -Path (Join-Path $package "*") `
        -DestinationPath (Join-Path $PSScriptRoot "dist/$packageName.zip") -Force
    Write-Host "Built dist/$packageName.zip. Copy SharedInventory.dll into BepInEx/plugins on the server and every client."
}
finally { Pop-Location }
