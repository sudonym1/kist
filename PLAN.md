# Handoff plan: recover from lost inventory refresh replies

Status: recovery fix implemented; automated validation passed; isolated installation and live acceptance pending.
Prepared: 2026-10-23.
Repository: `C:\Users\seamu\source\repos\SharedInventory`.

## Execution record (2026-10-23)

- Reproduced the original freeze through the production Client: a discarded ListReply left the later refresh at one request instead of the expected two.
- Implemented the injectable monotonic clock, ten-second deadlines, capped retry backoff, persistent explicit/failure demand, manual override, retired-reply rejection, and per-frame maintenance before player/menu early exits.
- Preserved cached stock, transfer semantics, optimistic batch deductions, and quality reconciliation. Added refresh recovery UI and test-only terminal expiry events, separate pending categories, counters, and bounded correlation storage.
- Production and testing builds passed 102 regression tests, production protocol checks, 11 deterministic harness checks, game-hook signature checks, and harness inclusion/exclusion checks using standalone .NET SDK 8.0.318.
- All seven load presets passed with zero violations. Only the intentional lost-deposit-confirmation preset retained an unresolved mutation (one deposit).
- Build logs and load reports are local artifacts under this worktree's `.work` directory. Packages are under `dist`.
- The user stopped Computer Use before installation. No new DLL was installed, no install backup was created, and no live recovery acceptance result is claimed. Genuine remote multiplayer also remains untested.
- Before resuming installation, inspect the isolated launcher's current behavior: it was observed rebuilding the original checkout before applying staged DLLs, which could overwrite this worktree's recovery build. Preserve its current version and ensure the next launch retains the intended validated binary.

The original handoff below records the implementation requirements and the remaining isolated-install/live-test procedure. Its planning-only authorization has been superseded by the user's request to execute it.

## Objective

Make shared-inventory refreshes recover after a lost request or reply, without reconnecting, replaying item mutations, or accepting stale inventory snapshots. Add client-level regression coverage and diagnostics that distinguish expired refreshes from uncertain transfers.

This handoff authorizes planning only. Implementation and a further test-install update are future work. Preserve the existing working-tree changes; do not reset or replace the current implementation with the historical `SharedInventory-Test-20261023/source` snapshot. Use a standalone .NET 8 or newer SDK. SharedInventory is independent of ThorsKist; no files from that mod are needed.

## Confirmed finding and evidence

The user ran the instrumented isolated game, exercised fault controls, exported a report, and exited normally. Two exports contain the same request history from one approximately four-minute host session:

- [Manual export, 15:50:45 local](SharedInventory-Test-20261023/game/BepInEx/config/SharedInventory-TestReports/20261023-225045-675.json).
- [Shutdown export, 15:50:56 local](SharedInventory-Test-20261023/game/BepInEx/config/SharedInventory-TestReports/20261023-225056-560.json).
- Logs: `SharedInventory-Test-20261023/game/BepInEx/LogOutput.log` and `SharedInventory-Test-20261023/Player.log`.

Request 167 was a `List`, started at session time 224.996 seconds. Its `ListReply` was deliberately discarded at 226.341 seconds. At the final export, it had remained pending for 20.606 seconds. Faults were off and scheduled/held packet queues were empty. Request 168, a withdrawal, completed successfully afterward.

The failure is in client refresh recovery:

1. `Client.List()` sets `listRequest` and refuses another request while `Listing` is true.
2. Only `Client.OnList()` receiving the matching reply clears that state.
3. No deadline expires the missing request.
4. Automatic refreshes remain blocked, and `Window` disables the Refresh button while `Listing` is true.

The missing reply was a read-only refresh, not an uncertain item transfer. This was application-message discard above Valheim's reliable transport, not evidence that ordinary packet loss bypasses retransmission.

Other observed behavior was consistent with the selected test conditions:

| Observation | Result |
| --- | --- |
| Requests | 168 issued, 167 completed; 93 Lists, 24 withdrawals, 1 deposit, 50 ingredient batches |
| Pending item transfers / ingredient batches | 0 / 0 |
| Transfer or batch error replies | None recorded |
| Final cache versus persisted shared inventory | Counts and qualities matched for all 384 entries; 32,149 units |
| Logged errors | One deliberately injected save failure; no unexpected exceptions found |
| Completed-request RTT | Median 315 ms; p95 2.02 s; maximum 5.52 s across mixed fault conditions |
| Gameplay-export frame time | p95 5.97 ms in the retained rolling sample |

Matching shared counts do not independently prove conservation across the character inventory. Frame and queue samples retain only the latest 8,192 observations. The two exports are cumulative snapshots, not independent runs. Remote multiplayer remains untested.

These reports and the test install are local, Git-ignored artifacts. Include copies of the evidence when handing this plan to someone without this workspace.

## Scope and behavior to preserve

- Fix refresh recovery in normal production builds as well as testing builds.
- Keep normal successful polling at one shared two-second interval across inventory, crafting/building menus, and supported station targets.
- Automatically retry only read-only `List` operations. Do not retry deposits, withdrawals, returns, or ingredient batches, and do not automatically restore items after missing confirmations.
- Preserve request ordering, count/quality reconciliation, optimistic crafting deductions, and the intentional completed-action/unpaid-shared-cost behavior after batch rejection.
- Keep Valheim's transport and the existing List/Deposit/Withdrawal protocol. No new RPCs, receipt system, save-format changes, server sessions, or synchronous waits are required.
- Keep test instrumentation compiled out by default. `EnableTestHarness`, runtime `Testing.Enabled`, and `Testing.Impairments` must retain their existing default-off behavior.

Disabling faults, disabling the runtime harness, and installing a normal DLL are separate operations. Keep the [shutdown instructions](TESTING.md#disable-fault-injection-and-testing) accurate after implementation, including the staged-update behavior of the isolated launcher. Those instructions currently describe the refresh-freeze workaround; update that limitation only after the recovery fix is validated.

## Implementation sequence

### 1. Add a failing client-level reproduction

Extend `tests/LabGameplayTests.cs` to issue a List through the production `Client`, discard its reply, advance virtual time, disable faults, and request another refresh. Establish that the current client remains stuck before changing production logic.

The protocol load runner substitutes logical peers and does not execute `Client.List()`/`Client.OnList()`. Its seven passing presets therefore did not cover this state-machine failure. Keep the new reproduction in the suite that compiles the actual client with game doubles.

### 2. Add refresh lifecycle maintenance

Use one injectable monotonic clock for all refresh entry points, including window opening, manual Refresh, polling, and failure-triggered refreshes. Retain the existing convenient constructor for production callers. Use the injected clock in tests rather than sleeping.

Track the active refresh ID, dispatch time/deadline, retry count, retry eligibility time, and outstanding refresh demand. Separate timeout maintenance from the decision to poll. Call maintenance from `Plugin.Update()` even before the early return for a missing/dead local player or closed menus.

Apply this initial policy:

- An attempt expires after 10 seconds without its matching reply.
- Expiration retires that ID, clears its active `Listing` state, and records the timeout.
- Automatic retry delays after successive expirations are 2, 4, 8, 16, then 30 seconds, capped at 30 seconds.
- A successful current refresh resets retry backoff and resumes ordinary polling.
- Send retries only while `Ready` and refresh demand exists. Active polling contexts supply demand; explicit/manual or failure-triggered demand must survive a timeout even if menus close.
- When readiness is lost, expire the attempt but defer sending until readiness returns. Session teardown discards the old client's state normally.
- Maintain one current logical refresh attempt. Older requests may still exist in the transport; do not introduce cancellation RPCs.
- Menu polling, redraws, and repeated failures must not reset backoff or create per-frame retry traffic.
- Manual Refresh may start one attempt during automatic backoff when no current attempt exists. Repeated clicks coalesce, and a failed manual attempt does not reset the retry failure count.

Preserve the existing follow-up-refresh requirement when a transfer/batch failure happens after a List was dispatched. A replacement List can satisfy demand that predates its dispatch; a newer failure must still request a follow-up. Avoid either losing that newer demand or creating a redundant retry loop through `refreshAgain`.

### 3. Reject retired replies without damaging reconciliation

Accept a snapshot only for the currently active refresh ID. A reply from a retired attempt must not update counts/qualities, clear a newer request, reset its deadline/backoff, or trigger a follow-up request.

Retain `lastSnapshot`, `latestTotals`, and pending-batch deduction rules. Specifically verify that a replacement snapshot and a late old reply cannot restore already-spent ingredients, forget authoritative quality, or subtract a batch twice. Retiring a refresh must not clear pending item transfers or ingredient batches.

If dispatch throws before a request can be sent, release the active refresh state and use the same recovery policy rather than leaving `Listing` stuck.

### 4. Expose recovery state and correct telemetry

Give the inventory window a short refresh-specific state such as “Refresh timed out; retrying” and make manual Refresh available once an attempt expires. Preserve cached stock during refresh recovery; communicate that it is cached rather than clearing it to an apparent empty inventory. Keep refresh status separate from deposit/withdrawal result messages.

In testing builds, add counters/events for refresh timeouts, retries, and ignored retired replies. Add an explicit terminal outcome for expired refresh attempts and remove them from active diagnostic pending counts. Keep timeout history separate from successful RTT samples and server rejection counts. A late reply to an expired attempt must not become a successful current completion.

Expose pending refreshes separately from uncertain item mutations. Use bounded correlation/history storage. Consider the ordering of `RpcApi` completion telemetry and `Client.OnList()` when classifying retired replies. All added diagnostic hooks must remain behind `SHAREDINVENTORY_TESTING`.

### 5. Add edge-case coverage

Use virtual time to cover the following cases:

| Case | Required result |
| --- | --- |
| Lost List request or ListReply | Attempt expires and refresh later succeeds without reconnecting |
| Repeated loss / persistent outage | Retry intervals follow the cap; polling and manual actions do not create duplicate active attempts |
| Ten-second outage overlapping a deadline | Eventual recovery; no permanent loading state |
| Old reply arrives before a replacement is sent | Retired reply is ignored |
| Old reply arrives while a replacement is active | New attempt and its deadline remain intact |
| Old reply arrives after replacement success | Accepted snapshot stays unchanged |
| Pending optimistic batches / newer transfer replies | Counts and qualities remain correct; no restored stock or double deduction |
| Failure-triggered follow-up during a timeout | Refresh demand survives and is coalesced correctly |
| Menus close, player dies, or connection drops | Maintenance continues; no dispatch while unready; resume or teardown behaves correctly |
| Dispatch exception or error reply | Client does not become permanently busy; recovery traffic stays bounded |
| Missing mutation confirmation | Existing uncertain-transfer behavior remains unchanged; no automatic mutation replay |
| Test telemetry after recovery | No active expired refresh; timeout and late-reply history remain visible |

Healthy polling, manual refreshes, and existing stale-snapshot regression tests must continue to pass. Update the gameplay lab's virtual-clock loop to execute maintenance explicitly.

## Expected files

| Files | Planned work |
| --- | --- |
| `src/Client.cs` | Clock injection, deadlines, demand/backoff handling, retirement and reply acceptance |
| `src/Plugin.cs` | Per-frame maintenance before readiness/menu early exits |
| `src/Window.cs` | Refresh recovery status and manual Refresh availability |
| `src/Testing/TestHarness.cs`, `src/Testing/TestPanel.cs` | Terminal timeout outcomes, separate pending categories, recovery metrics |
| `src/RpcApi.cs` | Adjust test-only completion classification if needed; preserve wire behavior |
| `tests/LabGameplayTests.cs`, `tests/GameplayTests.cs`, test doubles | Reproduction and state/reconciliation tests |
| `tools/ProtocolTests/LabChecks.cs` | Diagnostic lifecycle checks where appropriate |
| `README.md`, `API.md`, `TESTING.md` | Document safe refresh retries, deadlines, diagnostics, and repeat-test steps |

This is a change map, not a requirement to modify every file. Avoid unrelated gameplay, station-shortcut, persistence, or transport changes.

## Build and automated validation

The workspace has an independent SDK at `.work/dotnet/dotnet.exe`. The system PATH previously selected an older SDK, so select .NET 8 explicitly. From the repository root:

```powershell
$repo = (Get-Location).Path
$sdkDirectory = Join-Path $repo '.work/dotnet'
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
$game = Join-Path $repo 'SharedInventory-Test-20261023/game'
$bep = Join-Path $game 'BepInEx'
$env:DOTNET_CLI_HOME = Join-Path $repo '.work/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:PATH = $sdkDirectory + ';' + $env:PATH
& $dotnet --version

# Run separately and check each exit code before proceeding.
.\build.ps1 -ValheimDir $game -BepInExDir $bep
.\build.ps1 -ValheimDir $game -BepInExDir $bep -EnableTestHarness
.\tools\Run-LoadTests.ps1 -All -Dotnet $dotnet -ValheimDir $game -BepInExDir $bep
```

`build.ps1` runs regression, protocol, deterministic harness, game-hook signature, and harness inclusion/exclusion checks. Do not use `-SkipTests` for final validation. Inspect load-report violations and unresolved outcomes, not only latency. The intentional lost-deposit-confirmation preset should still retain its unresolved transfer.

Before this fix, 87 regression tests, 10 harness checks, production protocol checks, and all seven load presets passed. New test totals will increase. Read [TESTING.md](TESTING.md) for runner/report details. Existing assembly-reference warnings and the test-double nullable warning are not newly discovered gameplay failures.

## Isolated install update and live acceptance test

Perform this phase after implementation and automated validation. The existing isolated root is `SharedInventory-Test-20261023`; its launcher is `Launch-Test.cmd`, character is `testchar`, and world is `TESTWORLD`, UID `1505299267`.

1. Verify the isolated game has exited normally. Do not terminate unrelated game processes or replace loaded DLLs.
2. Make a fresh backup of the current saves, shared database, configs, active DLLs, and `pending-update`. Preserve the user's post-test inventory; do not restore the earlier baseline or reseed TestPlayer's stock.
3. Stage the new testing DLL in `pending-update/SharedInventory.dll` and apply the existing guarded updater. Verify source, staged, and installed hashes agree. The launcher reapplies staged DLLs, so replacing only the active DLL would undo the fix on the next launch.
4. Retain the isolation helper, guarded launcher, and local save path. Keep `FreeBuild = false` and faults initially off. Runtime testing is explicitly enabled in this isolated install; product defaults remain off.
5. Launch with Steam running, host the existing test world, and verify normal refreshes first.
6. Arm **Discard next reply**, then trigger a read-only Refresh without another action consuming the armed fault. Confirm the export identifies a discarded `ListReply`.
7. Disable faults. Verify expiration and recovery without reopening the session, automatic refreshes resume, manual Refresh becomes usable, and late/held replies cannot change newer stock.
8. Repeat with **Hold next reply**, let the attempt expire, obtain a replacement snapshot, then release the old reply. Also test the ten-second outage and a crafting failure overlapping refresh recovery.
9. Export reports before/during/after recovery. Verify active pending refresh returns to zero, mutation-pending counts are not cleared by timeout handling, and the expected timeout/retry/ignored-reply events appear.
10. Exit normally and compare exported cached counts/qualities with persisted shared counts. Record what was actually exercised and update the install guide/setup manifest. Genuine remote multiplayer remains a separate validation step.

## Completion criteria

- A discarded List request or reply cannot leave refreshes permanently blocked.
- Recovery happens without reconnecting and follows the defined timeout/backoff policy.
- Automatic and manual paths maintain one current attempt and bounded retry traffic.
- Retired replies cannot clear newer requests or overwrite newer inventory state.
- Existing count/quality and optimistic-consumption invariants remain intact.
- Item mutations are never automatically replayed or restored because of refresh timeout.
- Reports distinguish expired refresh history from active uncertain transfers.
- Production and testing builds pass their checks; normal DLLs contain no harness types.
- A new isolated in-game report demonstrates the original failure is resolved.
- The handoff records changed files, actual validation results, binary hashes, backup location, and remaining multiplayer limitations.
