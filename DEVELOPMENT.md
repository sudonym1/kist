# Developing Einherjar Chest

Use PowerShell 7 (`pwsh`), [just](https://github.com/casey/just), a stable .NET 8 SDK,
Valheim, and an extracted clean BepInEx 5 pack. The mod targets `netstandard2.1`;
the command-line test programs target .NET 8. Game assemblies are local build
references and are never distributed.

For the spoiler-light mod page, see [README.md](README.md). Full player
instructions, configuration and compatibility details are in [GUIDE.md](GUIDE.md)
(contains gameplay spoilers).

## Daily commands

```powershell
just create-isolated-environment
just build
just run
just clean
just clean-all
```

| Command | Behavior |
| --- | --- |
| `create-isolated-environment` | Create `.work/isolated`, copy the game and clean loader, and restore the preserved test characters, worlds, and inventories. Reuse an existing environment without overwriting progress. |
| `build` | Create the environment if needed, run regression/protocol/fault/game-hook checks, package the production mod, and build the save-isolation helper. Print the absolute Hexium ZIP path last. |
| `run` | Build and test, install the production mod and isolation helper, and launch the guarded isolated game. Start Steam first. |
| `clean` | Remove every project's `bin` and `obj` directories and `dist`, preserving the environment and its saves. |
| `clean-all` | Also remove `.work/isolated`, including its current saves and configuration. Preserve the separate restore snapshot. |

The justfile invokes `pwsh.exe`. The default command, `just`, runs `build`. Close the isolated game before
updating, relaunching, or deleting it; the tooling refuses to stop it for you.

## Local inputs

The default inputs are the Steam installation at
`C:\Program Files (x86)\Steam\steamapps\common\Valheim` and Gale's cached clean
BepInEx pack at
`%APPDATA%\com.kesomannen.gale\cache\denikson-BepInExPack_Valheim\5.4.2351`.
Override them before running `just`:

```powershell
$env:VALHEIM_DIR = 'D:\SteamLibrary\steamapps\common\Valheim'
$env:BEPINEX_PACK = 'D:\Downloads\BepInExPack_Valheim'
$env:DOTNET = 'D:\dotnet\dotnet.exe' # Optional when a compatible SDK is detected.
just build
```

The pack root must contain `BepInEx/core`, `winhttp.dll`, `doorstop_config.ini`,
and `.doorstop_version`. The SDK resolver checks PATH, `DOTNET_ROOT`,
`DOTNET_ROOT_X64`, local and main-checkout `.tools/dotnet` and `.work/dotnet`, and
standard Windows locations. `DOTNET` selects an explicit executable for the justfile workflow.
Source game files, existing mod profiles, and normal game saves are not modified.
Recreate the isolated environment to pick up a new game or loader version.

## Preserved test data

The shared restore snapshot lives in the **main checkout's** `.work/test-data`.
All worktrees find it through Git's common directory. It contains the preserved
`testchar`, `TESTWORLD`, their existing Einherjar hoard and ritual state, and the
other local test saves/configuration from the retired manual environment.

Every new environment validates the snapshot's SHA-256 manifest and copies its
`saves/` and `game/BepInEx/config/` files, verifying the restored hashes. Existing
environments are left intact. Missing or corrupt snapshots stop creation rather
than silently starting with empty characters or inventories.

For another machine, copy the entire snapshot to a local directory and set
`$env:TEST_DATA_DIR` to it. Its `manifest.json` uses `Schema: 1` and a `Files` array
of relative `Path` and `SHA256` records. Only `saves/` and `game/BepInEx/config/`
paths are restored. These are private local files, ignored by Git and excluded
from release archives. The original manual environment's saves, configuration,
and backups are also retained under the main checkout's
`.work/preserved-test-data/SharedInventory-Test-20261023`.

`clean-all` discards changes made since environment creation; recreation restores
the preserved snapshot. To retain newer progress, stop the game and back up both
`.work/isolated/saves` and `.work/isolated/game/BepInEx/config/SharedInventory`
before cleaning. Character/world saves, hoards, and ritual journals must stay
together; the world UID selects the matching hoard and ritual files.

## Build outputs and tests

The production DLL is `bin/Release/netstandard2.1/SharedInventory.dll`.
The upload ZIP is `dist/<manifest.name>-<manifest.version_number>.zip`; use the
absolute path printed at the end of `just build`. The Testing menu and fault
controls are excluded at compile time, and build checks verify that absence.
The isolation helper is built separately and never goes in the Hexium package.
See [PUBLISHING.md](PUBLISHING.md) for release contents and upload instructions.

`build.ps1` remains the underlying build entry point for explicit game/BepInEx
paths and opt-in testing builds. [TESTING.md](TESTING.md) covers the optional
harness, load runner, and real-engine acceptance. [API.md](API.md) describes data
and protocol semantics; [assets/README.md](assets/README.md) documents asset
sources. Historical handoffs, old results, and one-off migration records live in
[old_docs/](old_docs/README.md) and are not current setup instructions.
