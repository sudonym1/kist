# Load and network testing

Normal builds exclude all harness types, diagnostics, configuration, and UI. `EnableTestHarness` defaults to `false`. No test plugin or load runner is packaged with the normal mod. The harness uses SharedInventory's existing RPC protocol; it adds no server endpoints or gameplay recovery behavior.

## Build and enable the in-game harness

Use a .NET 8 or newer SDK and the same game/BepInEx references as the normal build:

```powershell
.\build.ps1 -ValheimDir '<isolated game>' -BepInExDir '<isolated game>\BepInEx' -EnableTestHarness
```

The opt-in DLL is `bin/Release/testing/netstandard2.1/SharedInventory.dll` and its separate package is `dist/SharedInventory-Testing.zip`. Normal output remains `bin/Release/netstandard2.1/SharedInventory.dll` and `dist/SharedInventory.zip`. Build checks inspect the assembly to verify harness inclusion or exclusion.

Install the opt-in DLL only in the isolated game. Launch once to create the config, exit, and set these entries in `BepInEx/config/local.sharedinventory.cfg`:

```ini
[Testing]
Enabled = true
Impairments = false
```

Both settings default to `false`. Restart after changing configuration. Activation also requires the marked test directory, `-sharedinventory-test`, and its matching `-savedir` argument from the guarded launcher described in [the isolation setup](WINDOWS-CODEX-SETUP.md). Retain the isolation helper. Set its `Testing / FreeBuild = false` when testing actual resource consumption.

Press F3, then select **Testing** inside the existing inventory window. It shares the inventory window's input blocking, Escape handling, and draggable bounds. Controls provide slow connection and limited bandwidth presets, a ten-second outage, hold/discard of the next request or reply, release of held packets, worker/save stalls, one failed save, and JSON report export. **Disable faults and release queued packets** immediately turns off impairments and makes queued packets eligible for delivery over subsequent frames. It cannot recreate a discarded message. Session shutdown cancels deferred network callbacks and exports diagnostics; queued server work retains the normal shutdown/save behavior.

Optional `[Testing]` configuration keys are `Seed`, `RequestDelayMs`, `ReplyDelayMs`, `JitterMs`, `BytesPerSecond`, `ProcessingDelayMs`, `SaveDelayMs`, and `TargetPeer`. Delays use milliseconds; bandwidth uses bytes/second per peer and direction, with zero meaning unlimited. `TargetPeer = 0` affects all peers. Fault settings only apply when `Impairments = true` or a panel control explicitly activates them. Settings changed through the panel are session-only.

On a host/server, request delays apply at server receipt and reply delays apply before sending, so one enabled host can simulate both directions, including its own player. On a remote client, its harness affects outbound requests; enable reply impairments on the server. Applying request delay on both endpoints adds both delays. Peer selection refers to the peer at that endpoint. The server's worker metrics cover all clients; round-trip latency and pending transfer/cache diagnostics describe the local client.

The panel shows queue depths, oldest queued/pending ages, pending transfers and crafting batches, cache age, RTT percentiles, reply throughput, traffic rate, frame time, worker wait, and save duration. Exported reports contain settings, counters, latency distributions, a bounded event trace, and cached inventory counts/qualities. Reports are written to `BepInEx/config/SharedInventory-TestReports`; their full paths appear in the BepInEx log. Samples retain the latest 8,192 observations per metric and traces retain the latest 10,000 events. Active diagnostics add overhead, so compare equivalent configurations.

## Disable fault injection and testing

Choose the level of shutdown you need:

| Goal | Action | Result |
| --- | --- | --- |
| Stop faults during the current session | F3 → Testing → **Disable faults and release queued packets** | Turns off injection while retaining diagnostics and the Testing tab. |
| Start future sessions with diagnostics only | Exit normally; set `Impairments = false` under `[Testing]`; restart through the guarded launcher | Configured faults are inactive at startup. Panel fault buttons can still explicitly activate them. |
| Disable the entire harness at runtime | Exit normally; set both `Enabled = false` and `Impairments = false`; restart | No test scheduler or diagnostics are created, and the Testing tab is absent. |
| Remove the harness code from the installed DLL | Build and install the normal DLL as described below | Harness types, UI, configuration binding, and diagnostic hooks are excluded from the binary. |

Apply these actions on every host/client where you enabled injection. Disabling one endpoint does not turn off another endpoint's faults. Configuration edits take effect after restarting; the panel's session-only changes do not rewrite startup settings. Both runtime settings and the build switch default to off. The prepared isolated install deliberately has `Enabled = true` and `Impairments = false` so its controls are available without starting with faults.

### Stop current faults

The panel button clears the active outage, armed hold/discard actions, worker/save delay settings, and remaining injected save failures. It releases held and delayed messages for delivery over subsequent frames. An already-running worker or save stall finishes normally; the button does not interrupt that operation.

Request/reply delay, jitter, and bandwidth values may remain displayed after disabling. They are inactive while the panel says **faults off**. A later fault button can reactivate those settings or select another preset. **Release held replies** alone does not disable other faults.

Disabling injection cannot recreate discarded messages, resolve an uncertain transfer, or automatically restore items. The current build also has a known refresh-recovery bug: discarding a `ListReply` can leave loading/Refresh blocked even after faults are off. Restart the isolated game session with faults disabled to clear that refresh state. The planned fix is documented in [PLAN.md](PLAN.md); it has not been implemented. Restarting is not a receipt or reconciliation mechanism for uncertain item transfers.

### Disable the harness on future launches

With the isolated game closed, edit `game/BepInEx/config/local.sharedinventory.cfg` inside the test install:

```ini
[Testing]
Enabled = false
Impairments = false
```

Edit the existing `[Testing]` section rather than adding a duplicate section. Restart using `Launch-Test.cmd`. The Testing tab should be absent, and the new launch's log should not contain `Shared Inventory test harness enabled`. The earlier log line may still exist in a retained historical log.

Keep `SharedInventory.TestIsolation.dll` and the launcher's `-sharedinventory-test` / `-savedir` arguments. They protect the test saves and are separate from error injection. Removing the test flag causes the isolation helper to reject the launch. `FreeBuild = false` in the helper's config controls gameplay costs; it does not disable injection.

### Build and install a DLL with all harness code excluded

From the source repository, run the normal build without `-EnableTestHarness`:

```powershell
.\build.ps1 -ValheimDir '<game>' -BepInExDir '<game>\BepInEx'
```

`build.ps1` explicitly sets `EnableTestHarness=false` when its switch is omitted. Direct MSBuild callers can use `dotnet build SharedInventory.csproj -c Release -p:EnableTestHarness=false` with their game/BepInEx reference properties.

Install `bin/Release/netstandard2.1/SharedInventory.dll` from the normal build, or use `dist/SharedInventory.zip`. The separate paths containing `testing` and the `SharedInventory-Testing.zip` package retain the harness. All peers should use the intended matching build.

For this repository's existing isolated install, first quit the test game normally and back up its current DLL/config. Then stage the normal DLL and use its guarded updater:

```powershell
$testRoot = Join-Path $PWD 'SharedInventory-Test-20261023'
Copy-Item -LiteralPath 'bin/Release/netstandard2.1/SharedInventory.dll' `
    -Destination (Join-Path $testRoot 'pending-update/SharedInventory.dll') -Force
& (Join-Path $testRoot 'Apply-Update.ps1')
```

The updater refuses to replace a running test game's DLL. Updating only the active plugin while leaving a test DLL in `pending-update` is insufficient: the launcher would reinstall the staged test DLL on the next launch. Retain the isolation helper and existing saves; no reseeding is needed.

Normal builds run an assembly check confirming that `TestSettings`, `TestHarness`, `LabMetrics`, and `TestPanel` are absent. To check an installed normal DLL independently after building the protocol tool:

```powershell
dotnet tools/ProtocolTests/bin/Release/net8.0/ProtocolTests.dll `
    --verify-harness '<installed SharedInventory.dll>' false
```

The normal DLL ignores any old `[Testing]` config entries because it contains no harness configuration code. The standalone load runner executes only when explicitly invoked; it is not installed in the game or started by a normal mod build.

## Automated load runner

The runner needs game assemblies and a .NET 8 SDK, but does not launch Valheim, connect to a server, or modify installed game files. Every run creates a fresh temporary database and deletes it after exporting the report. Logical peers replace routing only: production server handlers, real `ZPackage` codecs, the ordered worker, and file persistence execute.

```powershell
.\tools\Run-LoadTests.ps1 -All -ValheimDir '<game>' -BepInExDir '<BepInEx>'
.\tools\Run-LoadTests.ps1 -Scenario slow-network -ValheimDir '<game>' -BepInExDir '<BepInEx>'
```

Use `-Dotnet '<standalone SDK>\dotnet.exe'` if the SDK is not on PATH. Reports default to `.work/load-reports`. To compare another run against a saved report, use `-Scenario baseline -Baseline '<report.json>'`. `MaxBaselineRatio` in the scenario controls the permitted p95 ratio. The runner returns a nonzero exit code for invariant, unexpected error, unresolved-operation, or configured threshold failures.

| Preset | Workload |
| --- | --- |
| `baseline` | Ten peers, 100 requests/sec, synchronized menu polling. |
| `slow-network` | Asymmetric-capable request/reply delay, seeded jitter, and bandwidth limiting. |
| `polling-burst` | Fifty peers, 5,000 entries, a 500-request burst, ramp-up, and menu polling. |
| `scarce-stock` | Fifty peers competing for the same scarce ingredients through exact batches. |
| `lost-confirmation` | A committed deposit loses its reply; the peer disconnects, reconnects, and refreshes. One unresolved transfer is expected. |
| `backlog-recovery` | Sustained traffic during a ten-second outage, then recovery and queue drainage. |
| `slow-storage` | Delayed persistence plus one injected save failure, followed by recovery. |

Copy a JSON preset to define a custom scenario and pass its path as `-Scenario`. Settings include peer count, inventory entry count, initial stock, batch size, amount, operation weights, total offered requests/sec, ramp duration, burst size, hotspot fraction, duration, drain deadline, network settings, and performance thresholds. `MenuPolling` adds a List every two seconds per connected peer, with one outstanding poll per peer. The independent traffic generator is intentionally unrestricted by the real client's one-transfer-at-a-time UI limit. Large requests and raw request rates exercise server capacity beyond ordinary gameplay.

`Steps` execute in time order and support `hold`, `drop`, `release`, `outage`, `disable`, `disconnect`, `reconnect`, `worker-delay`, `save-delay`, `save-failure`, `list`, `deposit`, `withdrawal`, and `batch`. Steps specify `AtSeconds`, `Peer` (logical IDs start at 2), `Method` for hold/drop, and `Value` for durations/counts. Fault filters accept `Request`, `Reply`, or an exact RPC method name. Outages use seconds; worker and save delays use milliseconds. `Network.Active` defaults off, and a scripted fault step activates its impairment explicitly. Reordering is off by default; `Network.AllowReordering = true` enables adversarial application-message ordering tests.

Reports compare each received reply and the final persisted inventory against an independent reference model in the actual server admission order. They include initial/final counts, rejected batches, unpaid shared-material units, unresolved operation IDs, queue maxima, throughput, p95 latency, and violations. `AllowUnresolved` permits intentional loss scenarios; unresolved operations remain listed. `Context.BeforeCleanup` captures workload state before the final validation barrier. The original scenario is retained in `Context.Scenario`; a seed reproduces workload/fault choices, while operating-system scheduling still affects timing and inter-peer ordering. Percentiles cover completed requests only; inspect unresolved counts alongside them.

## Correctness checks and testing limits

`build.ps1` runs the gameplay regression suite, production protocol checks, ten deterministic fault-harness checks, game hook signature checks, and assembly isolation checks. Load presets are opt-in through the runner rather than part of every build. Four virtual-clock gameplay scenarios cover death during a delayed withdrawal, outage plus inventory capacity changes, competing crafting batches, and a deposit confirmation lost before reconnecting.

The test code preserves existing semantics: completed crafts/builds remain completed if their exact batch is rejected, rejected shared costs are recorded as unpaid, and a missing confirmation remains uncertain. Disabling faults does not automatically retry transfers, restore items, or introduce durable receipts.

Application-message discard, hold, and reorder are deliberate failure injection above Valheim's reliable transport. They do not model packet retransmission. Actual packet loss, connection establishment, real multi-process networking, Unity/Harmony execution, abrupt process failure, and in-game UI behavior require separate isolated game/network tests. Frame metrics are only available in-game. An injected save failure is a deterministic exception before writing, not a simulated disk crash or atomic-replacement failure.
