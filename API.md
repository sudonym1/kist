# Shared inventory database API

The database stores `itemId -> (count, quality)`. Item IDs are exact, case-sensitive prefab names, such as `Coal` and `SwordIron`. Counts and amounts are signed 64-bit integers. Stored counts are nonnegative; request amounts and quality must be positive. Quality is a signed 32-bit integer.

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

- Alt-click removes the selected local stack and sends its prefab ID, amount, and quality through `Deposit`.
- The UI aggregates by prefab ID. The highest quality is stored for the entire entry; other metadata is discarded.
- The client chooses a withdrawal amount of one or one normal stack, limited to one for nonstackable items.
- The client checks room before requesting a withdrawal and creates items at the quality awarded by the server after receiving the result.
- The client sends any amount it could not add back through an ordinary `Deposit` with the awarded quality.
- Resource availability uses carried items plus cached shared stock, matching ingredient quality and world-level rules. Consumption removes carried ingredients first and batches the shared shortfalls for the whole action.
- Station loading searches every allowed local input before shared alternatives, then uses the station's configured conversion order. Shared item descriptions are bound to a station instance/input kind and consumed once, only at the station's normal resource-removal point after its checks. A common policy and optional exact-prefab selection support future exclusions/shortcuts; neither feature is enabled yet. Station loading uses the existing batch withdrawal and does not change the v3 wire format.
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

Packet construction, parsing, database operations, and file persistence run on an ordered worker. The main thread only registers/dispatches Valheim RPCs and applies completed results to the player's inventory or UI. No Unity or game-state methods are called by the worker; `ZPackage` uses its own managed memory stream and readers/writers.

All peers must use v3. Legacy count-only saves load at quality 1 and are rewritten in the object format on the next dirty save. This change does not alter the existing save format.
