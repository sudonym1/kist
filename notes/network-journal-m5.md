# M5 adapter work and game evidence

Status: **complete for the adapter-foundation scope**, following the milestone
split. Remaining live integration is assigned to [M6](network-journal-m6.md),
and the former game-validation/release milestone is now M7. The normal gameplay RPC,
escrow, crafting and `.dat` save paths are still the legacy implementation.
The new journal adapters are exercised by a separate test plugin; they are not
connected to normal gameplay. Do not enable reservations on a real character
until M6's interception and lifecycle work is complete.

## Implemented

- `ItemWire.InventoryVersion` reads the running game's inventory header instead
  of stamping current `ItemData.Save` bytes with hardcoded format 109. Installed
  Valheim writes 110. This correction applies to normal gameplay too.
- `JournalItemCodec` separates quantity from canonical metadata, clears slot and
  equipped state, orders custom-data keys ordinally, retains variant, crafter,
  durability, world level, pickup/cheat flags and custom mod data, and validates
  prefab identity, payload bounds and trailing bytes. Durability uses stable
  nearest hundredth-unit canonicalization; repeated truncation of decoded floats
  otherwise loses a hundredth on values such as 1.13. Live item format is
  deliberately limited to the audited 110 layout and fails closed after an
  unaudited game update.
- `JournalCharacterInventory` maps process-local UUIDs to exact inventory object
  references, retains items while reserved, compares committed metadata before
  removal, rejects replacement/changed sources and remembers effect identities.
  Additions preserve exact metadata in an empty slot: vanilla's name/quality
  merge can discard crafter/variant/custom data. Full inventories fail without
  merging or dropping. A partial/throwing/uncertain effect cannot be attempted
  again in that adapter. No character-save wait or client file is introduced.
- `JournalGameThread` queues asynchronous verified effects with a finite pending
  limit, limits actions per drain, defers continuations to later frames, enforces
  its owning thread and cancels queued effects on disposal.
- `JournalLegacyDecoder` validates save-format-1 hird headers and compact item
  formats 108–110 without `Inventory.Load`. It retains header bytes and every
  stack, including unavailable prefab hashes and metadata. Duplicate/out-of-range
  slots become overflow; unknown items remain opaque and fail delivery until
  their prefab is available. 109 and 110 fixtures were tested in game; 108 uses
  the audited vanilla decoder but has not received its own runtime fixture.
  Vanilla's version-dependent old cheat-flag interpretation is retained. The
  migration library separately preserves the original complete file and backup.
  Decoding alone does not perform or activate the migration transaction.
- `KistStock` caches counts by inventory/name/quality and invalidates on the
  inventory's change callback. `KistUI` pools item rows, rebinds current button
  arguments, hides surplus rows and discards the pool with its root. Hird/forms
  retain their existing lifecycle. Initial list creation still visits every
  visible stack; this is pooling, not list virtualization or a measured frame-time
  improvement.
- SQLite uses `SQLitePCLRaw.provider.e_sqlite3` consistently instead of the bundle's
  target-dependent provider. The net472 bundle selected `dynamic_cdecl`, which
  failed in real Unity Mono looking for `System.ValueTuple, Version=4.0.2.0`.
  The explicit DllImport provider passes CoreCLR, Windows Framework x64 and
  Valheim Mono tests. The Framework probe now explicitly targets x64 and stages
  its native DLL alongside the executable. SQLite remains outside the ordinary
  release payload until live integration and platform packaging are complete.

## Reproduce the game probe

The installed game and loader supply private reference assemblies. Neither is
redistributed or changed. PowerShell setup is now available alongside the shell
script:

```powershell
./scripts/setup-refs.ps1 -GameDirectory <Valheim> -BepInExCore <installed-BepInEx-core>
./scripts/run-adapter-tests.ps1 -GameDirectory <Valheim> -BepInExCore <installed-BepInEx-core> -DotNet <dotnet.exe> -Launch
```

The runner builds with one MSBuild node, creates a fresh `.tools/m5/run-<uuid>`,
copies only the required test/plugin/provider DLLs, backs up existing local
characters/worlds/Kist files and verifies every copy with SHA-256. The backup
manifest stays in that ignored run directory. Do not package the test plugin.
CI compiles it; executing it requires an installed game/Unity runtime.

The test plugin requires both `-thorskist-adapter-tests` and a `-savedir` exactly
matching the runtime's sibling `saves` directory. Valheim 1.0.20 only parses
`-savedir` on its dedicated-server path; the test plugin therefore sets the local
save override before menu startup and forces newly created characters/worlds to
local storage within this process. It does not change Steam account settings or
edit existing cloud saves. Create/select only disposable characters and worlds.

Tests run at the menu after ObjectDB is ready, using scratch inventories rather
than the player's pack. In a disposable world, F8 builds a scratch Kist view and
checks row reuse/shrink; F9 closes it and requests the ordinary server state.
The scratch view has no authoritative vault and must not be used for crafting
or taking items.

## Verified locally (2026-10-18)

Windows x64, Valheim **1.0.20**, network version 41, Unity **6000.0.75f1**,
BepInEx **5.4.23.5** / pack **5.4.2351**, Mono CLR **4.0.30319.42000**.
The unpublicized game assembly SHA-256 is
`94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A`.

- Mod and adapter-test Release builds: zero warnings/errors.
- Real-game item tests: 48 assertions, including dictionary order, partial
  quantities, durability/variant/world-level/cheat/pickup round trips, metadata,
  exact reservation identity, simulated merge/split/slot/metadata/death/replacement
  changes, full inventory, duplicate effects and cached-count invalidation.
- Real-game decoder/dispatcher tests bring the total to 57 assertions. Fixtures
  retain an unregistered prefab, raw hird header, overflow and 109 metadata upgrade;
  queue tests cover deferred/bounded work and shutdown cancellation.
- Real Unity inventories plus production `ReplicationServer`, `ReplicationClient`,
  `JournalReplica` and SQLite: **66 cumulative assertions**. Login catches up to
  its marker; commit alone does not remove deposited items; verified processing
  removes/adds before acknowledgment; duplicate replay has no repeated removal;
  full-inventory addition stops without acknowledging; exclusion returns the
  pending withdrawal as retained overflow. This is an in-process adapter test,
  not a live multiplayer RPC integration test.
- Actual `ZRpc` / `ZSocket2` TCP loopback: **256 ordered 1 KiB messages**, checking
  callback RPC identity, sequence and full payload. No remote server or account
  is involved.
- A disposable local `M5Disposable` character and `M5DisposableWorld` were created,
  loaded, saved and reopened under the isolated runtime. The pooled-view probe
  passes **69 cumulative assertions**, including three initial rows, object reuse
  on redraw and hiding the surplus row after shrinking to two. The view was
  visually inspected and sorting exercised with computer use. All **568 existing
  local save files** still matched their pre-test SHA-256 backup manifest.
- Core harness after the provider change: **173,641 assertions**, including
  **45 forced child-process terminations**. Windows Framework x64 storage/session
  probe passes. Use workspace-local `TEMP`/`TMP` for crash/migration tests under
  the managed sandbox; its redirected OS temp path rejected file moves.
- Existing checks: **74 Harmony targets**, **93 game member references**, category
  checks (including 66 material names), version consistency and both local package
  validators pass. A local validation ZIP was built; nothing was published.

Runtime logs and backups are retained under ignored `.tools/m5/`. No installed
Gale profile or existing game assembly was modified.

## Transport audit and required binding

The installed game assemblies provide direct evidence:

| Path | Evidence | Conclusion / limit |
| --- | --- | --- |
| Steam `ZSteamSocket` | `SendQueuedPackages` uses FIFO and `SendMessageToConnection(..., 8, ...)`; only dequeues on success. | Flag 8 is reliable; same-lane reliable messages are ordered. This API uses the default lane. Disruption/reconnect still needs M7 tests. |
| Crossplay `ZPlayFabSocket` | `Send` appends sequence/type, retains in-flight payloads; `OnDataMessageReceivedCont` holds future IDs, drops past IDs, delivers only `m_next`, acknowledges and drains ordered pending data; `Recv` checks retransmission. | The game layer provides ordering/retransmission regardless of the underlying Party option. Crossplay reconnect/compression behavior still needs live tests. |
| TCP `ZSocket2` | `TcpClient` stream, length framing, FIFO writes chained by `EndWrite`, complete-frame receive queue. Direct game-RPC loopback passed. | Reliable ordered delivery within the live connection; it is incorrect to call every Valheim path TCP. |
| Listen-host local call | Local dispatch has no network socket. | Must still pass through the same journal/session/verification rules when connected. |

Steam constants and guarantees were cross-checked against Valve's
[networking types](https://partner.steamgames.com/doc/api/steamnetworkingtypes)
and [networking sockets documentation](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets).
No evidence currently requires a second item-journal retransmission protocol on
top of these reliable ordered live-connection paths. Session rejoin, deadlines
and mismatch handling are still required; this conclusion is not M7 validation.

**Do not use the existing routed callback as authentication.** The inspected
`ZRoutedRpc.RPC_RoutedRPC` deserializes `m_senderPeerID` and forwards it to
`HandleRoutedRPC`; it does not bind that field to the incoming `ZRpc`. The legacy
Kist header also accepts a claimed character ID/position. New journal ingress
must be registered on each direct `ZNetPeer.m_rpc`, verify that exact ready peer
is still current, derive account identity from the authenticated socket, bind
the character and session, and validate membership/position. Client replies must
likewise come from the actual server connection. Fixing only the supplied routed
sender ID is insufficient. The legacy protocol was not silently claimed secure.

## Handoff to M6 and M7

[M6: Live journal integration](network-journal-m6.md) carries forward every
remaining implementation and platform gate from the original M5 scope. The
adapter detects unexpected changes but **does not yet intercept gameplay**.
This milestone split changes scheduling and completion scope, not runtime
behavior or the evidence required before live deployment.

M7 needs a dedicated server and multiple independent authenticated clients,
transport disruption/reconnect tests and measured frame/request latency.
No second authenticated game client was available in this session. No purchase,
message, release publication or account/security-setting change was attempted;
no automatic approval rejection blocked this work.
