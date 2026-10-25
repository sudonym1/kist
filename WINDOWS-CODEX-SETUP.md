# Windows Codex handoff: isolated SharedInventory test

Carry out this setup on Windows. The user has authorized a separate test environment using synthetic test stock. Prepare it for the user to play-test. Build and test the actual DLLs; fix compilation problems in the isolated source copy if needed, and retain a patch/report of any changes. The WSL authoring session checked syntax and game API signatures but could not execute Windows .NET or run the C# tests.

The implementation must retain the agreed scope: server only stores prefab counts; clients are fully trusted; only List, Deposit, Withdrawal requests; client owns inventory capacity, stack sizes, metadata removal, and returns. Keep Valheim's RPC transport, with package encoding/decoding, database work, and saves off-thread. Do not add receipt systems, server item rules, or a separate transport.

## Locate inputs

For TestPlayer's existing second-computer client on `pyramid`, use
[PYRAMID-TEST-CLIENT.md](PYRAMID-TEST-CLIENT.md). That procedure updates its existing
install and preserves the test character; the initial clean-copy setup below is
historical and should not replace that client.

These paths were verified from WSL on October 23, 2026. Confirm they still exist before use:

| Input | Windows location |
| --- | --- |
| New mod source | `\\wsl.localhost\<DISTRO>\home\sconnor\tmp\SharedInventory` |
| Vanilla game files | `C:\Program Files (x86)\Steam\steamapps\common\Valheim` |
| Existing .NET 8.0.318 SDK | `C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe` |
| Clean BepInEx pack | `%APPDATA%\com.kesomannen.gale\cache\denikson-BepInExPack_Valheim\5.4.2351` |

Use `wsl.exe --list --quiet` to identify the distribution containing the mod folder. Do not assume the default distribution is correct. If the UNC source is unavailable, obtain the project folder from that WSL distribution; do not recreate the project from this document.

Alternatively, extract the supplied `SharedInventory-Windows-Test-Source.zip` and set `$RepoSource` to its extracted `SharedInventory` folder. This source bundle includes the exporter, test isolation helper, and all instructions; it does not include game binaries or compiled mod DLLs.

Personal inventory snapshots are not included. Use newly created test stock.

## Create a separate game copy

Use a new destination, for example `%USERPROFILE%\Desktop\SharedInventory-Test-20261023`. If it already exists, select another new destination. Preserve the existing directory. The source game, Gale profiles, original saves, and ThorsKist snapshots are read-only inputs to this work. Do not change Steam settings or cloud preferences. Do not stop an unrelated game process to make room for testing.

In a single PowerShell session, substitute the correct distribution:

```powershell
$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:USERPROFILE 'Desktop\SharedInventory-Test-20261023'
$RepoSource = '\\wsl.localhost\<DISTRO>\home\sconnor\tmp\SharedInventory'
$GameSource = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
$Pack = Join-Path $env:APPDATA 'com.kesomannen.gale\cache\denikson-BepInExPack_Valheim\5.4.2351'
$Dotnet = Join-Path $env:USERPROFILE 'ThorsKist\.tools\dotnet\dotnet.exe'
$Game = Join-Path $Root 'game'
$Source = Join-Path $Root 'source'

if (Test-Path -LiteralPath $Root) { throw 'Choose a new test destination.' }
foreach ($required in @("$GameSource\valheim.exe", "$Pack\BepInEx\core\BepInEx.dll", $Dotnet)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing input: $required" }
}
& $Dotnet --info
if ($LASTEXITCODE -ne 0) { throw '.NET SDK did not run.' }

New-Item -ItemType Directory -Path $Root, $Game, "$Root\saves" | Out-Null
Copy-Item -LiteralPath $RepoSource -Destination $Source -Recurse
[IO.File]::WriteAllText("$Root\.sharedinventory-test", 'sharedinventory-test-v1')

# Copy game binaries/data only. No production mods, configs, saves, links, or junctions.
$gameEntries = @('valheim.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe',
    'steam_appid.txt', 'MonoBleedingEdge', 'valheim_Data', 'D3D12')
foreach ($name in $gameEntries) {
    $inputPath = Join-Path $GameSource $name
    if (!(Test-Path -LiteralPath $inputPath)) { continue }
    $item = Get-Item -LiteralPath $inputPath -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked input: $inputPath" }
    if ($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $inputPath -Force -Recurse -Attributes ReparsePoint).Count) {
        throw "Linked input beneath: $inputPath"
    }
    Copy-Item -LiteralPath $inputPath -Destination (Join-Path $Game $name) -Recurse
}

# Bootstrap a clean BepInEx installation; copy no Gale plugins or patchers.
New-Item -ItemType Directory -Path "$Game\BepInEx", "$Game\BepInEx\plugins" | Out-Null
Copy-Item -LiteralPath "$Pack\BepInEx\core" -Destination "$Game\BepInEx\core" -Recurse
foreach ($name in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
    Copy-Item -LiteralPath (Join-Path $Pack $name) -Destination (Join-Path $Game $name)
}

foreach ($relative in @('valheim.exe', 'valheim_Data\Managed\assembly_valheim.dll')) {
    if ((Get-FileHash (Join-Path $GameSource $relative)).Hash -ne
        (Get-FileHash (Join-Path $Game $relative)).Hash) { throw "Game copy differs: $relative" }
}
```

Verify copy operations produced ordinary files, not hardlinks to original installations. The test must contain neither ThorsKist nor any plugin from the ordinary Gale profile. Do not launch it yet.

## Compile the mod and the test-only isolation plugin

The included `tools\TestIsolation` project is separate test tooling, not part of the shipped mod. It redirects the save root, disables cloud provider/enumeration in this process, filters character/world lists to local files, checks the marked test layout, and logs the current host world UID. Steam authentication/networking remain available. This is needed because `-savedir` alone is insufficient to hide cloud saves, and the existing ThorsKist test tooling documents a client path where it is not parsed.

```powershell
Push-Location $Source
try {
    & $Dotnet run --project tests\SharedInventory.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & $Dotnet build SharedInventory.csproj -c Release "-p:ValheimDir=$Game" "-p:BepInExDir=$Game\BepInEx"
    if ($LASTEXITCODE -ne 0) { throw 'Mod compilation failed.' }
    & $Dotnet build tools\TestIsolation\TestIsolation.csproj -c Release "-p:ValheimDir=$Game" "-p:BepInExDir=$Game\BepInEx"
    if ($LASTEXITCODE -ne 0) { throw 'Isolation plugin compilation failed.' }
    Copy-Item bin\Release\netstandard2.1\SharedInventory.dll "$Game\BepInEx\plugins\SharedInventory.dll"
    Copy-Item tools\TestIsolation\bin\Release\netstandard2.1\SharedInventory.TestIsolation.dll "$Game\BepInEx\plugins\SharedInventory.TestIsolation.dll"
} finally { Pop-Location }
```

Expect **18 passing regression cases**, including off-thread worker execution/order and nonblocking shutdown. A test-double pass is not an in-game pass. Resolve any missing assembly references or game API changes before continuing. Never bypass the isolation plugin to get a menu running. Do not copy reference DLLs or test-double assemblies into the game.

Only the two newly built plugin DLLs should be under the test game's `BepInEx\plugins` directory. Record their SHA-256 hashes in the handoff report.

## Make the guarded launcher

```powershell
$launcher = @'
@echo off
setlocal
cd /d "%~dp0game"
"%~dp0game\valheim.exe" --doorstop-enabled true --doorstop-target-assembly "%~dp0game\BepInEx\core\BepInEx.Preloader.dll" -sharedinventory-test -savedir "%~dp0saves" -logFile "%~dp0Player.log" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -console
'@
[IO.File]::WriteAllText("$Root\Launch-Test.cmd", $launcher, [Text.Encoding]::ASCII)
```

Launch only `Launch-Test.cmd`, with Steam already running. Verify the process executable is under `$Root\game`, the command line includes the exact isolated save path, and `game\BepInEx\LogOutput.log` contains:

```text
SHARED INVENTORY LOCAL-ONLY TEST ISOLATION: <test root>\saves
```

The character list should initially be empty. If normal characters or worlds appear, exit the test process and correct isolation before continuing. Do not select them. Keep the guard active throughout testing; starting `valheim.exe` directly is not the supported test launch.

## Create a new local test character/world

Create a new local character and world named `SharedInventoryTest`. Host that new world and keep the isolation guard enabled. Do not import existing saves or personal inventory snapshots.

Use a few newly created test items, such as wood, coal, and an iron sword. Deposit them through the shared inventory UI and record the quantities for withdrawal and persistence checks. No private seed file is required.

## Leave the environment ready for manual testing

Relaunch with `Launch-Test.cmd` and load the new test character/world. Place an Einherjar Chest with the hammer before testing shared inventory actions. The isolated helper's free-build mode can supply a test chest without ingredients; disable FreeBuild and supply one carried test essence when checking real construction costs. Shared actions require a valid chest loaded on that client within a fixed 100 metres. Client loading settings may reduce usable range, and an unloaded chest grants no access. There is no radius setting or server chest registry.

Stand near the loaded chest, press **F3** (the default key), and confirm that the deposited test items have the quantities you recorded.

If performing a smoke test, verify withdrawal of one coal and then one normal stack, Alt-click deposits, a single iron sword per withdrawal, and blocked withdrawal with a full inventory. Restore test stock through normal deposits. Check save persistence after a normal exit/relaunch. Do not describe a capacity-race mock test as a real network-latency test.

For an optional two-account multiplayer test, set up a second isolated client with the same two plugin DLLs and matching Valheim build. Use a separate local test character and join this test host; no seed is needed on the joining client. Check shared counts and simultaneous withdrawals. Do not join a production server for this test. If no second account is available, report multiplayer testing as pending.

Write `$Root\SETUP-RESULTS.md` with executable/launcher paths, the test world UID, database path/hash, mod and guard hashes, the actual regression/build results, save-isolation observations, any source fixes, and what was or was not exercised in-game. Leave the environment available for the user's testing; do not delete it afterward. The deliverable is the launcher path plus this report, not a claim that unperformed manual tests passed.
