# Local spending and asynchronous journal reconciliation

Status: **planned; no runtime changes implemented by this document**.
Prepared 2026-10-20 against `f5e4971`, following review of the 33 commits after
fork point `8fa289a`.

This is the active follow-up to [PLAN-NETWORK-JOURNAL.md](PLAN-NETWORK-JOURNAL.md).
Where they conflict, this plan supersedes the earlier requirements for
server-authorized withdrawals, independently settled material payments,
per-entry processing acknowledgments, and silently abandoned optimistic
payments. Earlier milestone notes remain evidence of the implementation that
existed at the time.

## Goal and accounting boundary

Spending and withdrawal use the player's local view of the shared database.
The Kist payment path does not wait for a server reply, SQLite commit,
replication entry, or another client's acknowledgment. Existing game mechanics,
such as a station owner's insertion RPC, remain separate. The journal shares
state and accounts for every item removed from the database.

**Destination durability is not a requirement.** Completion means an observed
in-memory inventory change or game action. Do not add character/world save
barriers, durable destination receipts, client databases, local completion files,
or an exactly-once guarantee across client process loss. A crash after an
in-memory credit can lose unsaved destination items; an ambiguous effect can
duplicate value. These remain accepted boundaries.

The server's accounting and recovery remain durable:

- Every committed vault debit has an equal, identified destination credit or
  pending-transfer credit in the same SQLite transaction.
- A pending transfer can finish or return to the vault exactly once in the
  server ledger. A timeout is not evidence that the destination never received it.
- Replication acknowledgment proves installation of shared state. It does not
  prove an inventory effect, authorize settlement, or prove a destination save.
- Every operation known to the server has a queryable outcome. Live clients keep
  uncertain operations and retry the same identity; they do not forget a cost
  because a reply is late or stock changed.
- If a client disappears before its operation ever reaches the server, the
  server cannot account for that unreported action. This is an accepted
  client-loss window, not a reason to add destination durability.

Retain SQLite as the server persistence layer and the server as the ordered
reconciliation authority. Retain authenticated peers, vault/epoch fencing,
metadata-safe item identity, overflow preservation, and bounded Unity work.
This does not require multi-server consensus or an unrestricted offline mode.

## Changes required by the review

| Current behavior | Required change | Main implementation areas |
| --- | --- | --- |
| Ordinary takes wait for a committed entry and do not reserve local stock | Apply takes locally and include every pending spend in availability | `JournalGameplaySession.cs`, `JournalCharacterInventory.cs`, `JournalStockProjection.cs` |
| Craft/build/station failures can discard the remaining payment | Keep a correlated operation with explicit settlement or conflict outcome | `JournalImmediatePayments.cs`, `JournalMaterials.cs`, `JournalStationInputs.cs`, `Contracts.cs` |
| Inventory delivery and state replication share one completion cursor | Track replica installation and item effects independently | `JournalReplica.cs`, `ReplicationClient.cs`, `ReplicationServer.cs` |
| Each entry waits for an acknowledgment reply; delivery polls every 100 ms | Coalesce cumulative acknowledgments and use bounded asynchronous delivery | `JournalClientConnection.cs`, `JournalServerConnection.cs`, `JournalVaultRuntime.cs` |
| Routine work copies, scans, serializes, and hashes retained history | Index active state, separate immutable snapshots, and compact retry history safely | `JournalMachine.cs`, `ReplicationState.cs`, `IncrementalWrites.cs`, `ReplicationRows.cs` |
| Consumption/deletion is represented as a synthetic inventory award | Record the actual destination kind and action identity | `Contracts.cs`, `JournalRecords.cs`, `StorageEncoding.cs`, `JournalWire.cs` |

## Concurrent spending policy

**Proposed default: preserve both local actions and record the shortfall as
outstanding material debt, settled by subsequent compatible deposits.** This is
a proposed gameplay policy, not an already implemented behavior or a requirement
for destination durability. It replaces silent free actions with explicit
accounting while retaining local execution.

Example: the shared view contains one wood; Alice and Bob each locally spend it.
The server orders both operations. One is funded from stock; the other credits
its named destination and records one wood of shortfall. A later deposit of
three compatible wood settles that shortfall and adds two spendable wood.
Record both operations, attribution, and the later settlement. Never present
negative quantities as physical `ItemRecord` stacks.

Use a separate debt balance keyed by vault/epoch and exact compatible item
identity, with operation/player attribution. All clients project the same net
availability; the UI exposes outstanding shortfalls. Debt is shared by the vault,
so later deposits by any member can settle it. Metadata variants must not pay
each other's debt accidentally. Retiring a vault must account for its outstanding
debt explicitly; it must not silently delete it.

The funded part and the shortfall are distinct ledger legs. Do not claim strict
physical item conservation during an overspend: the destination credit is backed
by available stock plus an explicitly recorded liability. Do not refund a
funded spend just because the client missed the server's reply when that command
already reported its local effect as applied.

Alternatives, if selected before implementation:

- Keep both actions and record the unpaid amount as a terminal, explicit
  conflict/waiver. No future-deposit settlement; duplication is intentional.
- Allocate spending rights in advance. Local actions can spend only assigned
  rights; obtaining more rights may wait for the network. This changes the
  meaning of "available from the local view" and is not the proposed default.

No variant should silently log-and-forget a payment, remove unrelated player
items later, or destroy an already produced building/craft to resolve a race.
Authorization, incompatible metadata, malformed commands, and wrong epochs
remain errors; the shortfall policy must never turn them into valid payments.

## Target operation model

### Identity and ledger

Introduce an action envelope with a stable client-generated operation ID,
authenticated actor, vault/epoch/session, observed replica sequence, action
kind, and one or more exact item legs. Retries reuse the complete envelope;
changed payload under the same identity is a conflict. Preserve a stable mapping
to any server-generated IDs.

Each leg identifies its source, quantity, canonical item metadata, and destination:

- Character inventory.
- A craft/build consumption sink, correlated with the action/output when known.
- A station input, including station identity and insertion ID.
- Intentional discard.
- Pending transfer, compensation return, or shortfall settlement.

An action spanning several stacks/materials has one parent identity and one
accounting outcome with per-leg funding/shortfall details. Commit its accounting
atomically. Do not replay an entire craft because one leg is unresolved.
Bound envelope size and leg count; reserve admission capacity before applying
an action too large to retain or send. A transport chunk is not a separate
logical payment.

The server stores stock deltas, destination credits, pending effects, shortfalls,
retry outcome, and journal records atomically. The materialized view and
snapshots include all state needed to calculate availability, including debt
under the proposed policy. Named sinks are accounting records, not durable
copies of Valheim objects.

### Local execution

1. Check the established session, cached permissions/range, local stock, item
   compatibility, destination capacity, and bounded pending-operation capacity.
2. Reserve the complete local cost before the game effect can occur. Availability
   is the confirmed replica plus pending local deltas; every take and every
   material-consuming action uses that same calculation.
3. Apply the inventory or game effect once on the Unity thread. Ordinary
   withdrawal validates capacity for the whole delivery before any mutation.
   Definite failure releases its reservation without a debit. An uncertain
   callback result must not cause the same effect to be executed again.
4. Queue the action and its observed in-memory effect result for asynchronous
   reconciliation. No server or disk wait is introduced into the gameplay call.
5. Retain the pending overlay until the corresponding authoritative outcome is
   installed in the replica. A command reply arriving first does not release it;
   otherwise the same stock becomes spendable twice. Install the result and
   retire the overlay atomically from the game thread's perspective.

Reserve costs before vanilla craft/build execution, then finalize or release
them from its observed outcome. This replaces post-action payment planning.
Station requests reserve the exact input before contacting the station owner.
The owner deduplicates insertion IDs and returns the original outcome on retry.
An ownership change or lost confirmation produces an explicit uncertain effect;
it must neither enqueue an unreserved second input nor silently erase payment.
Retry/query the same insertion while its owner can still identify the outcome.
If it remains unknowable at the bounded deadline, record that outcome and use
the documented ambiguity policy: compensate any pending debit once, or record
an unpaid uncertain action if no debit committed. Do not create a return for
items that were never debited. Any resulting duplicate value is accepted.
Station interaction may still require its owner RPC; the Kist payment path
introduces no extra database authorization round trip.

An ordinary accepted local withdrawal must not receive a second inventory award
when its journal entry arrives. Distinguish already-applied local effects from
server-directed effects in the protocol and adapter.

Keep deposits conservative: reserve the source, commit the server credit, and
remove the reserved source only on a verified effect. Do not make an unconfirmed
deposit spendable locally. Preserve the existing accepted duplication policy
when a depositor disappears after credit but before removal.

### Replication and effect completion

Maintain three separate facts:

- Highest consecutive shared-state sequence installed and verified.
- Pending local operations not yet reflected in that installed state.
- Per-operation effect status: pending, applied in memory, failed, uncertain,
  or compensated, with server accounting outcomes recorded separately.

Process verified journal entries in bounded batches without awaiting each
acknowledgment reply. Coalesce the highest installed sequence and send cumulative
replication acknowledgments asynchronously. Keep a bounded byte/entry window;
do not replace the old stop-and-wait path with an unbounded application queue.
Wake delivery on committed work/available window space; keep timers as fallback.

Send effect outcomes separately, in bounded idempotent batches. An already-applied
local effect can be reported in its action envelope and settled in that commit.
A replica acknowledgment alone must never settle a pending deposit removal or
server-directed award.

Store unresolved effects independently from the replay-log suffix. Checkpoint
and prune replicated state only when snapshots plus pending-operation records
retain everything needed for accounting/recovery. A full inventory or failed
effect must not prevent unrelated state updates from being installed. Ordinary
full-pack withdrawals should fail the local capacity check before spending.

### Failure and lifecycle rules

| Event | Required behavior |
| --- | --- |
| Duplicate command, entry, or effect report | Same accounting outcome; no second local effect, debit, destination credit, debt, or return |
| Temporary queue/admission pressure | Retry the same identity with bounded backoff; retain the local overlay; do not apply new actions beyond local capacity |
| Stock race | Record funded and shortfall quantities under the selected policy |
| Stale stack ID or metadata | Never debit a replacement variant; record an explicit conflict and reconcile only against a defined compatible identity |
| Permission/membership/range rejection after local action | Record a terminal rejected-after-effect outcome where the protocol can safely associate it with the original actor/vault; no unauthorized vault debit and no automatic taking of unrelated items |
| Server-directed effect fails or times out | Keep or compensate its pending-transfer credit once, independently of replication progress |
| Local effect was reported applied; reply is lost | Query/retry that operation; do not deliver or compensate it again merely because the reply was lost |
| Disconnect, exclusion, or restart | Fence old sessions; settle server-known pending transfers according to effect state; preserve committed credits/debt; never infer effect completion from a replica cursor |
| Client process loss | No destination-save guarantee and no client outbox recovery; retain server-known outcomes and document unreported/unsaved effect exceptions |
| Checksum/protocol mismatch | Stop new local spending, keep immutable operation identities while alive, and recover through a fenced rejoin |

Transport session identity and operation identity must be separate. A live client
rejoining the same vault/epoch queries known outcomes before retiring its pending
overlay or resubmitting an uncertain operation. Do not blindly rebind an old
operation to a new session. Wrong-world/epoch work can never spend new history.
Queries for historical outcomes remain authenticated and access-controlled.
Normal logout attempts to flush outstanding reports asynchronously with a
bounded timeout; it does not wait for a character/world save.

## Bounded cost and retention

Keep active state indexed by operation ID, actor/session, stack ID, and sequence.
Track pending counts directly. Remove nested scans of all lifetime results and
settlements from command admission, acknowledgment, and exclusion.

Persist immutable login snapshots separately from mutable session cursors.
Acknowledging a sequence must not reserialize the session's entire login image.
Write only changed protocol rows and update integrity metadata incrementally.
Do not rehash every historical result or settlement for each new operation.
Measure full-vault hashing separately; use versioned incremental verification
or less frequent full verification if that remains a material cost. Preserve
deterministic detection of corruption, including overflow and debt.

Separate hot pending/retry state from cold completed accounting history:

- Keep full payloads for unresolved operations and an explicit retry horizon.
- Compact completed operations into bounded active-window results and durable
  rejection fences/tombstones. Expired or unknown old identities must never be
  interpreted as fresh spending.
- Define the session-generation/request-window rules before deleting any retry
  record. A late login retry cannot resurrect a retired session; a fresh
  transport cannot bypass an epoch/generation fence.
- Keep historical credits/conflicts and compact per-operation outcomes in
  indexed archival tables. Optional accounting summaries must not remove the
  outcome lookup or retry fences. Ordinary commits must not load or hash them.
- Page outcome queries and recovery reads. Boot may validate retained history,
  but reopening must not require loading every completed payload into live RAM.

The implementation must prove duplicate rejection across compaction, restart,
and rejoin before enabling pruning. Simply adding a TTL to current result rows
is not sufficient.

## Delivery milestones

Each milestone adds tests for the changed contract and preserves relevant
existing regressions. New protocol behavior stays disabled until the complete
server/client/adapter path is available.

| Milestone | Work | Exit evidence |
| --- | --- | --- |
| L1: contracts and reference model | Specify action envelopes, destination legs, conflict policy, independent cursors/effects, retry fences, and server accounting invariants. Replace tests that require a server round trip for takes or silently discard costs. | Deterministic model covers local takes, last-item races, multi-material actions, both message orders, failures, and accepted client-loss boundaries. |
| L2: server ledger and migration | Implement atomic action accounting, explicit sinks, shortfalls/selected conflict outcome, effect reports, outcome queries, and versioned SQLite/wire encoding. Settle old sessions using the old rules before admitting new-protocol sessions. | Crash tests at every commit boundary; schema-6 upgrade/reopen preserves stock, metadata, overflow, old pending effects, and retry safety. |
| L3: common local gameplay path | Replace ordinary takes and post-action payment planning with one local reservation/operation tracker. Add station insertion deduplication, explicit discard, overlay rebasing, and reconnect outcome reconciliation. | Native adapter tests show immediate inventory/action effects with server replies deliberately withheld, and no second effect on later replication. |
| L4: independent asynchronous replication | Split replica acknowledgment from effect reports, coalesce acknowledgments, add bounded delivery windows and commit-triggered wakeups, and allow state catch-up past unresolved effects. | Delayed acknowledgment replies do not serialize batch application; full-pack/effect failures do not freeze shared state; slow peers cannot grow queues without bound. |
| L5: bounded storage/runtime cost | Index active operations, separate snapshots from cursors, implement safe retry compaction/archive, and eliminate history-wide live serialization/hashing. | SQL row audits plus allocation/serialization counters demonstrate hot-path work independent of lifetime completed-operation count. Old retries never debit twice. |
| L6: integrated game validation | Run the expanded harness, native checks, dedicated/listen-host two-client scenarios, history-size benchmarks, and supported-platform packaging checks. Update public behavior/configuration documentation. | Recorded correctness, latency, queue, allocation, and compatibility evidence; remaining platform coverage stated explicitly. No release publication implied. |

L2 depends on L1. L3 and L4 depend on the L2 contract. L5 follows the stable L2
schema and L4 retention rules. L6 requires L1-L5.

Use a new wire version and a transactional schema migration. Reject mixed
protocol peers clearly instead of falling back to old item RPCs. Existing
synthetic inventory awards cannot be retroactively assigned reliable station,
build, or discard identities; preserve their original records as legacy.
Back up before schema upgrade; rolling back requires the pre-upgrade database
and matching mod version, not an attempted reinterpretation by old code.

## Acceptance and measurement

Correctness cases must include:

- Rapid repeated takes and take-versus-craft on one client: the second action
  sees the first action's pending debit before any network response.
- Two clients spending the last item, partial stock across several materials,
  and subsequent deposits under the selected conflict policy.
- Accepted response before entry, entry before response, lost response, duplicate
  response, bounded admission retries, and reconnect with an uncertain result.
- Full destination inventory with no partial local addition; full vault
  compensation with preserved overflow; unsupported prefab and metadata variants.
- Station refusal, lost/duplicate confirmation, ownership change, and a burst of
  input requests before any confirmation.
- Replica catch-up and checkpointing while a separate effect remains unresolved.
  A replication acknowledgment must never settle that effect.
- Server crash before/after debit-credit commit, effect report, compensation,
  checkpoint, history compaction, and migration.
- Old commands/login retries after result compaction, session closure, rejoin,
  and epoch change; retries with altered payloads.
- Client crash after in-memory delivery: explicitly accepted destination loss;
  no save hook or durability gate is introduced to make this test pass.

Measure click-to-local-effect separately from server settlement latency. Test
0/100/300 ms added RTT, two active clients plus a slow observer, and held/lost
acknowledgment replies. An accepted local take should complete in its initiating
game-thread action, with zero network/disk waits; increasing RTT must not add a
round trip to that path. Observe frame time and bound work for complex actions.

Benchmark fixed active stock/pending work at 1,000, 100,000, and 1,000,000
completed operations, with at least 32, 512, and 2,048 occupied stacks. Record
p50/p95/p99 settlement/replication latency, Unity work, bytes encoded, allocations,
rows touched, queue sizes, and memory after checkpoint/reopen. Structural gates:
no full history scan/serialization on routine commits, no login snapshot rewrite
for cursor updates, and bounded memory for pending work. Record measured timing
budgets and hardware before treating timing comparisons as release gates.

The review baseline at `f5e4971` passed **175,344 library assertions**, including
**45 forced child-process terminations**. That is evidence for the old contract,
not completion of this plan. Reuse the harness, adapter probes, VerifyPatches,
CategoryCheck, and [M8 game matrix](notes/network-journal-m8.md), updating their
expected semantics where this plan supersedes them. Game validation uses
disposable worlds/characters and preserved backups.

Completion requires all milestones' evidence and documentation of the selected
conflict policy. Destination durability, strict physical conservation during
accepted ambiguity, and exactly-once game persistence are not completion gates.
