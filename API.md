# Einherjar Chest shared inventory database API

The database stores `itemId -> (count, quality)`. Item IDs are exact, case-sensitive prefab names, such as `Coal` and `SwordIron`. Counts and amounts are signed 64-bit integers. Stored counts are nonnegative; request amounts and quality must be positive. Quality is a signed 32-bit integer.

`EinherjarEssence` and `DimmedEinherjarEssence` are carried-only building materials. The RPC handlers reject deposits, single withdrawals, and entire batches containing either exact prefab before mutating the database. `List` preserves legacy/administratively seeded entries; clients hide them and exclude them from all shared availability. The count database itself remains a general store, and no migration silently deletes essence. The wire format is unchanged.

Dimmed Essence is nonstackable and carries `local.sharedinventory.essenceBinding` in `ItemData.m_customData`: invariant-culture `1|worldUID|x|y|z`. The chest ZDO stores the same string plus `local.sharedinventory.essencePaid` and `local.sharedinventory.essenceReturned` booleans. The immutable origin defines an inclusive 3D rebuilding sphere in that world using the same radius as chest access (currently 100 metres). Missing/malformed item bindings cannot authorize building. Legacy chest records without a binding use their saved position on recovery. New free/admin placements are marked unpaid. Owner-side hammer removal and damage destruction publish the same bound refund, once per chest during ordinary processing. Recovery is scoped to WearNTear.Destroy and its resource-drop call, so nonlethal damage and standalone resource dropping cannot refund a living chest. Explicitly blocked drops publish none. As with vanilla construction, character inventories and world saves are separate and not a crash-atomic transaction.

The real `TryPlacePiece` placement boundary selects the closest valid carried Dimmed Essence before regular Essence, consumes that exact item once, and passes its unchanged binding through `IPlaced.OnPlaced` to the new chest. Regular Essence establishes a new origin. Preview checks and ingredient display use the same selector; the actual placement coordinates are rechecked before payment. The subsequent vanilla recipe payment is suppressed for this chest only. Item tooltips and dropped-item hover append the player-relative compass bearing, direct 3D distance, height differences, radius and current location status without exposing coordinates or ingredient priority. Both materials follow the synchronized Essence portal policy. No chest registry, world scan or new inventory RPC is introduced.

## List()

Returns all nonzero counts:

```json
{"Coal": {"Count":400,"Quality":1}, "SwordIron": {"Count":10,"Quality":4}}
```

## Deposit(itemId, amount, quality)

Adds the supplied amount and returns the new total and maximum of stored and deposited quality. An unknown item ID creates a new entry. The server does not resolve the item, inspect metadata, check the player's inventory, enforce stack sizes, or distinguish a return from a deposit.

## Withdrawal(itemId, amount)

Atomically removes `min(amount, available)` and returns:

```json
{"withdrawn": 50, "remaining": 350, "quality": 1}
```

Missing items return zero. Entries with zero remaining are removed, including their quality. The reply retains the awarded quality even when the last item was withdrawn. There are no gameplay caps. Requests are serialized at the database so simultaneous withdrawals cannot overdraw it.

## Withdrawal(amounts)

The exact batch variant accepts a nonempty map of prefab IDs to positive Int64 quantities. All entries are validated before any mutation. If every ingredient is available, subtract all quantities and return each affected prefab's remaining total and quality. Otherwise reject the whole batch with no changes. Duplicate IDs in a wire request are combined with overflow checking before validation.

Clients use batches for resources already consumed by an immediate crafting/building or station-loading action. No ingredients are inserted into the backpack. Rejection leaves the completed action intact, including carried ingredients already consumed, and triggers a full refresh. The rejected batch's entire shared-material portion goes unpaid. There are no reservations, retries, or rollbacks.

## Client responsibilities

New shared transfers and spending require a living local player within 100 metres of a valid loaded local Einherjar Chest. Viewing and listing stock require no chest. The shared window loads and refreshes stock outside coverage, including its first opening with an empty cache. Explicit list demand waits for player/connection readiness. Carried actions remain available outside coverage. `Client.Ready` describes connection/life readiness separately from `CanAccessSharedInventory`: callbacks must reconcile deposits, grants, and capacity returns even after coverage is lost. Displayed counts remain available while shared availability for consumption is zero outside coverage. Polling for other menus and station controls pauses there; failure reconciliation retains its existing backoff. Coverage does not change the v3 count wire format or turn its trusted-client mutation API into an anti-cheat boundary.

- Alt-click removes the selected local stack and sends its prefab ID, amount, and quality through `Deposit`.
- The UI aggregates by prefab ID. The highest quality is stored for the entire entry; other metadata is discarded.
- The client chooses a withdrawal amount of one or one normal stack, limited to one for nonstackable items.
- The client checks room before requesting a withdrawal and creates items at the quality awarded by the server after receiving the result.
- The client sends any amount it could not add back through an ordinary `Deposit` with the awarded quality.
- Resource availability uses carried items plus cached shared stock, matching ingredient quality and world-level rules. Consumption removes carried ingredients first and batches the shared shortfalls for the whole action.
- Station loading searches every allowed local input before shared alternatives within each machine priority. The charcoal kiln excludes `FineWood` and prioritizes `Wood` across both sources before its other accepted woods. This exception applies only to kiln processing; other machines, crafting, and building can still use fine wood. Shared item descriptions are bound to a station instance/input kind and consumed once, only at the station's normal resource-removal point after its checks.
- Alt-number shortcuts initially display positive-stock accepted items sorted by localized display name with an ordinal prefab tie-breaker. Bindings and pages stay fixed for the current hover while counts update, retaining exhausted items at zero. Newly stocked types enter only after the hover ends and a fresh list is built. An initially empty list may populate when its first refresh arrives. Each press selects the last displayed exact prefab and revalidates it through the same station policy. Shortcuts load through the station's existing `UseItem` path, avoiding the player's eat/equip fallback on rejection. They use cached data and the existing batch withdrawal; the v3 wire format and refresh timer are unchanged.
- Shared deductions update the cache immediately. Replies reconcile only affected totals while preserving newer pending deductions. Older list replies cannot restore stock already spent by newer requests.
- The client calls `List` every two seconds while the shared window is open, regardless of chest coverage. While chest access is available, crafting/inventory UI, the hammer selection menu, and supported station input/fuel hover controls also activate polling. These sources share one timer; leaving a target and returning does not bypass its cooldown. Output controls and unrelated hover targets do not poll. Merely placing pieces with menus closed does not poll. Opening the shared window, explicit refreshes, failed batches, and failed or partial backpack withdrawals also request a list.

Request IDs are only response correlation IDs. There are no subscriptions, server-side client sessions, deduplication receipts, grant tokens, or special refund endpoints.

## Wire format and threading

Transport is `ZRoutedRpc` with one `ZPackage` argument. Requests target the server's peer ID; replies target the requesting peer. The host's client targets its own server through the same routed RPC mechanism.

| RPC name | Package fields, in order |
| --- | --- |
| `SharedInventory.v3.List` | `requestId: Int64` |
| `SharedInventory.v3.Deposit` | `requestId: Int64, itemId: String, amount: Int64, quality: Int32` |
| `SharedInventory.v3.Withdrawal` (single) | `batch: Bool = false, requestId: Int64, itemId: String, amount: Int64` |
| `SharedInventory.v3.Withdrawal` (batch) | `batch: Bool = true, requestId: Int64, entryCount: Int32, (itemId: String, amount: Int64) * entryCount` |
| `SharedInventory.v3.ListReply` | `requestId: Int64, error: String, entryCount: Int32, (itemId: String, total: Int64, quality: Int32) * entryCount` |
| `SharedInventory.v3.DepositReply` | `requestId: Int64, error: String, itemId: String, deposited: Int64, total: Int64, quality: Int32` |
| `SharedInventory.v3.WithdrawalReply` | `batch: Bool, payload: ByteArray` |

The withdrawal reply payload is a nested package encoded as a length-prefixed byte array. Single payloads contain `requestId: Int64, error: String, itemId: String, withdrawn: Int64, remaining: Int64, quality: Int32`. Batch payloads contain `requestId: Int64, error: String, entryCount: Int32, (itemId: String, remaining: Int64, quality: Int32) * entryCount`. Zero remaining entries have forgotten their quality; batch replies use quality 1 for those entries because they do not grant physical items.

An empty error string means success. An error reply ends immediately after the nonempty error string. Item IDs are never validated against a game prefab database on the server. Positive amounts and numeric overflow are database integrity checks, not gameplay limits.

Count packet construction, parsing, database operations, and file persistence run on an ordered worker. The main thread registers/dispatches Valheim RPCs and applies completed results to the player's inventory or UI. No Unity or game-state methods are called by the worker; `ZPackage` uses its own managed memory stream and readers/writers.

## Local chest access

Access uses a fixed 100-metre inclusive 3D radius and only valid loaded scene instances. `SharedChest.OnEnable`/`OnDisable` maintain a local set. Each check reads current player/chest positions and iterates only those chest components, O(number of loaded Einherjar chests), with no world-entity scan or extra index/cache. Placement ghosts, prefab-library objects, inactive pieces, and detached/invalid ZNetViews do not grant access.

Scene unloading immediately removes coverage; stored ZDO positions do not grant access. Loading a valid chest restores coverage without a snapshot or handshake. Reduced client loading/simulation settings may reduce effective range, as intended. Session shutdown clears the local set. There is no radius configuration, server chest registry, chest-state persistence, or chest-access RPC protocol. The old radius config entry is unused; normal count requests and transfer reconciliation keep the v3 wire format above.

All peers must use v3. Legacy count-only saves load at quality 1 and are rewritten in the object format on the next dirty save. This change does not alter the existing save format.

Refresh recovery uses one monotonic client clock. Each List attempt has a ten-second deadline; expiry or loss of readiness retires its ID. Automatic retries wait 2, 4, 8, 16, then 30 seconds (capped), only while ready and polling or explicit/failure demand remains. Explicit and failure demand survives closed menus. Manual Refresh can bypass automatic backoff when idle; repeated requests coalesce and failures retain the backoff count. Success resets backoff. Only the current attempt may apply a snapshot; retired replies cannot alter counts, qualities, deadlines, or pending deductions. Timeout maintenance runs even without a living player or open menus. Dispatch exceptions and List errors also release the active attempt and back off. Transfers and ingredient batches are never replayed by this mechanism.

## Ritual protocol (0.4.5)

These separate `ZRoutedRpc` endpoints run entirely on the main thread, each with one `ZPackage`. Every package begins with `worldUid: Int64`. The namespace is `SharedInventory.Ritual.v1.`; it does not change inventory v3 or add chest-access messages.

| Suffix | Fields after world UID, in order |
| --- | --- |
| `Request` | `begin: Bool, stone: ZDOID` |
| `Probe` | `serverSession: String, ritualId: String, nonce: String, serverSent: Double` |
| `Presence` | `serverSession: String, ritualId: String, nonce: String, serverSent: Double, clientTime: Double, character: ZDOID, unavailableReason: String, position: Vector3, ownerLife: String` |
| `Display` | `serverSession: String, revision: Int64, ritualId: String, message: String, presence: String, serverTime: Double, echoedClientTime: Double, countdown: Bool, serverStart: Double` |
| `Death` | `serverSession: String, ritualId: String, characterLife: String, ownerLife: String, expiresOnOwnerClock: Double` |
| `DeathComplete` | `serverSession: String, ritualId: String, characterLife: String, ownerLife: String, deathPosition: Vector3` |
| `Strike` | `serverSession: String, ritualId: String, characterLife: String, ownerLife: String, deathPosition: Vector3` |

Clients send requests to the world server. All seven original sacrificial stones offer the same alternate-use action through their RuneStone reading and ItemStand trophy/power controls. Both controls send the parent BossStone's ZDOID; the wire layout is unchanged. The server resolves that stone's actual scene instance, validates its BossStone prefab and authored stone position in the original loaded StartTemple, and checks the initiating character's reach. Copied stones outside those original positions and unrelated runestones cannot trigger it. Location callbacks maintain the original-site candidates; no entity scan runs in the ritual's idle loop. Hover inspection is limited to once per second. Begin and inspect requests are rate limited independently. Countdown cancellation displays only `Ritual Cancelled`; initial gathering failures still identify missing players.

The server builds membership from connected peers plus a graphical hosting player, including unready/dead/loading players, without using map publicity. Character ZDO ownership, health/death, player identity, and position are checked. During validation/countdown, targeted probes at most four times per second also require the owning client's current character, spawn state, and position. Responses require the current world/session/ritual, an issued nonce, the current peer/character, and a monotonic age no greater than 1.25 seconds. Initial confirmations time out after 1.5 seconds. The captured peer/life roster must remain unchanged, and server validation runs again at commitment.

Client displays accept only the world server and increasing revisions for the current endpoint session. Probe echoes estimate the offset between monotonic clocks using half a round trip; the server sends one five-second start schedule, with corrections carried by active probe responses. The display cannot count backwards, and cancellation revisions prevent delayed starts from restoring it. No per-frame network traffic or hidden anger state is sent. Network asymmetry can still shift remote presentation; phase 6 must measure real multiplayer timing. This remains trusted-client coordination, not server-owned movement or comprehensive anti-cheat. Install 0.4.5 on every peer; the extended presence payload requires matching versions.

At countdown commitment the server freezes the outcome and victim set. Eligible sacrifice uses one uniform server draw over all roster members. An early repeat targets the whole roster. The coordinator stays active through persistence and completion; later membership changes never redraw or add victims. Each owner issues a life nonce during probes and invalidates it on every death. Death commands require the server, world/session/attempt, current owned character and life nonce, a recent probe, and a finite expiry on that owner's monotonic clock. Claiming consumes the nonce before effects or death. A delayed packet cannot target a respawn, including a reused character ZDO.

The owner constructs a neutral last-hit record, plays cosmetic lightning/thunder and calls normal `Player.OnDeath()` directly. A completion is emitted only after that exact call returns with the player dead and normal tombstone handling complete. The server additionally waits for the same connected character's replicated dead flag and a matching position. A generic death event cannot award essence. Validated completion broadcasts a deduplicated cosmetic strike to other committed participants; the owner has already seen its strike with its death. Visual timing on remote screens includes transport/replication delay. Each target is independent and has a 15-second timeout. Missing acknowledgements never imply survival or authorize a replacement/replay.

Ritual files are `SharedInventory/ritual-<worldUID>.json` beneath the server configuration directory. Separate ordered storage workers perform initial validation and atomic temporary-file replacement, then deliver main-thread callbacks. Commitment is journaled before any death. Punishment's restarted deadline is included in that write. For sacrifice, the confirmed death starts the interval and persists a reward reservation before the server creates exactly one physical essence. A final write records publication/completion. Failures disable further initiation; restart loads the UTC deadline into a monotonic remaining interval and retains every uncertain record without replay. This is ordinary-processing deduplication, not an atomic transaction across death, world saves, item creation and files.

Explicit isolated test builds add `SharedInventory.Ritual.Test.v1.Request` and `Reply` only after runtime Testing guards pass. Their headers are `worldUid: Int64, testSession: String`; request fields are `operationId: Int64, action: Int32 (0 reset, 1 spawn, 2 lightning preview), expires: Double`, and reply fields are `operationId: Int64, status: String`. Only the graphical server's local peer is authorized; remote operators are unsupported. Operations reserve increasing IDs before work, correlate replies, reject expired/session-mismatched requests, disable pending controls and never replay an uncertain action. Reset persists the server deadline and rejects any active ritual. Spawn checks the living spawned owner and calls the same physical reward-spawn helper without altering anger or stock. Lightning preview uses the sanitized Obliterator presentation on that living player, without calling death, damage, reward or timer logic. Production contains neither endpoint nor button strings.
