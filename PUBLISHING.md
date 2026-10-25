# Publishing Einherjar Chest on Hexium

Version **0.10.1** fixes the chest radius preview's handling of destroyed Unity objects. It updates the existing [cmos/ThorsKist listing](https://valheim.hexium.gg/mods/cmos/ThorsKist), continuing the version sequence used by the 0.10.0 Einherjar Chest replacement release.

The game plugin, README, description and icon use **Einherjar Chest**. The manifest's `ThorsKist` name deliberately preserves the existing registry identity. Upload under the **cmos** team to update that listing. Changing the manifest name to `EinherjarChest` would target a different package identity. A visible listing-name change, if supported by Hexium, must preserve the existing listing; the public API documents no rename operation. No listing rename or upload is performed by these scripts.

## Build the upload

```powershell
.\build.ps1 -ValheimDir '<Valheim installation>' -BepInExDir '<BepInEx folder>'
```

The script runs regressions, protocol checks, fault-harness checks and game-hook checks. It verifies the compiled production DLL excludes all test types and controls even when `-SkipTests` is used. It then creates and validates `dist/ThorsKist-0.10.1.zip` and writes its SHA-256 alongside it. `dist/EinherjarChest.zip` is an identical convenience copy.

The upload includes only these files:

```text
manifest.json
icon.png
README.md
CHANGELOG.md
LICENSE
plugins/SharedInventory.dll
```

The icon is the original chest render. No development handoffs, test plugins, private inventories, game assemblies or native SQLite runtime are included. Production keeps the existing `local.sharedinventory` plugin ID and `SharedInventory.dll` name.

## Manual upload

1. Sign in to Hexium and update the existing **cmos / ThorsKist** listing with the versioned ZIP. Confirm it is an update to that listing before publishing.
2. Set installation location to **Client & Server**. Review the README, new chest icon and breaking-change notice.
3. Review the current categories. The existing listing was tagged **Outdated** when checked; remove that tag if the site's controls permit and the release is ready for the current game. Appropriate categories include Storage, Crafting, Pieces and User Interface.
4. Publish after your final gameplay acceptance. The recorded checks cover automated behavior and real host/remote sacrifices and cancellation; dedicated-server deployment, full remote punishment and adverse-network acceptance are not certified by the package validator.
5. Install the same version on the host/server and every client. Remove old Thor's Kist plugins before launch; their plugin ID differs, so a file overwrite alone is insufficient. Follow the public README. Data conversion is manual and outside this release.

The package format follows [Hexium's packaging documentation](https://hexium.gg/packaging). BepInExPack_Valheim is assumed by Hexium, so the manifest has no redundant dependency entry.
