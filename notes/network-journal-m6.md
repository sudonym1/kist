# M6: Live journal integration

Status: **planned**. This milestone takes the unfinished live-integration and
platform work from the original M5 scope. [M5](network-journal-m5.md) is complete
for adapter foundations and its recorded Windows runtime evidence. The former
M6 game-validation/release milestone is renumbered **M7**. Historical M1–M4 work
logs retain their original milestone references; the [main plan](../PLAN-NETWORK-JOURNAL.md#modules-and-milestones)
defines the current numbering.

The outcome is normal Valheim gameplay using the production journal as its only
Kist item authority. The current legacy RPC, escrow, optimistic crafting and
`.dat` persistence paths remain active until this integration is implemented.
Do not enable real-character reservations before interception and lifecycle
coverage is complete.

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

## M7 handoff and external prerequisites

M7 owns the full dedicated-server/listen-host multiplayer matrix, supported
assembly/mod combinations, last-stack races, bursts, termination, mismatch,
overflow, network disruption and reconnect. Confirm the Steam, crossplay and TCP
transport conclusions from M5 after M6's real RPC wiring. Measure frame/request
latency, visible stutter and the accepted client-loss/duplication exceptions
against the baseline before choosing numeric performance targets.

Testing needs multiple independent authenticated game clients and the supported
OS runtimes; a second authenticated client was unavailable during M5. Record
unavailable environments as external gates while continuing independent work.
Milestone completion does not authorize publishing a release. Purchases,
messages, release publication and account/security changes remain outside the
current authorization.
