# M4 replication, sessions, and evidence

M4 implements the game-independent replication/session layer on the M2 state
machine and M3 writer/storage. The library completion gate passes locally.
Live gameplay still uses the legacy RPC/save path. M5 must connect these APIs to
authenticated Valheim RPCs, real inventories, lifecycle events, UI and scheduling;
M6 still owns multiplayer validation. No transport guarantee is inferred from
these simulations.

## Review of M1-M3

The earlier notes establish usable boundaries, with these requirements carried
into M4:

- M1 reservations identify exact inventory objects. A command response cannot
  authorize deposit removal; only verified journal processing can do so. The
  real interception/provider platform gates remain open.
- M2 candidates and replay are immutable, single-stack and checksummed. Its
  low-level checkpoint method accepts a caller-provided cutoff; live code must
  instead use M4's participant-aware checkpoint API. Item field canonicalization
  and game stack limits remain M5 adapter work.
- M3 commits before publication and faults the writer on uncertain storage
  completion. Recovery alone does not prove character delivery. M4 persists
  acknowledgments and settlement, and closes/settles old sessions before access.
  Real legacy-save decoding remains unimplemented in this library layer.

## Server ordering and durability

`ReplicationServer.OpenAsync` exclusively owns a `JournalWriter`. Public calls
queue in invocation order on workers. Authentication, membership, world/access
checks and binding transport senders to player IDs precede this boundary; the
library's player string is an authenticated identity, never a display name.

Login atomically captures checkpoint C, creates a UUID session and appends Login
L. Its immutable baseline and catch-up entries are retained for identical login
retries, even after later checkpoint advancement. Mutations remain disabled
until an acknowledgment through L activates the same participant. A different
login request cannot create a second open session. Repeating a closed login
request returns the original closed session, which cannot be reopened.

Logout appends O and disables subsequent mutations in writer order. Its
participant remains until a queued, cumulative acknowledgment through O closes
it and settles all covered effects. Duplicate logout requests return the same
marker and recorded phase. A closed session's traffic cannot acknowledge or
mutate again. Known item requests remain queryable under their original identity;
they return their stable result without applying another item change.

SQLite schema 2 adds one versioned, SHA-256-protected `replication` row per vault.
It contains sessions, positions, delivery deadlines, cooldown expiry times,
immutable login transfer data, heartbeat schedule and per-operation settlements.
These commit in the same transaction as stock, journal, results and checkpoint.
Schema 1 upgrades transactionally with empty session metadata, preserving M3
stock/results. The replication revision guards same-head acknowledgment/session
writes against competing connections. Recovery checks session positions,
barriers, settlement bindings and compensation references as well as M3's stock
and deduplication invariants; missing/corrupt metadata fails closed.

Opening a server invalidates and settles all old sessions before allowing
requests or checkpoints. Pre-session M3 results without a session row are also
settled conservatively. Unacknowledged withdrawals return; deposited stock stays.
A failed/uncertain commit faults the writer, publishes no reply, and requires
reopen/recovery. Disposal still releases a faulted writer's storage.

## Admission, delivery, and timers

`ExecuteAsync` atomically reserves admission before queuing a new item request.
Depth includes queued/executing work and committed effects without settlement.
Identical queued retries share the original task; committed retries reuse their
result. Conflicting retries fail. Definitive rejection releases admission;
commit of an accepted item effect does not. Only persisted initiator processing
or exclusion settlement releases that pending depth. Observer acknowledgments
control retention independently. Depth errors change no stock, sequence or
terminal result. `AdmissionRetry` waits at least 100 ms asynchronously after each
depth error and sends the same command identity and payload again.

`QueueDeliveryAsync` returns at most the requested number of entries and commits
the first-queue deadline before returning them for asynchronous transport. Its
cursor cannot jump beyond already queued entries. Retries and later entries do
not extend earlier deadlines; partial acknowledgments remove only the covered
deadlines. Logout delivery stops at O. Acknowledgments, timers, login/logout,
exclusion and checkpoints bypass mutation admission.

The adapter calls `TickAsync` regularly and queues returned/retained journal
entries for each session independently. A heartbeat is due every 10 seconds with
active clients, including during mutation traffic. A delayed tick emits one
heartbeat and schedules the next interval; it does not replay a backlog of timer
ticks. Active delivery timeout, login completion and logout completion use
30-second deadlines. Logout cannot extend an earlier outstanding deadline.
`IReplicationClock` supplies UTC and monotonic elapsed time; live deadlines use
an anchored monotonic clock, while persisted UTC cooldowns survive reopen.
`SystemReplicationClock` is supplied for runtime adapters.

Exclusion atomically closes the session, releases checkpoint participation,
settles deposits and appends one compensation per pending withdrawal. Self-ban
and timeout persist a 30-second identity cooldown; ordinary disconnect can omit
it. Repeated exclusion cannot return twice or extend a cooldown. Ack/exclusion
races resolve by writer order. Other players' effects remain untouched.

Compensation preserves the exact original item bytes/quantity in a fresh stable
stack at overflow slot -1, bypassing ordinary capacity. The entry's `RequestId`
references the original operation ID; its own operation ID is retained in the
settlement record. It has no character effect. M4 deliberately uses separate
overflow stacks rather than guessing game merge/stack limits; normalization,
merging/free-slot presentation and real item limits are M5 work. Overflow remains
checksummed, persisted, replayable and withdrawable.

`CheckpointAsync` computes the minimum position of logging-in, active and
logging-out participants, additionally protecting unsettled item effects. With
no participants, all settled history can be checkpointed. Compensation entries
remain protected by remaining observers until those observers process them.

## Client processing and M5 handoff

`ReplicationClient` serializes calls in invocation order, verifies snapshots and
entries on workers, and dispatches one verified effect at a time through a
supplied asynchronous game-thread dispatcher. `maximumBatch` bounds each call;
the adapter splits longer transfers and yields when dispatching work. The client
sends processing acknowledgments only after successful installation/effects.
Duplicate entries re-acknowledge without repeating effects. Duplicate login
responses cannot reset a live replica. `StopRequests` latches requests off while
logout drains, even if an earlier acknowledgment completes afterward.

Verification failure immediately stops local processing/requests and sends
self-suspension. A lost request can be retried with `RetrySuspensionAsync`; the
server timeout is the fallback. Failed character addition/removal stops processing
and requests without acknowledging; normal timeout settles it. A new login
installs the authoritative settled effect IDs and replays all stock deltas while
skipping old-session character effects. No client files, character-save waits,
refunds or deposit reconciliation gates are introduced.

M5 must supply the RPC encoding/decoding, sender authentication, independent
bounded fan-out, periodic tick/checkpoint scheduling, bounded snapshot transfer,
game-thread dispatcher and inventory/reservation adapters. Do not synchronously
wait on these async APIs from gameplay. Validate reliable ordered delivery for
each supported transport before deciding whether additional gap recovery is
needed. Fault injection here exercises defensive handling, not live packet-loss
semantics. Listen-host characters must use the same session path.

## Verification (2026-10-18)

Windows x64, SDK 8.0.318 / runtime 8.0.21, SQLite 3.54.0:

```text
dotnet build tests/ThorsKist.Sync.Harness -c Release
dotnet run --project tests/ThorsKist.Sync.Harness -c Release --no-build
dotnet build tools/SyncRuntimeProbe -c Release
tools/SyncRuntimeProbe/bin/Release/net472/SyncRuntimeProbe.exe
python scripts/version.py check
git diff --check
```

Release builds pass with zero warnings/errors. The harness passes **10,162 M4
assertions**, **173,641 total assertions**, with **45 forced child-process
terminations** across M3 and M4. Local builds use the existing workspace .NET 8
SDK, workspace-local `DOTNET_CLI_HOME`, and disabled shared compilation/MSBuild
node reuse. No SDK files or dependency changes are included.

- Scenarios cover duplicate/closed logins, barriers/checkpoint races, cumulative
  acknowledgments, stale/unbound UUIDs, logout retries/draining, queued admission,
  pending depth, altered/uncertain retries, repeated 100 ms backoff and slow
  observers while another initiator keeps processing.
- Fake time tests cover 9,999/10,000 ms heartbeats, heartbeat during mutation
  traffic, oldest delivery deadline, idle timeout at 40 seconds, UTC clock jumps,
  cooldown at 29,999/30,000 ms, login timeout, and logout's earlier ack deadline.
- Production client simulations cover ordered batches, dispatch stalls, duplicate
  effects, lost ack completions, failed inventory effects, checksum/no-op/snapshot
  mismatch, lost/repeated self-ban requests, gap/reordering defense, stale delivery,
  settlement and ordinary rejoin without replaying old character effects.
- All 256 length-four schedules over take/deposit/ack/exclude compare independent
  stock and pending-depth accounting after every event and checkpoint. Twelve
  fixed seeds run 100 longer events each, adding retries, disconnect/rejoin,
  timeout/cooldown, logout and checkpoints. Independent scalar/primary/overflow
  and settlement ledgers compare after every event. Failures include seed/trace;
  automatic trace shrinking remains unimplemented.
- File-backed tests cover overflow above capacity, usable returns, restart
  cleanup, deposit retention, cooldown persistence, schema upgrade, corrupt
  replication metadata, same-head stale writes, and lost postcommit completion.
- The M4 child-process matrix forcibly terminates at four production boundaries
  (current-row write, journal write, precommit, postcommit) for login, ack,
  suspension/compensation, logout marker, protected checkpoint and restart cleanup:
  **24 terminations**. Reopen observes a complete prior/committed transaction,
  settles only pending effects once and releases old checkpoint participants.
  Existing M3's 21 crash cases still pass. Failed databases/fixtures are retained
  and the existing GitLab sync job runs the expanded suite without changes.
- The Windows net472 probe passes login/ack, item operations, overflow settlement,
  checkpoint and cooldown reopen on CLR 4.0.30319.42000, alongside its M3 checks.

This remains the full-image persistence implementation. Closed sessions, login
transfer images, results, used stack IDs and settlements are retained without a
finite pruning policy. The existing 64 MiB row limit fails closed; production
retention, transfer limits and measured performance need implementation evidence
before live deployment. No throughput/frame-time improvement is claimed.

M1's Unity/Mono/OS matrix and real reservation experiments remain open. This
checkout lacks game reference assemblies, so mod/Harmony/category/package checks,
real-save decoding, live transport guarantees and multiplayer behavior remain
M5/M6 gates. SQLite is still isolated from the live mod/release package. Character
save loss and ambiguous receipt duplication remain the accepted plan exceptions.
