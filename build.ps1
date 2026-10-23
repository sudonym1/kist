param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [string]$BepInExDir = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
if (-not $BepInExDir) { $BepInExDir = Join-Path $ValheimDir "BepInEx" }

if (-not $SkipTests) {
    & dotnet run --project (Join-Path $PSScriptRoot "tests/SharedInventory.Tests.csproj") -c Release "-p:ValheimDir=$ValheimDir"
    if ($LASTEXITCODE -ne 0) { throw "Regression tests failed." }
}

& dotnet build (Join-Path $PSScriptRoot "SharedInventory.csproj") -c Release `
    "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) { throw "Mod build failed." }

$package = Join-Path $PSScriptRoot "dist/SharedInventory"
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item (Join-Path $PSScriptRoot "bin/Release/netstandard2.1/SharedInventory.dll") $package
Copy-Item (Join-Path $PSScriptRoot "README.md") $package
Copy-Item (Join-Path $PSScriptRoot "API.md") $package
Compress-Archive -Path (Join-Path $package "*") `
    -DestinationPath (Join-Path $PSScriptRoot "dist/SharedInventory.zip") -Force
Write-Host "Built dist/SharedInventory.zip. Copy SharedInventory.dll into BepInEx/plugins on the server and every client."
