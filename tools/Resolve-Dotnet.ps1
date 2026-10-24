param([string]$Dotnet = '')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($Dotnet) {
    $command = Get-Command $Dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw "Cannot find dotnet executable '$Dotnet'. Pass -Dotnet <path-to-dotnet.exe>." }
    $Dotnet = $command.Source
}
$candidates = @($Dotnet)
if (-not $Dotnet) {
    $candidates = @('dotnet')
    foreach ($root in @($env:DOTNET_ROOT, $env:DOTNET_ROOT_X64,
            (Join-Path $repo '.tools/dotnet'), (Join-Path $repo '.work/dotnet'))) {
        if ($root) { $candidates += Join-Path $root 'dotnet.exe' }
    }
    if ($env:USERPROFILE) { $candidates += Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
}

# SDK selection follows the repository's global.json even when called elsewhere.
Push-Location $repo
try {
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        # --list-sdks works even when this host has no SDK matching global.json.
        $sdks = & $command.Source --list-sdks
        if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^8\.0\.\d+ \[')) { continue }
        $version = & $command.Source --version
        if ($LASTEXITCODE -ne 0 -or $version -notmatch '^8\.0\.\d+$') { continue }
        Write-Host "Using .NET SDK $version ($($command.Source))."
        return $command.Source
    }
    throw 'A stable .NET 8 SDK is required. Install it, set DOTNET_ROOT to its installation directory, or pass -Dotnet <path-to-dotnet.exe>.'
}
finally { Pop-Location }
