# M7: Live journal integration

Status: **integrated Windows candidate; loaded-world activation and QA gates remain open**. This milestone owns all live-integration and platform work
left after [M6: Transport foundations](network-journal-m6.md). The scope was moved
here at the user's request on 2026-10-19; no unfinished integration gate was
claimed to have passed. Game validation and release are now **M8**.

## SQLite cutover implementation (2026-10-20 UTC)

Preservation commits: `be429dd` keeps the profiling collector, analysis and legacy
baseline; `6137246` checkpoints all pre-existing M7 work. The journal F4 profiler
retains 29 targets, replacing removed legacy Save/Take targets with world-owner
tick/journal take; baseline sources remain in the preservation commits.

`JournalWorldOwner.cs` starts the production catalog/runtime at normal world
startup, installs `JournalDirectRpc`, routes listen-host sessions through the
same encoded protocol, and settles the old world before reopening. Prefab names
and configuration are captured on Unity's thread; SQLite, checksums, encoding
and server timers run on workers. Initial display installation and entry effects
share the bounded Unity queue. `JournalStockProjection` enforces thread ownership,
caches material counts and signals incremental changes without heartbeat redraws.

`KistServer` retains hird controls/presentation. Live legacy item mutation,
`.dat` load/save, optimistic client escrow and timeout refunds are removed.
Metadata commits through SQLite before buffered confirmations are sent.
`JournalControlRpc.cs` also authenticates hird controls on the exact ready direct
peer; old routed registrations are removed. The control wire is **protocol 5**.
State carries the journal epoch instead of an inventory mirror. Session failure/
catch-up closes Kist actions. Startup logs identify SQLite/journal and disabled
legacy writes; applied item entries log their sequence and kind.

`JournalMaterials.cs` procures missing materials as separate purpose-tagged,
single-stack withdrawals before vanilla creates a craft/station effect. A partial
refusal leaves delivered inputs in the character and creates no output. Placement
asks the player to place again when inputs arrive, avoiding delayed automatic
placement. Checks do not spend items; output retrieval/full/toggle paths avoid
procurement. Reservations block competing local payment. `JournalLogout.cs`
waits asynchronously for ordinary menu logout marker/acknowledgment; abrupt
shutdown/death/replacement uses exclusion/settlement. The journal never requests
or awaits a character save.

The **current disposable vault**, not the Desktop import source, was migrated
once: `.tools/m7/manual-install/saves/ThorsKist/kist_2186005207.dat`.
Source: **10,022 bytes**, SHA256
`4cda2c3dc49f96dacc6ff0f8ff4d2663653f8d0ca92fb78595e3f7bced32f5f1`.
Its verified `.journal-backup-<hash>` remains beside it. SQLite retains **587
stacks** and exactly **one migration record**. Native verification compares every
quantity, canonical item byte, slot/overflow position and hird header. A three-Wood
deposit/withdrawal uses ordinary `KistClient.Deposit`/`TakeAction` with a native
disposable inventory and the actual migrated SQLite vault. Temporary stock is
removed, restoring the exact original current checksum. Logout/reopen uses a
decoder that throws if legacy bytes are consulted; fresh processes see the prior
durable head. `.dat` stays unchanged. All **568 original save files** still match
the independently verified backup.

Evidence: `.tools/m7/manual-install/cutover-evidence.log`,
`cutover-candidate-final.log`, `.tools/m7/cutover-adapter-evidence.log`,
`cutover-harness.log`, `cutover-patches.log`, `cutover-native-build.log`,
`cutover-categories.log` and `cutover-original-save-audit.json`.
The explicit test-only `-thorskist-cutover-verify` switch runs
`tools/AdapterTests/CutoverVerification.cs` and exits the hidden isolated process.
Its temporary range setting is restored without saving configuration. Native
probe inventories are scratch objects, not the saved test character. These
checks do **not** prove normal-world owner startup, real-character lifecycle,
actual station/crafting outcomes or authenticated multiplayer.

Checks: **175,291 harness assertions**, including existing forced crash suites;
Windows reservation/projection/decoder/dispatcher/SQLite/control/session probes
pass (**148 cumulative adapter assertions**, plus **16 session/TCP** and **13
current-vault cutover** checks). Release mod/adapter builds: zero warnings/errors.
**124 Harmony targets** and **124 game member references** pass; category,
version and package checks pass. Sandboxed game startup could not initialize
Steam; hidden probes were rerun with Steam IPC access. Only agent-launched test
processes were stopped.

Final bundle: `.tools/m7/ThorsKist-M7-Windows-Journal-v10.zip`, containing matching
**Setup.cmd and Update.cmd** with managed/native Windows SQLite dependencies,
F4 profiling and process-only cloud isolation. Bundle SHA256: `43e0e0567d409e538e7ffbff1588bd8a877599169101e03f91ee60663e347524`.
It supersedes v4 and intermediate
cutover candidates. The local isolated installation is updated with that payload;
the second PC must apply Update before connecting and has not been remotely
updated or observed. Both game assembly hashes must match. Reproduce with
`scripts/build-windows-test.py` and the verified v4 loader baseline plus current
compiled plugin. Nothing is published.

Remaining activation gates: ordinary host authority startup log, loaded-character
deposit/take and save/exit/reopen; actual crafting/build/station effects and partial
failures; real reservations/death/replacement, logout/rejoin; authenticated two-PC
lifecycle/ingress; and F4 comparison. The user drives these checks in separate
installs. **M7 completion and M8 readiness are not claimed.** Historical sections
below describe prior stages; references to an absent owner or keeping legacy
active are superseded by this cutover candidate.

### First loaded-host startup: local control cursor correction

The user loaded M7TEST in the separate game. Production startup logged
`KIST AUTHORITY READY SQLite/journal; 1 hirds`; this is now real-world startup
evidence. The UI showed no configured Kist because local `ZPackage` delivery
passed the writer's end cursor to the server reader, generating `EndOfStreamException`
before Hello could identify the character. Network delivery resets the reader,
so the earlier journal/API and TCP probes did not cover this control boundary.

`JournalControlRpc.DeliverLocal` now creates a fresh byte-backed package for each
local request/reply without changing the sender cursor. Native regression covers
complete Hello and hird State/epoch decoding, repeated delivery, and independent
sender/receiver cursors: **9 assertions pass**. Release mod/adapter builds have
zero warnings/errors; Harmony/accessibility checks remain 124/124. The user saved
and exited before local binaries were updated. v9 Setup/Update payloads match the
local installation. The regression process exited automatically without loading
a world. SQLite remains at head 20 with 587 stacks and the legacy hash unchanged.

Evidence: `.tools/m7/host-v8-control-failure.log`,
`.tools/m7/manual-install/local-control-v9.log`,
`.tools/m7/local-control-build.log` and `local-control-patches.log`.
Hird UI and actual character actions on the corrected loaded host remain pending.

### Second loaded-host startup: preserve pending journal login

The user confirmed that the hird appeared on v9 but the Kist view was empty,
then saved and exited. SQLite still contained **587 stacks**, the same current
checksum and head **20**: no item-session login committed during that host run.
`JournalWorldOwner` was treating the client's initial stopped replica as a
terminal session and could close it before the asynchronous login was sent.

`JournalClientConnection` now distinguishes `LoginPending` from `ProcessingEnded`.
The owner keeps pending login alive, while actual closure or a stopped initialized
replica still triggers cleanup. Native session startup has a bounded 31-second
client timeout and retains the original failure message. Host session startup/
closure also logs explicitly. Bridge tests withhold server delivery and assert
that pending login remains alive, becomes active after verification, and becomes
terminal only at closure: **24 bridge assertions pass**.

The native current-vault regression now applies the same `ProcessingEnded` owner
check while driving login, and asserts every SQLite stock row reaches the display:
**LOGIN ACTIVE displayed=587; sequence=21**. Deposit at 22, withdrawal at 23,
logout and reopen pass: **15 cutover assertions**, original stock restored and
legacy hash unchanged. These are native inventory/API checks; corrected loaded
host display still needs user confirmation. The complete harness passes
**175,295 total assertions**; Release builds have zero warnings/errors and
Harmony/accessibility remain **124/124**.

v10 Setup/Update payloads are rebuilt and installed in the local separate copy.
Evidence: `.tools/m7/host-v9-empty-view.log`, `login-pending-build.log`,
`login-pending-harness.log`, `login-pending-patches.log`, and
`.tools/m7/manual-install/login-pending-v10.log`. No original saves were loaded.

## User-attended validation scope (2026-10-19)

### Fresh-session priority after baseline

The user explicitly requests **journal integration and SQLite replacing `.dat`
together**, followed by testing only the new system. Stop user-attended legacy
tests. The current manual build includes journal components but has no installed
normal-world journal owner: ordinary gameplay and persistence still use legacy
paths. The completed timing run is useful baseline evidence only.

Next work is implementation, not another diagnostics-only test build:

1. Install the production world/catalog/runtime owner at world startup, with
   authenticated direct-peer and listen-host sessions and bounded worker/game
   thread dispatch. Back up and transactionally import the current disposable
   world's `.dat` into SQLite once, preserving current gameplay changes,
   memberships, metadata, unknown records and overflow. Do not replay the desktop
   import or replace the user's current vault with the original source.
2. Route all ordinary deposits, withdrawals, crafting/station material use,
   metadata/membership changes, disconnect/rejoin and UI through journal/SQLite
   ownership. Finish reservations, permissions, acknowledgment and settlement
   wiring. Keep unresolved-session mutation paths closed. Remove full legacy
   state refreshes from the integrated item path.
3. Make SQLite the sole live persistence authority after migration. Disable the
   legacy `.dat` reader/writer and competing legacy mutation paths for migrated
   worlds. Preserve the source as a backup; never dual-write or silently fall
   back/reimport on restart. Provider/migration/load/commit faults fail closed.
4. Verify ordinary actions commit journal entries and SQLite stock; verify
   save/exit/reopen restores SQLite state without changing or rereading the old
   `.dat`. Add explicit startup/runtime authority evidence, not merely plugin
   presence or fixture PASS logs. Run meaningful automatic integration/fault
   checks before requesting manual validation.
5. Update both isolated Windows installations/installer payloads with matching
   SQLite dependencies and the integrated plugin. Only then ask for new-system
   single-host and two-account tests, retaining F4 for before/after comparison.

Implementation files already present include `JournalGameplaySession.cs`,
`JournalDirectRpc.cs`, `JournalStockProjection.cs`, `JournalReservationGuard.cs`,
`Patches_Reservations.cs`, `src/ThorsKist.Sync/JournalVaultRuntime.cs`,
`JournalClientConnection.cs`, `JournalServerConnection.cs`, and
`src/ThorsKist.Sync.Sqlite/JournalWorldCatalog.cs` / `JournalWorldRuntime.cs`.
Inspect their current contracts and the normal `Main`, `Patches_Net`,
`KistServer`, `KistClient`, crafting/station and UI entry points before wiring.
The substantial uncommitted M7 work must be preserved; do not reset the tree.

The local separate game may still be running. The user controls all gameplay
input. Obtain save/exit handoff before replacing its binaries; do not stop it
or operate its controls unasked. Original Steam installation is
`D:/SteamLibrary/steamapps/common/Valheim`; the disposable full copy is
`.tools/m7/manual-install`. Local test world is `M7TEST`, UID `2186005207`,
character ID `2144103681`, current legacy vault
`saves/ThorsKist/kist_2186005207.dat` beneath that copy. Test-only min members=1;
production default stays 2. Portable v4 is the **pre-cutover baseline build**,
not the final integrated installer. Do not hand it out as journal gameplay.

### Saved profiling work

The test-only profiler is `tools/AdapterTests/MainThreadProfiler.cs` and its
Unity-independent collector `MainThreadTimings.cs`, wired in `Plugin.cs` with
the Unity UI reference in `AdapterTests.csproj`. Aggregation tests are
`tests/ThorsKist.Sync.Harness/TimingTests.cs`, linked collector source in the
harness project, and `--timing-only` in `Program.cs`. All 17 checks pass; both
Release builds succeeded without warnings; native startup installed 29 targets.
Reusable analysis is `scripts/analyze-main-thread.py`.

The compact baseline summary/method data are retained in
`notes/performance/m7-legacy-baseline-20261020/`. Full CSVs remain under
`.tools/m7/manual-install/BepInEx/timings/20261020-034820-1ca50b/`. A separate
checkpoint ZIP under `.tools/m7/checkpoints/profiling-baseline-20261020.zip`
preserves the profiling sources, supporting files, plan, raw capture, analysis,
and v4 installer without modifying the current running game. This is a local
archive checkpoint; the working tree remains uncommitted.

The user limits the current validation target to **Windows**, with two Windows
machines and two independent Steam accounts. Begin with BepInEx and Thor's Kist
only. Linux/macOS execution and representative third-party inventory mods are
outside this requested validation scope; earlier references to those platforms
remain historical requirements and do not block this Windows-only target.

Manual reservation controls are in the isolated adapter-test plugin only:
F5 adds a disposable test kit; F6 reserves one item from hotbar slot 1; F7
releases without removal/refund; F10 applies a verified deterministic deposit
and checks duplicate suppression. These controls require the guarded isolated
test process and a loaded character. They exercise real input/death/inventory
hooks, but do not establish normal-world cutover or authenticated journal RPCs.
F8/F9 retain the separate pooled-UI scratch fixture/clear controls.

Prepared runtime: `.tools/m5/run-b0b3aca0bc7643748905320b9e334cba`.
Manual observations and connected-peer evidence are pending; do not mark them
passed from automated startup probes.

The user additionally requires a separate **full game installation on both
machines**, not merely separate preloader/save paths. The initial test process
was stopped. `scripts/setup-manual-test.ps1` now copies only game assets and an
explicit checksummed test payload into a new empty destination, rejects source/
destination overlap and linked inputs, verifies each copied file, and creates
a launcher targeting that copied executable. It never copies normal saves or
the source installation's plugin/config folders. The local test copy is
`.tools/m7/manual-install` (**1,023 copied files verified**). The second-PC setup
bundle is `.tools/m7/ThorsKist-M7-Windows-Manual.zip`; its default destination is
`Desktop/ThorsKist-M7-Test`. It checks the source game's assembly checksum to
require matching game builds. New test characters/worlds must use local storage;
Steam cloud entries can still be visible and must not be selected. Remote setup
and all user-observed test outcomes remain pending.

The local copy is running the initial manual-reservation payload. A subsequent
`AuthenticatedPeerProbe` test-plugin build adds a dedicated, bounded challenge/
reply RPC, production `JournalDirectRpc.Identify` capture on actual ready peers,
and production ingress rejection checks using those captured identities.
It logs account fingerprints instead of raw account IDs and does not install
the gameplay owner or mutate items. This diagnostic has compiled, but live
two-account execution is pending. The second-PC v2 bundle is
`.tools/m7/ThorsKist-M7-Windows-Manual-v2.zip`. Before the two-PC phase, update
and restart the local test copy with this matching plugin; do not attribute
authenticated-probe results to the earlier running payload.

### Cloud enumeration isolation correction

The user observed regular characters/worlds in the separate game copy. The local
save override was active (the new `m7testlocal` character was written beneath
`manual-install/saves/characters_local`), but Steam cloud enumeration remained
available independently. This was an isolation gap; merely asking the user not
to select cloud entries was insufficient for the requested separate test setup.

`TestSaveIsolation` now patches the guarded test process's save-provider support,
cloud enumeration and save-root lookup, and filters character/world lists to
local sources. It changes no Steam preferences or network authentication. The
test game was closed from world selection and restarted with the patched plugin.
Startup confirms `M7 LOCAL-ONLY SAVE ISOLATION`; visual inspection confirms only
`M7TESTLOCAL` appears in character selection and the world list is empty (the new
world dialog opens). All **568 original save files** match the pre-test backup.
No original world was loaded during this correction.

Use `.tools/m7/ThorsKist-M7-Windows-Manual-v4.zip` on the second PC. Earlier bundles
are superseded. `Setup.cmd` creates a new separate installation; `Update.cmd`
updates an existing separate test installation while preserving its local test
saves. The updater refuses normal Steam installation paths, requires the guarded
test launcher/installation manifest, checks payload hashes and rejects linked
destinations. Its main-Steam-path rejection was verified before any write.

## Implementation evidence (2026-10-19)

### Main-thread timing captures

At the user's request, the guarded test plugin now instruments 28 Kist methods
plus the generic Unity canvas update as a separate comparison metric. F4 toggles
capture, with a 90-second / 30,000-frame limit and three warm-up frames excluded.
`MainThreadTimings` has 17 deterministic assertions covering nested union,
exclusive method time, non-Kist control scopes, frame/GC context, sample-ring
retention, frame caps, percentiles and invalid-scope recovery. Release builds
of the harness and adapter succeed without warnings.

The measured Kist scope is synchronous elapsed time on the Unity thread, with
nested instrumented calls counted once per frame. CSV reports retain method
inclusive/exclusive totals, p50/p95/p99/max calls, per-frame Kist union, frame wall
interval, GC deltas, UI state and stack count. Background workers are excluded;
report export runs on a worker. Frame wall includes VSync/GPU/OS and must not be
reported as Kist blocking. Generic canvas layout is not attributed to Kist.
Deferred rendering and uninstrumented hooks are not fully attributed, and small
probe overhead remains in the measured spans. Invalid nesting is reported.

Protocol: capture idle with Kist closed, then a separate capture while opening,
scrolling, sorting and transferring items from the imported vault. Repeat with
the second authenticated PC. Keep fixture controls out of performance captures.
Evidence belongs under `manual-install/BepInEx/timings/<UTC timestamp>/`.
First user-attended capture: `20261020-034820-1ca50b`, single listen-host,
no second player. User reported an idle period, opening the Kist, withdrawing
approximately 16 stacks and depositing them back. Instrumentation records 15
Take calls and 15 Deposit calls; displayed stack counts go 587 -> 572 -> 587.
The 34.700-second capture contains 6,819 complete frames and zero invalid scope
nestings. Analysis is retained beside the CSVs as `analysis.json`, reproducible
with `.tools/m7/analyze-main-thread.py <capture-directory>`.

Measured synchronous Kist main-thread elapsed time:

| Phase | Mean per frame | p95 | Maximum |
| --- | ---: | ---: | ---: |
| Idle before opening (3,760 frames / 16.006 s) | 0.012 ms | 0.014 ms | 0.305 ms |
| Opening frame | 486.586 ms | — | 486.586 ms |
| Transfer frames (30) | 83.938 ms | 95.188 ms | 101.457 ms |

The opening frame spends 395.093 ms in `BuildKistRows` and includes a state
refresh; a collection is also observed during that frame interval. The generic
canvas metric totals 100.353 ms that frame, outside the Kist union, and is not
exclusively attributed to Kist. Full frame wall is 618.891 ms.

`KistClient.ReadState` executes 31 times: mean 79.803 ms, maximum 92.391 ms.
Its exclusive total is 2,460.935 ms of 2,473.888 ms inclusive, making full legacy
state reconstruction the dominant measured transfer cost. Source inspection
shows `ReadState` calls `Kist.Load(inv)` for each full state, while `SendState`
serializes the entire authoritative inventory. This supports the full-rebuild
bottleneck; deeper attribution inside `Inventory.Load` is not measured here.
None of the 30 transfer frame intervals records a GC collection. Two disk saves
take 2.048 / 2.788 ms, so these saves do not explain the ~80-ms transfer stalls.

All transfer frames exceed both a 16.67-ms frame budget and 50 ms of measured
Kist work. Whole-capture p99 is only 0.027 ms because transfers are fewer than
1% of frames; it must not be used to dismiss these stalls. This is a measured
legacy gameplay baseline, not a journal-performance or two-PC pass. Opening
and transfer stalls remain performance issues to address and compare after
integration. UI state is sampled at frame entry: identify the opening frame by
the `KistUI.Open` call, not by the first `ui_open=1` row.

The user saved and fully exited before updating the isolated local installation
to the v4 payload. Relaunched copied executable as PID 14036; user retains game
input. The matching portable bundle is
`.tools/m7/ThorsKist-M7-Windows-Manual-v4.zip` (Setup for new copies, Update for
existing separate copies). Payload checksums were regenerated; the archive has
no user saves or desktop-import data. The normal Steam installation was not
updated.

### User-attended desktop vault import

At the user's request, imported `Desktop/kist_1410349507.dat` into the isolated
`M7TEST` world's legacy Kist (`kist_2186005207.dat`). The user saved and fully
exited before staging. Test saves and configuration were backed up under
`manual-install/before-desktop-import-20261019203909`, including an independent
copy of the desktop original. Source SHA256:
`BB1AE01B48E53EF78C4D435D508B9411412B498699EB1A9A4E8671E0852C5B33`.

Independent binary audit found **590 stacks / 19,814 items**, format 109, no
duplicate slots, and a required height of **125 rows**. The test-only importer
restores/verifies at most 32 records per frame before installing anything,
refuses an occupied destination or unavailable/changed item metadata, and
preserves the original **9,893 inventory bytes** exactly in the adapted save.
It changes only the test hird header to make the loaded test character owner,
add that character as a member, and put the anchor at the character's position.
The isolated config now has `minHirdSize = 1` and `kistRows = 125`; the guarded
test plugin also forces minimum membership to 1 for future test sessions.

The game log reports `M7 DESKTOP IMPORT PASS stacks=590 quantity=19814`.
The retained `BepInEx/manual-import/prepared-test-vault.dat` was independently
checked against the desktop source: inventory bytes match exactly and the
desktop file's SHA256 is unchanged. The user confirms the inventory is visible
and that subsequent withdrawal/use explains the live file's 587 stacks and
normal format-110 reserialization. Do not reimport over those gameplay changes.
This is a real-save decoder/loaded legacy gameplay import, not a normal-world
SQLite cutover pass. The user is driving the game; no further mouse/keyboard
input is authorized while that handoff remains in effect.

### Lifetime, persistent world, gameplay session and retention continuation

- `JournalVaultRuntime` owns independent async connection pumps, heartbeat and
  deadline timers, checkpointing, exclusion/settlement and awaited shutdown.
  Provider failure latches access closed. Unity does not wait on its timers or DB.
- `JournalClientConnection` joins correlation, login reassembly, verification,
  bounded Unity dispatch, cumulative ACKs, logout/rejoin and self-suspension.
  Control replies remain independent of queued effects, avoiding ACK deadlock.
  Explicit local invalidation stops queued effects before requesting suspension
  and closing control receive.
- `JournalWorldRuntime` owns catalog-backed lazy vault writers, immutable
  membership snapshots, metadata CAS, departed-peer settlement, retirement of
  empty vaults and async world teardown. Retired epochs and retry history remain
  durable; unknown or overflow stock prevents disband. Stale `.dat` files cannot
  re-create retired vaults. This library owner is not yet selected at normal
  `KistServer` startup.
- Access decisions now distinguish login, deposit, take, craft and station use.
  Each queued frame retains its captured authenticated account/character/range
  facts. Command purpose survives wire, SQLite, replay and deduplication. Existing
  inventory-purpose history checksums retain their original bytes.
- Wire version **3** (`ThorsKist_Journal_v3`) carries 256 KiB canonical records.
  Typed, ordered command/entry transfers retain 60 KiB RPC frame bodies, bounded
  queues, shared allocation budgets and fixed transfer deadlines. Control replies
  remain independent; oversized entries register their deadline before transfer.
- SQLite schema **5** stores checksummed session and settlement rows separately
  from its small replication root. Root verification covers every retained row,
  including closed retry tombstones. Closing releases immutable login payloads,
  retaining UUIDs, request keys, positions, cooldown and logout identity. Schema
  upgrades remain transactional. A test persists/reopens over 64 MiB of active
  login images, checks v4 migration and detects missing closed-session history.
  Request results and settlement identities are deliberately retained; there is
  no aggregate 64 MiB replication blob. Memory/rewrite costs still grow with
  retained history and need profiling; this change does not claim constant cost.
- Login settlement lists now cover only the login replay window. Checkpointed
  effects retain durable settlement rows without expanding future login images.
- `JournalGameplaySession` owns a real character adapter, dispatch queue,
  reserved deposits and UUID-based withdrawals. `JournalStockProjection` restores
  at most 32 stacks per frame and applies changed rows individually, preserving
  original records, opaque prefabs and overflow. The pooled UI captures UUIDs
  instead of slots when a journal session is selected. Selected journal mode
  blocks legacy deposit/take replies and legacy material consumption; crafting
  remains disabled in that mode until its async path is implemented.
- `JournalLegacyDecoder.DecodeCaptured` parses canonical legacy records on a
  worker from immutable prefab-name/version snapshots. Unity/Mono verified that
  known and unknown records match the game-thread decoder.

Evidence: `.tools/m7/final-harness.log` passes **175,274 assertions**, including
the fault/crash suite, 20 client/server bridge checks (256 KiB payload, exact
retry and withdrawal), 38 access checks and 10 retention/migration checks.
`.tools/m5/run-34d86029e55047e986a4df0755b7c822/BepInEx/LogOutput.log`
records **16 session/gameplay TCP checks**, **6 stock-projection checks**,
**63 reservation checks**, worker migration, catalog and SQLite provider passes
in the real Windows Unity/Mono runtime. These use disposable inventory objects
and real `ZRpc`/`ZSocket2` loopback, not independent authenticated game accounts.
The subsequent native run at
`.tools/m5/run-819434c6e1d2412788cf6dc82f885144/BepInEx/LogOutput.log`
passes the same session/gameplay checks with **100 KiB of real item custom data**,
exercising typed command and entry transfers over real RPCs.

`scripts/stage_journal_runtime.py` now stages an explicit allowlist of managed
dependencies and selected native assets from the pinned NuGet package, checking
PE/ELF/Mach-O architecture and recording source checksums. Candidate packaging
rejects missing, changed or unexpected dependencies, including test plugins and
duplicate framework/game assemblies. Windows, Linux x64 and macOS x64 native
assets are staged together; macOS arm64 can be staged separately because its
loader filename collides with x64. These are candidate assets, not OS runtime
passes. `.tools/m7/package-candidate/ThorsKist-0.8.6.zip` passes package and Hexium
checks. The exact candidate dependency payload also passes the SQLite, catalog,
large-item session and gameplay probes in Windows Unity/Mono at
`.tools/m5/run-64d8c6b5d8b4423abc812232d0ebf8f5/BepInEx/LogOutput.log`.
Nothing was published or installed into the normal game tree. The 568 original
save files and independently verified backups remain byte-identical.
No normal-game activation or other-OS pass is inferred.

Open implementation work still includes normal-world authority selection and
metadata routing, the listen-host boundary, all crafting/station actions and
integration of candidate dependency staging into release CI. Live authenticated multiplayer, representative
inventory mods, Linux and macOS provider validation remain external gates.

The following production components are implemented, but are **not installed in
normal gameplay**:

- `JournalReply` encodes compact, correlated control results without transferring
  persistent session rows or authorizing character effects. `JournalRequestClient`
  bounds outstanding requests, shares concurrent identical requests, rejects
  changed payloads, treats timeouts/disconnects as uncertain, and repeats depth
  admission with the same bytes after at least 100 ms.
- `JournalServerConnection` connects authenticated ingress/binding to the
  production replication server. It binds login UUIDs, retains one immutable login
  transfer, uses a shared outbound transfer budget before encoding, handles
  command/acknowledgment/logout/self-suspension replies, and fans out byte- and
  count-bounded entry prefixes. Each connection has its own asynchronous gate and
  retains at most one pending frame under send backpressure. Delivery retries do
  not move persisted deadlines. The future world owner must run independent
  pumps, tick/checkpoint the server, and call `CloseAsync` on worker errors or
  disconnect; the connection component does not itself install those timers.
- Kist session closure releases the binding without closing the game connection.
  Cooldown rejoin gets a new UUID; old login retries cannot unbind the new session.
  Recorded logout completion can be retried without reopening the closed UUID.
  Timed-out login transfers release their retained image when pumped, and
  connection cleanup retains the session address even if the direct adapter has
  already closed its binding.
- `JournalDelivery` checks worst-case entry encodability before command admission
  and splits fan-out by encoded bytes as well as entry count. Larger existing mod
  records are retained by storage but are still an **open protocol-access gate**;
  this change does not truncate or silently discard them.
- `JournalWorldCatalog` supplies asynchronous startup/import/recovery, atomic
  vault-and-header creation and compare-and-swap metadata updates. SQLite schema 3
  adds a world authority marker, including for empty worlds. Reopen never reads
  stale `.dat` bytes after initialization. Import-commit/marker-commit interruption
  recovers from SQLite without needing the old source. Late/concurrent import
  cannot replace an initialized authority. Wrong-world or corrupt state fails
  closed. The production legacy writer remains active until the eventual world
  owner performs the cutover; this catalog is currently exercised by tests only.

Library coverage is in `CoordinatorTests` and `CatalogTests` in the existing
harness (`--m7-only` runs these suites). It covers compact-reply truncation,
correlation/conflicts, uncertain outcomes, exact 100 ms admission backoff,
committed journal effects, fixed deadlines, backpressure, independent peers,
logout/rejoin, transfer cleanup, catalog reopen and stale-save resurrection.
The ordinary harness includes these tests and all existing fault/crash suites.

The isolated Windows Unity/Mono probe additionally covers a real format-108
multi-item inventory (each record omits its trailing cheat byte), canonical
metadata, unavailable prefabs and overflow; backed-up real-decoder-byte import
and catalog reopen; and two compact replies over real `ZRpc`/`ZSocket2` loopback.
These are adapter/loopback checks, **not authenticated multiplayer evidence**.
Logs, validation packages and crash artifacts are retained under ignored
`.tools/m7/`; the disposable game runtime and its independently verified save
backup are under `.tools/m5/run-042f5b78a9b544189e47701f3dbf2b59/`.

Verification results: **175,150 total harness assertions**, including **637 new
coordination** and **17 new catalog** assertions, and all **45 existing forced
process terminations**. Mod/adapter/harness Release builds have zero warnings or
errors. All **75 Harmony targets**, **114 game-member accessibility references**,
category checks, version consistency and both package validators pass. Windows
Unity/Mono passes the decoder/import/reopen and compact-wire probes, plus the
existing inventory/dispatcher/SQLite/TCP probes (**73 cumulative adapter
assertions**). All **568 original save files** match the separately verified
backup, with zero mismatches. The validation package is local only and the hidden
test processes were stopped after verification.

Reproduce library validation with the existing cached .NET SDK and packages,
`dotnet build tests/ThorsKist.Sync.Harness -c Release -m:1 -nodeReuse:false
-p:UseSharedCompilation=false`, then run the harness DLL (or append `--m7-only`).
In the Windows sandbox set `TEMP`/`TMP` to `.tools/m7/tmp` and
`THORSKIST_SYNC_ARTIFACTS` to `.tools/m7/crash-artifacts`; use workspace-local
`DOTNET_CLI_HOME`/`NUGET_PACKAGES` as in M6. The existing
`scripts/run-adapter-tests.ps1` stages the disposable runtime with save backups;
the headless invocation adds `-batchmode -nographics` and retains the explicit
test switch and isolated `-savedir` guard. Runtime evidence here does not cover
the uninstalled connection coordinator running against authenticated peers.

Still required before activation: a world owner installing these components;
authoritative account/character/catalog membership and action-specific ranges;
live persistence cutover and hird lifecycle; reservation/session-owner integration
and loaded-character/mod coverage;
crafting/station routing and bounded replica/UI integration; retention policy and
the persistent 64 MiB envelope; larger mod-record access; supported-platform
packaging/provider execution; and live authenticated-peer checks. The existing
direct-RPC owner remains uninstalled. No M7 completion or M8 readiness is claimed.

### Reservation interception continuation (2026-10-19)

`JournalReservationGuard` and `Patches_Reservations` now protect registered journal
inventory adapters. Harmony installs the hooks, but normal legacy gameplay has
no registered journal character inventory: live reservations remain disabled.

- Guard object, quantity, index, string and bulk removals; incoming merges and
  item creation; source/destination moves and stack-all; resize, drag, split
  selection and grid swaps; equip/queued equip, use/consume/food, drop and ammo
  selection. Guard moves before destination credit and reject repeated legacy
  deposits of a reserved object. These paths freeze the whole reserved source.
- Incoming vanilla additions to an inventory with pending reservations are
  conservatively refused, including unrelated pickups, until settlement. Exact
  verified journal additions still install standalone stacks without merging.
  Material counts exclude frozen sources during recipe evaluation and raw local
  payment counting; ordinary inventory counts retain the visible quantities.
  This does not replace crafting/station consumption with journal withdrawals.
- A precise, one-use removal permit allows only the verified object/quantity and
  vanilla's whole-stack delegation. Reentrant callbacks cannot reuse it for
  another removal or merge. Partial removal also checks the residual metadata
  and position. Failed effects leave the replica acknowledgment position intact.
- Death/destruction, grave transfer, inventory loading/replacement and world
  teardown invalidate the adapter. Replacement is checked before and after
  character effects, including replacement from inside `Changed` callbacks.
  Invalidation releases locks without removal/refund and refuses later effects.
  The future session owner must wire `Invalidated` to local request stop and
  asynchronous server suspension/exclusion; that live owner is still absent.
- Poll one adapter and one reserved snapshot per frame to detect direct list or
  metadata edits that bypass vanilla notifications. Adapter registration and
  pending reservations have defensive caps (16 and 64 respectively). These are
  memory bounds, not measured performance targets. Ownership is unregistered on
  disposal and reset at world shutdown/plugin destruction.

Verification: **63 new real Windows Unity/Mono reservation assertions** pass,
including actual patched inventory/interaction entry points, reentrant partial
and whole removals, changed metadata, replacement before/during effects, grave
transfer and crafting counts. Interaction prefixes are exercised with managed
character/GUI shells so no real character is loaded; this does not establish
loaded-character death/UI or representative inventory-mod compatibility.
Existing item/decoder/dispatcher/SQLite/RPC probes still pass (**136 cumulative
adapter assertions**). The full harness passes **175,164 assertions**, including
14 added reservation-integrity polling assertions and all 45 existing crash
terminations. Release builds, **116 Harmony targets**, **115 game-member
accessibility references**, category/version checks and both package validators
pass. All **568 original save files** match their independently verified backup.

Logs and local validation packages use `.tools/m7/reservations-*`. The isolated
runtime and save backup are
`.tools/m5/run-837e288ced454f69be03c5ba0147db90/`. No release was published.
Remaining reservation gates are live owner/session wiring, real-character
lifecycle/UI actions, representative mods and supported OS execution; the
normal journal cutover and the rest of M7 remain open.

Normal host and remote gameplay must use the production journal as the only
Kist item authority. Until this milestone's activation gates pass, keep the
legacy authority active and the new direct transport owner uninstalled. Do not
enable real-character reservations before interception and lifecycle coverage
is complete.

## Work carried forward from M6

- Install the world/session coordinator and direct-RPC adapter; implement reply
  codecs/correlation, live login and independent entry fan-out. Retain immutable
  snapshot/transfer identities and request IDs/payloads across retries, including
  the minimum 100 ms admission backoff. Supply authoritative catalog membership
  and action-specific range checks. Give listen hosts the same session boundary.
- Validate encodability before mutation admission. The M6 wire bounds include a
  24 KiB item payload, 60 KiB frame body, 32-entry batch and 64 MiB transfer image;
  split batches by bytes as well as count. Define access to existing larger mod
  records without loss or truncation. Bound retained transfer/result/session/
  settlement state and address the persistent 64 MiB replication envelope while
  preserving retry suppression identities.
- Verify the actual authenticated peer checks, including stale/malformed/unbound
  traffic, character ownership, server-only client replies, and closed sessions.
  Verify PlayFab socket-entity stability across reconnects and map account identity
  across supported backends; do not trust the supplied platform ID or a TCP IP
  address as account authentication. M6's compilation and TCP loopback evidence
  does not establish these live guarantees.
- Complete persistence cutover, reservations, crafting/station actions, lifecycle,
  authoritative UI and runtime packaging as detailed below. All original live
  integration completion gates are retained.

## Implementation sequence

1. **Authenticate and bound the transport.** Register journal messages on each
   direct `ZNetPeer.m_rpc`, bind the callback to that exact current ready peer,
   derive account identity from the authenticated socket, and bind character,
   vault, epoch and session. Validate membership and position; accept client
   replies only from the server connection. The routed sender field and legacy
   claimed character/position header are insufficient authentication, as recorded
   in M5's audit. Add versioned, bounded wire frames, immutable bounded snapshot
   transfers, worker encoding and independent per-client fan-out. Preserve
   request IDs/payloads through admission retries and enforce the 100 ms backoff.
2. **Establish one persistence authority.** Connect SQLite catalog/metadata and
   the real legacy decoder to world startup. Back up before transactional import,
   preserve hird headers and every stack (including unknown prefabs and overflow),
   and verify import/reopen on real save bytes. Cover the remaining compact-format
   108 fixture. Replace the `.dat` writer without dual authority or stale-state
   resurrection. Load/commit faults must fail closed before admitting gameplay.
   Address retained session/result/settlement/transfer growth and the existing
   64 MiB envelope limit explicitly; pruning must preserve retry identities.
3. **Protect real reservations.** Intercept equip/use/drop, merges, splits,
   dragging, moving, string removals and repeated deposits. Handle death,
   inventory replacement, unexpected mutations and representative inventory mods.
   Preserve exact object identity and never remove a replacement item. M5's
   adapter detects changed objects but does not itself block gameplay mutations.
4. **Wire all actions and lifecycle events.** Replace optimistic crafting and
   station consumption with separate single-stack withdrawals and handle partial
   failures. Route listen-host characters through the same session rules as
   remote players. Disable every Kist mutation/material-use path during catch-up,
   suspension and logout. Wire heartbeat, acknowledgment deadlines, checkpoint,
   self-suspension, exclusion, compensation, disconnect, hird departure, teardown
   and ordinary rejoin. Only successful character addition or reserved removal
   authorizes acknowledgment; no character-save dependency or client protocol
   storage is allowed.
5. **Connect authoritative presentation.** Bind live stack IDs, overflow and
   session/access status to the pooled UI and cached counts. Install bounded
   per-frame replica effects and deltas, including timeout/mismatch feedback,
   without full legacy mirror rebuilds. Overflow remains visible, persisted,
   usable and checksummed above normal capacity.
6. **Package supported runtimes.** Stage and audit native/managed dependencies
   for each supported OS and validate the actual packaged provider in game.
   Windows x64 Mono evidence exists; Linux/macOS and representative mod dependency
   conflicts remain open. Never infer those passes from Windows or .NET-only
   probes. Keep adapter-test plugins, game references and disposable saves out
   of the release payload.

## Completion gate

- Normal host and remote gameplay use the journal exclusively for Kist item
  mutations, and authenticated stale/malformed/unbound traffic is rejected.
- Backed-up legacy import and reopen retain quantities, metadata, memberships,
  unknown records and overflow; no competing `.dat` writer remains.
- Reservation interception and every crafting/station path obey the same
  acknowledgment, suspension and compensation rules. Failed effects never
  acknowledge; uncertain deposits are not refunded or reconciled against
  replacement character items.
- Login/logout, deadlines, settlement, checkpoint protection, disconnect,
  teardown and rejoin are integrated. A slow client cannot stall another
  client's admitted work; verification/database/encoding work stays off Unity's
  thread and inventory work is bounded per frame.
- Required adapter/fault tests, Release builds, Harmony/accessibility, category,
  version, package and supported-platform provider checks pass. Preserve
  reproducible logs and fault artifacts. Use disposable worlds/characters and
  back up any existing saves before modifying them.

## M8 handoff and external prerequisites

[M8: Game validation and release](network-journal-m8.md) owns the full
multiplayer, disruption/reconnect and performance matrix after M7 is integrated.
M7 still requires its own live authenticated peer, migration, interception and
supported-platform checks; moving the broader matrix to M8 does not waive them.

Testing needs independent authenticated game clients and supported OS runtimes.
A second authenticated client was unavailable during M5/M6. Record unavailable
environments as external gates while continuing independent work. Preserve
reproducible evidence and use disposable worlds/characters with save backups.
Milestone completion does not authorize publication, purchases, messages or
account/security changes.
