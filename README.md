# Einherjar Chest

In the tenth realm, earn a shared hoard through Oden's ritual. His lightning brings one chosen warrior before him in Valhalla, then he sends them back to finish their saga. An echo of their essence remains in the tenth realm as his gift to the gathering. Carry it home to build an Einherjar Chest.

**Einherjar Chest replaces Thor's Kist starting with version 0.10.0.** Install the same version on the server and every client, including the host. The existing Hexium package identity remains `ThorsKist` so this release updates the original listing.

## Earn your first chest

1. Gather **every connected player** inside the original seven-stone sacrificial circle. Everyone must be alive and fully spawned.
2. Hover any original boss stone's reading or trophy surface and use **Begin Einherjar ritual**: **Shift+E** with default bindings. Normal reading, trophy placement and boss-power selection still work.
3. Stay inside for the **5... 4... 3... 2... 1...** countdown. Leaving, dying, joining or disconnecting cancels it.
4. Oden chooses one player at random and kills them with lightning, bringing the warrior before him in Valhalla. Their saga is unfinished, so he sends them back to the tenth realm: this is why the warrior respawns. An echo of their essence remains as his gift to the gathering: **one Einherjar Essence** at their death position. Everyone sees `Oden honors <Player> in Valhalla. Their saga continues in the tenth realm.` after the essence is created. Solo rituals work. Normal death, tombstone and respawn rules still apply.
5. Pick up the essence to discover the hammer recipe. Build an **Einherjar Chest** for exactly **one carried essence**, with no other ingredients or workbench required.

Oden punishes avarice. Another gathering can bring lightning to every participant and yield no essence. The stone's warning deserves your attention.

Hammer deconstruction or damage destruction returns one purple **Dimmed Einherjar Essence**. It rebuilds a chest within **the chest access radius (currently 100 metres) of the original chest position**, measured in three dimensions, in the same world. Rebuilding preserves that original site, so repeated moves cannot extend its range. Its tooltip shows bound X/Y/Z coordinates and whether your current position is in range; placement checks the actual proposed chest position. Dimmed Essence can be traded and stored in ordinary containers, and does not stack. Building uses a valid carried Dimmed Essence first, otherwise one regular Essence. Newly built free/admin chests yield no refund. Existing chests use their current site when first recovered.

Neither Essence type can enter shared storage. By default they also cannot pass through portals, including unrestricted portals; the host/server can enable portal travel for both with **Server / AllowEssenceThroughPortals**. The lightning does not damage nearby structures.

Hugin offers guidance the first time your character loads with the mod, directing you to the sacrificial stones. Further lessons explain essence, chests, shared supplies, stations and chest coverage as you encounter them. Every player killed by Oden's punishment receives its explanation after respawning. Enable **Tutorials** in Valheim's settings to see these ravens. Talking to Hugin saves each lesson as seen for that character across worlds and adds it to the native text collection; unacknowledged lessons remain pending through normal saves and logout.

## Use shared stock

Every Einherjar Chest accesses the same world inventory. Transfers and shared spending require a living player within **100 metres of a chest loaded on their client**. The distance is a three-dimensional sphere; placement and inspection display its boundary. Lower loading-distance settings can reduce usable coverage. The radius is fixed.

| Action | Control |
| --- | --- |
| Browse and search shared stock anywhere | **F3** |
| Close the window | **F3** or **Escape** |
| Deposit an entire carried stack in range | **Alt-click** it in your inventory |
| Withdraw in range | Choose **1** or **one stack** |
| Craft, upgrade or build | Use the normal interface; carried ingredients are used first |
| Load machine inputs or fuel | Use the machine normally; carried inputs are preferred |
| Load a specific displayed machine input | **Alt+1** through **Alt+9**, then **Alt+0** |
| Change machine input shortcut page | **Alt+PageUp / PageDown** |

Smelter-based machines, cooking stations, fermenters and refillable fireplaces can use shared stock. Shortcuts appear in the targeted input/fuel control's hover text and stay bound to the displayed item while you keep targeting it. The charcoal kiln rejects fine wood from both carried and shared stock.

Outside chest coverage, carried items still work and F3 still shows shared stock. Multiple chests provide access to the same inventory. Destroying the last chest leaves its stock saved for the next chest you build.

## Installation and replacing Thor's Kist

Requires **Valheim and BepInEx 5**. Install on **both the server and every client**. No Jotunn or SQLite runtime is required.

1. Close the game/server and back up your world, characters and mod saves.
2. Uninstall the old Thor's Kist package on every peer. For a manual install, remove its plugin directory and any duplicate `ThorsKist.dll`, `ThorsKist.Sync.dll` or `ThorsKist.Sync.Sqlite.dll` from other plugin directories. Keep old saves separately if you need them.
3. Install this release through your mod manager, or copy `plugins/SharedInventory.dll` from the ZIP into `BepInEx/plugins/EinherjarChest/`.
4. Start every peer with the same version. Keep only one copy of `SharedInventory.dll` in the plugins tree. The startup log should show **Einherjar Chest 0.11.2**.

This is a new implementation with one shared inventory per world. Thor's Kist hirds, anchors, configuration and inventory data are not imported. **There is no automatic data migration.** Do any conversion manually while the server is stopped.

The plugin retains the ID `local.sharedinventory`, the DLL name `SharedInventory.dll` and its existing Shared Inventory save/config paths. The old Thor's Kist plugin has a different ID; the new plugin declares it incompatible to prevent simultaneous loading. Production builds contain **no Testing menu, fault injection or test operator endpoints**.

## Configuration and backups

Settings are created in `BepInEx/config/local.sharedinventory.cfg`:

- **Client / ToggleKey**: inventory shortcut, default `F3`.
- **Server / RitualAngerMinutes**: hidden world-wide ritual interval, default `30`. Must be finite and positive. Changes apply to future intervals. Players see no timer or remaining-time display.
- **Server / AllowEssenceThroughPortals**: default `false`. Set to `true` on the host/server to allow carried essence through portals. The value is synchronized to clients; other items and normal portal restrictions still apply. Edit the host/server configuration before starting the world.

The server stores stock in `BepInEx/config/SharedInventory/<worldUID>.json` and ritual state in `BepInEx/config/SharedInventory/ritual-<worldUID>.json`. Back up both with your world. Stock saves every two seconds while dirty and at normal shutdown; atomic replacement keeps a previous `.bak`. Ritual anger persists across restarts and counts real offline time. Corrupt ritual state disables initiation instead of silently resetting it.

## Inventory behavior and compatibility

Shared storage keeps a count and the **highest deposited upgrade quality** per item type. Depositing one quality-4 sword into ten quality-1 swords makes eleven quality-4 swords. Withdrawals have full durability. When stock reaches zero its quality is forgotten. Variants, crafter names, custom data and other individual-item metadata are discarded. Consider this before depositing personalized or modded equipment.

Crafting and machine loading complete immediately. If another player spends the same stock before the server accepts the batch, the completed action stays and its shared cost can go unpaid; server counts never become negative. A lost transfer confirmation is uncertain and is never automatically replayed. Abrupt crashes can lose recent stock changes or a ritual reward. Character saves, shared stock and world items are separate saves.

Clients are trusted. This is intended for cooperative play; it does not enforce chest range or prevent cheating by modified clients. Other mods that replace crafting, inventory or machine interactions may conflict. Mixed client/server versions are unsupported. Tested against **Valheim 1.0.20** on Windows; separate dedicated-server and adverse-network acceptance remain outstanding.

## Source and credits

Source and build instructions: [github.com/sudonym1/kist](https://github.com/sudonym1/kist). See [PUBLISHING.md](https://github.com/sudonym1/kist/blob/main/PUBLISHING.md), [DEVELOPMENT.md](https://github.com/sudonym1/kist/blob/main/DEVELOPMENT.md) and [TESTING.md](https://github.com/sudonym1/kist/blob/main/TESTING.md) for release and developer details.

Original procedural chest/essence models and matching rendered icons are included in the source. Lightning and sound use the installed game's Obliterator presentation; extracted game assets are not shipped. Thor's Kist by Brian Miller inspired the original shared-vault idea. Earlier Thor's Kist code and notices remain in repository history.

MIT license. This is an unofficial Valheim mod and is not affiliated with Iron Gate Studio or Coffee Stain Publishing.
