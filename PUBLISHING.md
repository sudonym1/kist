# Publishing Einherjar Chest on Hexium

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

Review [CHANGELOG.md](CHANGELOG.md), player instructions, and your multiplayer
acceptance results. Select the intended team/listing in Hexium and upload the
versioned ZIP printed by `just build`, preserving the identity in its manifest.
Set installation location to Client & Server. Install the same version on every
peer, following [README.md](README.md).

Never upload `dist/EinherjarChest-Testing.zip`. That opt-in development package
contains the Testing code and is not the production release. Automated checks
do not certify separate dedicated-server deployment, real adverse-network
behavior, or every multiplayer gameplay case; see [TESTING.md](TESTING.md).
