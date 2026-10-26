> Archived: superseded instructions and historical evidence. See [current development instructions](../DEVELOPMENT.md).

# Einherjar phase 4 evidence

Completed 2026-10-24 for version 0.4.4. Phase 4 original-plaque triggering,
gathering validation, and countdown are complete. Phase 5 outcomes, persistence,
and test controls are next.

## Implemented behavior

- The original Eikthyr plaque adds an alternate-use **Begin Einherjar ritual**
  action, connected-player presence, gathering/random-sacrifice guidance, and
  the thematic avarice warning. Normal use still reads the original text.
  Trophy attachment and guardian-power selection use their existing ItemStand.
- Verified the actual object as `BossStone_Eikthyr(Clone)`, with a root
  `RuneStone` containing `$guardianstone_eikthyr_desc`. It is not an Eikthyr
  summoning altar or the nearby vegvisir. The server resolves the requested
  scene ZDO, Eikthyr prefab ID, and authored position in the original loaded
  StartTemple. Original-site identity comes from the world's location record/
  icon and loaded pose; an ambiguous location fails closed. Copies elsewhere
  and other plaques/circles do not qualify. The initiator must be within five
  metres of the plaque as well as inside the gathering boundary.
- The boundary follows the seven standing-stone origins in StartTemple local
  X/Z coordinates: `(-6.40, 5.04)`, `(-1.76, 8.11)`, `(7.02, 5.53)`,
  `(9.61, -4.28)`, `(1.61, -11.27)`, `(-5.38, -8.71)`, `(-9.13, -1.03)`.
  The local player-foot height must be between -1 and +2.5 metres, inclusive.
  This allows the interior ground/jumping while excluding high stone tops,
  underground positions, nearby starting terrain, and different circles.
  The polygon transforms with the original site's pose. It does not use a
  generic proximity radius, the former 20-metre proposal, or chest coverage.
- Connected peers plus the graphical hosting player form the server roster.
  Dead, respawning, loading, and unready peers remain required. Private map
  positions are ignored. Server character ownership, player identity, health/
  death and position are checked, together with targeted owner confirmations
  of current character/spawn state/position. No individual readiness UI exists.
- Missing players receive named/reasoned errors through the initiator's actual
  center-screen message UI. No countdown starts on rejection. One active
  attempt captures peer and character-life identity; another trigger cannot
  replace it. Held alternate use is ignored and requests are rate limited.
- All validated participants receive a server-owned monotonic five-second
  schedule. Probe echoes estimate clock offsets; increasing display revisions
  reject delayed starts/cancellations. Numbers cannot count backwards under
  clock corrections. Leaving, dying, joining, disconnecting, changing character,
  losing confirmation, or losing the original site cancels before commitment,
  clears the countdown, and prints its reason. Validation runs again at the
  exact commitment boundary.
- At five seconds, one committed ritual ID/frozen-roster handoff is emitted.
  The current phase 4 endpoint displays **The gathering is complete.** and
  returns idle. There are no deaths, lightning, rewards, hidden timer, ritual
  state files, ritual configuration, or phase 5 test buttons yet.
- New ritual messages use `SharedInventory.Ritual.v1.*` and run on the main
  thread. Inventory v3, its worker, saves, essence rules, and fixed loaded-chest
  access are unchanged. Location callbacks maintain candidates; the idle
  coordinator sends no probes and scans no entities. Active probes run at most
  four times per second. Hover inspection runs at most once per second.

## Verification

Production and testing builds pass **133 gameplay regressions**, inventory
protocol checks using actual ZPackages, **eleven fault-harness checks**, game
hook signature checks, and test-type inclusion/exclusion checks. The sixteen
new regressions compile production gathering/display logic and cover named
missing players, solo/empty rosters, exact five-second boundaries, one-attempt
commitment, frozen membership, death/leave/join/disconnect/character/confirmation
cancellation, restart IDs, client clock offsets with symmetric latency,
duplicate/reordered packets, stale sessions, and clock-jitter behavior. Existing
game-reference assembly warnings remain; builds have zero errors.

The guarded graphical and headless ritual suites pass in
`SharedInventory-Test-Phase2` beneath this worktree, using only the local
`EinherjarAssets` world, UID `4232492362`, and test character. Runtime:
**Valheim 1.0.20 / Unity 6000.0.75f1**.

The graphical suite verifies actual scene identity, other/wrong/copied stones,
points inside/outside each measured stone, vertical exclusion, private-map host
membership, connected loading-player errors, dead-host and near-outside
rejection, held-use suppression, non-server display rejection, routed owner
confirmations, cancellation clearing, restart, membership change, and a complete
`5... 4... 3... 2... 1...` countdown with one commitment after five seconds.
It also exercises normal text reading, actual carried Eikthyr trophy attachment,
and actual guardian-power selection without initiating a ritual. The headless
suite streams the original site and verifies that the server process is not a
participant and the ritual creates no cameras.

Reviewed the real-world overhead `ritual-boundary-review.png`: the amber
polygon joins the seven standing stones around the central spawn platform,
excluding the surrounding starting terrain. This overlay exists only in the
development capture; it adds no production render resources.

## Reproduction and evidence locations

Use the marked isolated copy with runtime testing enabled, impairments off,
FreeBuild false, and access to the running Steam client. Never use production
saves. Run one mode at a time:

```powershell
.\tools\Test-Assets.ps1 -TestRoot .\SharedInventory-Test-Phase2 -Suite Ritual -Mode Review -ExitAfter -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\tools\Test-Assets.ps1 -TestRoot .\SharedInventory-Test-Phase2 -Suite Ritual -Mode Headless -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\build.ps1 -ValheimDir .\SharedInventory-Test-Phase2\game -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
.\build.ps1 -ValheimDir .\SharedInventory-Test-Phase2\game -EnableTestHarness -Dotnet C:\Users\seamu\ThorsKist\.tools\dotnet\dotnet.exe
```

Reports under the isolated root: `ritual-acceptance.txt`,
`ritual-headless-acceptance.txt`, and `ritual-boundary-review.png`. Check for
`passed` on the first report line after each process exits; reports from a prior
run remain until replaced. Current runtime logs are `asset-Review.log` and
`asset-Headless.log`, which subsequent runs overwrite. Build evidence is in
`.work/phase4-production-build.log` and `.work/phase4-testing-package.log`.
Production contains no RitualAcceptance type or message observer. Guarded
launches update only the isolated copy; the normal package remains separate.

## Limits and next phase

The synthetic remote used for loading/join rejection exists only inside one
frame of the real engine test; it proves roster handling, not a remote transport
or remote screen. Clock/packet tests use deterministic schedules. Full actual
host-plus-remote countdown timing and dedicated-executable deployment remain
phase 6 acceptance. Headless checks use the installed graphical executable in
server role with `-batchmode -nographics`, not a separate dedicated binary.

The protocol trusts client movement and owner confirmations; it does not provide
comprehensive anti-cheat. Half-RTT clock estimates cannot eliminate asymmetric
network delay. An unavailable/old-mod client blocks confirmation safely rather
than being silently omitted. There is no mixed-version compatibility claim.

Phase 5 must extend the committed handoff into outcomes and completion, retaining
one active attempt while it dispatches correlated lightning/deaths, confirms the
targeted life, persists the hidden deadline, and publishes/deduplicates one
eligible reward. Replace the interim completion/reset path in `Ritual.Update`;
do not draw a victim, advance a deadline, or reward a cancelled gathering.
Implement the guarded test-only controls there. No phase 5 behavior or full
phase 6 multiplayer result is claimed by this record.
