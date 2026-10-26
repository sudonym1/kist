# Publishing Einherjar Chest on Hexium

Einherjar Chest is published under its own **`EinherjarChest`** package identity
under the **cmos** team. Earlier packages used `ThorsKist`; leave that listing
separate. Keep the existing version sequence, `local.sharedinventory` plugin ID,
`SharedInventory.dll` name and save/config paths for compatibility.

The README and manifest description are spoiler-light teasers. Full player
instructions live in [GUIDE.md](GUIDE.md), with a spoiler warning. Push the guide
to the repository's `main` branch before publishing so the mod page's absolute
links work. The bundled changelog contains detailed gameplay changes; keep those
out of the teaser description.

## Build the upload

From a configured Windows checkout, run:

```powershell
just build
```

See [DEVELOPMENT.md](DEVELOPMENT.md) for prerequisites, local paths, and test-data
setup. The final output line gives the absolute path to
`dist/<manifest.name>-<manifest.version_number>.zip`. The package identity and
version come directly from [manifest.json](manifest.json); use that versioned ZIP
for the intended listing. The build does not upload or publish anything.

Use the newly built archive; older `ThorsKist-*.zip` archives may still be present
in `dist/`.

The build runs regressions, protocol checks, deterministic fault-harness checks,
and game-hook checks. Production sets `EnableTestHarness=false`; the Testing
menu, fault injection, and test operator endpoints are excluded at compile time.
Assembly checks verify their absence even when a direct `build.ps1` caller skips
regression tests. Use the full `just build` checks for a release.

The release ZIP contains exactly:

```text
manifest.json
icon.png
README.md
CHANGELOG.md
LICENSE
plugins/SharedInventory.dll
```

The packager validates manifest identity/version against the DLL, the 256 x 256
icon, exact ZIP contents, and packaged file hashes. It writes a `.zip.sha256`
checksum beside the versioned ZIP. `dist/EinherjarChest.zip` is a convenience
copy. Neither archive includes the isolation helper, test saves, hoards, game
assemblies, archived docs, or development scripts.

## Upload

Review [CHANGELOG.md](CHANGELOG.md), the teaser README, chest icon, migration
notice and your multiplayer acceptance results. Verify the player-guide link
works. Sign in to Hexium under **cmos** and upload the versioned ZIP printed by
`just build` as the separate **cmos / EinherjarChest** package, preserving the
identity in its manifest. Set installation location to Client & Server. Choose
appropriate categories, such as Storage, Crafting, Pieces and User Interface.

Install the same version on every peer, following [README.md](README.md). Remove
the old `ThorsKist` package first, including earlier Einherjar Chest releases
distributed under that name; keep only one `SharedInventory.dll`. Preserve
existing Einherjar Chest saves and configuration. Original Thor's Kist plugins
also require removal, and their data is not automatically imported. See the
[installation and migration details](GUIDE.md#installation-and-replacing-thors-kist).

Never upload `dist/EinherjarChest-Testing.zip`. That opt-in development package
contains the Testing code and is not the production release. Automated checks
do not certify separate dedicated-server deployment, real adverse-network
behavior, or every multiplayer gameplay case; see [TESTING.md](TESTING.md).
