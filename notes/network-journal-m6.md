# M6: Transport foundations

Status: **complete for the transport-foundation scope**. On 2026-10-19, the
user requested moving the unfinished work to a later milestone. The implemented
and verified transport increment is now M6; all pending live integration and
platform gates move to [M7](network-journal-m7.md), and game validation/release
moves to [M8](network-journal-m8.md). Earlier work logs retain historical numbering.
The [main plan](../PLAN-NETWORK-JOURNAL.md#modules-and-milestones) defines the current
scope and order. This scheduling change is not evidence of completed gameplay
integration or cross-platform validation.

Normal gameplay continues using legacy RPCs, escrow, optimistic crafting and
`.dat` persistence. Do not enable real-character reservations before M7 completes
interception and lifecycle coverage.

## Implemented and verified (2026-10-19)

M6 completes the verified transport-foundation increment described here.
Normal gameplay still uses the legacy authority. `JournalDirectRpc` registers its
direct message on `ZNet.OnNewConnection`, but its world owner is deliberately not
installed by startup yet. Installing it before the persistence cutover and
reservation interception are ready would violate the activation constraint above.

- `JournalWire` defines a separate versioned binary protocol with strict lengths,
  enum validation, UTF-8 decoding, truncation/trailing-data rejection and immutable
  frame bodies. Commands, session controls, login images and entry batches have
  typed codecs. Login decoding verifies the snapshot and the complete prefix
  through the matching Login marker. Command responses cannot authorize effects;
  only verified journal processing does that.
- Frames are at most **61,469 bytes** (29-byte header plus 60 KiB body). One item
  payload is at most **24 KiB**, so its delta and character effect fit in one frame.
  Entry batches contain at most 32 entries and must also fit the byte bound;
  callers must split by encoded size. These are transport bounds, not measured
  performance targets. Before live activation, validate encodability before
  admitting mutations, and define how existing mod records beyond this limit
  remain accessible. Do not discard or silently truncate such records.
- `JournalTransfer` freezes an image, hashes it on its worker and produces bounded
  chunks on demand. `JournalTransferReceiver` admits one image per connection,
  reserves its declared size from a shared **128 MiB** in-flight budget before
  allocation, enforces a **64 MiB** image limit and a fixed 30-second monotonic
  deadline, and rejects gaps, interleaving, duplicates and hash mismatches.
  Error, expiry and teardown release its reservation. This does not solve the
  persistent replication-envelope growth/retention gate carried into M7.
- `JournalConnectionWorker` serializes decoding and handling independently per
  connection, counting queued and executing work against frame/byte limits.
  Each peer has separate bounded inbound and outbound queues (16 frames/256 KiB
  by default). Cancellation skips queued work and observes worker failures;
  Unity teardown never waits for a worker. One blocked peer cannot occupy another
  peer's queue. The send pump drains at most two frames per peer per update and
  pauses when the game socket already has 256 KiB queued.
- `JournalPeerBinding` and `JournalRequest` bind the connection object, socket
  account, character, world, vault, epoch and session. Tests reject forged player
  headers, changed connections/characters/epochs, closed bindings, wrong-direction
  messages and a command whose correlation differs from its request ID.
- The game adapter checks the exact callback RPC against the current ready peer;
  clients accept replies only from `GetServerPeer()`. Server identification checks
  the character ZDO's owner, player ID and finite replicated position before
  calling the future catalog membership/range resolver. Disconnect/change/fault
  closes the binding and queues exclusion through the supplied authority callback.
  Game-thread checks/RPC calls and worker decoding are separate.

Socket-account audit: Steam `GetHostName()` returns its networking Steam ID.
Crossplay `GetHostName()` returns a platform ID read from a package. The installed
`PlayFabManager.CheckIfUserAuthenticated` implementation simply invokes its
callback with `true`, so it is not independent evidence for that supplied ID.
The new adapter uses the connected Party socket's `m_remotePlayerId` entity ID
with a `playfab:` namespace instead. TCP endpoint addresses are not authenticated
accounts and are rejected by this adapter. Crossplay entity stability, account
mapping across backends and reconnect behavior still require live verification.
Do not infer those results from the loopback test.

Verification evidence:

- Core Release build and full harness: **174,496 assertions**, including the
  existing **45 forced process terminations** and **855 new** wire, peer, worker,
  transfer, login-replay and request-deduplication assertions. The new tests include
  exhaustive truncation of representative frames, commands and login images;
  a maximum-size item's delta/effect; global transfer exhaustion; fixed deadlines;
  corruption cleanup; reconnect binding rejection; and slow-peer isolation.
- Mod and adapter-test Release builds: zero warnings/errors. **75 Harmony targets**
  resolve and **114 game member references** pass runtime accessibility checking.
  Category checks, version consistency and both package validators pass. The
  validation ZIP is local under `.tools/m6/package/`; it is not a release.
- Actual Windows Unity/Mono runtime (same game version/assemblies as M5):
  **M6 JOURNAL TCP PASS**, four exact commands and three image chunks through the
  production codecs, bounded connection worker and real `ZRpc`/`ZSocket2` loopback.
  This tests byte transfer and worker dispatch, not authenticated multiplayer or
  the still-uninstalled world authority.
- Existing runtime item, decoder and dispatcher probes pass; the legacy TCP probe
  passes its 256 ordered 1 KiB messages; SQLite/session/inventory probes pass their
  **66 cumulative assertions**. No character was loaded for this headless run.
  All **568 existing save files** still match the independently verified backup.
- Build/harness/patch logs are retained under ignored `.tools/m6/`. The isolated
  runtime, backup manifest and Unity logs are under ignored
  `.tools/m5/run-c1200ceba84945809b86787b0422f1af/`, using the existing staging
  script. The initial low-integrity game launch exited before tests; a permitted
  launch outside low-integrity mode produced the passing runtime evidence above.
  No release was published and no installed game/profile files were replaced.

Reproduce the new library tests with the existing .NET 8 harness and
`--wire-only`; the ordinary harness also includes them. In this Windows sandbox,
use the cached SDK/packages, `-m:1 -nodeReuse:false -p:UseSharedCompilation=false`,
workspace-local TEMP/TMP for crash fixtures, and disable NuGet audit for offline
restore. The source projects keep their existing audit settings.

## Completion gate and handoff

The narrowed M6 gate is met: bounded codecs/transfers, connection/session binding,
independent workers/queues, direct-RPC registration and lifecycle hooks compile
against the supported game assemblies, pass deterministic/fault tests, and pass
the Windows Unity/Mono RPC loopback probe. Existing build, Harmony/accessibility,
category, version and package checks pass, with original saves unchanged.

M6 does not install the world transport owner, replace the legacy authority or
enable normal journal gameplay. All unfinished transport coordination, live
peer authentication tests, persistence/migration, reservation interception,
crafting/station integration, lifecycle/access controls, authoritative UI and
supported-platform packaging are recorded with their original completion gates
in [M7](network-journal-m7.md). The full multiplayer/disruption/performance matrix
and release readiness are [M8](network-journal-m8.md).
