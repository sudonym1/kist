# TestPlayer's second-computer test client

Use this procedure to update the **existing** isolated client on Pyramid and join
the test host on the first computer. SSH administration is `seamus@pyramid`;
Windows gameplay and Steam run as **TestPlayer**, using her own licensed account.
The joining computer needs no .NET SDK or source checkout.

The procedure was exercised on 2026-10-24. See
[the verification record](PYRAMID-TEST-CLIENT-RESULTS.md) for the tested versions,
hashes, backup location, and the distinction between startup and multiplayer
gameplay checks.

## Existing installation and its history

| Purpose | Path on Pyramid |
| --- | --- |
| Isolated root | `C:\Users\testplayer\SharedInventory-Test-20261023` |
| Guarded launcher | `C:\Users\testplayer\SharedInventory-Test-20261023\Launch-Test.cmd` |
| Desktop shortcut | `C:\Users\testplayer\OneDrive\Desktop\Shared Inventory Multiplayer Test.lnk` |
| Test character | `saves\characters_local\testplayertest.fch` beneath the root |
| Game and plugins | `game`, with exactly `SharedInventory.dll` and `SharedInventory.TestIsolation.dll` in `game\BepInEx\plugins` |
| Staged plugins reapplied on launch | `pending-update` |
| Logs | `Player.log` and `game\BepInEx\LogOutput.log` beneath the root |
| Network reports | `game\BepInEx\config\SharedInventory-TestReports` |

The October 23 Codex session `01a16b00-2869-74a3-a5da-1d1be8f4e9d9`
created this installation. Its untracked setup script was recovered at
`C:\Users\seamu\source\repos\SharedInventory\.work\Prepare-Pyramid-Isolation.ps1`.
The machine retains `SETUP-RESULTS.json`, `PAYLOAD-MANIFEST.json`, and
`SYNC-RESULTS.json`. Those describe its original 0.4.1 preparation, not the
current release. Preserve them as historical evidence.

The original setup copied only clean game entries from Pyramid's licensed Steam
installation: `valheim.exe`, `UnityPlayer.dll`, `UnityCrashHandler64.exe`,
`steam_appid.txt`, `MonoBleedingEdge`, `valheim_Data`, and `D3D12`. It checked all
995 copied files, added a clean BepInEx 5.4.2351 runtime and the two plugins,
created empty local saves and the `.sharedinventory-test` marker, granted TestPlayer
Modify access, and created the shortcut on her redirected OneDrive desktop.
It did not copy her normal saves, Gale plugins, or the host's shared database.
TestPlayer subsequently created `testplayertest` in these isolated saves.

**Keep that install and character.** Do not reseed stock or replace the whole game
folder to update the mod. The separate [original setup guide](WINDOWS-CODEX-SETUP.md)
explains the clean-copy/isolation design if the installation ever needs rebuilding;
its old version/test-count examples are historical.

## 1. Check the two machines

TestPlayer should exit this isolated Valheim normally before an update. Leave Steam
running in her Windows desktop session. Do not launch the game directly from
the SSH administrator shell: it would run as Seamus, outside her desktop/Steam
session. Do not stop another game process to make room.

On the first computer, open PowerShell in the current repository checkout:

```powershell
$ErrorActionPreference = 'Stop'
$Repo = $PWD.Path
$HostGame = Join-Path $Repo 'SharedInventory-Test-Phase2\game'
$RemoteRoot = 'C:\Users\testplayer\SharedInventory-Test-20261023'
# This existing file contains Pyramid's previously trusted host key.
$KnownHosts = 'C:\Users\seamu\ThorsKist\.pyramid-known-hosts'
$SshOptions = @('-o', 'BatchMode=yes', '-o', "UserKnownHostsFile=$KnownHosts")
ssh @SshOptions seamus@pyramid hostname
if ($LASTEXITCODE -ne 0) { throw 'SSH failed; resolve authentication/host trust first.' }
```

On this workstation Codex's sandbox cannot use the normal SSH agent, and its
default SSH home resolves to `C:\Windows`. Use the explicit trusted-hosts file
above and, when required by Codex, normal sandbox escalation for SSH/SCP. Do not
disable host-key checking or embed a password/private key in a script. A changed
host key requires independent verification.

If name lookup is intermittent, the tested LAN address on October 24 was
`10.10.0.184`. After confirming that is still Pyramid, add
`'-o', 'Hostname=10.10.0.184', '-o', 'HostKeyAlias=pyramid'` to `$SshOptions`.
This retains verification against the trusted **pyramid** key. The address may
change; do not substitute it without checking which machine it identifies.

For multiline remote PowerShell, use UTF-16LE `EncodedCommand` to avoid quoting
problems. Define this function in the same local PowerShell session:

```powershell
function Invoke-Pyramid([string]$Script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    ssh @SshOptions seamus@pyramid powershell -NoProfile -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0) { throw 'Remote PowerShell failed.' }
}
Invoke-Pyramid @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Get-CimInstance Win32_ComputerSystem | Select-Object UserName
Get-CimInstance Win32_Process -Filter "Name='valheim.exe' OR Name='steam.exe'" |
    ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
        [pscustomobject]@{ Name=$_.Name; PID=$_.ProcessId; Owner=$owner.User; Path=$_.ExecutablePath }
    } | ConvertTo-Json
Get-Content -LiteralPath 'C:\Users\testplayer\SharedInventory-Test-20261023\Launch-Test.cmd'
'@
```

Expected: TestPlayer owns Steam; this isolated `valheim.exe` is stopped. Her launcher
checks the Windows username, applies `pending-update`, then runs the isolated
game with `-sharedinventory-test` and its exact `-savedir`. The update helper
below also checks the marker, plugins, loading configuration, game compatibility,
and stopped process before writing anything.

## 2. Build and prepare the small client payload locally

Build from the **same checkout** used for the host. This runs the gameplay,
protocol, fault-harness, assembly-exclusion, and actual-game-hook checks. Build
the separate save-isolation helper too. The normal release ZIP alone does not
include this helper or enable Testing.

```powershell
$Dotnet = & .\tools\Resolve-Dotnet.ps1 -Dotnet 'C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe'
& .\build.ps1 -ValheimDir $HostGame -EnableTestHarness -Dotnet $Dotnet
& $Dotnet build .\tools\TestIsolation\TestIsolation.csproj -c Release `
    "-p:ValheimDir=$HostGame" "-p:BepInExDir=$HostGame\BepInEx"
if ($LASTEXITCODE -ne 0) { throw 'Isolation helper build failed.' }

$Payload = Join-Path $Repo ('.work\pyramid-client-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $Payload) { throw 'Choose a new payload directory.' }
New-Item -ItemType Directory -Path $Payload | Out-Null
Copy-Item .\bin\Release\testing\netstandard2.1\SharedInventory.dll $Payload
Copy-Item .\tools\TestIsolation\bin\Release\netstandard2.1\SharedInventory.TestIsolation.dll $Payload
Copy-Item .\tools\Update-TestClient.ps1 $Payload
$pluginNames = @('SharedInventory.dll', 'SharedInventory.TestIsolation.dll')
$gameNames = @('valheim.exe', 'UnityPlayer.dll', 'valheim_Data\Managed\assembly_valheim.dll', 'valheim_Data\Managed\assembly_utils.dll')
$manifest = [ordered]@{
    Schema = 1; Build = 'Testing'; Commit = (git rev-parse HEAD)
    ModVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo("$Payload\SharedInventory.dll").FileVersion
    Plugins = @($pluginNames | ForEach-Object {
        [ordered]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath (Join-Path $Payload $_)).Hash }
    })
    GameFiles = @($gameNames | ForEach-Object {
        [ordered]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath (Join-Path $HostGame $_)).Hash }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$Payload\CLIENT-MANIFEST.json" -Encoding UTF8
Get-FileHash -LiteralPath "$Payload\Update-TestClient.ps1"
```

The manifest records the locally built plugins and four compatibility inputs;
it is a transfer/compatibility check, not a signature or a full game-file audit.
No game assets, save files, Steam credentials, or server inventory are uploaded.
If Pyramid's game hashes differ, the update stops: align both game versions
through their licensed installations and rebuild/revalidate before trying again.

## 3. Upload and update the existing client

Continue in that local PowerShell session:

```powershell
$RemoteStage = $RemoteRoot + '\incoming-' + (Split-Path $Payload -Leaf)
Invoke-Pyramid "New-Item -ItemType Directory -Path '$RemoteStage' -ErrorAction Stop | Out-Null"
$UploadFiles = @('SharedInventory.dll', 'SharedInventory.TestIsolation.dll', 'CLIENT-MANIFEST.json', 'Update-TestClient.ps1')
foreach ($name in $UploadFiles) {
    scp @SshOptions (Join-Path $Payload $name) ("seamus@pyramid:" + $RemoteStage.Replace('\','/') + '/')
    if ($LASTEXITCODE -ne 0) { throw "Upload failed: $name" }
}
$UpdaterHash = (Get-FileHash -LiteralPath "$Payload\Update-TestClient.ps1").Hash
Invoke-Pyramid @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
if ((Get-FileHash -LiteralPath '$RemoteStage\Update-TestClient.ps1').Hash -ne '$UpdaterHash') { throw 'Updater checksum mismatch.' }
& '$RemoteStage\Update-TestClient.ps1' -TestRoot '$RemoteRoot' -PayloadDirectory '$RemoteStage'
"@
```

The versioned [update helper](tools/Update-TestClient.ps1) validates the incoming
DLL hashes against the manifest and Pyramid's game hashes against the host. It
copies and checksum-verifies a backup of test saves, configs, installed/staged
plugins, and launch/update scripts before installing both DLLs through the
existing `Apply-Update.ps1`. It then checks that installed **and staged** copies
match, and verifies every test-save file and both configs remain byte-identical.
Keep `Testing / Enabled = true`, `Impairments = false`, and isolation
`FreeBuild = false`; the script rejects other settings rather than overwriting
configuration. Production saves and the host's database are outside this update.

The old `SharedInventory-TestPlayer-Sync-20261023` task embeds a 0.4.1 hash and old
`SharedInventory.next.dll`. The helper archives its task XML and disables it to
prevent a downgrade. **Do not manually run that old sync script.** The normal
`SharedInventory-TestPlayer-Test-20261023` launch task and desktop shortcut are kept.

The new `CLIENT-UPDATE-RESULTS.json` records the backup path, installed hashes,
preservation checks, and version. A failed/partial update is not ready to launch;
inspect its error and verified backup first.

## 4. Launch as TestPlayer and verify startup

TestPlayer can double-click **Shared Inventory Multiplayer Test** on her desktop.
Keep the old shortcut name; it now starts the updated DLLs.

For an SSH-triggered launch, first inspect the existing task, then start it. Its
principal must be TestPlayer with `Interactive` logon, its action the launcher above,
and its triggers empty. Do not use the disabled **Sync** task.

```powershell
Invoke-Pyramid @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$task = Get-ScheduledTask -TaskName 'SharedInventory-TestPlayer-Test-20261023'
[pscustomobject]@{
    TaskName=$task.TaskName; State=[string]$task.State
    User=$task.Principal.UserId; LogonType=[string]$task.Principal.LogonType
    Execute=$task.Actions.Execute; Arguments=$task.Actions.Arguments
    Xml=(Export-ScheduledTask -TaskName $task.TaskName)
} | ConvertTo-Json
'@
# Run after confirming the task definition and that TestPlayer is logged in:
Invoke-Pyramid "Start-ScheduledTask -TaskName 'SharedInventory-TestPlayer-Test-20261023'"
```

Wait for the menu, then run this read-only verification. Check log freshness;
an old log is not proof of the updated process.

```powershell
Invoke-Pyramid @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\Users\testplayer\SharedInventory-Test-20261023'
Get-Content -LiteralPath "$root\CLIENT-UPDATE-RESULTS.json"
Get-Item -LiteralPath "$root\game\BepInEx\LogOutput.log" | Select-Object LastWriteTime
Get-Content -LiteralPath "$root\game\BepInEx\LogOutput.log" -Tail 35
Get-CimInstance Win32_Process -Filter "Name='valheim.exe'" | ForEach-Object {
    $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    [pscustomobject]@{ PID=$_.ProcessId; Owner=$owner.User; Session=$_.SessionId; Path=$_.ExecutablePath; CommandLine=$_.CommandLine }
} | ConvertTo-Json
'@
```

Expect `Loading [Einherjar Chest 0.4.5]`, exactly two loaded plugins with zero
failures, `Einherjar Chest test harness enabled; impairments default OFF`, and
`EINHERJAR CHEST LOCAL-ONLY TEST ISOLATION:` pointing to this root's `saves`.
The process must be TestPlayer's, in her interactive session, with the isolated exe
and savedir. TestPlayer should see her existing **testplayertest** character. If normal
production characters appear, exit immediately and repair isolation. `-savedir`
alone does not filter cloud saves; retain the isolation DLL and marker.

Steam remains available for authentication/networking. The guard suppresses
cloud saves only inside this test process; it does not alter Steam Cloud settings.
The ordinary Steam/Gale game and TestPlayer's normal saves remain separate.

## 5. Join and exercise the test session

1. On the host computer, close its isolated game normally before installing
   the **same two DLLs** from `$Payload`. Update both its installed plugin files
   and `pending-update` if its launcher uses staging. Check hashes against
   `CLIENT-MANIFEST.json` on both endpoints. Do not let an older launcher rebuild
   from a different checkout and silently replace the tested DLL.
2. Start the host's guarded launcher and select its intended isolated world and
   test character. Enable **Start Server**, use a temporary test password, and
   choose the connection backend. Historical `TESTWORLD` / `10.10.0.217` values
   in October 23 notes are not proof of today's active world/address.
3. For Crossplay, share the current **join code** with TestPlayer. Crossplay cannot
   connect using local or loopback IP addresses. For Steam-only LAN testing,
   leave Crossplay off and use the host's current LAN address/server entry.
   [Valheim's official guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/)
   describes the backend differences. Allow the exact isolated game through the
   host firewall if prompted; do not disable the firewall globally.
4. TestPlayer selects **testplayertest**, then **Join Game** and the current test host/code.
   Do not create/host a second copy of the world on Pyramid or seed a client
   inventory database. The host owns shared stock and ritual state.
5. Confirm both players appear in the connected roster. F3 opens shared stock
   outside coverage, while transfers/spending require a nearby loaded chest.
   Verify a deposit is visible on the other client and perform competing
   withdrawals; compare counts and the physical items on both characters.
6. With faults off and FreeBuild false, test both players gathering at the
   original stones, rejection when one is absent, cancellation when one leaves,
   countdown on both screens, one sacrifice/one essence, then both-player
   punishment/no essence. These are lethal tests: use the isolated characters.
   Reset cooldown and Spawn essence are **host-only** Testing operations;
   TestPlayer's remote client is intentionally unauthorized.
7. Use [TESTING.md](TESTING.md) for fault/recovery cases and
   [the handoff acceptance list](EINHERJAR-HANDOFF.md#acceptance-checks) for Phase 6.
   Export reports on both endpoints and exit normally after the session.

This setup/startup check does not certify multiplayer gameplay, real network
latency behavior, or the separate dedicated-server executable. Record those
results only after both accounts join and the actions are observed.

## Logs, future updates, and rollback

Read the isolated logs through SSH, using the paths in the table. Do not use
TestPlayer's normal `AppData\LocalLow\IronGate\Valheim\Player.log` to diagnose this
launcher: it explicitly redirects `Player.log` into the test root. Retrieve test
reports with SCP into this repository's `.work`, without uploading personal saves.

Repeat steps 1-4 for future mod changes. Always update both staged and installed
plugins as a matched pair. No source build or .NET installation is required on
Pyramid. Keep game builds aligned and retain the verified backup until gameplay
has passed.

For rollback, first exit the isolated game normally. Use the exact backup path
from `CLIENT-UPDATE-RESULTS.json`; copy its `game\BepInEx\plugins` DLLs back to
both the installed and `pending-update` locations, then verify matching hashes.
Restore configuration only if needed. Preserve the current `saves` separately
before restoring older test-save files; rolling back DLLs alone does not require
reverting character progress. Roll back the host to the matching mod too. Leave
the historical Sync task disabled. Do not delete or overwrite normal game saves.
