# M1 implementation and evidence

M1 code is implemented; the milestone completion gate remains open for real
Valheim inventory experiments and provider loading on the supported game/OS
matrix. No live deposit, RPC, persistence or crafting behavior is switched to the
new protocol by this change.

## Module boundaries

- `src/ThorsKist.Sync` targets `netstandard2.0`, has no game references, and is
  referenced by the existing `net472` mod. Commands describe exactly one stack
  and one quantity, with request, session, player, vault and epoch identity.
  Effects carry separate operation/effect IDs. Item payloads are defensively
  copied and retain opaque vanilla data, including unknown prefab records.
- Clock, command transport/handler, storage and character inventory contracts
  separate worker work from game-thread inventory work. The recovery-image
  storage contract is provisional: M2/M3 must replace its opaque image with
  the typed atomic state/log/result/checkpoint batch after implementing those
  models. It is not a SQLite journal implementation.
- `src/ThorsKist.Sync.Sqlite` isolates provider dependencies from the library
  and current mod. It opens file-backed WAL/FULL connections. M3 owns schema,
  recovery, migration, writer scheduling and crash tests.
- `tests/ThorsKist.Sync.Harness` runs production contracts and reservation code
  with explicit delivery scheduling and fake time. Its handler records commands;
  it does not implement or claim to test the M2 state machine.
- `JournalInventoryPrototype.cs` adapts the real `Inventory`/`ItemData` API,
  serializes with `ItemData.Save`, checks exact reference membership and removes
  through the exact-object overload. It is not enabled in the legacy escrow
  path. CI builds this source against supplied game references.

## Reservation findings and interception plan

Slots, prefab names and serialized equality alone cannot identify a source stack:
another identical item can occupy the same slot. The prototype therefore holds
the source `ItemData` reference and the owning `Inventory`, plus a snapshot of
all serialized fields. A partial reservation conservatively locks the **whole
source stack**. The adapter must reject merging into it, splitting it, moving it,
dropping it, equipping/consuming it and spending it by name. Any unexpected
serialized change or loss of reference membership causes removal failure,
without searching for a replacement. Changes that return to exactly the same
serialized state cannot be detected by snapshots alone; interception/generation
tracking needs real-game validation before enabling the adapter.

Verified removal checks request quantity and before/after inventory quantities.
Only successful exact removal records the effect as applied. Failed or partial
removal cannot acknowledge; later replay fails closed if the snapshot changed.
Known completed request/effect identities stay in process memory. Rejection
releases a reservation without removal/refund; exclusion clears reservations
without removing items. There is no client file or automatic timeout refund.

The fake-adapter scenarios cover merge, split, partial external removal,
metadata change, death/removal of source, identical replacement, duplicate
reservation/effect, explicit rejection and failed/partial adapter removal.
They establish engine behavior, **not proof of vanilla interception**.

| Path | Existing evidence / required prototype experiment |
| --- | --- |
| Repeated deposit | `Patches_Deposit` calls `KistClient.Deposit`; check lock before unequip or deposit. Current escrow removes immediately and must remain separate until M5. |
| Spending by name | `Patches_Craft.Patches_RemoveItem` intercepts `(string,int,int,bool)`; reserved quantities must be excluded from counts and selection. Simply blocking the exact-object overload is insufficient. |
| Stations | `KistStations.Patches_GetItem` and station callers can ignore removal failure; test selection and effect production against supported assemblies. |
| Exact removal, split, transfer | Audit every `Inventory.RemoveItem`, `MoveItemToThis`, `MoveAll`, `AddItem`, clear/load and direct stack mutation overload in real assemblies; guard source and destination merges. Method names here are audit candidates, not verified Harmony targets. |
| Drag/drop/equip/consume | Audit InventoryGui drag/split, Humanoid drop/equip and consumable paths. Protect before any irreversible game effect. |
| Death/inventory replacement | Invalidate reservations when original ownership is lost; never resolve old requests into a tombstone/replacement inventory. |
| Other mods | Test backpack/extra-slot, quick-stack and crafting/consumption mods; direct field/list mutation can bypass Harmony. Fail closed on detected changes; representative versions and remaining bypasses must be recorded in M5. |

No new Harmony patches are installed yet because their signatures and conflict
behavior cannot be verified without the game assemblies. M5 must finish coverage
before reservations are enabled in normal gameplay.

## Reproduction and provider packaging

With .NET 8 SDK:

```text
dotnet build src/ThorsKist.Sync -c Release
dotnet run --project tests/ThorsKist.Sync.Harness -c Release
dotnet build tools/SyncRuntimeProbe -c Release
```

Run `tools/SyncRuntimeProbe/bin/Release/net472/SyncRuntimeProbe.exe` on Windows
or with Mono on Linux, preserving its full output directory. Also test inside
BepInEx/Unity; desktop CLR loading is only one compatibility check.

The provider pins `Microsoft.Data.Sqlite.Core` 8.0.25 and
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.14. The bundle supplies managed provider and
native assets ([provider packaging documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)).
Native assets must retain platform-specific layout; copying only managed DLLs
does not constitute a deployable provider. The current release package includes
the new dependency `ThorsKist.Sync.dll` only; SQLite is an isolated probe until
M3/M5 choose and verify the live deployment layout. Do not ship runtime-specific
probe executables or the downloaded development SDK with the mod.

CI adds an independent Linux .NET 8 harness job, retaining its measured output.
Existing mod build, Harmony, categories and package checks remain intact.
Publishing depends on both test jobs and advertises the complete ZIP. Package
validation requires both mod and sync DLLs; manual installation instructions
also name both files.

## Local measurements (2026-10-18)

Windows x64, SDK 8.0.318, .NET runtime 8.0.21, SQLite 3.54.0.
58 assertions passed. The separate Windows `net472` probe also loaded SQLite
3.54.0 successfully on CLR 4.0.30319.42000. Its first run exposed flattened
native assets (wrong-architecture root DLL); the probe now explicitly stages
Windows x86/x64, Linux x64 and macOS x64 native assets in runtime directories.
The corrected output loaded successfully. Exceptions are caught and printed
with exit code 1 instead of triggering an application-error dialog. This layout
is a prototype for later plugin packaging, not proof of Unity loading.
File-backed tests verify WAL, FULL synchronous setting,
rollback and 100 committed rows surviving reopen. These are not process-kill or
power-loss tests. One run of 100 single-row 256-byte WAL/FULL commits measured
p50 **1.004 ms**, p95 **1.317 ms**, p99 **1.632 ms**. Connection setup is excluded.

Synthetic snapshot/delta baseline: 100 encode-and-SHA256 iterations, each stack
an integer ID, payload length and 256 opaque bytes; one changed stack per delta.
Recipients multiply encoded bytes only; no RPC, game decoding, UI or frame time
is measured. The codec is a harness fixture, not the future canonical encoding.
No optimization or numeric performance gate is inferred from these results.

| Stacks | Full bytes / recipient | Delta bytes / recipient | Full encode+hash µs | Delta encode+hash µs | Full allocated bytes / encode | Delta allocated bytes / encode |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 4 | 4 | 3.30 | 0.56 | 529 | 528 |
| 32 | 8,452 | 268 | 20.20 | 0.78 | 41,376 | 1,328 |
| 512 | 135,172 | 268 | 194.37 | 0.53 | 659,712 | 1,328 |
| 2,048 | 540,676 | 268 | 643.71 | 0.55 | 2,638,128 | 1,328 |
| 2,056 (synthetic overflow) | 542,788 | 268 | 609.39 | 0.59 | 2,640,240 | 1,328 |

Harness output includes 1/4/10-recipient byte estimates. Warmup/JIT and short-run
noise are included; these are starting measurements, not a game speedup claim.
Actual vanilla encode/decode, stock searches, UI, frame/request percentiles and
allocation baselines still require dedicated/listen-host game sessions.

## Completion gate

| Gate | Evidence |
| --- | --- |
| Library builds without game assemblies | Local Release build through harness; independent CI job added. |
| Fake clock/transport drive requests | Explicit retry delivery and 99/100 ms delay assertions; no sleeps/network. |
| Reservation prototype informs interface | Exact-reference adapter, conservative whole-stack lock, explicit failure outcomes and engine scenarios implemented. Real merge/split/death/mod experiments remain open. |
| Provider loads on supported platforms | Windows modern .NET file-backed and net472 desktop CLR probes passed. Mono/Unity and other supported OS results must be recorded before closing M1. |

This checkout has no `lib/`, `.refs/` or Valheim installation. Consequently the
mod build, Harmony verifier, category check, real inventory experiments, actual
game baseline and runtime matrix cannot be verified here. Use
`scripts/setup-refs.sh` with a licensed game/BepInEx install, run the existing
CI checks, then record game/mod versions and experiment results here.

## Review follow-up

Five focused reviewers checked contracts, packaging, harness coverage, milestone
scope and integration. The accepted fixes bind completed effect IDs to the
original request and quantity (mismatches fail), publish only the complete ZIP,
and make the sync harness a prerequisite for publishing. Small regression checks
cover effect-ID collisions, full-stack source removal and effect replay after
exclusion; package validation now requires the sync dependency.

The reviewed harness passes **65 assertions**. The 58-assertion baseline run
above is retained as the original measurement. No later-milestone state machine,
runtime matrix or new abstraction was added in response to review.
