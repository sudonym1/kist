> Archived: superseded instructions and historical evidence. See [current development instructions](../DEVELOPMENT.md).

# Einherjar phase 3 evidence

Updated 2026-10-24 for version 0.4.3. Phase 3 access is complete; phase 4
(original Eikthyr plaque trigger/gathering/countdown) is next.

The user revised the original phase 3 design: remove the server access registry,
access messages, and radius setting. Only loaded local chests grant access.
Reduced effective range from client loading settings is accepted. This record
supersedes the initial registry implementation and its evidence in commit
`a59c854` (version 0.4.2); those earlier results do not describe current behavior.

## Current behavior

- Fixed **100-metre inclusive 3D radius**, checked entirely on the client.
  A chest must be a valid active loaded scene instance. An unloaded chest gives
  no access even if its stored ZDO remains nearby. Reloading restores access.
- `SharedChest.OnEnable`/`OnDisable` maintain a local set. Each action reads
  current positions and checks only those components: **O(number of loaded
  Einherjar chests)**, not O(all game entities). The user accepts this simple
  linear check; no spatial index, access cache, world scan, or per-chest Update
  was added. Placement ghosts, prefab-library objects, inactive components,
  and detached/invalid network views grant nothing.
- No server chest registry, startup scan, coverage persistence, access snapshot,
  access RPCs, or radius setting. Old `SharedChestRadiusMeters` entries are
  unused. Client simulation/loading settings may reduce usable range below the
  displayed 100-metre maximum. The inventory database remains on the server.
- F3/Alt-click, shared crafting/upgrades/building, machine input/fuel loading,
  and shortcut counts/consumption remain gated. Carried behavior remains usable
  outside coverage. Machines use the player's position. Overlaps do not multiply
  stock. Session teardown clears the local chest set.
- Connection/life readiness stays separate from access. Pending callbacks still
  complete or return grants after range loss, unloading, or death. Rejected
  deposits restore/drop originals; late inventory snapshots preserve newer
  totals. Ordinary polling pauses without access while required reconciliation
  continues. Existing mutation uncertainty and optimistic crafting semantics
  are unchanged.
- The single local three-circle sphere, fixed numeric radius, HUD status, and
  guarded Testing entry without a chest remain. Headless execution creates no
  preview render resources. Production excludes acceptance helpers.

## Verification

Production and testing builds pass **117 regressions**, existing production
count-protocol checks using actual ZPackages, eleven deterministic fault-harness
checks, game hook checks, and test-type inclusion/exclusion checks. The loaded
access tests compile the production ChestAccess code, covering inclusive/vertical
boundaries, unload/reload, overlap, inactive/invalid/ghost exclusion, current
positions, player death, carried actions, stale station selections, in-flight
transfers, rejected deposits, capacity/death returns, inventory snapshot
reconciliation, and polling. Removed registry code and its tests are no longer
compiled. Existing game-reference assembly warnings remain; builds have zero
errors.

Guarded graphical and headless runs passed in the existing marked
`SharedInventory-Test-Phase2` copy beneath this worktree, local world
`EinherjarAssets` (UID 4232492362), Valheim 1.0.20 / Unity 6000.0.75f1. Runtime
Testing was explicitly enabled, impairments were off, and FreeBuild was false.
No production saves were used. Reports verify:

- A real loaded prefab immediately grants local access, including the exact
  100-metre 3D boundary.
- Natural scene unloading removes coverage while retaining the persistent ZDO.
  Moving that unloaded record still grants nothing.
- Reloading the same chest through the game's scene creation path restores
  coverage immediately. Disable/re-enable callbacks remove/restore coverage.
- Detached/destroyed instances grant nothing; normal destruction removes the
  world record. Headless mode creates no preview resources.
- In the graphical run, live shared-resource availability follows load/unload,
  with carried resources preserved. The actual hammer ghost grants no access,
  while still showing the three-circle preview and fixed numeric radius.

Both runs exited normally. `access-radius-preview.png`, rendered from the actual
world/placement sphere, was visually inspected. It shows the fixed 100-metre
sphere with terrain and scene objects naturally occluding hidden portions.

Reproduce with `tools/Test-Assets.ps1 -Suite Access -Mode Headless` or
`-Suite Access -Mode Review -ExitAfter`, using the guarded configuration in
TESTING.md. Reports/render remain under the isolated test root:
`access-headless-acceptance.txt`, `access-acceptance.txt`, and
`access-radius-preview.png`. Saved runtime logs are
`.work/loaded-chest-headless-runtime.log` and
`.work/loaded-chest-review-runtime.log`; build logs are
`.work/loaded-chest-production-build.log` and
`.work/loaded-chest-testing-build.log`. Packages are
`dist/EinherjarChest.zip` and `dist/EinherjarChest-Testing.zip` (version 0.4.3).

README.md, API.md, TESTING.md, WINDOWS-CODEX-SETUP.md, EINHERJAR-HANDOFF.md,
and the historical notes in PLAN.md / EINHERJAR-PHASE2-RESULTS.md now reflect
this loaded-only policy. The handoff explicitly prohibits reintroducing the
removed access registry/settings or adding an unrequested access index/cache.

## Remaining scope

This retains the trusted-client count API and existing crash/uncertain-transfer
limits. No new anti-cheat, durable receipts, or mutation retries are claimed.
Separate dedicated-executable and host-plus-remote acceptance remain phase 6;
headless evidence uses the installed game executable in server role with
`-batchmode -nographics`. Lower client loading range is intended behavior,
not a missing registry feature.

Plaque targeting, original-circle gathering, countdown, sacrifice/lightning,
hidden ritual timer, and phase 5 controls remain pending. The user's removal
of chest-access settings does not implement or remove those later ritual tasks.
