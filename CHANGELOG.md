# Changelog

## 0.10.1

- Fix repeated null-reference exceptions when the chest radius preview encounters a destroyed hover object or preview object. These exceptions could interrupt inventory refreshes and window input for the affected frames.

## 0.10.0

- Replace Thor's Kist with Einherjar Chest, a new implementation with one shared inventory per world.
- Earn Einherjar Essence through a five-second ritual at the original sacrificial stones. All connected players must gather; Odin chooses one sacrifice and drops one essence. Avarice can bring punishment to the whole gathering.
- Build an original Einherjar Chest using one carried essence. Shared transfers, crafting, building and machine loading require a loaded chest within 100 metres. Shared stock can be browsed anywhere.
- Add original chest and essence models, matching icons, a centered inventory window, stack transfers and machine input shortcuts.
- Keep essence out of portals and shared storage. Chest destruction never refunds essence or deletes shared stock.
- Persist world stock and ritual state separately, with backups. Improve remote-client initialization and add correlated ritual diagnostics.
- Exclude the Testing menu, fault injection and operator endpoints from production packages.
- This is a breaking replacement: remove the old Thor's Kist plugin on every peer. Existing hirds, configuration and inventory data are not imported. There is no automatic data migration.

Earlier Thor's Kist releases remain in the repository history and the existing Hexium listing.
