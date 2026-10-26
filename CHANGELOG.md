# Changelog

## 0.11.2

- Publish under the separate `EinherjarChest` package identity. Use a spoiler-light mod page and move the full player instructions into a linked guide.
- Replace raw coordinates in Dimmed Essence tooltips and dropped-item hover with player-relative compass direction, distance and height guidance. Preserve the original site binding and rebuilding range.
- Clarify that Oden brings the chosen warrior from the tenth realm to Valhalla, then sends them back to finish their saga, leaving an echo of their essence as a gift. Update Hugin, ritual messages, item descriptions and player instructions.
- Damage destruction now returns one Dimmed Einherjar Essence, preserving the same original world/site and 100-metre rebuilding area as hammer recovery. Legacy chests also recover their Essence after damage destruction.
- Keep refunds restricted to actual owner-side destruction, deduplicate repeated resource drops, and retain no-refund behavior for new free/admin chests and explicitly blocked drops. Update Hugin and chest descriptions to explain damage recovery.

## 0.11.1

- Match Dimmed Essence's rebuilding radius to the chest access radius, currently 100 metres. Placement checks and tooltips share the same radius; the original site binding remains unchanged.
- Add persistent per-character Hugin guidance for rituals, essence, chests, shared supplies, stations, coverage and Oden's wrath. The essence lesson explains Dimmed Essence recovery and its original-site rebuilding limit.

## 0.11.0

- Hammer deconstruction returns one purple, nonstackable Dimmed Einherjar Essence. It can rebuild a chest within 20 metres of its original site in the same world; repeated rebuilding preserves that original site. Damage destruction returns nothing.
- Prefer a valid carried Dimmed Essence when building, falling back to regular carried Essence. The hammer shows the selected ingredient. Invalid placement consumes neither item.
- Show bound X/Y/Z coordinates, rebuilding radius and current location validity in Dimmed Essence tooltips. Binding travels with the item through ordinary trades, drops and saves.
- Exclude both Essence types from shared storage and apply the existing server portal setting to both. Existing chests bind to their current site on recovery; newly placed free/admin chests cannot create refunds.

## 0.10.3

- Add `Server / AllowEssenceThroughPortals` (default `false`) to allow carried Einherjar Essence through portals. The host/server's setting synchronizes to clients; other portal restrictions still apply.
- Update essence portal eligibility and its description to follow the setting, including for already carried essence.

## 0.10.2

- Show `Oden honors <Player>'s sacrifice...` on every participant's countdown overlay for six seconds after essence creation, including the sacrificed player. The announcement survives death messages.

## 0.10.1

- Fix repeated null-reference exceptions when the chest radius preview encounters a destroyed hover object or preview object. These exceptions could interrupt inventory refreshes and window input for the affected frames.

## 0.10.0

- Replace Thor's Kist with Einherjar Chest, a new implementation with one shared inventory per world.
- Earn Einherjar Essence through a five-second ritual at the original sacrificial stones. All connected players must gather; Oden chooses one sacrifice and drops one essence. Avarice can bring punishment to the whole gathering.
- Build an original Einherjar Chest using one carried essence. Shared transfers, crafting, building and machine loading require a loaded chest within 100 metres. Shared stock can be browsed anywhere.
- Add original chest and essence models, matching icons, a centered inventory window, stack transfers and machine input shortcuts.
- Keep essence out of portals and shared storage. Chest destruction never refunds essence or deletes shared stock.
- Persist world stock and ritual state separately, with backups. Improve remote-client initialization and add correlated ritual diagnostics.
- Exclude the Testing menu, fault injection and operator endpoints from production packages.
- This is a breaking replacement: remove the old Thor's Kist plugin on every peer. Existing hirds, configuration and inventory data are not imported. There is no automatic data migration.

Earlier Thor's Kist releases remain in the repository history and the existing Hexium listing.
