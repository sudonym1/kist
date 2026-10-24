# Einherjar phase 3 evidence

Completed 2026-10-24. Implemented the first incomplete handoff phase: chest
access coverage. Phase 4 (original Eikthyr plaque trigger/gathering/countdown)
is next. PLAN.md and phase 2's historical evidence are preserved.

## Delivered

- `src/ChestCoverage.cs` defines inclusive 3D coverage with finite/positive
  radius validation and cached chest positions. Squared distances use double
  arithmetic. Overlaps provide access once, without multiplying stock.
- `src/ChestAccess.cs` scans server ZDO sectors incrementally once after world
  loading. Hooks track prefab assignment, remote deserialization, movement,
  and actual ZDO deletion. Scene unloading is not deletion. Observed changes
  are reconciled at the next server action check even before plugin Update;
  network deltas remain batched. Idle operation has no scans or messages.
- A separate server-authenticated registry snapshot/change stream synchronizes
  chest positions and `Server / SharedChestRadiusMeters`, default 100. Clients
  wait for a complete registry, recover from gaps or malformed updates, ignore
  stale/duplicate updates, and cannot override the server radius. Session
  changes discard coverage. No ritual or anger state is transmitted.
- F3/Alt-click, transfers, shared crafting/upgrades/building, machine inputs
  and fuel, shortcut totals/selection/consumption all check current player
  coverage. Carried behavior remains available. Machines need no separate
  coverage check. Essence restrictions remain intact.
- Connection/life readiness remains separate from shared access. Range loss
  blocks new actions and ordinary polling, while pending callbacks and required
  recovery still complete. Rejected deposits restore/drop originals; accepted
  withdrawals finish once or return unused grants, including after death.
  Late list reconciliation uses cached totals even while access is unavailable.
- One local preview displays three amber great circles around the actual
  hammer ghost or inspected chest. Numeric radius and a small access HUD label
  accompany it. No per-chest Update or headless rendering resources. A guarded
  testing session can reach Testing without a chest; inventory actions remain
  gated. Production excludes acceptance helpers.

## Verification

Production and testing builds pass **116 regressions**, production count
protocol checks, registry protocol checks with actual ZPackages and logical
peers, eleven deterministic fault-harness checks, game hook checks, and
production/testing assembly isolation checks. The new tests cover boundary,
vertical distance, overlaps, invalid values, range loss during transfers,
death/capacity returns, rejected deposits, carried actions, stale machine
selections, late totals, ordinary polling and recovery. Registry checks cover
server ownership, initial waiting, live settings, snapshots/deltas, malformed,
forged, stale and gapped messages, reconnect, destruction, and zero scans or
network packets over 1,000 idle update ticks.

Guarded real-engine runs used the existing marked `SharedInventory-Test-Phase2`
copy under this worktree, local world `EinherjarAssets` (UID 4232492362), Valheim
1.0.20 and Unity 6000.0.75f1. No production saves were used. Runtime testing was
explicitly enabled with impairments off and FreeBuild=false.

`access-headless-acceptance.txt` and `access-acceptance.txt` report passed for:

- Initial server registry matching all chest ZDOs; the graphical restart also
  restored a deliberately persisted unloaded chest fixture.
- Actual prefab creation, natural scene unloading with the ZDO retained, and
  continued coverage without a loaded chest GameObject.
- Live radius changes and exact 3D boundary, unloaded-record movement, actual
  ZDO deserialization and destruction, and independent remaining coverage.
- Headless operation with no preview render resources.
- Live shared-resource availability inside/outside coverage, real hammer
  selection/ghost, three-circle preview, and numeric radius inspection.

The actual world/preview camera render in `access-radius-preview.png` was
visually inspected: the amber circles outline the 100-metre sphere, with terrain
and scene objects naturally occluding its lower/hidden portions. This is a
rendered scene artifact, not a Windows UI screenshot. The first graphical
attempt exposed the acceptance helper's stale saved camera direction; the
helper now runs the real placement ray update before checking the preview.
An initial sandboxed launch could not connect to Steam and never loaded a
world; the successful guarded runs used normal Steam access and exited normally.

The final version also passed the original phase 2 graphical asset/gameplay
acceptance again, including real placement/payment, essence pickup and portal
eligibility, chest interaction, removal/damage with no refund, and embedded
icons. That report remains `asset-acceptance.txt`; the saved log is
`.work/phase3-assets-regression.log`. The final headless access rerun also passed
restart restoration. Existing game-reference assembly version warnings remain;
both builds have zero errors.

Reproduce using `tools/Test-Assets.ps1 -Suite Access` with Headless or Review
mode, as documented in TESTING.md. Raw access reports/render remain under the
test root; saved runtime logs are `.work/phase3-access-headless.log` and
`.work/phase3-access-review.log`. Build logs are
`.work/phase3-production-build.log` and `.work/phase3-testing-build.log`.
Packages are `dist/EinherjarChest.zip` and `dist/EinherjarChest-Testing.zip`.

## Limits and next phase

This retains the existing trusted-client design. The server owns registry and
radius data; gameplay admission is local. Raw v3 count RPCs are unchanged and
are not an anti-cheat boundary. Transfers still have no durable receipts or
automatic mutation retries, and optimistic ordinary crafting can remain unpaid
after rejection, as previously documented. No stronger crash guarantees are
claimed.

Remote synchronization is covered by logical-peer protocol tests, not a claim
of completed host-plus-remote multiplayer acceptance. Separate dedicated-server
executable and remote acceptance stay in phase 6. Headless evidence uses the
installed game executable in server role with `-batchmode -nographics`.
Plaque targeting, original-circle gathering, countdown, sacrifice/lightning,
hidden timer, and phase 5 test controls remain for phases 4 and 5.
