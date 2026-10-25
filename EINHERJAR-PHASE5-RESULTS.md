# Einherjar phase 5 evidence

Completed 2026-10-24 for version 0.4.5. Ritual outcomes, hidden world persistence,
reward correlation and guarded Testing controls are implemented. Phase 6 actual
host-plus-remote and dedicated-executable acceptance remains next.

## Implemented behavior

- One server commitment freezes sacrifice versus punishment using the hidden
  timer at countdown completion. Sacrifice makes one uniform `Random.Next`
  selection over the full roster, including the initiator. Punishment targets
  every committed member. Joins, disconnects and later character changes never
  redraw, expand the victim set, or convert punishment to success.
- Per the user's phase 5 revision, lightning uses the installed Obliterator's
  `Incinerator.m_lightingAOEs` (`lightningAOE`) prefab. A private inactive copy
  preserves its particles, lights, camera shake and original ZSFX clips:
  `Obliterator_Lightning_01..03` and `Obliterator_Electricity_Start_01..03`.
  A whitelist removes AOE, colliders, network views and unknown gameplay scripts
  before activation. No extracted game assets or generated substitute thunder
  are shipped. Templates share the game's assets and effects have bounded lives;
  the headless process creates no presentation resources.
- Death targets the owning client and exact character/life nonce confirmed
  during gathering. Session/attempt identity, recent probes, finite expiry,
  ownership and one-use claims reject duplicates, stale commands and respawns,
  including hypothetical character-ZDO reuse. All ordinary deaths invalidate
  the life nonce. Effects are separately deduplicated.
- A neutral last-hit record is established before calling normal
  `Player.OnDeath()`. This handles fresh characters with no previous hit and
  bypasses damage mitigation/god mode while retaining ordinary tombstone,
  skill/world death settings and respawn behavior. The owner sends completion
  only after that exact invocation returns dead. The server also waits for the
  same connected character's replicated dead flag and matching death position.
  An unrelated death cannot award essence.
- Punishment displays exactly **Odin is enraged by your avarice** before death
  dispatch. Targets are independent; a failed/missing owner does not block the
  others. Missing confirmation expires after 15 seconds with no replacement,
  automatic command replay or reward. The owner sees lightning with its death;
  other committed participants receive the correlated cosmetic strike after
  server confirmation, including transport/replication latency.
- The server alone creates one real essence pickup after confirmed sacrifice
  and normal tombstone handling. Punishment never takes this reward path.
  Essence uses the existing registered prefab and content restrictions.

## State, failure and Testing controls

`Server / RitualAngerMinutes` defaults to 30 and must be finite and positive.
The hidden interval is world-wide. A successful sacrifice starts it only after
confirmed death; punishment restarts it at commitment, persisted before deaths.
Cancelled/invalid gatherings do not alter it. Configuration changes affect
future starts without changing an established deadline.

`BepInEx/config/SharedInventory/ritual-<worldUID>.json` is separate from stock.
The ordered storage worker performs load, validation, flushed temporary writes
and atomic replacement with `.bak`; main-thread callbacks advance gameplay only
after required writes succeed. Initial creation is persisted before readiness.
Corruption, world mismatch, interrupted initialization and write failures fail
closed. Shutdown drains queued file work, with previous-session ordering before
the next load. No Unity operations execute on the storage worker.

UTC deadlines count offline time. Running intervals use a monotonic clock, so
manual wall-clock jumps do not change them; restart restores remaining time from
the saved deadline and current UTC clock. The journal preserves outcome/victims,
confirmed lives/positions, reward reservation and final publication. A confirmed
sacrifice persists its timer and publication reservation before spawning. Restart
retains uncertain attempts for manual diagnosis and never replays death or reward.
No deadline, remaining-time or anger-state field is sent to ordinary clients.

Testing adds **Reset cooldown**, **Spawn essence**, and the user-requested
**Test lightning** preview. The preview plays the original Obliterator effect on
the living test player without invoking damage, death, reward or timer logic.
Reset clears and persists server state, rejecting any active ritual. Spawn creates
one physical pickup near a living spawned operator without a chest or gathering.
Existing explicit build/runtime/marker/savedir guards apply; faults need not be
active. Only the graphical local hosting peer is authorized. Remote operators are
unsupported. IDs are reserved before processing, pending buttons disable, replies
are correlated, stale sessions/duplicates are rejected, and uncertain operations
are never automatically replayed. Production excludes the controls, handlers,
endpoint/button strings and acceptance tooling. F3 browsing remains available outside chest coverage in all builds; transfers
and shared spending remain gated, and Testing controls retain their guards.

## Verification and reproduction

Production and testing builds pass **164 gameplay regressions**, actual-ZPackage
inventory protocol checks, **eleven fault-harness checks**, actual game hook
signature checks (including the normal death/last-hit path), and assembly
inclusion/exclusion checks for all three buttons and test endpoints.

New production-logic tests cover ordered persistence before deaths/reward,
uniform roster-slot eligibility, solo play, duplicate/concurrent commitment,
wrong life/session/owner/attempt and duplicate acknowledgements, independent
punishment timeouts, exact before/at/after deadline boundaries, a countdown
crossing expiry, offline restart, running wall-clock jumps, configuration edits,
world-bound atomic files/backups, corruption/interrupted creation, write failures,
uncertain spawning, reset persistence, invalid configuration and stopped sessions.

Guarded graphical and headless acceptance use the local `EinherjarAssets` world,
UID `4232492362`, in `SharedInventory-Test-Phase2` beneath this worktree, on
Valheim **1.0.20 / Unity 6000.0.75f1**. The graphical suite verifies original
Obliterator audio actually playing, particle provenance, component removal,
god-mode sacrifice, normal tombstone/respawn, one physical post-death reward,
durable journal/deadline, duplicate/respawn-delayed command rejection, identical
early-repeat countdown, exact punishment text and no reward, unchanged nearby
structure health, unchanged shared stock, dead-player rejection, pending-control
suppression, unauthorized/stale/duplicate requests, and persisted reset. Preview
checks prove one effect, unchanged player health, no death/reward/timer changes,
and dead-player rejection. Reviewed the actual-world lightning render, including
the bolt from above at the original circle. Headless acceptance verifies no
operator privilege or presentation resources and registered reward spawning.
The existing gathering suite also passes with its final handoff now lethal.

```powershell
.\tools\Test-Assets.ps1 -TestRoot .\SharedInventory-Test-Phase2 -Suite Outcomes -Mode Review -ExitAfter -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\tools\Test-Assets.ps1 -TestRoot .\SharedInventory-Test-Phase2 -Suite Outcomes -Mode Headless -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\tools\Test-Assets.ps1 -TestRoot .\SharedInventory-Test-Phase2 -Suite Ritual -Mode Review -ExitAfter -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\build.ps1 -ValheimDir .\SharedInventory-Test-Phase2\game -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\build.ps1 -ValheimDir .\SharedInventory-Test-Phase2\game -EnableTestHarness -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
```

Run one game mode at a time with access to the running Steam client. Reports:
`outcome-acceptance.txt`, `outcome-headless-acceptance.txt`, and
`ritual-acceptance.txt`; verify a newly timestamped first line of `passed`.
`ritual-lightning-review.png` is the actual-game render, and
`obliterator-lightning-prefabs.txt` audits source components, timing and clips.
Logs in the isolated root are overwritten by the next launch; retained build/
runtime evidence is under `.work/phase5-*`. Packages are
`dist/EinherjarChest.zip` and `dist/EinherjarChest-Testing.zip`.

## Rebase integration

Rebased onto main at `cd9dadc`. Retained read-only inventory browsing outside
coverage, UI simplification, the full-health fallback, all seven original stone
triggers through reading/trophy surfaces, and exact `Ritual Cancelled` messages.
Combined the short cancellation text with phase 5 owner-life nonce validation,
and adapted outcome acceptance to the BossStone site API. Build evidence after
integration is in `.work/rebase-*-build.log`; the 164 regressions include the
three additional browsing cases from main.

## Limits carried to phase 6

These tests prove a real solo graphical host and headless server role. The
headless run uses the graphical executable with `-batchmode -nographics`, not a
separate dedicated executable. Multi-process remote screens, remote ownership,
latency/asymmetry, disconnect timing and multi-victim punishment still require
phase 6 acceptance. Deterministic multi-member tests are not remote transport
evidence. Matching 0.4.5 is required on every peer; no mixed-version claim.

The protocol trusts client movement and owner confirmations; a modified client
can refuse a death. Death, item creation, world saving and ritual files are not
one atomic transaction. A crash can lose an essence or leave publication
uncertain. Reservations prevent automatic duplicate recovery, without claiming
exactly-once cross-crash reward delivery. Inspect the retained journal and world
before manual recovery; never blindly reissue an uncertain reward.
