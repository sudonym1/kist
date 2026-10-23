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

## Client responsibilities

- Alt-click removes the selected local stack and sends its prefab ID, amount, and quality through `Deposit`.
- The UI aggregates by prefab ID. The highest quality is stored for the entire entry; other metadata is discarded.
- The client chooses a withdrawal amount of one or one normal stack, limited to one for nonstackable items.
- The client checks room before requesting a withdrawal and creates items at the quality awarded by the server after receiving the result.
- The client sends any amount it could not add back through an ordinary `Deposit` with the awarded quality.
- The UI calls `List` when opened, on Refresh, and every two seconds while visible.

Request IDs are only response correlation IDs. There are no subscriptions, server-side client sessions, deduplication receipts, grant tokens, or special refund endpoints.

## Wire format and threading

Transport is `ZRoutedRpc` with one `ZPackage` argument. Requests target the server's peer ID; replies target the requesting peer. The host's client targets its own server through the same routed RPC mechanism.

| RPC name | Package fields, in order |
| --- | --- |
| `SharedInventory.v2.List` | `requestId: Int64` |
| `SharedInventory.v2.Deposit` | `requestId: Int64, itemId: String, amount: Int64, quality: Int32` |
| `SharedInventory.v2.Withdrawal` | `requestId: Int64, itemId: String, amount: Int64` |
| `SharedInventory.v2.ListReply` | `requestId: Int64, error: String, entryCount: Int32, (itemId: String, total: Int64, quality: Int32) * entryCount` |
| `SharedInventory.v2.DepositReply` | `requestId: Int64, error: String, itemId: String, deposited: Int64, total: Int64, quality: Int32` |
| `SharedInventory.v2.WithdrawalReply` | `requestId: Int64, error: String, itemId: String, withdrawn: Int64, remaining: Int64, quality: Int32` |

An empty error string means success. An error reply ends immediately after the nonempty error string. Item IDs are never validated against a game prefab database on the server. Positive amounts and numeric overflow are database integrity checks, not gameplay limits.

Packet construction, parsing, database operations, and file persistence run on an ordered worker. The main thread only registers/dispatches Valheim RPCs and applies completed results to the player's inventory or UI. No Unity or game-state methods are called by the worker; `ZPackage` uses its own managed memory stream and readers/writers.

All peers must use v2. Legacy count-only saves load at quality 1 and are rewritten in the object format on the next dirty save.
