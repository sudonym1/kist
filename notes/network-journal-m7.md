# M7: Live journal integration

Status: **planned**. This milestone owns all live-integration and platform work
left after [M6: Transport foundations](network-journal-m6.md). The scope was moved
here at the user's request on 2026-10-19; no unfinished integration gate was
claimed to have passed. Game validation and release are now **M8**.

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
