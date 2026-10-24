# Einherjar Chest implementation handoff

Prepared: 2026-10-24.
Status: mod renamed to Einherjar Chest; ritual and chest gameplay changes are not implemented.
Repository: `C:\Users\seamu\source\repos\SharedInventory`.

## Objective

Replace unrestricted shared-inventory access with permanent, earned access points. Keep the mechanism understandable and inexpensive to run. Establishing a remote access point should require travel with a physical resource.

This document is the gameplay handoff. `PLAN.md` contains an earlier inventory-refresh recovery handoff and its verification evidence; preserve it. Its historical restriction against adding RPCs applied to that recovery task. This feature requires additional ritual, configuration, and chest-registry messages.

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
- Chest access radius is server-configurable, default **100 metres**.
- Dismantling a chest with the hammer does not return its essence.
- There is a **hidden, server-configurable 30-minute timer** governing repeated rituals. An early repeat strikes every participating player with lightning, kills them all, and produces **no essence**.
- The existing **Testing** tab must have buttons to **reset the cooldown** and **spawn one Einherjar Essence**. Keep these in the isolated, explicitly enabled test harness, excluded from normal production builds.
- There is no visible cooldown, remaining-time display, or unavailable-until indicator. Players must still be able to initiate a ritual during the hidden interval.
- Punishment message, with spelling and capitalization corrected:

  > Odin is enraged by your avarice

## Proposed defaults and unresolved design choices

These details make the plan implementable but are recommendations, not additional user-confirmed requirements. Resolve changes before coding dependent behavior.

| Detail | Proposed default |
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
| Chest destroyed by damage | Recommend essence is unrecoverable for every destruction cause, preventing dismantling rules being bypassed by attacking the chest. |
| Ordinary death consequences | Use the game's current death, tombstone, and respawn rules. Do not silently add permanent death, forced teleportation to a new realm, or extra skill penalties. |

The punishment text accompanies the all-player death.

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

Create and ship a dedicated chest mesh/model with its own recognizable silhouette. Reusing vanilla interaction/building components is acceptable; substituting only a vanilla mesh, tint, name, or particle effect is not. The essence also needs a dropped-world model and an inventory/recipe icon. Its visual design is open; a distinct original model is recommended, although only the chest's unique model is an explicit user requirement.

Revalidation on 2026-10-24 found no existing model, texture, prefab, or icon assets in the source repository. Valheim's installed UnityPlayer reports **6000.0.75f1**. The installed Unity editor is **6000.5.0f1**, and Blender was not found in PATH or its standard installation directory. The newer editor's presence does not establish asset-bundle compatibility with the older game runtime. Do not silently use that editor to build release bundles.

Recommended first-version approach: author original low-poly meshes deterministically in C# using Unity's Mesh API. This produces real original geometry without requiring Blender or a Unity asset-bundle build. Generate each mesh/material set once during prefab setup and share it across instances; do not rebuild geometry per chest or frame. Version-controlled generator code and parameters are the editable model source. Keep visual direction proposed until reviewed:

| Asset | Proposed original geometry | Icon |
| --- | --- | --- |
| Einherjar Essence | Small faceted, rune-marked amber/gold soul crystal, visibly distinct from existing resources; restrained emission rather than a permanently running effect. | Transparent-background render of the actual final crystal, readable at inventory size. |
| Einherjar Chest | Low-poly Nordic reliquary with an angular lid, distinct carved end profiles, dark bands, and a matching inset crystal/rune motif. | Three-quarter transparent-background render of the actual final chest for the hammer menu. |

Create both icons from the approved models with consistent lighting, angle, framing, and scale. Recommended workflow: a development-only capture tool renders each model to a texture in the isolated game, exports transparent PNGs, and those PNGs are checked in and embedded for release. Use 256 x 256 PNGs initially, then inspect at actual UI size. Release clients load sprites once; the dedicated server performs no camera rendering. Wire the essence sprite to its item icon list and the chest sprite to its Piece/hammer icon. The placement ghost uses the chest's actual model.

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

Suggested server-owned configuration:

| Setting | Default | Meaning |
| --- | --- | --- |
| `SharedChestRadiusMeters` | `100` | Radius used consistently by every client. |
| `RitualAngerMinutes` | `30` | Hidden minimum interval between successful/committed punishment outcomes under the proposed reset rule. |

Validate finite, positive values. Synchronize gameplay settings from the server; clients cannot choose larger access radii or shorter intervals. The anger interval may be visible to server administrators in configuration, but never send its deadline or remaining time to ordinary clients. A runtime interval change applies to future timer starts; leave an already established deadline unchanged unless explicitly designed otherwise.

Ordinary deduplication does not provide an atomic transaction across character death, world-item spawning, and server files. On restart, do not replay unfinished death commands or automatically reissue an uncertain reward. Preserve diagnostic state for manual recovery. A crash around death or spawning can still lose an essence or leave an uncertain outcome; stronger crash guarantees are separate work and must not be claimed without implementation and tests. Fail ritual initiation safely if its state cannot be loaded or persisted.

## Efficient chest access at a 100-metre default

Do not assume every chest within 100 metres has a loaded Unity GameObject on the client. Determine coverage from authoritative placed-chest positions, including unloaded network objects.

Recommended implementation: build a server registry from chest ZDOs once during world initialization, using an incremental scan if necessary. Update it on actual placement/destruction, and synchronize a snapshot plus changes to clients. A GameObject unloading is not chest destruction. Delay access until the initial registry is ready; do not temporarily grant unrestricted access.

For typical chest counts, cached squared-distance comparisons are sufficient. Refresh the HUD/access indication a few times a second, and recheck current position at the start of every shared action. Do not scan all world objects, allocate physics overlap results each frame, or issue per-frame RPCs. Add a spatial index only if measured chest counts warrant it.

On leaving coverage or losing the last covering chest, block new shared actions immediately on the next action check. Complete or reconcile transfers already initiated; range loss must not strand removed items, bypass returns, or replay mutations. Preserve the existing inventory worker, reliable game transport, and shared refresh/backoff behavior. Stop ordinary access-driven polling outside coverage while preserving required error recovery and completion work.

## Integration map

| Existing code | Planned integration |
| --- | --- |
| `src/Plugin.cs` | Configuration, session lifecycle, shared access service, F3/Alt-click gating, access display. |
| `src/Client.cs` | New-action access checks, essence transfer exclusions, range-safe completion/recovery. Keep `Ready` separate from `CanAccessSharedInventory`; existing callbacks use `Ready` for item recovery. |
| `src/ResourceInventory.cs` / `src/Crafting.cs` | Gate only the shared portion of requirements/consumption; require carried essence for chest placement. Preserve ordinary carried-resource behavior. |
| `src/StationInventory.cs` and station shortcut paths | Gate shared selection/counts/consumption while retaining carried selection, existing machine rules, and shortcut identity guarantees. |
| `src/Window.cs` | Chest-open interaction and understandable unavailable status; expose no anger state. |
| `src/RpcApi.cs` / `src/Worker.cs` | Preserve count protocol and worker guarantees; add distinct ritual/configuration/registry handling without routing Unity operations onto the database worker. |
| New content-registration module and asset resources | Stable essence/chest prefab IDs, original mesh generators or authored models, embedded icons, optional asset bundle, hammer ghost, localization, portal restriction, nonrecoverable essence requirement. Register on server and every client. |
| New ritual/access modules | Plaque targeting, original-location verification, visible gathering errors, synchronized five-second countdown, correlated lightning/death effects, server state machine, per-world persistence, chest registry and local access checks. |
| `src/Testing/TestPanel.cs` and test-only server handlers | Reset the persisted cooldown, spawn one essence, and expose the test panel without a chest. Preserve production exclusion and isolated-launch guards. |

Registering custom items/pieces with Jotunn is an established option, but the project currently has no Jotunn dependency. Choose direct registration versus that dependency explicitly during implementation. The preferred asset plan supplies original geometry and matching icons for both objects; the chest's unique model is mandatory. Avoid accidentally retaining a cloned chest's ordinary container storage or vanilla storage interaction alongside the shared window.

The current optimistic shared crafting behavior is unchanged for ordinary ingredients: rejected shared payment can leave a completed craft/build. Essence must never use that path. Test two players attempting chest construction with only one physical essence; only the player actually carrying it can place the chest. Do not expand this feature into a rewrite of all shared crafting transactions.

## Implementation sequence

1. **Confirm unresolved defaults and visual direction.** In particular: timer restart after punishment, timer counting offline time, essence loss under enemy destruction, and chest artwork. Retain confirmed hidden punishment, five-second countdown, lethal lightning, unique chest model, and 100-metre default.
2. **Prove the asset path first.** Produce a recognizable chest/essence model prototype, load them in the isolated game, capture both icons, and verify material/ghost/headless behavior before building the whole feature around the asset pipeline. Prefer original scripted meshes; use a compatible asset-bundle toolchain if visual review requires it. Then establish stable IDs, hammer recipe, discovery/localization, carried-only essence consumption, shared-storage/portal exclusions, and refund behavior.
3. **Implement access coverage.** Server registry including unloaded chests, configuration synchronization, action gating across all shared paths, radius visuals, and safe handling of pending operations.
4. **Implement the original-plaque trigger.** Verify the actual Eikthyr plaque object in an isolated game, preserve normal interactions, add visible missing-player errors, synchronized five-second countdown, and server roster validation.
5. **Implement outcomes, persistence, and test controls.** Random sacrifice with a lethal lightning strike, lightning strikes on every player during punishment, exact message, hidden deadline, death/effect correlation, reward ownership and deduplication, restart handling, and the test-only cooldown-reset/essence-spawn controls.
6. **Validate and document.** Run relevant regressions, real-game hook checks, and isolated host plus remote multiplayer acceptance. Update player/admin documentation without exposing timer state in ordinary player UI. Ship the same compatible version to server and every client.

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
- Every shared-stock action works inside coverage and is unavailable outside; carried-item actions continue. Exercise radius boundaries, overlaps, 100-metre coverage with unloaded chest objects, reconnect snapshots, and actual chest destruction versus unloading.
- Entering/leaving coverage during transfers, death during withdrawal, deposit rejection, and capacity-return paths do not introduce item loss or extra grants. Existing quality/count and refresh-recovery regressions pass.
- Final-chest destruction preserves shared stock. Multiple chests never duplicate or partition it. Profiling confirms no ongoing world scans or per-frame networking.

## Remaining gameplay critique

Revalidated on 2026-10-24 against the repository, installed game/editor, and official Unity/Jotunn documentation. The mechanics are feasible; the combined feature is moderate implementation work rather than just a recipe/UI change. The main uncertain work is final asset appearance, game-compatible effects/materials, and remote ownership/death synchronization. No ritual, model, icon, or multiplayer acceptance prototype exists yet; the earlier successful regression/build results concern the existing mod and rename.

- This deliberately makes the repeat rule a hidden punishment. A thematic warning can make the danger legible, but players cannot know precisely when it becomes safe from in-game feedback. That is an intentional exception to the original goal that mechanisms be obvious.
- Gathering at spawn plus physical, nonportal essence restores travel to establish access. Once bases have chests, goods still move instantly between them. The design preserves expansion travel rather than routine hauling between established bases.
- A 100-metre radius covers a substantial base or resource site and can reach outside its walls. This is the requested default, but radius visualization matters so players understand that coverage is positional, not based on buildings or ownership.
- Players can log out to reduce the required group, wait offline for the proposed real-time interval, stockpile essences across eligible rituals, or use low-investment characters. These follow from connected-player membership and an ordinary physical reward; do not add account tracking or unrequested penalties to counter them.
- All-player punishment enables griefing because one player can deliberately trigger it when everyone is gathered. The required deliberate plaque action, thematic warning, and visible five-second countdown make initiation apparent; leaving the circle before commitment cancels the attempt. There is no separate individual readiness-confirmation step.
- The ritual's steady-state cost is tiny: one stored deadline and an idle coordinator. Roster checks and effect messages occur only during attempts; chest access uses cached positions and local arithmetic.
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
