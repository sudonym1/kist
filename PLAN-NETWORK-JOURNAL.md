# ThorsKist network journal plan

Thor's Kist is a Valheim mod that provides shared item storage. A **hird** is a player group; its **Kist**, or **vault**, is the shared inventory. An **anchor** is its world location for access-distance checks. A **take** (withdrawal) moves items to a character; a **deposit** moves them back. A **stack** groups items in an inventory slot. A **station**, such as a smelter, consumes materials. The **invite list** (roster in code) lists connected players eligible for invitation.

Use one server-ordered **replication log**, also called the **journal**, for Kist changes and item deliveries. SQLite stores state, log entries, request results, and acknowledgments. Its write-ahead log (WAL) handles database crash recovery separately from our replication log. Details, terminology references, and tests are in [the companion document](notes/network-journal-design.md).

## Three server states

| State | Contents |
| --- | --- |
| Checkpoint state | A saved snapshot at sequence C, bounded by active, logging-in, and logging-out checkpoint participants. |
| Replication log (journal) | Entries C+1 through the latest sequence H, retained until included in a checkpoint. These entries are already committed to SQLite. |
| Current state | Checkpoint state with entries C+1 through H applied: the materialized current Kist, used to validate new requests. |

Thus `current state = replay(checkpoint state, replication log)`. Checkpoint advancement and deletion of the covered log prefix (log truncation) leave current state unchanged. SQLite WAL checkpoints are a separate disk-storage operation.

## Normal processing

Each item transaction applies to only one stack and carries one character effect for an exact quantity. An action needing multiple stacks, including crafting, submits separate transactions with separate request IDs, operation IDs, journal sequences, and effect identities. There is no multi-stack item transaction. SQLite transactions may atomically group journal entries for commit or session settlement; that does not combine their item identities or acknowledgments.

1. The client submits a command with a unique request ID (idempotency key). The server assigns an operation ID; retries with the same request ID return the same operation/result.
2. The single server writer chooses the processing order and validates against current state, including earlier journaled changes.
3. In one SQLite transaction, update current state, store the request result, and append an entry containing sequence number, operation ID, exact changed stack records/deletions, any character item delivery, and the resulting Kist checksum. Commit before sending. This is the transactional outbox pattern: state and the outgoing log entry are stored together.
4. Active clients apply entries in sequence to temporary replica state, verify the checksum, and install the result. Each item transaction has one journal effect identity and exact quantity; processing and duplicate suppression advance independently for each stack transaction. Only the recipient adds the delivered quantity to its character. Earlier documents called these deliveries “awards.”
5. Immediately send a processing acknowledgment. Its sequence means all entries through that sequence were verified and applied, including this client's per-item deliveries and deposit removals. This is a cumulative acknowledgment, not confirmation of a character save. Suppress duplicate processing in memory and resend acknowledgments. Do not request or await character saves.
6. Continue processing without waiting for all members. The minimum checkpoint-participant position is the checkpoint cutoff. Participants include active sessions and protected login/logout sessions as defined below. In one SQLite transaction, apply that prefix to checkpoint state, advance C, and delete the covered entries. Offline or suspended clients cannot hold back the cutoff and must log in again.

Append a **no-op heartbeat entry** every 10 seconds per vault with active clients, even during other activity. It advances sequence with an unchanged Kist checksum and requires normal verification/acknowledgment. The 30-second acknowledgment timeout detects an idle stalled client within roughly 40 seconds. A timeout indicates suspected failure, not proof of cheating or disconnection.

All client protocol state is in memory only: replica state, request IDs, reservations, last applied sequence, and applied per-item effect IDs. No client protocol files or other persistent client storage are used. Kist sessions are separate from game connections. Login after a cooldown uses the ordinary login sequence below.

## Admission and asynchronous work

Define a configurable maximum transaction depth for server mutation admission: accepted transactions queued for or executing in the single writer, plus committed item transactions awaiting the initiating client's processing acknowledgment, count toward the limit. Database commit alone does not release an item transaction's admission slot. Check existing request results first; an accepted retry returns its existing status without consuming another slot. Atomically check depth and reserve a slot before admitting a new mutation. Release an item transaction's slot exactly once when its processing acknowledgment is persisted, definitive rejection/failure is recorded, or exclusion settlement commits its deposit completion or withdrawal compensation. Mutations with no character effect release their slot on commit or definitive failure. Observer acknowledgments govern checkpoint retention separately and do not keep a completed item transaction pending. If admitting new work would exceed the maximum, return `TRANSACTION_DEPTH_EXCEEDED` without admitting the command, changing stock, allocating a journal sequence, or recording a terminal operation result. The client waits at least 100 ms asynchronously, then retries the same request ID and payload. Repeated depth errors repeat that backoff; they neither suspend the player nor reset existing acknowledgment deadlines. Acknowledgments, heartbeats, session cleanup, and compensation bypass mutation admission and must continue when the limit is reached so pending transactions can settle and release slots.

The initiating RPC may synchronously validate and admit or reject a request. All cross-client effects travel through the committed journal and asynchronous sequence acknowledgments. Never wait synchronously for another client or perform a synchronous all-client update. Database commits, encoding, checksums, checkpoint work, and network fan-out run outside the Unity game thread. Apply Unity inventory/UI changes on that thread in bounded batches, yielding between batches; never drain an unbounded journal or rebuild every client's state in an update loop. A slow client cannot block processing of other clients' admitted work or acknowledgments; new work remains subject to the pending-transaction limit. Start each active client's 30-second acknowledgment deadline when an entry is first queued for asynchronous delivery to it; retries and later entries cannot extend it.

The transport contract assumes reliable, ordered RPC delivery within a live connection. Validate that contract for every supported Valheim transport in M5 and confirm it in M6; TCP specifically is not yet established. Application recovery covers disconnects and uncertain outcomes across reconnects. Add application retransmission or gap recovery only if transport validation shows it is needed.

## Login sequence

1. The client sends a login request with a unique request ID. The server authenticates it and checks membership and suspension. Each accepted login receives a server-generated session UUID, bound to player, vault, and epoch. Only one logging-in, active, or logging-out session may exist for a player/vault; a different login request while one exists returns `SESSION_EXISTS`. A retry of the same request returns the same UUID and marker. Every mutation, journal delivery, acknowledgment, and logout is bound to that UUID; closed UUIDs cannot be reused.
2. Under the single writer, one SQLite transaction captures checkpoint C, creates a logging-in session, appends a `Login` entry at L with an unchanged Kist checksum, and registers checkpoint protection at C. Commit before replying. The initial position C is a retention floor, not a claim of client verification. Retain an immutable snapshot at C and every entry after C, including entries arriving during transfer.
3. The client verifies and installs the snapshot, then replays C+1 through L, including its own login entry. It advances its journal position by acknowledging only verified, fully processed entries. Mutation requests remain disabled during catch-up.
4. When the server records an acknowledgment through L, it activates the session in the same writer order and enables requests; transactions from excluded sessions have already been settled on the server. Keep the same participant and acknowledgment position, with no removal/re-addition gap. Entries after L continue through normal ordered replication.

Appending the login entry and registering checkpoint protection must be atomic. A checkpoint ordered before login supplies the newer C; one ordered after login is bounded by that session's position. Duplicate login requests reuse the same session/marker. Login has a 30-second completion deadline from commit, unaffected by retries; mismatch or timeout uses suspension/cleanup and releases protection. Restart invalidates incomplete sessions before checkpointing or admitting new logins.

```mermaid
sequenceDiagram
    participant Client
    participant Server
    participant DB as SQLite
    Client->>Server: Login request with request ID
    Server->>DB: Atomically capture C, register logging-in session at C, and append Login entry L
    DB-->>Server: Commit
    Server-->>Client: Session UUID, snapshot at C with checksum, and journal C+1 through L
    Note over Server,DB: Protect snapshot and suffix from pruning, new entries after L remain ordered and retained
    Client->>Client: Verify snapshot and replay through Login L
    Client->>Server: Acknowledge verified processing through L
    Server->>DB: Record acknowledgment and activate same participant
    Server-->>Client: Kist access enabled, continue entries after L
```

## Deposit sequence

1. The client reserves the exact item quantity in its character inventory and sends a deposit with a unique request ID. The item remains locally present, but cannot be spent, moved, dropped, or deposited again while unresolved. Keep the request identity and reserved-item description in memory only.
2. The server validates access, item records, and collective capacity in writer order. In one SQLite transaction it credits the Kist, stores the result, and appends `Deposit` entry D identifying the depositor and exact local quantity to remove. Commit before publishing; retries never credit twice.
3. The depositor processes the journal in order and verifies D's checksum. Only after seeing this committed entry does it remove the reserved local quantity, remember the per-item effect as applied, and acknowledge D. Other clients apply the Kist credit without removing character items. A separate success response or packet receipt is insufficient to authorize removal.
4. Duplicate delivery/replay must not remove the item again while its applied per-item identity is known. Explicit rejection releases the reservation without removal. A missing response is uncertain: retry/query the same request ID, with no automatic refund or second deposit. If removal fails, do not acknowledge D or later entries; stop Kist processing and use the 30-second suspension and server settlement.

The Kist credit is usable after server commit; it does not wait for the depositor's acknowledgment. During suspension, the server settles every accepted pending item transaction in favor of the Kist: finish deposits and compensate unacknowledged withdrawals. An admission rejection never credits a deposit. Finish or reject already admitted deposit work in writer order before closing the old session; reject later old-session traffic. Settled deposits retain their Kist credit and never require a future character removal or reconciliation gate. After the 30-second ban, ordinary login can restore access. Lost client state or character-save disagreement may leave duplicate items on the character; that is an accepted exception. Do not remove unrelated replacement items. No client storage is used.

```mermaid
sequenceDiagram
    participant Client as Depositor
    participant Server
    participant DB as SQLite
    participant Others as Other clients
    Client->>Client: Reserve quantity, keep item locally
    Client->>Server: Deposit request with request ID and item records
    Server->>DB: Commit Kist credit, result, and Deposit entry D with removal instruction
    Server-->>Client: Journal entry D
    Server-->>Others: Journal entry D
    Client->>Client: Verify D, remove reserved quantity once, and remember applied per-item effect
    Client->>Server: Acknowledge D after removal
    Others->>Others: Verify and apply Kist credit
    Others->>Server: Acknowledge D
    Server->>DB: Persist acknowledgments, checkpoint within participant cutoff
```

## Logout sequence

1. The client stops submitting new mutations and sends a logout request with a unique request ID for its current session. Requests racing logout are ordered by the server writer.
2. In one SQLite transaction, mark the session logging-out and append a `Logout` entry at O with an unchanged Kist checksum. Reject subsequent mutations for that session. Keep its checkpoint participation at its existing acknowledgment position until logout completes.
3. Send all missing entries through O. The client verifies and applies them, including its deliveries and deposit removals, then acknowledges O. The marker follows every operation accepted for this session, so this acknowledgment covers them all.
4. Under the writer, persist the final acknowledgment, close the session, and remove its checkpoint participation atomically. Then confirm logout. Others continue processing; the closing session need not process entries after O. Further access requires ordinary login.

Duplicate logout requests reuse O and return recorded status; a lost completion reply cannot reopen the session. Logout has a 30-second completion deadline from marker commit, unaffected by retries; it cannot extend an earlier outstanding acknowledgment deadline. Timeout, mismatch, or disconnect uses exclusion: invalidate the session, compensate only unacknowledged withdrawals, finish pending deposits and retain their settled results, and release checkpoint protection atomically. An ordinary disconnect need not impose a cooldown. Logout does not request or await a character save.

```mermaid
sequenceDiagram
    participant Client
    participant Server
    participant DB as SQLite
    Client->>Client: Stop new Kist mutations
    Client->>Server: Logout request with session and request ID
    Server->>DB: Commit logging-out status and Logout entry O, keep checkpoint participation
    Server-->>Client: Missing journal entries through O
    Client->>Client: Verify and apply through O, including deliveries and deposit removals
    Client->>Server: Acknowledge O
    Server->>DB: Atomically record final ack, close session, and remove checkpoint participation
    Server-->>Client: Logout complete
```

## Timeouts, suspension, and compensation

If an active client leaves an entry unacknowledged for 30 seconds, impose a **30-second Kist access suspension**, called a Kist ban in the UI. Exclude it from requests and checkpoint acknowledgments. The player stays connected to Valheim. Track the oldest unacknowledged entry; later traffic cannot extend its deadline.

In one SQLite transaction, persist suspension and session invalidation, exclude the client, finish its pending deposits, cancel all its unacknowledged withdrawals, and append **compensating entries** returning their items. This is compensation, not rollback: original entries remain unchanged, and unrelated players' operations remain intact. Returns bypass capacity and may exceed 100% occupancy. Overflow stays visible, usable, checksummed, and persisted; ordinary deposits cannot create it.

Serialize acknowledgment and suspension handling: whichever commits first determines which deliveries remain pending. Ignore old-session acknowledgments and never return an operation twice. Items already added to a character are not removed, so a lost acknowledgment can cause duplication. A character crash or failed save after acknowledgment can lose items. Both remain accepted exceptions; this is not exactly-once character delivery.

Enforce suspension against authenticated player identity so reconnecting cannot bypass it. After 30 seconds, permit normal Kist login. No server kick or Valheim ban-list change occurs. Disconnect/hird departure also excludes the session and returns its pending deliveries, but need not impose a timed ban. Apply the same Kist rules to a listen host's local character.

Crafting and other shared-material consumption use the same single-stack withdrawal journal, acknowledgment, timeout, and compensation rules as ordinary takes. Effects already produced in the game are not undone; returning unacknowledged materials follows the same accepted duplication policy as any withdrawal. Existing crafting behavior can produce its effect before the server rejects or reduces material consumption. Retaining Kist stock in that case is an accepted error in favor of preserving Kist contents; this plan does not require authorization before the game effect.

## Checksum mismatch and login replay

On any checksum mismatch, discard failed temporary changes, stop Kist processing locally, and request **self-suspension** (self-ban). Do not add items or acknowledge the failed entry. The server immediately runs the same suspension/compensation handler, waits the 30-second cooldown, then permits ordinary login. There is no repair within the failed session. Retry a lost self-suspension request; the acknowledgment timeout is the fallback.

Login uses the journaled Login barrier L above: install verified checkpoint C and replay C+1 through L, checking every checksum, then continue later entries in order. Apply every required Kist change, but skip character effects already applied in memory or settled by exclusion: canceled withdrawals and completed deposits from closed sessions. Replay their Kist deltas normally, without applying old character effects. Canceled withdrawals still apply their original Kist deltas; later compensating entries return the stock. Acknowledge verified replayed entries even when their character deliveries are skipped. A mismatch during login repeats suspension, cooldown, and login.

## Normal take sequence

Successful withdrawal; character saving is independent.

```mermaid
sequenceDiagram
    participant Client
    participant Server
    participant DB as SQLite
    participant Others as Other active clients
    Client->>Server: Take command with request ID
    Server->>Server: Choose order and validate current state
    Server->>DB: Commit Kist debit, operation result, journal entry N and checksum
    Server-->>Client: Journal entry N
    Server-->>Others: Journal entry N
    Client->>Client: Apply temporary Kist changes and verify checksum
    Client->>Client: Install replica changes and add delivered items Skip deliveries already applied or canceled
    Client->>Server: Acknowledge N immediately
    Others->>Others: Apply Kist changes and verify checksum
    Others->>Server: Acknowledge N
    Server->>DB: Record cumulative acknowledgments
    Server->>DB: Advance checkpoint through minimum participant position Truncate covered log prefix atomically
    Note over Server,DB: Checkpoint advancement leaves current state unchanged.
```

## Error handling sequence

A checksum-mismatch report or acknowledgment timeout starts the same flow, including failures during login and heartbeat processing.

```mermaid
sequenceDiagram
    participant Client
    participant Server
    participant DB as SQLite
    participant Others as Remaining active clients
    Note over Client,Server: Trigger is a checksum-mismatch report or an acknowledgment timeout.
    Server->>DB: Commit 30-second Kist suspension and session exclusion Finish deposits, cancel pending withdrawals, and append compensating returns
    Server-->>Client: Kist suspended until server expiry time Player remains connected to the game
    Client->>Client: Stop Kist processing during cooldown
    Server-->>Others: Compensating journal entries
    Others->>Server: Verify and acknowledge entries
    Server->>DB: Advance checkpoint through remaining participants' minimum position
    Note over Client,Server: Wait 30 seconds for suspension to expire
    Client->>Server: Normal Kist login
    Server->>DB: Commit Login L and checkpoint protection at C
    Server-->>Client: New session, checkpoint C, and journal through L
    Client->>Client: Verify checkpoint and replay journal Check every checksum and skip applied or settled character effects
    Client->>Server: Acknowledge verified catch-up
    Server->>DB: Record acknowledgment through L and activate same participant
    Server-->>Client: Kist access restored
```

Any new checksum mismatch or acknowledgment timeout repeats this error flow.

## Modules and milestones

Build a **state-sync library** without Unity dependencies and a **Valheim mod** adapting inventories, actions, RPCs, and UI to it. Inject clock and transport to test faults without launching the game. All milestones are planned; test production library code, not a separate copy.

In M1, prototype deposit reservations against the real inventory adapter before fixing its interface. Establish exact stack identity and behavior under merging, splitting, partial removal, death, unexpected inventory changes, and representative mods. Verify that reserved quantities cannot be spent, moved, or dropped and that failures never remove replacement items; complete adapter coverage in M5.

The performance goal is an architecture that enables performance through deltas, asynchronous execution, and bounded work. Measure before choosing optimizations or numeric performance gates. Performance targets and deduplication/settlement retention policies remain open implementation decisions; retained identities must still prevent old retries from executing again.

| Milestone | Deliverables | Completion gate |
| --- | --- | --- |
| M1: Module boundaries and harness | Define single-stack item/command records and clock, storage, transport, inventory interfaces. Prototype deposit reservation identity and interception paths. Verify SQLite packaging and record performance baseline. | Library builds without game assemblies; fake clock/transport drive requests; reservation prototype informs the inventory interface; provider loads on supported platforms. |
| M2: State machine | Implement three states, ordering/deduplication, checksums, replay, checkpoints, and deposit removal identities. | Deterministic and independent model tests verify state equivalence, once-only server changes, and no repeated known character deliveries. |
| M3: SQLite recovery | Atomic state/log writes, operation results, checkpoint transactions, save migration with backup. | File-backed database and process-termination tests pass at commit/checkpoint/migration boundaries; committed server changes survive without repeating. |
| M4: Replication and sessions | Acknowledgments, 10-second heartbeats, timeout/self-suspension, overflow compensation, journaled login/logout barriers, server settlement of pending deposits, UUID sessions, pending-transaction admission backpressure, and login after cooldown. | Fault simulations cover reliable ordered connections, disconnects, uncertain outcomes, retries, races, stale sessions, and mismatch/rejoin; loss/reordering injections also exercise defensive behavior without assuming the live transport exposes packet loss. Login/logout cannot race checkpoints; deposits credit/remove once in-session; suspended clients cannot pin checkpoints or leave deposit reconciliation blocking rejoin. Duplicate logins cannot create concurrent sessions. Committed item transactions retain depth until acknowledged or settled; depth rejection requires 100 ms backoff and never mutates stock. |
| M5: Mod adapters | Real serialization, RPCs, inventory addition/removal, in-memory deposit reservations, single-stack effect tracking, crafting withdrawals, overflow, lifecycle, access checks, pooled rows and cached counts. Validate reliable ordered RPC delivery for each supported transport. | Adapter tests and existing build/Harmony/category/package checks pass. Acknowledge only successful character addition or deposit removal; no character-save dependency; suspend all Kist paths. Record transport guarantees and whether application gap recovery is necessary; verify reservation identity and interception paths from M1. |
| M6: Game validation and release | Dedicated-server/listen-host sessions with multiple clients, supported assemblies, representative mods, and baseline comparison. Confirm M5 transport guarantees under network disruption and reconnect. | Last-stack races, bursts, termination, connection interruption, mismatch/rejoin, and overflow pass. Verify asynchronous replication and bounded game-thread work; report measured frame/request latency, observed stutter, and accepted client-loss/duplication cases. Numeric performance targets and optimizations follow evidence. |

## Test strategy

Test the library with deterministic scenarios, independent model-based fault simulation, and SQLite crash recovery. Test the mod with adapters, existing CI verifiers, and real multiplayer sessions. The [detailed test plan](notes/network-journal-design.md#test-plan) defines assertions and evidence. Library tests need no Valheim runtime; mod integration requires supported game assemblies/sessions. Implementation and runtime verification remain outstanding.
