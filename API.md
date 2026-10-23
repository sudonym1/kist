# Shared inventory database API

The database stores `itemId -> count`. Item IDs are exact, case-sensitive prefab names, such as `Coal` and `SwordIron`. Counts and amounts are signed 64-bit integers. Stored counts are nonnegative; request amounts must be positive.

## List()

Returns all nonzero counts:

```json
{"Coal": 400, "SwordIron": 10}
```

## Deposit(itemId, amount)

Adds the supplied amount and returns the new total. An unknown item ID creates a new entry. The server does not resolve the item, inspect metadata, check the player's inventory, enforce stack sizes, or distinguish a return from a deposit.

## Withdrawal(itemId, amount)

Atomically removes `min(amount, available)` and returns:

```json
{"withdrawn": 50, "remaining": 350}
```

Missing items return zero. Entries with zero remaining are removed. There are no gameplay caps. Requests are serialized at the database so simultaneous withdrawals cannot overdraw it.

## Client responsibilities

- Alt-click removes the selected local stack and sends its prefab ID and amount through `Deposit`.
- The UI aggregates by prefab ID. No per-item metadata is stored or sent.
- The client chooses a withdrawal amount of one or one normal stack, limited to one for nonstackable items.
- The client checks room before requesting a withdrawal and creates default items after receiving the result.
- The client sends any amount it could not add back through an ordinary `Deposit`.
- The UI calls `List` when opened, on Refresh, and every two seconds while visible.

Request IDs are only response correlation IDs. There are no subscriptions, server-side client sessions, deduplication receipts, grant tokens, or special refund endpoints.

## Wire format and threading

Transport is `ZRoutedRpc` with one `ZPackage` argument. Requests target the server's peer ID; replies target the requesting peer. The host's client targets its own server through the same routed RPC mechanism.

| RPC name | Package fields, in order |
| --- | --- |
| `SharedInventory.v1.List` | `requestId: Int64` |
| `SharedInventory.v1.Deposit` | `requestId: Int64, itemId: String, amount: Int64` |
| `SharedInventory.v1.Withdrawal` | `requestId: Int64, itemId: String, amount: Int64` |
| `SharedInventory.v1.ListReply` | `requestId: Int64, error: String, entryCount: Int32, (itemId: String, total: Int64) * entryCount` |
| `SharedInventory.v1.DepositReply` | `requestId: Int64, error: String, itemId: String, deposited: Int64, total: Int64` |
| `SharedInventory.v1.WithdrawalReply` | `requestId: Int64, error: String, itemId: String, withdrawn: Int64, remaining: Int64` |

An empty error string means success. An error reply ends immediately after the nonempty error string. Item IDs are never validated against a game prefab database on the server. Positive amounts and numeric overflow are database integrity checks, not gameplay limits.

Packet construction, parsing, database operations, and file persistence run on an ordered worker. The main thread only registers/dispatches Valheim RPCs and applies completed results to the player's inventory or UI. No Unity or game-state methods are called by the worker; `ZPackage` uses its own managed memory stream and readers/writers.
