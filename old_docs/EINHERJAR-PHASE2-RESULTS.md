> Archived: superseded instructions and historical evidence. See [current development instructions](../DEVELOPMENT.md).

# Einherjar phase 2 evidence

Historical phase 2 asset/content evidence. Chest access is now implemented as a fixed 100-metre client-side check against loaded chests only; unloading removes coverage and client loading settings may reduce usable range. The current implementation and verification are in EINHERJAR-PHASE3-RESULTS.md. Later-phase statements below describe the state at phase 2 completion.

Completed 2026-10-24. The first incomplete handoff phase was the asset/content
path. Phase 3 access coverage is next. Rebased onto local main at `c202f74`.
The existing main-branch inventory-window theme is retained. PLAN.md is unchanged.

## Delivered

- Original procedural chest and crystal geometry in `src/ContentModels.cs`,
  registration/localization in `src/Content.cs`, and stable prefab IDs in
  `src/ContentIds.cs`. No additional mod dependency or asset bundle.
- Actual-model transparent 256 x 256 `assets/chest.png` and `assets/essence.png`,
  embedded in the DLL and used in the hammer and item/recipe UI. Both images
  have visible alpha coverage and transparent margins. The original chest
  silhouette, inset crystal, dark bands, and rune motif were reviewed in-game.
- Exactly one carried essence per chest through normal hammer requirements
  and local consumption. Pickup discovers the recipe; no station or mundane
  ingredient is required. Invalid placement leaves essence intact. No essence
  refund on removal or damage. The chest has no vanilla Container or copied
  stock and opens the existing shared window.
- Essence exclusion from client transfers, all shared availability/resource
  paths, and server mutation admission. Legacy entries remain saved. Mixed
  withdrawal batches containing essence reject atomically. Portal eligibility
  and transition checks also cover unrestricted portal behavior; ordinary admin
  teleports are unaffected. Vanilla no-cost building remains an admin override.
- Guarded development-only capture and real-engine acceptance tools, with a
  reproducible launcher in `tools/Test-Assets.ps1`. Production excludes these
  types and entry points. See `assets/README.md` for the source/capture workflow.
- Headless registration skips meshes, materials, image decoding, and cameras.
  Clients share generated model resources across instances and scene changes.

## Checks performed

Both production and testing builds pass 106 gameplay/client regressions, actual
ZPackage protocol checks (including essence exclusion and legacy preservation
across save/reload), eleven deterministic fault-harness checks, real game hook
checks, and production/testing assembly isolation checks. Existing game-reference
version conflicts for System.Net.Http and System.IO.Compression remain build
warnings; the new production code adds no C# compiler warnings.

The live proof used a new marked game copy beneath this worktree at
`SharedInventory-Test-Phase2`, Valheim 1.0.20 and Unity 6000.0.75f1. Only the mod
and the isolation helper are installed. Saves are local under that copy's
`saves` directory. The dedicated test character/world is `EinherjarAssets`, world
UID `4232492362`; no production character or world was imported. FreeBuild=false
and impairments=false were used.

`asset-acceptance.txt` reports **passed** for prefab registration, recipe/refund
configuration, collider/pivot/geometry checks, world pickup exactly once,
normal and unrestricted portal eligibility, recipe discovery, hammer selection,
original placement ghost, invalid placement preserving essence, valid terrain
placement outside the circle, exact local consumption, live chest interaction,
removal and damage with no essence drop, and embedded icon visibility/alpha.
The valid placement path calls the game's TryPlacePiece and ConsumeResources;
it does not override location, collision, support, or other placement rules.
Generated terrain candidates are searched within a bounded nearby ring.

Visual review confirmed chest scale, recognizable silhouette, game materials,
terrain contact, ghost geometry, and matching readable icons in the hammer,
recipe, and pickup UI. Earlier iterations exposed the inherited snow-overlay
shader, overlapping capture stages, and the game's zero-alpha piece rendering;
those are fixed in the final generator/capture path. The test helper also now
respects teleport debounce and chooses valid terrain rather than assuming the
point directly ahead is buildable. The reported possible camera crash was
checked: the current process was responding, no crash report was found, and
the successful final live acceptance and subsequent normal shutdown completed.

`headless-acceptance.txt` reports **passed** after a fresh guarded run with
`-batchmode -nographics`, checking both stable IDs, ObjectDB registration,
recipe/refund/portal configuration, colliders, server role, and absence of mod
mesh/material/icon/camera initialization. This is the installed game executable
running without graphics, not a separate dedicated-server binary. Valheim's
client startup logs include AsyncResourceUpload messages with graphics disabled;
the mod's no-render registration checks passed and the process exited normally.

Raw reports/logs remain under `SharedInventory-Test-Phase2`; full build logs are
`.work/phase2-production-build.log` and `.work/phase2-testing-build.log`. A normal
guarded `Launch-Test.cmd` is left in the test root for further inspection.
The normal/testing packages are `dist/EinherjarChest.zip` and
`dist/EinherjarChest-Testing.zip`.

## Scope left for later phases

At phase 2 completion, access was unrestricted; phase 3 has since implemented
loaded-only client access as described above. The original plaque trigger, gathering/countdown, ritual outcomes,
hidden timer, and phase 5 test buttons are not implemented. Original-circle
calibration, separate dedicated-executable deployment, two-client construction,
and full multiplayer/portal acceptance remain in their later phases. This
record makes no claim of completing the whole gameplay feature.
