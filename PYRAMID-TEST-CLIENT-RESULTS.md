# Pyramid client setup verification

**Update, October 24:** both test installations now have **0.4.6**, fixing
remote attachment before the world handshake. The original 0.4.5 startup proof
below remains historical; 0.4.6 has passed the new real-engine readiness regression
and has been deployed for a fresh two-account retry. See the handoff's Phase 6
multiplayer-fix record.

The local auto-build launcher at
`C:\Users\seamu\source\repos\SharedInventory\SharedInventory-Test-20261023\Launch-Test.cmd`
now builds from `C:\Users\seamu\.codex\worktrees\60fb\SharedInventory`, which was
rebased onto main `dcda15f` before the fix. Keep that checkout available until the
fix is merged and the launcher's build path is deliberately updated.

Current `SharedInventory.dll` SHA-256 on both machines, installed and staged:
`8366A4559B254678F06F7090DA9D4BEB8EEC2132F9B465D088C7C6D6C14D64BE`.
The isolation-helper hash remains `11D05A5287FB279194933C03FC49CBDEC5828FF9CB843166309DC1C5DA22C8A2`.
Production and Testing builds passed 166 regressions, protocol checks, 11 harness
checks, hook checks, and inclusion/exclusion checks. The guarded headless ritual
suite passed at 17:28:55 PDT, including the absent-world attachment regression.
The initial sandboxed engine launch could not initialize Steam and was discarded;
the passing rerun used the normal Steam session. No old report was counted as a
new pass.

Verified pre-0.4.6 backups:

```text
Local: C:\Users\seamu\source\repos\SharedInventory\SharedInventory-Test-20261023\backups\before-remote-join-fix-20261024-172917
Pyramid: C:\Users\testplayer\SharedInventory-Test-20261023\backups\before-client-update-20261024-172920-90d1fc6e
```

All 33 local and five remote test-save files were unchanged by deployment.
Both games were stopped for installation and are ready to relaunch. The real
two-account retry has not yet been observed after this fix.

Verified October 24, 2026 (America/Los_Angeles). The **existing** installation at
`C:\Users\testplayer\SharedInventory-Test-20261023` was updated in place from 0.4.1 to
**Einherjar Chest 0.4.5**, Testing build. No replacement environment was created.
The existing desktop shortcut and `testplayertest` character are retained.

## Executed procedure

Recovered the original October 23 setup from local Codex session
`01a16b00-2869-74a3-a5da-1d1be8f4e9d9`, its untracked preparation/sync scripts,
and Pyramid's retained setup manifests. Confirmed the current remote paths,
Steam owner, Windows desktop owner, game binaries, plugin versions, launcher,
configs, test-save files, and scheduled tasks through SSH as `seamus@pyramid`.
These live checks established the baseline; old session claims alone were not
treated as current verification.

Parsed all six PowerShell blocks in [the instructions](PYRAMID-TEST-CLIENT.md)
and the [update helper](tools/Update-TestClient.ps1). Extracted and executed the
first four blocks as the actual build/upload/update procedure. Inspected the
existing interactive task, started it as TestPlayer, then checked fresh startup logs,
process identity, and unchanged saves. A later hostname lookup failed; the
documented `Hostname=10.10.0.184` / `HostKeyAlias=pyramid` fallback worked with
the same previously trusted host key.

After launch, reran the updater against the running client: it rejected the
attempt before creating a backup or changing installed files, and left the game
running normally.

The build ran from source commit `4b81beda395b5881aeddd5f324540614d87a0da2`
with documentation/helper additions pending, using .NET SDK **8.0.318**.
**164 gameplay regressions**, inventory protocol checks, **11 fault-harness
checks**, Testing inclusion checks, and real-game hook checks passed. The separate
isolation-helper build succeeded. Existing `MSB3277` reference-version warnings
for `System.Net.Http` and `System.IO.Compression` were emitted; there were no
build errors.

## Installed artifacts

| Plugin | SHA-256 (installed and `pending-update`) |
| --- | --- |
| `SharedInventory.dll` (file version 0.4.5.0) | `60FCEEC7387D6F246404F21BA838D3AC5B40C10664DC895409F6F43C99D165F8` |
| `SharedInventory.TestIsolation.dll` | `11D05A5287FB279194933C03FC49CBDEC5828FF9CB843166309DC1C5DA22C8A2` |

Four game inputs matched both the local host test copy and Pyramid's isolated
copy: executable, UnityPlayer, `assembly_valheim.dll`, and `assembly_utils.dll`.
Pyramid's ordinary Steam installation had the same hashes. This verifies those
compatibility inputs, not a fresh full audit of all 995 game files.

The verified pre-update backup is:

```text
C:\Users\testplayer\SharedInventory-Test-20261023\backups\before-client-update-20261024-171652-d669e3c2
```

It contains test saves, configs, both plugin DLLs, pending updates, launcher,
update/sync helpers, old incoming DLL, and the historical sync task definition.
All **five** existing test-save files and both configuration files were
byte-identical immediately after installation. All test-save files still matched
the backup after startup. No character/world save was imported, reset, or seeded.
The helper does not write outside the isolated root except to disable its named
legacy sync task; ordinary Steam/Gale game files and production saves were not
update targets.

The legacy `SharedInventory-TestPlayer-Sync-20261023` task is disabled to prevent its
hardcoded 0.4.1 helper from downgrading the install. The normal TestPlayer launch task
and desktop shortcut were retained.

## Actual remote startup

Startup passed at **2026-10-24 17:17:59 PDT** (`2026-10-25T00:17:59Z`). The
running process was PID **39336**, owner **testplayer**, interactive session **3**,
with this exact executable:

```text
C:\Users\testplayer\SharedInventory-Test-20261023\game\valheim.exe
```

Its command line includes the BepInEx preloader, `-sharedinventory-test`, the
exact isolated `-savedir`, and a root-local `Player.log`. Fresh BepInEx output:

```text
Valheim Version: 1.0.20
Loading [Einherjar Chest 0.4.5] (local.sharedinventory)
Einherjar Chest test harness enabled; impairments default OFF.
Loading [Einherjar Chest Test Isolation 0.1.0] (local.sharedinventory.testisolation)
EINHERJAR CHEST LOCAL-ONLY TEST ISOLATION: C:\Users\testplayer\SharedInventory-Test-20261023\saves
Chainloader startup complete (2 loaded, 0 skipped, 0 failed)
```

BepInEx 5.4.2351 and Unity 6000.0.75f1 were present. There were no BepInEx error
entries or isolation failures. `Testing.Enabled=true`, `Impairments=false`, and
isolation `FreeBuild=false` are retained. TestPlayer owns the running Steam process.
The Unity player log confirms Steam initialized, PlayFab authenticated via Steam,
and menu music started. Installed and staged plugin hashes were checked again
after launch and still match the new manifest.
The game was left running for manual use; it was not joined to a host by this
verification.

Remote evidence: `CLIENT-MANIFEST.json`, `CLIENT-UPDATE-RESULTS.json` (startup
verified true; multiplayer verified false), `STARTUP-CHECK-START.txt`,
`Player.log`, and `game\BepInEx\LogOutput.log`, all under the test root. Local
generated build/update evidence is in `.work/pyramid-document-update.log` and
`.work/pyramid-run-paths.json`; those local files are intentionally untracked.

## Remaining acceptance

This is an installation and real-process startup pass. It does not claim a
visually inspected character-selection screen, joined two-account session,
multiplayer ritual/death/reward pass, adverse-network pass, or dedicated-server
executable pass. Before joining, verify the host uses the same compatible build
and the intended isolated world; the document includes the host/client hash check
and manual acceptance sequence. Full Phase 6 acceptance remains outstanding.
