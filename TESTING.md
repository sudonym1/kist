# Load and network testing

Normal builds exclude all harness types, diagnostics, configuration, and UI. `EnableTestHarness` defaults to `false`. No test plugin or load runner is packaged with the normal mod. The harness uses SharedInventory's existing RPC protocol; it adds no server endpoints or gameplay recovery behavior.

## Build and enable the in-game harness

For the Dimmed Essence feature, run `tools/Test-Assets.ps1 -TestRoot '<marked isolated root>' -Mode Review -ExitAfter`, then repeat with `-Mode Headless`. Asset acceptance creates a fresh isolated test character on each run and cleans test chests/materials in its own EinherjarAssets world. It checks real hammer placement, one bound refund, local rebuilding with immutable origin, dimmed-first payment and regular fallback, inventory trade/save/load, both portal policies, damage/legacy/admin behavior, and the hammer ingredient tooltip. It also exports the purple model and inventory icon to `asset-capture`. Headless acceptance verifies registration without render assets and recovery after actual chest unload/reload. Reports are `asset-acceptance.txt` and `headless-acceptance.txt` beneath the test root.

Separate host-plus-remote acceptance should verify a refund picked up by another player, a transfer through an ordinary container and tombstone, save/restart with a rebuilt chest, and two clients attempting to dismantle the same chest. All peers must use the same 0.11.1 build. Item-local metadata and owner-side refund deduplication do not turn the trusted-client mod into an anti-cheat boundary.

Use a .NET 8 or newer SDK and the same game/BepInEx references as the normal build:

```powershell
.\build.ps1 -ValheimDir '<isolated game>' -BepInExDir '<isolated game>\BepInEx' -EnableTestHarness
```

The opt-in DLL is `bin/Release/testing/netstandard2.1/SharedInventory.dll` and its separate package is `dist/EinherjarChest-Testing.zip`. Normal output remains `bin/Release/netstandard2.1/SharedInventory.dll` and `dist/EinherjarChest.zip`. Build checks inspect the assembly to verify harness inclusion or exclusion.

Install the opt-in DLL only in the isolated game. Launch once to create the config, exit, and set these entries in `BepInEx/config/local.sharedinventory.cfg`:

```ini
[Testing]
Enabled = true
Impairments = false
```

Both settings default to `false`. Restart after changing configuration. Activation also requires the marked test directory, `-sharedinventory-test`, and its matching `-savedir` argument from the guarded launcher described in [the isolation setup](WINDOWS-CODEX-SETUP.md). Retain the isolation helper. Set its `Testing / FreeBuild = false` when testing actual resource consumption.

Press F3, then select **Testing** in the window's footer. It shares the inventory window's input blocking, Escape handling, fixed centered position, and enlarged UI scale. Controls provide slow connection and limited bandwidth presets, a ten-second outage, hold/discard of the next request or reply, release of held packets, worker/save stalls, one failed save, and JSON report export. **Disable faults and release queued packets** immediately turns off impairments and makes queued packets eligible for delivery over subsequent frames. It cannot recreate a discarded message. Session shutdown cancels deferred network callbacks and exports diagnostics; queued server work retains the normal shutdown/save behavior.

The shared inventory and guarded Testing entry are available without a chest. The shared window loads and refreshes stock outside coverage, including on first entering a world; withdrawal buttons remain disabled there. Deposits and shared spending still require a loaded Einherjar Chest within the fixed 100-metre radius. Place a test chest before running transfer acceptance; lower client simulation/loading settings may reduce usable range. Chest access has no server registry or radius setting.

Optional `[Testing]` configuration keys are `Seed`, `RequestDelayMs`, `ReplyDelayMs`, `JitterMs`, `BytesPerSecond`, `ProcessingDelayMs`, `SaveDelayMs`, and `TargetPeer`. Delays use milliseconds; bandwidth uses bytes/second per peer and direction, with zero meaning unlimited. `TargetPeer = 0` affects all peers. Fault settings only apply when `Impairments = true` or a panel control explicitly activates them. Settings changed through the panel are session-only.

On a host/server, request delays apply at server receipt and reply delays apply before sending, so one enabled host can simulate both directions, including its own player. On a remote client, its harness affects outbound requests; enable reply impairments on the server. Applying request delay on both endpoints adds both delays. Peer selection refers to the peer at that endpoint. The server's worker metrics cover all clients; round-trip latency and pending transfer/cache diagnostics describe the local client.

The panel shows queue depths, oldest queued/pending ages, pending transfers and crafting batches, cache age, RTT percentiles, reply throughput, traffic rate, frame time, worker wait, and save duration. Exported reports contain settings, counters, latency distributions, a bounded event trace, and cached inventory counts/qualities. Reports are written to `BepInEx/config/SharedInventory-TestReports`; their full paths appear in the BepInEx log. Samples retain the latest 8,192 observations per metric and traces retain the latest 10,000 events. Expired refresh attempts produce terminal refreshExpired events with an Outcome, separate from successful RTT samples and server rejections. Counters refreshTimeouts, refreshRetries, and ignoredRefreshReplies describe recovery. PendingRefreshes and PendingMutations distinguish refreshes from uncertain mutations; client transfer/batch counts remain visible independently. Request correlation retains at most 10,000 entries and emits correlationEvicted if exhausted. Active diagnostics add overhead, so compare equivalent configurations.

## Production Hugin tutorials

Normal and testing builds both include native Hugin guidance. Enable Valheim's
**Tutorials** setting and use a living, fully spawned local character. No test
flag is required for real guidance. Native landing sites, enemies, events and
vanilla messages can delay the raven; queuing never marks a lesson seen.

| Lesson | First eligible event |
| --- | --- |
| Oden's bargain | First fully spawned world load with this mod, directing the player explicitly to the sacrificial stones. |
| A gift dearly earned | Successful pickup of regular or Dimmed Einherjar Essence; explains hammer recovery and the original-site rebuilding limit. |
| One hoard, many doors | Opening an actual Einherjar Chest, including another player's; F3 alone does not qualify. |
| The hoard lends a hand | A positive successful player deposit; rejected deposits and internal withdrawal returns do not qualify. |
| Feed the fires | Targeting a supported input/fuel control with chest coverage and suitable shared stock. |
| Beyond the chest's reach | An actual denied out-of-coverage transfer, such as Alt-clicking a carried stack; passive range checks do not qualify. |
| Oden's wrath | Each local player actually killed by a validated punishment death, delivered after respawn. Ordinary sacrifice and bystanders do not qualify. |

Seen keys are permanent per-character `local.sharedinventory.tutorial.<lesson>.v1`
native tutorial keys. Native Talk records acknowledgement and the journal text.
Pending event flags and the preferred waiting lesson use only this mod's bounded
keys in `Player.m_customData`, saved/loaded with the character. This includes wrath
before native respawn snapshots the dead character. Normal character save/crash
limitations still apply; this adds no separate save file or forced disk writes.

Only one production lesson occupies the native raven queue at a time. New event
lessons can replace unread older hints when no owned dialog is visible; deferred
lessons remain pending, not falsely acknowledged. Wrath takes priority, and the
introduction follows pending event lessons. Repeated triggers do not repeatedly
steal priority. After acknowledgement, Hugin's native dialog/departure is respected
and the next lesson waits at least five seconds. Tutorials off, death, teleport,
logout and character/world changes clear only owned runtime messages; pending
events and native seen history remain with their character. No timer is revealed.

In the existing isolated environment, use a fresh test character for arrival.
Then check each event above, ignored introduction → friend's chest, readable
dialog before the next lesson, Tutorials off/on, ordinary sacrifice versus repeat
punishment, every participant's post-respawn wrath, and logout before reading it.
Verify rejoining/another world preserves pending guidance, another character has
independent history, acknowledged text is rereadable, and vanilla ravens remain
unchanged. Automated doubles and game IL checks do not establish native landing,
animation, interaction, journal layout or multiplayer runtime acceptance.

## Hugin tutorial proof of concept

With the guarded testing build enabled as above, enter a world with a living,
fully spawned local test player. Open **F3 → Testing**, scroll below the ritual
controls, and choose **Queue / replay Hugin tutorial (POC)**. No chest or host
privileges are required. Close F3, look around on open, safe ground, and interact
with Hugin normally. The native raven handles landing, animation, interaction,
dialog and acknowledgement; existing raven messages, enemies, events and landing
conditions can delay his arrival. The panel reports queued, acknowledged, or a
specific unavailable reason. Repeated clicks while queued do not add copies.

After acknowledgement, use the same button to replay. Replay replaces only this
sample, including any still-visible sample dialog. **Tutorials must be enabled
in Valheim's settings**; this action never overrides that setting. Disabling it,
dying, teleporting, losing player readiness, switching character/world, or leaving
the session clears the sample. Queue it manually again when ready. It never starts
automatically and is absent from production builds.

The fixed key is `local.sharedinventory.testing.raven-poc.v1`. Cleanup removes
only that key's temporary message/seen state and this controller's tutorial
definition/current dialog. Vanilla and other mods' messages retain their state
and queue order. The sample has no journal label, so it adds no Known Text entry.
It uses native game assets and introduces no dependencies or network messages.

The POC temporarily suspends real guidance while its sample is pending or its
dialog is visible. Its replay/cleanup never resets permanent production seen
keys or pending flags; real guidance resumes afterward.

For manual acceptance in the existing isolated install, check the first queue,
rapid repeated clicks, native interaction/dialog, replay while the previous
dialog is visible and after it closes, Tutorials off/on, death/respawn, and
logout/rejoin or character/world switch with a sample pending. Confirm unrelated
tutorials still appear normally. These are graphical runtime checks; lifecycle
tests with doubles and installed-game API/IL checks do not establish animation,
landing, interaction or visual layout acceptance.

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

Disabling injection cannot recreate discarded messages, resolve an uncertain transfer, or automatically restore items. The recovery build expires a missing List request or reply after ten seconds and retries read-only refreshes with 2/4/8/16/30-second capped backoff. Cached stock stays visible during automatic recovery, and retired replies are ignored. Explicit refresh demand survives closed menus; polling resumes only in active contexts. This behavior is covered by client-level virtual-clock tests; consult [PLAN.md](PLAN.md) for actual live acceptance results. A full refresh is not a receipt or reconciliation mechanism for uncertain item transfers.

### Disable the harness on future launches

With the isolated game closed, edit `game/BepInEx/config/local.sharedinventory.cfg` inside the test install:

```ini
[Testing]
Enabled = false
Impairments = false
```

Edit the existing `[Testing]` section rather than adding a duplicate section. Restart using `Launch-Test.cmd`. The Testing tab should be absent, and the new launch's log should not contain `Einherjar Chest test harness enabled`. Older builds used `Shared Inventory test harness enabled`; earlier log lines may still exist in a retained historical log.

Keep `SharedInventory.TestIsolation.dll` and the launcher's `-sharedinventory-test` / `-savedir` arguments. They protect the test saves and are separate from error injection. Removing the test flag causes the isolation helper to reject the launch. `FreeBuild = false` in the helper's config controls gameplay costs; it does not disable injection.

### Build and install a DLL with all harness code excluded

From the source repository, run the normal build without `-EnableTestHarness`:

```powershell
.\build.ps1 -ValheimDir '<game>' -BepInExDir '<game>\BepInEx'
```

`build.ps1` explicitly sets `EnableTestHarness=false` when its switch is omitted. Direct MSBuild callers can use `dotnet build SharedInventory.csproj -c Release -p:EnableTestHarness=false` with their game/BepInEx reference properties.

Install `bin/Release/netstandard2.1/SharedInventory.dll` from the normal build, or use `dist/EinherjarChest.zip`. The separate paths containing `testing` and the `EinherjarChest-Testing.zip` package retain the harness. All peers should use the intended matching build.

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

The guarded model/content workflow is `tools/Test-Assets.ps1 -TestRoot '<isolated root>' -Mode Review|Capture|Headless`; select one mode per run. It uses a dedicated local EinherjarAssets test character/world and can reset their test essence and place/remove test pieces. See [the asset source instructions](assets/README.md) for setup, transparent icon capture, real-engine checks, and headless limits. `AssetCapture` and `ContentAcceptance` are included in the production-exclusion assembly check. No asset-test endpoints are registered.

The runner needs game assemblies and a .NET 8 SDK, but does not launch Valheim, connect to a server, or modify installed game files. Every run creates a fresh temporary database and deletes it after exporting the report. Logical peers replace routing only: production server handlers, real `ZPackage` codecs, the ordered worker, and file persistence execute.

```powershell
.\tools\Run-LoadTests.ps1 -All -ValheimDir '<game>' -BepInExDir '<BepInEx>'
.\tools\Run-LoadTests.ps1 -Scenario slow-network -ValheimDir '<game>' -BepInExDir '<BepInEx>'
```

The runner uses the same automatic .NET 8 SDK selection as `build.ps1`; use `-Dotnet '<standalone SDK>\dotnet.exe'` to select an installation explicitly. Reports default to `.work/load-reports`. To compare another run against a saved report, use `-Scenario baseline -Baseline '<report.json>'`. `MaxBaselineRatio` in the scenario controls the permitted p95 ratio. The runner returns a nonzero exit code for invariant, unexpected error, unresolved-operation, or configured threshold failures.

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

## Chest coverage acceptance

Use an existing marked isolated copy with `[Testing] Enabled = true`, `Impairments = false`, and the isolation helper's `FreeBuild = false`. Close that copy before launching:

```powershell
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Access -Mode Headless
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Access -Mode Review -ExitAfter
```

Supply `-Dotnet '<.NET 8 SDK>\dotnet.exe'` if needed. Run with access to the running Steam client. Both modes enforce the existing marker/savedir/runtime guard, use only the local `EinherjarAssets` test character/world, and write `access-headless-acceptance.txt` or `access-acceptance.txt` under the test root. The graphical mode also renders `access-radius-preview.png` from the actual world and placement sphere for visual review. Helpers launch hidden; `-ExitAfter` exits normally after reporting. Production contains no access acceptance helper.

Throwaway instances exercise actual prefab loading, natural scene unloading with the saved ZDO retained, loss of coverage while unloaded, reloading the same ZDO, immediate disable/re-enable behavior, destruction, fixed 100-metre 3D boundaries, and headless operation. Graphical checks cover live resource gating and confirm that the real hammer ghost provides no access while still displaying the radius preview. The current helper creates no persistent fixture and uses no access registry or settings. It does not bypass the marked-directory guard or use production saves.

The 120 out-of-game regressions cover loaded-only coverage, overlaps, inactive/invalid/ghost rejection, reload, player life/position, carried crafting/machine behavior, stale shared selections, transfer completion/returns across range loss and death, cached-total reconciliation, and polling. They also cover first-open browsing without a chest or cached stock, deferred first loads while player/connection readiness is pending, outside-range refresh recovery, and continued withdrawal blocking. Access checks iterate only loaded Einherjar Chest components, O(number of loaded shared chests), not all entities. There is no spatial index/cache or extra access network traffic to test. Production protocol checks continue to cover the existing shared inventory database. Full host-plus-remote deployment remains phase 6 acceptance.

## Sacrificial stones and gathering acceptance

Use the same guarded isolated copy, runtime Testing enabled, FreeBuild = false, impairments off, and access to Steam:

```powershell
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Ritual -Mode Review -ExitAfter
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Ritual -Mode Headless
```

Run one mode at a time. Supply `-Dotnet '<.NET 8 SDK>\dotnet.exe'` if needed. The existing marked-directory/savedir guard and local-only `EinherjarAssets` bootstrap apply. Reports are `ritual-acceptance.txt` and `ritual-headless-acceptance.txt`; graphical testing also writes `ritual-boundary-review.png`, an overhead render with the measured perimeter. Inspect the first report line for `passed`; an old report remains until the new run completes. Launch logs are `asset-Review.log` / `asset-Headless.log` and are replaced on subsequent runs.

The graphical suite exercises all seven original stones through both reading and trophy/power controls, normal reading on each stone, copied/wrong stones, circle geometry, actual trophy placement and power selection, private-map host membership, connected loading-player errors, full health with the game's omitted health field, explicit zero health, dead-host and near-outside rejection, held-use suppression, unauthorized display rejection, routed owner confirmation, cancellation, restart, and five-second completion. Leaving during a countdown must show exactly `Ritual Cancelled`. Temporary test trophies are consumed/removed and the original power is restored. A synthetic connected peer is added and removed within one frame to exercise loading/join cases; this is not a remote networking test. The headless suite checks all seven stone identities, original-site streaming, and exclusion of the server process from participants without rendering resources. Production excludes `RitualAcceptance` and its message observer.

The regression suite now has 164 tests, including frozen roster/life changes, single-attempt commitment, exact countdown boundaries, client clock offsets, reordered/duplicate displays, cancellation clearing, persistence failures, hidden-timer boundaries, restart/offline time, reward reservations and respawn-safe death commands. The current graphical gathering suite ends with an actual phase 5 lethal outcome; trophy/power checks run first. Actual host-plus-remote timing, dedicated-executable deployment, real network asymmetry/loss, and full acceptance remain phase 6; see [phase 4 evidence](EINHERJAR-PHASE4-RESULTS.md) and [phase 5 evidence](EINHERJAR-PHASE5-RESULTS.md).

## Ritual outcomes and Testing controls

Ritual diagnostics are written at Info level to each game's `BepInEx/LogOutput.log`. Search for `Einherjar ritual trace` and correlate `world`, `localSession`, and `attempt`. The server logs the initiating peer and whether it is the server player or a client, the exact zero-based ordered roster (including peer IDs and names), each raw `System.Random.Next` result and its exclusive upper bound, the selected victim, death dispatch or skip reason, confirmed death, and reward publication. `rng-initialized` includes the loaded assembly's build ID; `drawNumber` increases for that generator's lifetime and is unaffected by a cooldown reset. Punishment records `selectedIndex=none` and consumes no random draw. Selection is logged before persistence (`persistence=pending`); a subsequent `death-dispatch` proves it reached dispatch. Client logs record accepted death commands and their result. These diagnostics do not reveal the hidden deadline in player UI. Copy both logs before relaunching, because startup replaces them.

In an explicitly enabled, marked isolated test launch, F3 opens the shell outside chest coverage so **Testing** remains reachable. Transfers and shared spending still require chest coverage. With impairments off, **Reset cooldown** clears and persists the current world's server deadline. It rejects an active gathering, countdown or outcome. **Spawn essence** creates one real pickup near the living, fully spawned hosting player, without changing anger or shared stock. **Test lightning** plays the original Obliterator bolt and sound on that player without damage, death, reward, or timer changes. Only the graphical local host is an authorized operator; remote and headless operator controls are unsupported. Pending controls disable until a correlated reply or ten-second timeout. A timeout reports uncertainty and never retries the action. Session changes discard pending operations. Production builds contain neither buttons nor endpoints.

Use the same runtime/isolation guard and Steam access as above:

```powershell
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Outcomes -Mode Review -ExitAfter
.\tools\Test-Assets.ps1 -TestRoot '<isolated test root>' -Suite Outcomes -Mode Headless
```

The graphical suite is lethal to its isolated `EinherjarAssets` character and uses normal tombstones/respawn. It verifies a god-mode sacrifice, original Obliterator particles/audio with damage components removed, one post-death essence, persisted deadline/publication, duplicate and respawn-delayed commands, early punishment/message with zero rewards, unchanged nearby structure health, active-reset rejection, dead-player control rejection, unauthorized/stale/duplicate test requests, and persisted reset. The preview separately verifies unchanged player health, no death/reward/timer change, and one effect per operation. It writes `outcome-acceptance.txt`, `ritual-lightning-review.png`, and a prefab/component audit in `obliterator-lightning-prefabs.txt`. Headless testing writes `outcome-headless-acceptance.txt`, checks operator rejection and rendering-free registration/reward spawning. Run one mode at a time; verify a newly timestamped report begins with `passed`. Logs are overwritten by the next run. The full outcome journal retains prior uncertain test attempts for diagnosis, with no automatic death/reward replay.

Timer and failure tests inject monotonic/UTC clocks and controlled persistence completions into production outcome logic. They cover before/at/after expiry, countdown crossing expiry, configuration changes, wall-clock jumps during play, offline expiry, world-bound files, corruption/interrupted initialization, atomic replacement backups, death timeouts, frozen punishment rosters, write failure before death/reward and uncertain spawning. These are deterministic failure tests, not a claim of atomic recovery across game-world saves, deaths and files. A crash can lose an essence or leave its publication uncertain; inspect the journal and world before manual recovery.

## Correctness checks and testing limits

`build.ps1` runs the gameplay regression suite, production protocol checks, eleven deterministic fault-harness checks, game hook signature checks, and assembly isolation checks. Load presets are opt-in through the runner rather than part of every build. Virtual-clock tests compile the production Client and cover lost List requests/replies, deadlines, capped retry timing, manual overrides, closed menus, readiness loss, dispatch failures, error replies, retired snapshots, failure-triggered follow-ups, count/quality reconciliation, and uncertain mutations. The gameplay lab explicitly runs refresh maintenance on every virtual tick, including during death or disconnect. The protocol load runner substitutes logical peers and does not exercise this client state machine.

The test code preserves existing semantics: completed crafts/builds remain completed if their exact batch is rejected, rejected shared costs are recorded as unpaid, and a missing confirmation remains uncertain. Disabling faults does not automatically retry transfers, restore items, or introduce durable receipts.

Application-message discard, hold, and reorder are deliberate failure injection above Valheim's reliable transport. They do not model packet retransmission. Actual packet loss, connection establishment, real multi-process networking, Unity/Harmony execution, abrupt process failure, and in-game UI behavior require separate isolated game/network tests. Frame metrics are only available in-game. An injected save failure is a deterministic exception before writing, not a simulated disk crash or atomic-replacement failure.

## Repeat the refresh recovery acceptance test

Use the guarded isolated launcher with runtime testing enabled, FreeBuild = false, and faults initially off. Export a healthy baseline. In Testing, arm **Discard next reply** and trigger only a read-only refresh (ordinary polling can consume the armed fault). Export immediately and verify the discarded method is ListReply. Disable faults; after ten seconds the attempt expires, and after two more seconds its replacement can succeed without reconnecting. Check cached stock remains visible, manual Refresh is available between attempts, and pending refreshes return to zero.

Repeat with **Hold next reply**. Wait for expiration and a successful replacement before **Release held replies**; check ignoredRefreshReplies increases and accepted stock/quality stays unchanged. Also exercise the ten-second outage and a rejected crafting batch during recovery. Export before/during/after each case and after normal shutdown. Verify mutation-pending counts are preserved, timeouts never enter successful RTT samples, and final exported counts/qualities match persisted shared stock. Remote multiplayer and character-inventory conservation need separate validation.
