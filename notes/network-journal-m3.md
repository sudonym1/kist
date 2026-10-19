# M3 SQLite recovery and evidence

M3 implements the game-independent storage/recovery layer on M2's immutable
state machine. Live gameplay still uses `KistServer`'s existing save/RPC path.
M1's Unity/Mono/platform gates remain open. M4 owns persisted session lifecycle,
acknowledgments, deadlines, admission and compensation; these are not implemented
by M3. In particular, a recovered item result does not prove character delivery.

The library completion gate passes on Windows x64: file-backed production-schema
tests and **21 forced child-process terminations** cover initialization, deposits,
withdrawals, checkpoints and migration. Migration decoding is an explicit adapter
boundary: the transaction/backup pipeline is tested with a fixture decoder, not
with real Valheim saves. M5 must supply and validate that decoder before enabling
the migration or switching the live save path.

## Durable state and recovery

`SqliteJournalStorage` implements `IJournalStorage` using the existing pinned
provider and WAL/FULL configuration. Schema version 1 contains:

| Table | Contents |
| --- | --- |
| `vaults` | World/vault/epoch, capacity, head H, checkpoint C, checksum schema and stock/history checksums |
| `current_stacks` | Materialized stock at H, including overflow and raw unknown-prefab bytes |
| `checkpoint_stacks` | Separate stock at C |
| `journal` | Consecutive entries C+1 through H, with exact deltas and character effects |
| `request_results` | Original command, definitive status, entry and stable operation/effect identities |
| `used_stack_ids` | All allocated stack IDs, including deleted stacks |
| `migration`, `legacy_metadata` | Import marker, source hash/path/full original bytes, and per-vault opaque hird metadata |

One SQLite transaction writes the complete typed commit image, including current
stock, checkpoint, journal suffix, results and used IDs. Checkpointing updates C
and removes the covered prefix in that same transaction. No historical request
or used-stack identity is pruned. This first implementation validates and writes
the full image per commit; it makes no throughput or allocation improvement
claim. Measure this production path before optimizing it or choosing retention
limits. M1's synthetic codec/provider timings are not production commit timings.

Recovery reads one consistent database transaction. It validates the checkpoint,
replays each retained entry, compares replay head and checksum to materialized
current rows, and checks row keys, vault/epoch/schema, results, effect bindings,
operation identities and used stack IDs. A separate SHA-256 checksum over results
and used IDs detects missing/altered deduplication records after journal pruning.
This detects corruption, not malicious rewriting of both data and checksums.
Versioned binary row envelopes reject unsupported versions, truncated/trailing
data and rows above 64 MiB. Unsupported database schemas and failed recovery
stop access; they never silently recreate empty stock or fall back to the save.

`ListVaultsAsync` discovers persisted epochs for bootstrap. Open a storage instance
with each discovered identity and recover it before serving it. An epoch mismatch
fails. This is not an older-backup restore facility; restoring older history still
requires an explicit new epoch and policy before live access.

## Writer and uncertain outcomes

`JournalWriter` creates transitions, commits and installs state in one serialized
worker order. It returns a transition only after successful durable commit and
installation. Encoding, hashing, provider calls, recovery and checkpoint work run
outside the caller's synchronization context. No transport or inventory callback
occurs inside a transaction. M4/M5 schedule publication and bounded inventory/UI
work separately, and must not synchronously wait on this writer from gameplay.

Storage checks expected head under an immediate write transaction, verifies that
prior results and stack IDs remain present, rejects checkpoint regression even
at the same head, and verifies the successor against existing durable history.
Separate connections cannot both spend the same head. A commit may group new
entries, but cannot prune newly created entries before they have first been
persisted; checkpoint existing durable history in a subsequent commit. The
caller still supplies the safe checkpoint cutoff; no automatic checkpoint timer
or participant manager is introduced here.

Cancellation is honored before work starts. Once a transaction starts it finishes
or fails without reporting a successful commit as cancellation. Any commit error,
including an error after SQLite committed but before completion reached the
writer, faults that writer. Dispose and reopen/recover it before further access;
retry/query the original request identity. Do not publish a candidate or retry
the mutation under a new request ID. Known-result retries after restart or
checkpoint pruning return the same result, operation and per-item effect.

## Migration contract

Call `LegacySaveMigration.ImportAsync` before activating a legacy-save writer or
new journal writer. The adapter must stop legacy mutations/saves for the entire
migration, fully validate the format and every record, assign vault epochs and
stable stack IDs, and return all vault states plus exact hird metadata. It must
preserve membership, invites, anchor, item metadata and unknown-prefab bytes;
`Inventory.Load` alone cannot establish preservation because it can discard
records. M3 does not guess the vanilla item binary layout or invoke Unity APIs
on the worker. The supplied decoder must be safe to execute on that worker.

Before importing, the pipeline reads the source and creates a hash-named sibling
backup through a unique temporary file, flushes it, renames it without overwrite,
and verifies its bytes. A mismatching existing backup fails. The original save is
never changed or deleted. All decoded vaults, their metadata, the full original
source bytes and a source-hash import marker commit together into an empty
database. A decoder exception or interrupted transaction leaves no imported
vaults. Restart/retry with the same source uses the marker without decoding or
overwriting any vault. A different source or nonempty unmarked database fails.
Interrupted backup temporary files may remain; retry ignores them and verifies
the completed backup. No automatic cleanup removes evidence of an interruption.

`ReadLegacyMetadataAsync` retrieves the preserved metadata after state recovery.
Real `.dat` decoding, game-version fixtures and end-to-end migration using a
licensed game installation remain M5 checks. The fixture decoder used here
intentionally does not claim compatibility with `ZPackage`/`Inventory.Save`.

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

Both Release builds pass with zero warnings/errors. The harness passes
**158 M3 assertions**, **163,479 total assertions**, including all existing M1/M2
checks. Local commands use the workspace's development .NET 8 SDK, a
workspace-local `DOTNET_CLI_HOME`, and disabled shared compilation/node reuse.
Dependencies were restored with approved network access. No SDK/runtime files
are part of this change.

- Round trips cover raw payloads, accepted/rejected results, no-ops, partial/full
  checkpoints, deleted IDs, altered retries, overflow and isolated vaults.
- Failed writes at current-row/journal/precommit boundaries leave the previous
  state. Lost completion after commit recovers the committed result. Late
  cancellation does not hide a commit. A blocked commit returns no result, while
  a separate WAL reader observes the complete prior state.
- Concurrent writer requests cannot overspend; competing storage connections
  reject stale heads. Same-head stale checkpoints cannot resurrect pruned rows.
- Corrupt materialized rows, checkpoint/head/schema, journal envelopes/keys,
  results and used IDs fail recovery, including deleted historical identities
  after pruning. Unsupported database versions fail closed.
- Each child process blocks at a production storage boundary and signals the
  parent. The parent forcibly kills it without disposal or graceful shutdown,
  reopens the same database and asserts the previous or committed state. Four
  boundaries each cover initialization, deposit, withdrawal and checkpoint;
  five cover migration backup, partial writes, precommit and postcommit. Retries
  do not repeat a committed credit/debit or change its operation/effect identity.
- Migration tests preserve the source/backup, all vaults, opaque metadata and
  unknown/overflow records; failed decoding, changed source, nonempty destination,
  corrupt backup and interrupted/repeated import cannot partially replace state.

The existing CI sync job runs the same crash matrix and retains combined output
plus failed databases/fixtures under `sync-crash-artifacts/`. Locally failed runs
retain their temporary directory and print its path; successful runs clean up.
The net472 Windows probe now exercises production journal commit, checkpoint,
reopen and deduplication on CLR 4.0.30319.42000, in addition to provider loading.

These are process-termination guarantees with storage honoring flushes, not
power-loss, disk-loss, older-backup restore or character-save guarantees. Unity,
Mono, Linux/macOS native loading, real-save migration and multiplayer behavior
remain unverified locally. The mod build/Harmony/category/package checks require
the absent game reference assemblies. SQLite remains an isolated dependency and
is not added to the live mod/release package until its M5 deployment is validated.
