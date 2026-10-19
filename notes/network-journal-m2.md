# M2 state machine and evidence

M2 implements the game-independent, in-memory transitions on top of M1 commit
`853a122`. It does not switch live gameplay to the journal. SQLite durability is
M3; authenticated sessions, acknowledgment tracking, settlement, compensation,
timers and admission are M4. M1's real-game/provider matrix gates remain open.

M2's completion gate is satisfied for the library: deterministic and independent
model tests verify checkpoint/replay equivalence, once-only server changes, and
suppression of repeated known character effects. M3 can build on these typed
transitions and storage contracts; live persistence still uses the legacy path.

## State and ordering

`JournalMachineState` holds an immutable checkpoint at C, a consecutive journal
suffix C+1 through H, materialized current stock, request results and used stack
IDs. `JournalMachine.Execute` produces a candidate transition without modifying
its input. The caller serializes transitions in one writer order. Validation
uses current stock, so two withdrawals cannot both spend the same last items.
Each accepted single-stack transaction has distinct operation/effect identities
and one exact character quantity. Definitive rejections get an unchanged-stock
journal entry; wrong-vault/epoch and conflicting retries leave state untouched.

Request IDs are scoped to authenticated player within a vault/epoch. A retry
must match session, kind, stack, quantity, prefab, format and payload bytes; it
returns the original result, operation and effect. Request records and used
stack IDs are retained indefinitely in this milestone, including after journal
pruning. Deleted stack IDs cannot be reused. No finite retention policy is
claimed. UUID generation is deliberately independent of sequence numbers.

Deposits create a free ordinary slot or increase one matching stack. The library
models capacity as occupied stack slots, with no game-specific maximum quantity
per stack yet. Commands must already be split at destination stack boundaries;
M5 must enforce real item stack limits and normalize the item schema. Exact
withdrawals may reduce or delete an ordinary or overflow stack. Overflow is
represented explicitly by slot -1, participates in hashes/replay, and is usable.
Ordinary deposits cannot allocate overflow; generating compensating returns
into overflow is M4.

`Checkpoint(state, cutoff)` folds only the selected prefix into a new snapshot,
prunes that prefix and preserves current stock, results and used IDs. Snapshots
captured before later transitions remain immutable. This is a low-level
transition: **the caller must supply a safe processing cutoff**. M4 computes it
from active/login/logout participant positions and operation settlement. M2 has
no participant manager and must not be wired to live checkpoint scheduling yet.

The provisional M1 opaque storage-image contract is replaced by a typed
`StorageCommit` containing the expected head and complete immutable candidate
state. M3 must persist state/log/results/checkpoint atomically and only then
install/publish it, provide validated recovery construction, and verify recovered
current rows against replay. There is no production storage implementation here.

## Canonical checksum schema 1

SHA-256 covers the following binary sequence. Integers are signed 32-bit little
endian. Strings use strict UTF-8 without BOM, prefixed by their byte length as an
integer. UUIDs use their lowercase 32-character `N` string representation rather
than platform UUID byte order. Stack records sort by UUID string ordinally.

| Order | Fields |
| --- | --- |
| Header | Schema (1), world, vault, epoch, slot capacity, record count |
| Each record | Stack UUID, slot (-1 for overflow), prefab, quantity, item format version, payload byte length, exact payload bytes |

Sequence is carried by the snapshot/entry envelope and checked for consecutive
progress, independently of the stock hash. A no-op therefore advances sequence
with the same checksum. Replay validates identity, epoch, schema, slot
uniqueness, the exact single-stack delta/effect relationship, and resulting
checksum before returning a candidate state.

This is a canonical encoding of **M1's opaque records**, not a claim that vanilla
serialization is canonical across field/dictionary order. Semantically identical
game items with different payload bytes hash differently. Unknown-prefab bytes
are preserved. Field-level encoding of quality, variant, durability, crafter,
world-level data, sorted custom data and floats remains the M5 assembly audit
and adapter task. Explicit `ItemRecord.Quantity` is authoritative in this model;
the library never decodes or rewrites opaque bytes when quantities change.

## Client replay and effects

`JournalReplica` installs verified snapshots and processes one entry per call.
It applies deltas to temporary state and verifies the checksum before touching
character inventory. Only the exact recipient session applies its effect.
Successful application or a known completed identity advances the cumulative
processing sequence; duplicates return `Duplicate` without touching inventory,
allowing the caller to re-acknowledge. A verified rejection releases the current
recipient's reservation without removal/refund. No command response authorizes
deposit removal.

Failed addition/removal leaves progress unchanged; the caller stops draining at
that entry. A gap or checksum/schema/delta failure latches `NeedsSuspension`,
discards temporary state and cannot be repaired in that session. M4/M5 send the
self-suspension request and processing acknowledgments; this class sends none.
Game adapters capture the immutable baseline and sequence, run
`JournalReplay.Prepare` on a worker, then call `ProcessVerified` on the game
thread. The unforgeable verified-update token avoids hashing on the game thread
and is checked against its original baseline before installation. Worker
verification errors are reported through `VerificationFailed` for the same
session; stale worker results cannot suspend a replacement. `Process` combines
these steps for deterministic hosts. Snapshot installation has no inventory
calls and can run on the worker during login before handing the replica to its
processing owner. M4/M5 still own scheduling, marshaling and bounded batches.

Ordinary login uses a new session, a verified snapshot and authoritative settled
effect IDs. Known applied identities survive replica reset in process; identity
collisions with changed content fail. Old-session character effects are skipped
while their stock deltas still replay. **M4 must settle the old session before
supplying a replacement login.** Stale transport deliveries cannot affect the
replacement. No client files, character-save dependency or reconciliation gate
are introduced. Character persistence still has the loss/duplication exceptions
in the main plan.

## Verification (2026-10-18)

With .NET SDK 8.0.318 / runtime 8.0.21 on Windows x64:

```text
dotnet build tests/ThorsKist.Sync.Harness -c Release
dotnet run --project tests/ThorsKist.Sync.Harness -c Release --no-build
python scripts/version.py check
git diff --check
```

The Release build passed with zero warnings/errors. The harness passed
**163,256 M2 assertions**, **163,321 total assertions** including M1 contracts,
reservation checks and the existing file-backed SQLite provider probe. That
probe is still not M3 recovery evidence. The existing CI sync job automatically
runs the expanded harness.

- Deterministic scenarios verify immutable candidate commits, exact deposit
  removal identity, last-stack ordering, whole/partial takes, definitive
  rejections, altered retries, reused stack IDs, checkpoint pruning, no-ops,
  wrong epoch, overflow, corrupt snapshots/entries/no-ops, failed inventory
  effects, duplicate processing, replacement login, settled replay and effect-ID
  collisions, worker-prepared effects and stale worker results. Checksums vary
  with every encoded field and remain independent of
  stack enumeration order and current culture; a fixed byte vector pins encoding.
- All 1,024 length-five schedules over deposit/take/retry/checkpoint are compared
  after every event with an independently written quantity/metadata/slot ledger.
- Twenty fixed seeds each run 250 mutation/retry/altered-retry/no-op/checkpoint
  events. The reference ledger selects outcomes without production mutation,
  replay or checksum helpers. Every event compares current stock, checkpoint
  stock, positions, retained identities and result count. Real production replay
  also checks checkpoint-plus-suffix equivalence. Recipient inventories compare
  against independent character accounting, including duplicate delivery of
  every entry; observer replicas install checkpoints and replay suffixes.
  Failures include seed and event trace; automatic trace shrinking is not
  implemented.

The installed default SDK is too old for the harness. Locally these commands
used the existing development .NET 8 SDK under the main checkout's `.tools`, a
workspace-local `DOTNET_CLI_HOME`, and disabled shared compilation/MSBuild node
reuse for the sandbox. No SDK files are included in the change.

This checkout has no game reference assemblies. The mod build, Harmony/category
checks against actual assemblies, release packaging and game/runtime validation
remain for the existing CI/game environments. No M3 crash guarantee, M4 session
fault coverage, transport guarantee or game-thread performance result is implied
by this M2 completion evidence.
