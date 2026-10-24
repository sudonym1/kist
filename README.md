# Einherjar Chest

A small BepInEx 5 mod for a world-wide shared item inventory, viewable anywhere with transfers available near an Einherjar Chest. Install the same DLL on the server and every client, including the host.

Previously named Shared Inventory. The DLL name (`SharedInventory.dll`), plugin ID (`local.sharedinventory`), configuration paths, save paths, and count RPC identifiers retain their existing names for compatibility. Chest content and access coverage are implemented; the ritual described in [the gameplay handoff](EINHERJAR-HANDOFF.md) is still pending.

The hammer can build an **Einherjar Chest** from exactly **one carried Einherjar Essence**, with no other ingredients or workbench requirement. Picking up essence discovers its recipe. The chest has an original Nordic reliquary model and opens the existing shared inventory. Essence has an original rune-marked crystal model and cannot be deposited, withdrawn, spent from shared counts, or taken through portals, including unrestricted portals/resource settings. Legacy shared essence entries remain saved but unavailable. Removing or destroying a chest never refunds its essence or changes shared stock. Vanilla no-cost/admin building remains an administrative override. The ritual that earns essence will arrive in a later phase; administrators can supply essence through the game's console meanwhile. See [asset sources and capture instructions](assets/README.md).

Shared transfers and spending require a living player within **100 metres of a loaded Einherjar Chest**, measured as an inclusive three-dimensional sphere from the chest's floor pivot. Viewing and searching shared stock require no chest, including on first loading into a world outside coverage. The radius is fixed; there is no chest-access setting or server access registry. If a chest is unloaded on your client, it grants no transfer access, even if its saved world record is within 100 metres. Lower client simulation/loading-distance settings may therefore reduce the usable range. This is intended. Any old `SharedChestRadiusMeters` entry is unused.

Placement and inspection show the 100-metre maximum with three amber circles outlining the sphere. F3 opens the inventory anywhere; withdrawal buttons are disabled outside coverage. Alt-click, crafting, upgrades, building, machine inputs/fuel, and shortcut counts/consumption require a loaded chest within range for their shared portion. Carried items work outside coverage. Machine loading checks the player's position. Overlapping chests provide the same single inventory. Unloading or destroying a chest removes its coverage; reloading a valid chest restores it. Destroying the last chest leaves stock saved for a future chest.

Chest access is checked entirely on the client by iterating only loaded Einherjar Chest components: **O(number of loaded Einherjar chests)**, not all game entities. Load/unload callbacks maintain that small set; there are no world scans, access RPCs, spatial indexes, or access caches. The shared inventory database still runs on the server.

Losing range or the last loaded covering chest blocks new shared transfers and spending. Existing transfers finish or return unused grants, including after death. The shared inventory window keeps loading and refreshing stock outside coverage; polling for other menus and station controls pauses there while required reconciliation continues. Denied transfers show "There is no Einherjar Chest in range." This retains the mod's trusted-client scope. Use the same compatible version on every peer.

Use synthetic stock for isolated testing; see [the Windows setup instructions](WINDOWS-CODEX-SETUP.md).

Press **F3** to open the searchable shared inventory window. Press **F3** again or **Escape** to close it; closing with Escape does not open the pause menu. **Alt-click** an item in your normal inventory to deposit that entire stack. The withdrawal buttons take **1** or **one normal stack**. Nonstackable items can only be withdrawn one at a time.

The window stays centered and uses a charcoal, aged-gold, and amber reliquary theme. Its default size is 675 x 810 pixels with enlarged text, controls, and item icons. Shorter screens reduce the scrolling list's height before reducing the UI scale. The old `Client / WindowPosition` setting is ignored; the window is no longer draggable.

Craft, upgrade, and build directly from shared stock through the normal game interfaces. Carried ingredients are consumed first; any shortfall comes from the cached shared inventory without occupying backpack slots. Recipe discovery, stations, ingredient quality, output capacity, placement rules, and game resource-cost settings still apply. Only the local player's resource paths see shared items; ordinary inventories and containers do not.

Load processing inputs and fuel from shared stock into Smelter-based machines, cooking stations, fermenters, and refillable fireplaces using their normal interactions. The station searches all accepted carried inputs before trying shared stock in its configured conversion order. Shared inputs do not require backpack space. Click/hold behavior, capacity, access, fire, roof, and item compatibility checks remain the game's. Loading completes immediately with the same optimistic withdrawal behavior as crafting. Station owner RPCs retain their normal multiplayer capacity races.

While looking at a supported station's input or fuel control, its hover text lists **Alt+1** through **Alt+9**, then **Alt+0**, with each accepted item's name and total carried + shared count. Press the displayed combination to load one of that exact item, using carried stock first. Number-pad digits work too. Each new hover builds an alphabetical list by localized item name, with prefab ID breaking name ties and zero-stock items omitted. Bindings then stay fixed while that input control remains targeted: a depleted item stays in its slot with (0), restocking updates its count, and newly available item types wait until the next hover. Looking away and returning rebuilds the list. Input focus changes alone do not rebind keys. Lists longer than ten items use pages, selected with **Alt+PageUp / PageDown**. A press is bound to the displayed prefab and revalidated, so a stock change cannot silently load a different item. Holding a shortcut does not repeat. Alt-number loading takes priority over normal hotbar use only while targeting a supported station and gameplay input is active. Menus and text entry do not load items. Existing refresh rules apply; displaying counts and selecting shortcuts send no additional list requests.

The **charcoal kiln** rejects fine wood from both carried and shared stock, including explicit item use. Normal loading prefers regular wood from either source before other accepted wood types; Alt-number shortcuts can explicitly choose another allowed wood. Fine wood remains available for crafting, building, and other machines. This policy is scoped to the kiln's processing input. Fireplace fireworks keep their existing behavior.

Actions complete immediately, with one exact batch withdrawal sent afterward for their shared ingredients. The cache is deducted immediately so repeated actions do not reuse the same displayed stock. The server either deducts the whole batch or rejects it. If stock changed and the batch fails, the completed action stays, its entire shared portion goes unpaid, and the client refreshes. This intentionally allows occasional overspend between players; server counts never become negative.

The shared inventory stores one count and highest upgrade quality per prefab: ten iron swords remain one entry of ten. Depositing a quality 4 sword into ten quality 1 swords produces eleven quality 4 swords. Lower-quality deposits never reduce that quality. Every withdrawal uses the shared quality, with full durability for that level. When stock reaches zero its quality is forgotten. Other metadata (variant, crafter, world level, custom data) is still discarded. The list shows quality above level 1.

The client checks room before withdrawing. If inventory capacity changes during the request, anything that could not actually be inserted is sent back through `Deposit`. A stack action requests up to one normal stack, limited by available stock; if that amount does not fit initially, the action makes no request.

## Server and threads

The server stores a `prefab ID -> (Int64 count, Int32 quality)` database. Clients are trusted; gameplay checks run locally. The RPC admission layer additionally rejects all essence mutations, preserving legacy shared essence entries. The only requests are [`List`, `Deposit`, and `Withdrawal`](API.md).

The mod uses Valheim's existing reliable RPC transport. A dedicated worker constructs and parses packages, serializes database requests from all clients, and reads/writes saves. Valheim's packet dispatch stays on Unity's main thread, along with local inventory changes and UI rendering. Main-thread completions are pumped in Update and LateUpdate with a one-millisecond budget checked between actions; gameplay never waits for the database worker.

The host's player uses the same RPCs and worker queue as remote players. There is no direct database shortcut for that player. There are no subscriptions or server-side transfer sessions. The client refreshes the full list every two seconds while the shared window, crafting/inventory UI, or hammer selection menu is open, or while looking at a supported station input/fuel control within interaction range. These triggers share one timer, including when switching between targets or menus. Looking away stops polling when no menu is open; output controls and unrelated objects do not start it. Merely holding the hammer or placing pieces with menus closed does not refresh. Opening the window and failed withdrawals also request a list. Successful transfer replies update only the affected cached counts. A missing refresh expires after ten seconds. Read-only List retries wait 2, 4, 8, 16, then at most 30 seconds; successful refreshes reset the backoff. Cached stock stays visible during recovery, and late replies from expired attempts are ignored. Item mutations are never automatically replayed or restored. Incremental synchronization is deferred.

## Build

Requires a .NET 8 SDK, Valheim, and BepInEx 5. References come from your game installation and BepInEx profile; no game DLLs are distributed with this source.

From Windows PowerShell in this folder:

```powershell
.\build.ps1 -ValheimDir 'C:\Program Files (x86)\Steam\steamapps\common\Valheim' `
  -BepInExDir "$env:APPDATA\com.kesomannen.gale\valheim\profiles\My Valheim\BepInEx"
```

If BepInEx is inside your Valheim installation, omit `-BepInExDir`. The script runs regression tests, builds the mod, and packages `dist/EinherjarChest.zip`. Copy the DLL to `BepInEx/plugins/SharedInventory/` on the server and all clients. No other DLLs from the build references should be copied.

The build and load-test scripts automatically select a stable .NET 8 SDK, checking PATH, `DOTNET_ROOT` / `DOTNET_ROOT_X64`, repository-local `.tools/dotnet` and `.work/dotnet` installations, and standard Windows installation locations. They print the selected SDK and path. Use `-Dotnet '<SDK installation>\dotnet.exe'` to select another installation. If none is compatible, they stop with setup instructions. The repository's `global.json` permits newer .NET 8 feature bands and patches; the shipped mod still targets `netstandard2.1`.

From Linux/WSL with a Linux .NET SDK, run:

```sh
dotnet run --project tests/SharedInventory.Tests.csproj -c Release
dotnet build SharedInventory.csproj -c Release \
  -p:ValheimDir='/mnt/c/Program Files (x86)/Steam/steamapps/common/Valheim' \
  -p:BepInExDir='/mnt/c/Users/seamu/AppData/Roaming/com.kesomannen.gale/valheim/profiles/My Valheim/BepInEx'
```

The mod output is `bin/Release/netstandard2.1/SharedInventory.dll`. The toggle key is configurable in `BepInEx/config/local.sharedinventory.cfg`.

## Saves and limits

Old count-only saves load with quality 1; previously discarded upgrades cannot be recovered. New saves store `{"Count":10,"Quality":4}` per prefab. All clients and the server must update together to the v3 RPC protocol in version 0.2.0. The save format is unchanged.

The server saves each world's counts and qualities to `BepInEx/config/SharedInventory/<world UID>.json` every two seconds while dirty and when the session stops. Saving uses a temporary file and atomic replacement, retaining the previous file as `.bak`. A corrupt save causes requests to fail instead of replacing the inventory with an empty one.

This deliberately simple protocol has no durable transfer receipts or automatic retries. An abrupt process failure can lose the most recent unsaved counts, and a connection lost during a transfer can leave the client and database out of sync. Character saves and shared inventory saves are independent.

## Validation

Loaded-only chest access evidence and reproduction are in [the updated phase 3 results](EINHERJAR-PHASE3-RESULTS.md). Isolated graphical/headless acceptance covers load/unload/reload, destruction, live resource gating, fixed 3D boundaries, and the placement preview. Full host-plus-remote deployment remains phase 6.

Opt-in load and poor-network testing is documented in [TESTING.md](TESTING.md). Normal builds exclude the harness entirely; test builds also require explicit runtime activation, and impairments default off. To stop current faults, use **F3 → Testing → Disable faults and release queued packets**. To disable the harness on future launches, exit and set both `Enabled = false` and `Impairments = false` in the config's `[Testing]` section. See [complete shutdown and normal-build installation instructions](TESTING.md#disable-fault-injection-and-testing), including the isolated launcher's staged DLL update. The standalone runner uses temporary inventories and includes burst, contention, outage, lost-confirmation, and slow-storage presets.

`tests/` exercises the production count database, client transfer logic, item normalization, and background worker using small game/transport doubles. It covers concurrent-style overdraw, counts beyond Int32, absence of server gameplay caps, incompatible inventory stacks, capacity races, partial insertions, ordinary-deposit returns, death during withdrawal, rejected deposits, and worker thread/order/shutdown behavior.

The Windows production and testing builds pass 117 regression tests, including client refresh recovery under a virtual clock, count/quality reconciliation, optimistic crafting deductions, window input, and station shortcuts. `tools/ProtocolTests` checks production RPC handlers using Valheim's actual `ZPackage`, including success/error round trips, competing batches, main-thread routing, and persistence; it also runs eleven deterministic harness checks. Build checks verify game hook signatures and harness inclusion/exclusion. All seven out-of-game load presets passed; the intentional lost-deposit-confirmation case retains its uncertain transfer. An isolated host acceptance run confirmed automatic and manual recovery after discarded ListReplies, rejection of a late held reply after a withdrawal, and recovery from outage-delayed delivery. Final cached counts and qualities matched all 385 persisted entries, with no pending operations. See [PLAN.md](PLAN.md) for evidence and remaining validation, including crafting-failure overlap and remote multiplayer.

## Isolated test building

The test-only isolation helper enables Valheim's no-placement-cost mode for the local character, unlocking all build pieces and removing resource/station costs. It operates only in the marked isolated game launched with `Launch-Test.cmd`. The production mod does not enable this mode. Set `Testing / FreeBuild = false` in `BepInEx/config/local.sharedinventory.testisolation.cfg` before testing real crafting/building resource consumption. This disables free building while retaining save isolation.
