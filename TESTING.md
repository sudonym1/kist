# Testing Einherjar Chest

## Automated production checks

```powershell
just build
```

This runs gameplay regressions, production protocol checks using the game's
`ZPackage`, deterministic fault-harness checks, game hook signature checks,
release identity checks, and production harness-exclusion checks. It also builds
the separate save-isolation helper. The versioned ZIP printed last is the
production Hexium upload; see [PUBLISHING.md](PUBLISHING.md).

Tests cover shared counts/quality, concurrent spending, inventory capacity and
returns, optimistic shared crafting, worker ordering, loaded-chest coverage,
ritual gathering/outcomes/persistence, Dimmed Essence binding/recovery, portal
policy, station shortcuts, tutorial state, and refresh recovery. Test doubles
and signature checks do not replace real-engine or multiplayer acceptance.

## The isolated game

`just run` builds, tests, and launches the production mod in `.work/isolated`.
New environments restore the preserved test characters, worlds, hoards, and
ritual state automatically. See [DEVELOPMENT.md](DEVELOPMENT.md) for the shared
snapshot, custom paths, cleanup behavior, and backups. The isolation helper
requires the marked `game` layout, `-sharedinventory-test`, and the exact local
`-savedir`; it also disables cloud save enumeration in that process. Keep it
installed for both production and testing builds.

Use `testchar` and `TESTWORLD` for the preserved manual playtest. Automated asset
and gameplay suites use their own `EinherjarAssets` character/world. They can
reset that automated character and place/remove test pieces. Stop the game and
back up its saves and `BepInEx/config/SharedInventory` before destructive manual
tests. Stock and ritual files are selected by the world's UID; changing the UID
or copying an unrelated inventory does not preserve the same hoard.

## Enable the optional Testing menu

Start with `just build`, then build the separate testing DLL:

```powershell
.\build.ps1 -ValheimDir '.work/isolated/game' -EnableTestHarness -Dotnet $env:DOTNET
.\tools\Dev.ps1 -Action Run -EnableTestHarness
```

Start Steam first. Exit the game normally after the first testing launch to
create configuration. In `.work/isolated/game/BepInEx/config/local.sharedinventory.cfg`,
edit the existing `[Testing]` section:

```ini
[Testing]
Enabled = true
Impairments = false
```

In `local.sharedinventory.testisolation.cfg`, set `[Testing] FreeBuild = false`
when testing actual construction/material consumption. Relaunch using
`tools/Dev.ps1 -Action Run -EnableTestHarness`. Both DLLs are installed by that
guarded launcher. `just run` always reinstalls the production mod, which has no
Testing menu regardless of the config setting.

The testing DLL is `bin/Release/testing/netstandard2.1/SharedInventory.dll`;
its package is `dist/EinherjarChest-Testing.zip`. Neither is suitable for Hexium.
Compile-time inclusion, runtime `Enabled`, and the isolation guard must all pass
for the Testing menu to appear. Faults start off with `Impairments = false`.

In F3 → Testing, diagnostics and fault controls remain reachable without chest
coverage. Shared transfers/spending still require a living player within 100
metres of a chest loaded on that client. **Disable faults and release queued
packets** stops impairments and releases delayed traffic; it cannot recreate a
discarded message or resolve an uncertain mutation. Reports are exported beneath
`game/BepInEx/config/SharedInventory-TestReports`.

To remove Testing code, exit normally and run `just run`. To disable it while
retaining a testing DLL, set `Enabled = false` and `Impairments = false`, then
restart through the guarded launcher.

## Real-engine acceptance and asset capture

With the guarded testing build/configuration prepared and the game closed:

```powershell
.\tools\Test-Assets.ps1 -TestRoot '.work/isolated' -Mode Review -ExitAfter -Dotnet $env:DOTNET
.\tools\Test-Assets.ps1 -TestRoot '.work/isolated' -Mode Headless -Dotnet $env:DOTNET
```

The default Assets suite checks registration, hammer placement/payment, bound
refunds after removal/damage, rebuilding range, trading/save/load, portal policy,
and model/icon presentation. Use `-Suite Access`, `-Suite Ritual`, or
`-Suite Outcomes` for coverage, sacrificial-stone gathering/countdown, or lethal
outcomes/persistence. Run one suite/mode at a time with Steam available.
`-Mode Capture` exports model icons; see [assets/README.md](assets/README.md).

Check newly timestamped acceptance reports under `.work/isolated` and the game
logs, not just process startup. The outcomes suite intentionally kills its
automated test character and uses normal tombstone/respawn behavior. Reports
and captures may be replaced on later runs; save any evidence you need first.
Headless Unity checks do not establish separate dedicated-executable behavior.

## Load and fault presets

```powershell
.\tools\Run-LoadTests.ps1 -All -ValheimDir '.work/isolated/game' -Dotnet $env:DOTNET
.\tools\Run-LoadTests.ps1 -Scenario slow-network -ValheimDir '.work/isolated/game' -Dotnet $env:DOTNET
```

Reports default to `.work/load-reports`. The runner accepts an explicit scenario
JSON and `-Baseline '<report.json>'` for a single scenario comparison. Inspect
invariant violations, unresolved operations, rejected batches, and unpaid shared
costs alongside latency. The lost-confirmation preset deliberately retains an
uncertain transfer; it must not silently replay it.

Application-message discard/hold/reorder occurs above Valheim's reliable
transport. It does not simulate packet retransmission or real multi-process
networking. Injected save failures do not establish cross-file crash atomicity.

## Manual checks still needed

Use matching builds on every isolated peer. Exercise host and remote gathering,
cancellation, sacrifice, punishment and respawn; trading and container/tombstone
handoff of bound Essence; simultaneous chest removal; save/restart; and separate
dedicated-server and adverse-network cases. Character inventories, shared stock,
ritual state, and world items have separate saves and no atomic transaction.

For refresh recovery, export a baseline, discard a read-only ListReply, and
verify ten-second expiration followed by a retry after two seconds without
reconnecting. Also test manual refresh, a late held reply, an outage, and a
rejected crafting batch during recovery. Counts/quality must remain coherent;
mutations must never replay automatically.

For Hugin, use a fresh automated test character to check arrival guidance,
essence/chest/deposit/station/range events, punishment guidance after respawn,
Tutorials off/on, ignored messages, logout/rejoin, per-character persistence,
and native journal rereading. Do not reset the preserved manual character just
to get a fresh tutorial state.

Historical evidence and superseded procedures are retained in
[old_docs/](old_docs/README.md); their paths, versions, and test counts describe
the original runs.
