# Einherjar Chest shared inventory database API

The database stores `itemId -> (count, quality)`. Item IDs are exact, case-sensitive prefab names, such as `Coal` and `SwordIron`. Counts and amounts are signed 64-bit integers. Stored counts are nonnegative; request amounts and quality must be positive. Quality is a signed 32-bit integer.

`EinherjarEssence` is a carried-only material. The RPC handlers reject deposits, single withdrawals, and entire batches containing that exact prefab before mutating the database. `List` preserves legacy/administratively seeded entries; clients hide them and exclude them from all shared availability. The count database itself remains a general store, and no migration silently deletes essence. The wire format is unchanged.

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

New shared actions require a living local player inside the server-synchronized chest registry's coverage. Carried actions remain available outside it. `Client.Ready` describes connection/life readiness separately from `CanAccessSharedInventory`: callbacks must reconcile deposits, grants, and capacity returns even after coverage is lost. Cached counts remain stored while shared availability is zero outside coverage. Ordinary access-driven polling pauses there; failure reconciliation retains its existing backoff. Coverage does not change the v3 count wire format or turn its trusted-client mutation API into an anti-cheat boundary.

- Alt-click removes the selected local stack and sends its prefab ID, amount, and quality through `Deposit`.
- The UI aggregates by prefab ID. The highest quality is stored for the entire entry; other metadata is discarded.
- The client chooses a withdrawal amount of one or one normal stack, limited to one for nonstackable items.
- The client checks room before requesting a withdrawal and creates items at the quality awarded by the server after receiving the result.
- The client sends any amount it could not add back through an ordinary `Deposit` with the awarded quality.
- Resource availability uses carried items plus cached shared stock, matching ingredient quality and world-level rules. Consumption removes carried ingredients first and batches the shared shortfalls for the whole action.
- Station loading searches every allowed local input before shared alternatives within each machine priority. The charcoal kiln excludes `FineWood` and prioritizes `Wood` across both sources before its other accepted woods. This exception applies only to kiln processing; other machines, crafting, and building can still use fine wood. Shared item descriptions are bound to a station instance/input kind and consumed once, only at the station's normal resource-removal point after its checks.
- Alt-number shortcuts initially display positive-stock accepted items sorted by localized display name with an ordinal prefab tie-breaker. Bindings and pages stay fixed for the current hover while counts update, retaining exhausted items at zero. Newly stocked types enter only after the hover ends and a fresh list is built. An initially empty list may populate when its first refresh arrives. Each press selects the last displayed exact prefab and revalidates it through the same station policy. Shortcuts load through the station's existing `UseItem` path, avoiding the player's eat/equip fallback on rejection. They use cached data and the existing batch withdrawal; the v3 wire format and refresh timer are unchanged.
- Shared deductions update the cache immediately. Replies reconcile only affected totals while preserving newer pending deductions. Older list replies cannot restore stock already spent by newer requests.
- The client calls `List` every two seconds while the shared window, crafting/inventory UI, or hammer selection menu is open, or while looking at a supported station input/fuel control. These sources share one timer; leaving a target and returning does not bypass its cooldown. Output controls and unrelated hover targets do not poll. Merely placing pieces with menus closed does not poll. Refresh, failed batches, and failed or partial backpack withdrawals also request a list.

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

Count packet construction, parsing, database operations, and file persistence run on an ordered worker. The main thread registers/dispatches Valheim RPCs and applies completed results to the player's inventory or UI. Chest registry messages run separately on the main thread and never enter the database worker. No Unity or game-state methods are called by the worker; `ZPackage` uses its own managed memory stream and readers/writers.

## Chest registry protocol

`SharedInventory.Access.v1.Request` requests an initial or recovery snapshot, with an empty package. The server queues requests during its one-time incremental world-ZDO scan. Clients accept snapshots and changes only from their server; unavailable initialization, malformed messages, or revision gaps deny access until a complete snapshot arrives. A missing server/mod never grants unrestricted access.

`Snapshot` and `Changes` use a header of `worldUID: Int64, revision: Int64, radiusMeters: Single`, followed by `entryCount: Int32`. Snapshot entries contain `chestID: ZDOID, x: Single, y: Single, z: Single`. Change entries contain `chestID: ZDOID, exists: Bool`, followed by the same three coordinates when present. Snapshots replace the registry atomically; changes increment its revision once per batch. Stale and duplicate changes are ignored. A new session requests a fresh snapshot instead of retaining another world's positions. Retry requests occur at five-second intervals only until initialization/recovery succeeds.

Chest positions come from server ZDOs, including unloaded objects. Prefab assignment, deserialization, movement, and actual ZDO destruction update the registry. Scene unloading never removes a chest. Server radius changes travel in the same ordered revision stream. No anger state or ritual deadline is included. Typical chest counts use cached squared-distance comparisons; there are no ongoing world scans or idle registry RPCs.

All peers must use v3. Legacy count-only saves load at quality 1 and are rewritten in the object format on the next dirty save. This change does not alter the existing save format.

Refresh recovery uses one monotonic client clock. Each List attempt has a ten-second deadline; expiry or loss of readiness retires its ID. Automatic retries wait 2, 4, 8, 16, then 30 seconds (capped), only while ready and polling or explicit/failure demand remains. Explicit and failure demand survives closed menus. Manual Refresh can bypass automatic backoff when idle; repeated requests coalesce and failures retain the backoff count. Success resets backoff. Only the current attempt may apply a snapshot; retired replies cannot alter counts, qualities, deadlines, or pending deductions. Timeout maintenance runs even without a living player or open menus. Dispatch exceptions and List errors also release the active attempt and back off. Transfers and ingredient batches are never replayed by this mechanism.
