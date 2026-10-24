# Einherjar Chest implementation handoff

Prepared: 2026-10-24.
Status: phases 1 through 3 complete (2026-10-24). Original content and client-side loaded-chest access are implemented. The current user-approved access policy is fixed at 100 metres and may be reduced by client loading settings. Phase 4 original-plaque trigger is next; ritual gameplay is not implemented. See EINHERJAR-PHASE3-RESULTS.md for access evidence and validation limits.
Repository: `C:\Users\seamu\source\repos\SharedInventory`.

## Objective

Replace unrestricted shared-inventory access with permanent, earned access points. Keep the mechanism understandable and inexpensive to run. Establishing a remote access point should require travel with a physical resource.

This document is the gameplay handoff. `PLAN.md` contains an earlier inventory-refresh recovery handoff and its verification evidence; preserve it. Its historical restriction against adding RPCs applied to that recovery task. Ritual coordination will require additional messages in later phases. Chest access uses no server registry, configuration, or access messages.

## Implemented visual foundation

The inventory window now uses the planned reliquary palette: dark charcoal timber, aged-gold frame and angular bindings, an amber crystal/rune emblem, and a larger serif title. It is fixed to the screen centre with a preferred size of 675 x 810 pixels, using a 1.5x UI scale. Controls, fonts, and icons scale together; item icons are 48 pixels at the preferred scale. Shorter screens shorten the list first, with scale reduction only when needed to fit the minimum panel. Legacy saved window positions are ignored and dragging is removed. The header has no tagline, and the full-width search field has no manual Refresh button; the existing automatic two-second refresh remains in use. The existing Testing tab uses the same enlarged, centered shell, with its switch in the footer. That initial presentation work is now followed by completed chest/essence content and access coverage, recorded below. Ritual gameplay and its new Testing controls remain planned.

## Confirmed user requirements

- The mod is named **Einherjar Chest**. The plugin display name, inventory-window title, assembly title/product metadata, package names, and current documentation use this name. Preserve `SharedInventory.dll`, the `local.sharedinventory` plugin ID, existing configuration/save paths, namespaces, and RPC identifiers for compatibility with installed versions and test launchers.
- The ritual takes place only at the original sacrificial stones where boss trophies are hung.
- Use the Eikthyr plaque to trigger the ritual.
- **Every currently connected player must be physically inside the original stone circle before the ritual can start.** Being elsewhere in the starting area or near a different circle does not qualify. The dedicated server process is not a player; a hosting player is.
- If any connected player is absent from the circle, print a visible error message and do not start the ritual.
- When all connected players are present and the ritual is triggered, show a **five-second countdown: `5... 4... 3... 2... 1...`**. At the end, strike the randomly selected player with lightning and kill them. The hidden early-repeat punishment still kills all participants and awards no essence.
- The sacrifice is random. The initiator is eligible on exactly the same terms as everyone else. There is no volunteer selection.
- A successful ritual kills one player and drops exactly one new material, **Einherjar Essence**.
- Essence cannot enter the shared inventory and cannot pass through portals.
- A new chest is built with the hammer and costs one essence. Additional mundane ingredients have not been requested.
- The built chest must have a **unique model**, visibly distinct from vanilla chests. A renamed or recoloured vanilla chest model alone does not satisfy this requirement.
- Multiple chests access the same existing world-wide shared inventory.
- Shared deposit and withdrawal are available only within a chest's radius. Apply the same restriction to all other shared-stock use.
- Chest access radius is fixed at **100 metres**, checked entirely on the client against loaded Einherjar Chests. Unloaded chests grant no access. Reduced usable range from client simulation/loading settings is explicitly accepted. No radius setting, server chest registry, or access RPCs.
- Dismantling a chest with the hammer does not return its essence.
- There is a **hidden, server-configurable 30-minute timer** governing repeated rituals. An early repeat strikes every participating player with lightning, kills them all, and produces **no essence**.
- The existing **Testing** tab must have buttons to **reset the cooldown** and **spawn one Einherjar Essence**. Keep these in the isolated, explicitly enabled test harness, excluded from normal production builds.
- There is no visible cooldown, remaining-time display, or unavailable-until indicator. Players must still be able to initiate a ritual during the hidden interval.
- Punishment message, with spelling and capitalization corrected:

  > Odin is enraged by your avarice

## Phase 1 decisions: gameplay defaults

On 2026-10-24, the user selected **Adopt all recommended rules** while executing phase 1. The recommended gameplay defaults below are adopted for implementation. In particular, the timer is world-wide, counts offline time, restarts after punishment, and essence is unrecoverable under every chest destruction cause.

| Detail | Adopted rule |
| --- | --- |
| Timer scope | One timer per world, shared by all players and all ritual initiators. |
| Timer starts | After a confirmed successful ritual sacrifice. |
| Early-repeat punishment | Kill every participant in the validated connected-player roster; award no essence. |
| Timer after punishment | Restart the hidden interval when punishment is committed. Repeated early attempts continue to anger Odin. |
| Cancelled or invalid attempt | No deaths, no reward, no timer change. |
| Timer during shutdown | Real elapsed time counts; persist an absolute UTC deadline. |
| Boundary | At or after the deadline, a valid ritual succeeds normally. |
| Gathering boundary implementation | Match the original stone circle's physical gathering area; calibrate and verify the boundary in-game. Do not use the former proposed 20-metre radius or the 100-metre chest access radius. |
| Eikthyr progression | No mounted-trophy requirement unless requested. Using the plaque alone must not silently introduce a boss-progression gate. |
| Solo play | Allowed; the only player is sacrificed on success and killed on punishment. No hidden two-player minimum. |
| Chest destroyed by damage | Essence is unrecoverable for every destruction cause, including enemy damage, player damage, and hammer removal. This prevents bypassing permanent essence consumption by attacking the chest. |
| Ordinary death consequences | Use the game's current death, tombstone, and respawn rules. Do not silently add permanent death, forced teleportation to a new realm, or extra skill penalties. |

The punishment text accompanies the all-player death.

The timer's world scope keeps the same rule for every initiator. Persisting an absolute UTC deadline lets real time pass during shutdown; while running, use the monotonic-clock behavior specified below. A successful sacrifice starts the interval only after its ritual death is confirmed. Punishment restarts it at commitment, with persistence completed before death commands are dispatched. Invalid or cancelled attempts never change it. At the exact expiry boundary the ritual is eligible, evaluated at countdown commitment rather than initiation. Changing the configured interval affects future timer starts, leaving an existing deadline unchanged.

Chest destruction never deletes or refunds shared contents: the stock remains in the existing world database and becomes accessible again through another chest. Only the essence used for construction is permanently consumed. Ordinary death, tombstone, and respawn rules still apply to ritual participants.

## Player experience

### Plaque and gathering

Preserve the Eikthyr plaque's normal reading function. Add a clearly labelled ritual action, for example an alternate-use binding shown in hover text. Do not trigger a lethal ritual merely by reading the plaque. Preserve trophy placement and boss-power selection.

The plaque explains that all connected players must gather and that one player will be chosen at random. It may warn thematically that Odin punishes avarice, without revealing a timer, numeric interval, or current anger state. This reconciles discoverability with the deliberately hidden rule.

Show connected-player presence and missing/unavailable names. Connected players who are dead, respawning, or not fully spawned block the ritual rather than disappearing from the required group. Private map-position settings do not exempt a player. Once all prerequisites pass, a deliberate ritual interaction starts the countdown; do not add the previously suggested individual readiness-confirmation step.

Starting either an ordinary ritual or an early-repeat punishment requires every currently connected player to be inside the original circle. Check this before starting the countdown or committing any outcome. If anyone is outside, reject the attempt, identify the missing players, and leave deaths, rewards, and the hidden timer unchanged. Interacting with the plaque alone cannot start the ritual while other players are elsewhere. Everyone must remain inside through the countdown; leaving cancels the attempt before commitment.

Print the error visibly to the initiating player through the game's message UI, not only the server log. Proposed localized text: **All connected players must be at the original stone circle. Missing: {names}.** Show unavailable/dead/loading players with their reason when applicable. A rejected start produces no countdown or lightning. Avoid repeating the message every frame while an interaction key is held.

On a valid start, display **5...**, **4...**, **3...**, **2...**, and **1...** prominently to every participant at one-second intervals, then commit the outcome at the end of five seconds. Do not add another delay after the final number. A cancelled countdown must disappear and print its cancellation reason. This visible execution countdown is separate from the hidden 30-minute timer and appears identically for either outcome. Do not preview whether the attempt will succeed or cause punishment. At commitment, choose the outcome using the server's timer. A countdown finishing after the deadline counts as an eligible ritual.

On ordinary success, select and announce the random sacrifice at commitment and strike that player with a visible, audible lightning bolt from above. Synchronize the effect with their ritual death; lightning is required, not an optional flourish. Only the selected player dies from this strike: cosmetic effects must not deal area damage to other players, destroy the site, or ignite nearby objects. Kill the selected character through a reliable owner-side ritual-death path rather than relying on lightning damage overcoming armour, resistance, or other ordinary protections.

Drop one essence at the selected player's death position after confirming the ritual death. Surviving players can collect it. In solo play, it remains available after respawning. Spawn it after normal death/tombstone handling so it is not accidentally absorbed into the tombstone inventory.

On an early repeat, show the exact punishment message to all participants before dispatching a correlated lightning strike and death command for each of them. Every player in the committed roster is visibly struck by lightning and dies; no essence is created, including by any ordinary-sacrifice death hook. Keep the effects cosmetic with respect to incidental damage: each committed death is targeted, and the site and nearby structures remain intact.

### Essence and chests

Essence is a physical inventory item with a clear description: used to build a shared chest, cannot be shared or teleported, and is consumed permanently by construction. Require the essence in the builder's backpack and consume it locally through the normal placement resource path. Block essence deposits, shared resource counting, and shared withdrawals, including any legacy or administratively seeded shared entry. Preserve such entries without granting them until an explicit migration policy exists; do not silently delete stock.

Set the essence's portal eligibility appropriately and verify it under the supported world's portal/resource settings. If those settings override item restrictions, enforce the agreed essence restriction specifically at the portal transition. Other mods that deliberately replace portal rules require compatibility evaluation.

All chests reference the existing shared database. They do not hold copies or divide the stock. Destroying the final chest leaves shared contents intact; another chest restores access. Set the essence requirement's recovery flag false.

Display the chest radius during placement/inspection and a small shared-access status while connected. F3 and Alt-click remain convenient within coverage; outside it, explain that a shared chest is required. Gate shared crafting, upgrades, building, machine fuel/inputs, shortcut counts, and shortcut consumption as well as explicit transfers. Keep carried-item behavior intact outside coverage.

Measure chest access as a three-dimensional distance from the player to any valid chest, including the boundary. Overlaps do not stack or multiply benefits. For machine loading, the player's position is the access condition; do not add an unrequested second requirement that the machine also be inside the sphere.

### Models, icons, and asset production

Create and ship a dedicated chest mesh/model with its own recognizable silhouette. Reusing vanilla interaction/building components is acceptable; substituting only a vanilla mesh, tint, name, or particle effect is not. The essence also needs a dropped-world model and an inventory/recipe icon. Phase 1 adopts a distinct original crystal model for the essence as well as an original chest model.

Revalidation on 2026-10-24 found no existing model, texture, prefab, or icon assets in the source repository. Valheim's installed UnityPlayer reports **6000.0.75f1**. The installed Unity editor is **6000.5.0f1**, and Blender was not found in PATH or its standard installation directory. The newer editor's presence does not establish asset-bundle compatibility with the older game runtime. Do not silently use that editor to build release bundles.

### Phase 1 decisions: visual direction

On 2026-10-24, the user selected **Adopt the proposed artwork**. The following visual direction is confirmed for the phase 2 prototype; the actual models, icons, materials, and game appearance still require creation and in-game review.

Use the handoff's preferred first-version asset path: author original low-poly meshes deterministically in C# using Unity's Mesh API. This produces real original geometry without requiring Blender or a Unity asset-bundle build. Generate each mesh/material set once during prefab setup and share it across instances; do not rebuild geometry per chest or frame. Version-controlled generator code and parameters are the editable model source.

| Asset | Adopted original geometry | Icon |
| --- | --- | --- |
| Einherjar Essence | Small faceted, rune-marked amber/gold soul crystal, visibly distinct from existing resources; restrained emission rather than a permanently running effect. | Transparent-background render of the actual final crystal, readable at inventory size. |
| Einherjar Chest | Low-poly Nordic reliquary with an angular lid, distinct carved end profiles, dark bands, and a matching inset crystal/rune motif. | Three-quarter transparent-background render of the actual final chest for the hammer menu. |

Create both icons from the actual models after their in-game review, with consistent lighting, angle, framing, and scale. Use a development-only capture tool to render each model to a texture in the isolated game, export transparent PNGs, and check in and embed those PNGs for release. Use 256 x 256 PNGs initially, then inspect at actual UI size. Release clients load sprites once; the dedicated server performs no camera rendering. Wire the essence sprite to its item icon list and the chest sprite to its Piece/hammer icon. The placement ghost uses the chest's actual model.

Jotunn's RenderManager can render a GameObject to a Sprite if that dependency is adopted; a small development-only camera capture can do the same without adding it. Do not add Jotunn solely to obtain two icons. Image generation is useful for concept art or texture exploration, but a raster picture is not a 3D model. Purchased/downloaded or image-to-3D assets are optional alternatives, not an assumed dependency; they still require geometry/material cleanup and integration.

If visual review requires more detailed authored art, use a conventional Blender/model-source -> compatible Unity project -> asset-bundle pipeline instead. Verify an appropriate editor version, game-compatible materials, and platform targets before depending on it. Keep model sources, texture/material sources, licenses for any external assets, and a reproducible bundle build in the repository. Bundles and PNGs can be embedded in the DLL or packaged alongside it. The procedural route does not require an asset bundle; update packaging according to the route actually chosen.

Register the same stable chest/essence prefab IDs on server and clients; avoid rendering-only initialization on headless servers. Runtime-created render resources must be shared and released at the appropriate lifecycle boundary. Add the Unity module references actually needed for mesh, collider, image loading, optional capture, and effects APIs; this project does not currently reference all of them. Reuse suitable game materials at runtime where practical without redistributing extracted game assets.

Keep the models inexpensive: modest geometry, few shared materials, simple colliders, correct scale/pivot, and limited effects. Avoid always-running particle systems or separate per-frame scripts on every chest. Verify placement, interaction reach, hammer selection/removal, terrain contact, radius-preview origin, and dropped-essence pickup with the final models. Asset creation and in-game visual review are required deliverables, not deferred polish. Neither a detailed art finish nor exact game-style matching is established until a prototype has been reviewed in-game.

### Testing controls

Extend `src/Testing/TestPanel.cs` with **Reset cooldown** and **Spawn essence** buttons. Reuse `EnableTestHarness`/`SHAREDINVENTORY_TESTING`, runtime `Testing.Enabled`, and the guarded isolated-launch checks. Fault injection need not be active for either button. Compile the UI, control handlers, and any test-only RPC registrations out of production builds.

- **Reset cooldown:** clear/persist the current world's hidden anger deadline on the server so the next valid ritual is eligible immediately. Do not merely clear a client cache. Disable/reject the action while a ritual is active so it cannot change an outcome halfway through. Report completion or failure in the Testing tab; no numeric anger display is required.
- **Spawn essence:** spawn exactly one real essence pickup near the invoking living test player, using the same registered prefab as the ritual reward. This must work without a chest, without gathering at the circle, and without altering the anger timer. Normal pickup, portal restrictions, and carried-only chest construction still apply. Dead/unspawned players receive a clear unavailable message.
- Gate server handling on the isolated test session and an authorized test operator, such as the hosting player. An arbitrary client's claim that testing is enabled is insufficient. Remote operator support, if needed, should use the server's existing admin authority. These controls are test conveniences, not production gameplay or public server endpoints.
- Preserve an explicitly test-only F3/window entry outside chest coverage so the Testing tab is reachable before the first chest exists. The inventory tab and all shared actions remain range-gated. No such coverage exception exists in a normal build.
- Treat each deliberate press as one operation, disable it while pending, and correlate replies to prevent duplicate processing. Do not automatically replay an uncertain spawn. Cancel stale requests when changing sessions/worlds.

## Ritual coordination and failure behavior

Use one server-controlled ritual coordinator per world. Suggested phases: idle, validating gathering, countdown, committed sacrifice or committed punishment, waiting for death completion, finished. Permit only one active attempt. Assign a unique ritual ID and bind all commands and acknowledgements to it.

1. Validate the triggering player, the original Eikthyr plaque, the authoritative start-location identity, and the complete connected-player roster.
2. Validate that every currently connected player is alive, fully spawned, and physically inside the original stone circle's gathering boundary. Reject an incomplete gathering before starting the countdown or evaluating an outcome, including punishment, and send the initiating player a visible error naming missing/unavailable players.
3. Start the five-second countdown for all participants. Synchronize clients to a server-owned schedule so they display `5... 4... 3... 2... 1...` and finish together; do not issue per-frame network messages. Revalidate periodically while active and immediately before commitment.
4. Before commitment, cancel if anyone leaves the original circle, dies, connects, disconnects, or changes character. Explain the cancellation without exposing timer state.
5. At commitment, capture the roster and decide normal sacrifice versus punishment exactly once. A successful attempt chooses one uniformly random roster member using server-owned randomness. Repeat selections across separate successful rituals are allowed.
6. For punishment, persist the new anger deadline before dispatching correlated lightning/death commands for every participant. The committed victim list is the whole captured roster, with no essence outcome.
7. For normal sacrifice, dispatch the selected victim's correlated lightning effect and death command. After confirming that ritual death, persist the deadline before publishing the essence reward.
8. Death commands target the owning client and the particular character life that participated. Use a ritual ID, life identifier, expiry, and duplicate guard so delayed/repeated packets cannot kill a respawned character.
9. Accept only death completion associated with the committed ritual and life. An unrelated death must not create essence. Do not treat a missing acknowledgement as proof that the character survived.
10. Never select replacement victims, re-run the random draw, or replay death commands into a later life. A selected player disconnecting before sacrifice confirmation yields no reward. In punishment, disconnection must not turn the attempt into a normal sacrifice or award essence.

After commitment, roster changes do not re-evaluate the outcome or expand the victim set. A newly connecting player is not retroactively killed. Connected victims are processed independently so one missing client does not prevent the remaining punishment deaths. Missing or invalid participants time out; report the attempt as incomplete internally, without disclosing the hidden timer through player UI.

Use server-known character state and targeted client confirmations, not the publicly visible map player list alone. The server remains the sole reward authority even when scene ownership changes. The current mod trusts clients; this adds authoritative coordination, not comprehensive protection against modified clients that refuse death commands.

## Persistence and configuration

Keep ritual state separate from the item-count database, keyed by world UID. Persist the next eligible UTC time and enough attempt/outcome state to reject duplicate reward publication during ordinary processing. Use a temporary file and atomic replacement. Do not reset anger on server restart, initiator change, or character replacement.

During a running process, use a monotonic clock for countdowns and elapsed intervals; restore remaining time from the persisted UTC deadline at startup. Document the effect of manual server-clock changes. Do not perform disk work synchronously in a frame: await completion through the existing worker/main-thread pattern, advancing the ritual only after required writes complete.

Suggested server-owned ritual configuration (phase 5; not yet implemented):

| Setting | Default | Meaning |
| --- | --- | --- |
| `RitualAngerMinutes` | `30` | Hidden interval started by a confirmed successful sacrifice or restarted by committed punishment. |

Validate a finite, positive ritual interval. The server owns the ritual interval; chest access has no configuration or synchronization. The anger interval may be visible to server administrators in configuration, but never send its deadline or remaining time to ordinary clients. A runtime interval change applies to future timer starts; leave an already established deadline unchanged unless explicitly designed otherwise.

Ordinary deduplication does not provide an atomic transaction across character death, world-item spawning, and server files. On restart, do not replay unfinished death commands or automatically reissue an uncertain reward. Preserve diagnostic state for manual recovery. A crash around death or spawning can still lose an essence or leave an uncertain outcome; stronger crash guarantees are separate work and must not be claimed without implementation and tests. Fail ritual initiation safely if its state cannot be loaded or persisted.

## Client-side access through loaded chests

User revision on 2026-10-24 supersedes the original phase 3 registry/radius-setting design: use a fixed 100-metre inclusive 3D check against loaded local Einherjar Chests only. An unloaded chest grants no access even if its persistent ZDO is nearby. Lower client simulation/loading settings may reduce effective range; this is intended. Loading restores access; unloading removes it. There is no server chest registry, world scan, access handshake/snapshot, or radius setting.

`SharedChest.OnEnable` and `OnDisable` maintain a local set of chest components. Check current positions and valid live ZNetViews at each shared action; exclude ghosts, prefab-library objects, inactive pieces, and detached network objects. Complexity is O(number of loaded Einherjar chests), not O(all world entities). The user explicitly accepts that small linear check; do not add a spatial index or access cache. Refresh the HUD indication a few times a second. No per-chest Update or per-frame networking is needed.

On leaving range or unloading/destroying the last covering chest, block new shared actions on the next check. Complete or reconcile transfers already initiated; access loss must not strand removed items, bypass returns, or replay mutations. Preserve the inventory worker, reliable count transport, and shared refresh/backoff behavior. Stop ordinary access-driven polling while unavailable, retaining required recovery and completion work. Reconnect restores access only when a valid covering chest loads on that client.

## Integration map

| Existing code | Planned integration |
| --- | --- |
| `src/Plugin.cs` | Session lifecycle, local loaded-chest access, F3/Alt-click gating, access display. No chest-access configuration. |
| `src/Client.cs` | New-action access checks, essence transfer exclusions, range-safe completion/recovery. Keep `Ready` separate from `CanAccessSharedInventory`; existing callbacks use `Ready` for item recovery. |
| `src/ResourceInventory.cs` / `src/Crafting.cs` | Gate only the shared portion of requirements/consumption; require carried essence for chest placement. Preserve ordinary carried-resource behavior. |
| `src/StationInventory.cs` and station shortcut paths | Gate shared selection/counts/consumption while retaining carried selection, existing machine rules, and shortcut identity guarantees. |
| `src/Window.cs` | Chest-open interaction and understandable unavailable status; expose no anger state. |
| `src/RpcApi.cs` / `src/Worker.cs` | Preserve count protocol and worker guarantees; add distinct ritual handling in later phases without routing Unity operations onto the database worker. |
| New content-registration module and asset resources | Stable essence/chest prefab IDs, original mesh generators or authored models, embedded icons, optional asset bundle, hammer ghost, localization, portal restriction, nonrecoverable essence requirement. Register on server and every client. |
| New ritual/access modules | Plaque targeting, original-location verification, visible gathering errors, synchronized five-second countdown, correlated lightning/death effects, server state machine, per-world persistence, local loaded-chest access checks. |
| `src/Testing/TestPanel.cs` and test-only server handlers | Reset the persisted cooldown, spawn one essence, and expose the test panel without a chest. Preserve production exclusion and isolated-launch guards. |

Registering custom items/pieces with Jotunn is an established option, but the project currently has no Jotunn dependency. Choose direct registration versus that dependency explicitly during implementation. The preferred asset plan supplies original geometry and matching icons for both objects; the chest's unique model is mandatory. Avoid accidentally retaining a cloned chest's ordinary container storage or vanilla storage interaction alongside the shared window.

The current optimistic shared crafting behavior is unchanged for ordinary ingredients: rejected shared payment can leave a completed craft/build. Essence must never use that path. Test two players attempting chest construction with only one physical essence; only the player actually carrying it can place the chest. Do not expand this feature into a rewrite of all shared crafting transactions.

## Implementation sequence

1. **Complete: confirm defaults and visual direction (2026-10-24).** The user adopted all recommended rules and the proposed artwork. The gameplay and visual decision sections above are the implementation baseline. Confirmed hidden punishment, five-second countdown, lethal lightning, unique chest model, and fixed 100-metre maximum remain requirements (subject to client chest loading).
2. **Complete: prove the asset path first (2026-10-24).** Original scripted chest/essence meshes were loaded and visually inspected in the isolated game, and both matching transparent PNG icons were captured and embedded. Direct prefab registration, ghost/material/headless checks, stable IDs, hammer recipe, discovery/localization, carried-only essence consumption, shared-storage/portal exclusions, and refund behavior are implemented. See the phase 2 completion record and evidence below.
3. **Complete: implement access coverage (2026-10-24).** Fixed 100-metre client-only checks against loaded chests, action gating across every shared-stock path, placement/inspection sphere and HUD status, and range-safe transfer reconciliation. The original registry and setting were removed at the user's request. See the phase 3 completion record and EINHERJAR-PHASE3-RESULTS.md.
4. **Implement the original-plaque trigger.** Verify the actual Eikthyr plaque object in an isolated game, preserve normal interactions, add visible missing-player errors, synchronized five-second countdown, and server roster validation.
5. **Implement outcomes, persistence, and test controls.** Random sacrifice with a lethal lightning strike, lightning strikes on every player during punishment, exact message, hidden deadline, death/effect correlation, reward ownership and deduplication, restart handling, and the test-only cooldown-reset/essence-spawn controls.
6. **Validate and document.** Run relevant regressions, real-game hook checks, and isolated host plus remote multiplayer acceptance. Update player/admin documentation without exposing timer state in ordinary player UI. Ship the same compatible version to server and every client.

### Phase 1 completion record

- Gameplay defaults confirmed, including punishment restart, elapsed offline time, and permanent essence loss under enemy destruction.
- Chest and essence artwork direction confirmed, with original procedural geometry and matching model-rendered icons selected for the initial prototype.
- Existing confirmed requirements retained; no additional mundane chest ingredients, progression gate, readiness confirmation, or extra death penalty added.
- Reviewed this handoff for consistency with the adopted decisions. This phase changes documentation only; no gameplay implementation, build, or in-game acceptance result is claimed.
- At phase 1 completion, phase 2 was next. Its asset prototype and visual review are now complete; original-circle boundary calibration remains a phase 4 real-game check.

### Phase 2 completion record

- Rebased this worktree onto local `main` at `c202f74`, retaining its inventory-window reskin.
- Added original deterministic Mesh API sources in `src/ContentModels.cs`: Nordic reliquary chest, faceted rune-marked crystal, simple colliders, shared materials, and floor-aligned chest pivot. No editor, asset bundle, or Jotunn dependency was introduced. Asset sources, provenance, and capture instructions are in `assets/README.md`.
- Registered stable `EinherjarChest` and `EinherjarEssence` prefab IDs on the world server/client path. The chest has no vanilla Container or private inventory; interacting with a valid placed chest opens the existing shared window. The hammer recipe requires exactly one carried essence, no workbench, and no mundane ingredients. Pickup discovers the recipe. Its essence requirement is nonrecoverable for removal and damage destruction.
- Added client/shared-resource and server RPC exclusion of essence without deleting legacy shared entries or changing the count protocol. Portal eligibility is false, and a Humanoid eligibility postfix plus the portal transition check enforce the restriction even for unrestricted portals. Administrative teleports remain available.
- Captured and embedded actual-model 256 x 256 transparent PNGs. Visual inspection found and fixed selection of the vanilla snow shader and zero-alpha icon output; the development capture reconstructs alpha from two background renders. In-game hammer/recipe/pickup UI icons are visible and recognizable.
- Guarded real-engine acceptance passed: registration, pickup, discovery, normal/unrestricted portal eligibility, hammer selection, original placement ghost, invalid placement preserving essence, valid terrain placement, exact local consumption, placed-chest shared interaction, and no refund through either WearNTear removal or damage. Both icon alpha masks are checked. The headless server-role run passed and generates no model/material/icon/camera resources. These checks use Valheim 1.0.20 / Unity 6000.0.75f1 in a separate local test copy, not a production save.
- Production/testing builds pass 106 regressions, production protocol checks including legacy essence preservation and atomic batch rejection, eleven fault-harness checks, real game hook checks, and test-type inclusion/exclusion checks. Development capture/acceptance types and entry points are excluded from production.
- The test copy remains at `SharedInventory-Test-Phase2` beneath this worktree, with its normal guarded `Launch-Test.cmd`, local `EinherjarAssets` character/world (UID `4232492362`), and detailed reports. Reproducible guarded testing is in `tools/Test-Assets.ps1`; complete evidence and limits are in `EINHERJAR-PHASE2-RESULTS.md`.
- At phase 2 completion, phase 3 was next and access was unrestricted. Phase 3 is now complete with the loaded-only behavior below. No original-plaque trigger, ritual outcome, hidden timer, or phase 5 Testing buttons are claimed. Dedicated-executable and remote multiplayer acceptance remains in phase 6; the phase 2 headless proof uses the installed game runtime with `-batchmode -nographics` and server role enabled.

### Phase 3 completion record (updated for the user revision)

- The current implementation is client-side loaded-chest access with a fixed 100-metre inclusive 3D maximum. Lower effective range due to client loading settings is accepted. The earlier server registry, access messages, startup scan, synchronized radius, and radius setting have been removed.
- A local set maintained by chest enable/disable callbacks supplies O(number of loaded Einherjar chests) checks. Only valid active scene instances count; ghosts and stored/unloaded ZDOs grant nothing. There is no spatial index or access cache. Session shutdown clears the set.
- F3, Alt-click, shared availability, crafting/upgrades/building, station input/fuel selection, shortcut counts, and shared consumption remain gated. Carried actions continue outside coverage. Machine access checks the player only, and overlaps do not multiply stock.
- `Client.Ready` remains separate from access. Pending callbacks still reconcile after range loss or unloading; dead/detached/capacity-limited grants return through the unchanged deposit path. Cached authoritative inventory totals survive access loss. Ordinary polling pauses while required recovery continues. No mutation replay or durable receipts were added.
- The single local three-circle preview, fixed numeric radius, HUD status, and guarded Testing entry without a chest remain. Preview resources are absent on headless servers.
- Current verification and reproduction are in EINHERJAR-PHASE3-RESULTS.md. Earlier registry verification belongs to version 0.4.2/commit a59c854 and does not describe current behavior. Version 0.4.3 implements the loaded-only revision.
- Count RPCs, saves, worker guarantees, plugin ID, namespace, and DLL name are preserved. Separate dedicated-executable and host-plus-remote acceptance remain phase 6. Phase 4 plaque/gathering/countdown and phase 5 outcomes/timer/test controls remain pending.

## Acceptance checks

- Trigger works only at the original Eikthyr plaque; reading, trophy placement, and boss powers still work. Copies elsewhere and Eikthyr summoning altars cannot trigger it.
- Every connected player, including the host, is required and equally eligible. Missing, loading, dead, and private-map players behave as specified. Dedicated servers and solo hosts work.
- With even one connected player outside the original circle, the ritual prints a visible error to the initiator naming missing players and cannot start: no countdown, lightning, death, reward, or timer change. Verify this both before and during the hidden anger interval. Test players near but outside the circle and at a different circle. With everyone inside and available, triggering starts the countdown without individual readiness confirmations. Leaving during the countdown cancels before commitment.
- All participants see `5... 4... 3... 2... 1...` at one-second intervals; the outcome commits after five seconds. Verify host and remote-client timing, cancellation clearing the display, held interaction not spamming errors/restarts, and no cooldown or outcome preview.
- A cancelled countdown creates no reward, kills nobody, and leaves the timer unchanged. Concurrent triggers produce a single attempt.
- Eligible ritual visibly strikes one uniformly selected participant with lightning, kills that character, and produces exactly one essence in ordinary operation. Nearby players and structures take no incidental damage. Duplicate and late messages never repeat a strike, create another reward, or kill a new life.
- An early repeat displays exactly: **Odin is enraged by your avarice**. Every committed participant receives a visible lightning strike and dies; no essence drops from any of those deaths, and incidental structure damage is absent.
- Test timer boundaries immediately before, at, and after expiry using an injected clock, including server restart, world switching, punishment reset, and configuration changes. No remaining-time or anger-state UI leaks.
- Verify normal death protections/settings do not prevent ritual death. Retain ordinary tombstone/respawn consequences; do not rely on an arbitrary large damage number that armour/resistance can defeat.
- Essence cannot be deposited or withdrawn through any shared path, transported through portals, spent from shared counts, or refunded by hammer dismantling. Verify the chosen damage-destruction rule.
- Shared chest costs exactly one carried essence. Invalid placement does not consume it. No-cost/debug settings are explicitly tested and documented as administrative overrides where appropriate.
- Chest uses a unique model and matching icon/placement ghost; essence has a working dropped model and matching inventory/recipe icon. Check in generator/model sources and PNGs, plus bundle instructions if bundles are used. Inspect in-game for recognizable silhouette, correct scale, collisions, interaction/removal, materials, radius origin, pickup, legible small icons, and acceptable rendering cost. Verify a headless server can register both without rendering errors or icon rendering.
- In an enabled isolated test build, the Testing tab is reachable without any chest. Reset cooldown affects server state, persists across restart, and is rejected during an active ritual. Spawn essence creates one pickup per deliberate press without touching the timer or shared stock. Test delayed/duplicate messages and dead players. Production assemblies contain neither control UI nor registered test endpoints, and unauthorized/non-isolated requests cannot activate them.
- Every shared-stock action works inside coverage and is unavailable outside; carried-item actions continue. Exercise fixed 100-metre 3D boundaries, overlaps, loss of coverage on scene unloading, restoration on reload/reconnect, reduced effective range with lower client settings, and actual chest destruction. Verify that stored ZDOs and hammer ghosts grant no access.
- Entering/leaving coverage during transfers, death during withdrawal, deposit rejection, and capacity-return paths do not introduce item loss or extra grants. Existing quality/count and refresh-recovery regressions pass.
- Final-chest destruction preserves shared stock. Multiple chests never duplicate or partition it. Profiling confirms no ongoing world scans or per-frame networking.

## Remaining gameplay critique

Revalidated on 2026-10-24 against the repository, installed game/editor, and official Unity/Jotunn documentation. Phase 2 has since proved the original procedural models, embedded icons, and content path in-game; see its completion record. Ritual effects, remote ownership/death synchronization, and full multiplayer acceptance remain unimplemented/unverified. The combined feature is moderate implementation work rather than just a recipe/UI change.

- This deliberately makes the repeat rule a hidden punishment. A thematic warning can make the danger legible, but players cannot know precisely when it becomes safe from in-game feedback. That is an intentional exception to the original goal that mechanisms be obvious.
- Gathering at spawn plus physical, nonportal essence restores travel to establish access. Once bases have chests, goods still move instantly between them. The design preserves expansion travel rather than routine hauling between established bases.
- A 100-metre radius covers a substantial base or resource site and can reach outside its walls. This is the fixed maximum; client chest loading may reduce usable range. Radius visualization matters so players understand that coverage is positional, not based on buildings or ownership.
- Players can log out to reduce the required group, wait offline for the adopted real-time interval, stockpile essences across eligible rituals, or use low-investment characters. These follow from connected-player membership and an ordinary physical reward; do not add account tracking or unrequested penalties to counter them.
- All-player punishment enables griefing because one player can deliberately trigger it when everyone is gathered. The required deliberate plaque action, thematic warning, and visible five-second countdown make initiation apparent; leaving the circle before commitment cancels the attempt. There is no separate individual readiness-confirmation step.
- The ritual's steady-state cost is tiny: one stored deadline and an idle coordinator. Roster checks and effect messages occur only during attempts; chest access iterates only loaded Einherjar Chests with local arithmetic.
- Five seconds gives little time to notice a malicious trigger or leave the circle, especially while reading UI or under latency. Gathering is still required, but this design does not eliminate griefing. Lightning is only presentation; the reliable death path and unrelated-death rejection still need real multiplayer tests.
- One server schedule can keep countdown displays close, but packet latency prevents a guarantee that every remote screen shows the strike on exactly the same frame. Correctness means one committed outcome and one death per targeted life, with coherent visual timing.
- A cheap faceted essence and authored low-poly chest are practical to generate from code, but recognizable art needs a visual iteration loop. Original geometry satisfies uniqueness; it does not automatically guarantee a polished Valheim-style result. Prove this first instead of assuming a full asset workflow exists.
- The installed newer Unity editor is an asset-bundle compatibility risk, not evidence that the conventional pipeline is ready. A procedural mesh plus embedded PNG route avoids this editor/bundle dependency while keeping an optional path to more detailed art.
- The circle boundary must be authored/calibrated to the original site's geometry. A generic proximity check is insufficient, and an authoritative roster is not the same as server-owned player movement. Retain the trusted-client scope instead of claiming complete anti-cheat enforcement.

## Asset feasibility references

- [Unity 6 Mesh API](https://docs.unity.com/en-us/engine/6000.0/manual/assets-and-media/asset-types/mesh/creating-meshes/usingthe-mesh-class): procedural geometry is a supported mesh-authoring route.
- [Unity AssetBundle compatibility guidance](https://unity.com/blog/engine-platform/unity-asset-bundles-tips-pitfalls): do not assume newer-editor bundles are usable in an older runtime; platform and shader compatibility also need validation.
- [Jotunn RenderManager](https://valheim-modding.github.io/Jotunn/api/Jotunn.Managers.RenderManager.html): a registered GameObject can be rendered into an icon sprite if Jotunn is adopted.
- [Jotunn asset loading](https://valheim-modding.github.io/Jotunn/tutorials/asset-loading.html): textures and bundles can be embedded or shipped alongside the plugin.
- [Jotunn items](https://valheim-modding.github.io/Jotunn/tutorials/items.html) and [pieces](https://valheim-modding.github.io/Jotunn/tutorials/pieces.html): custom resource and hammer-piece registration patterns.
