param(
    [string]$Scenario = 'baseline',
    [switch]$All,
    [string]$Dotnet = '',
    [string]$ValheimDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$BepInExDir = '',
    [string]$OutputDirectory = '',
    [string]$Baseline = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$Dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -Dotnet $Dotnet
$ValheimDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ValheimDir)
if ($BepInExDir) { $BepInExDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BepInExDir) }
if ($OutputDirectory) { $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) }
if ($Baseline) { $Baseline = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Baseline) }
if (Test-Path -LiteralPath $Scenario -PathType Leaf) { $Scenario = (Resolve-Path -LiteralPath $Scenario).Path }
Push-Location $repo
try {
    if (-not $BepInExDir) { $BepInExDir = Join-Path $ValheimDir 'BepInEx' }
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo '.work/load-reports' }
    if ($All -and $Baseline) { throw 'Use a single scenario when comparing against a baseline.' }
    $project = Join-Path $PSScriptRoot 'ProtocolTests/ProtocolTests.csproj'
    & $Dotnet build $project -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir"
    if ($LASTEXITCODE -ne 0) { throw 'Protocol load runner build failed.' }
    $runner = Join-Path $PSScriptRoot 'ProtocolTests/bin/Release/net8.0/ProtocolTests.dll'
    $names = @($Scenario)
    if ($All) { $names = @('baseline', 'slow-network', 'polling-burst', 'scarce-stock', 'lost-confirmation', 'backlog-recovery', 'slow-storage') }
    $failed = @()
    foreach ($name in $names) {
        $scenarioPath = Join-Path $PSScriptRoot "ProtocolTests/scenarios/$name.json"
        if (Test-Path -LiteralPath $name -PathType Leaf) { $scenarioPath = (Resolve-Path -LiteralPath $name).Path }
        $reportName = [IO.Path]::GetFileNameWithoutExtension($scenarioPath) + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json'
        $arguments = @($runner, '--load', $scenarioPath, '--output', (Join-Path $OutputDirectory $reportName))
        if ($Baseline) { $arguments += @('--baseline', $Baseline) }
        & $Dotnet @arguments
        if ($LASTEXITCODE -ne 0) { $failed += $name }
    }
    if ($failed.Count) { throw "Load scenarios failed: $($failed -join ', ')" }
}
finally { Pop-Location }
