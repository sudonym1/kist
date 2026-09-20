# Thor's Kist

A shared global storage vault for your hird.

Alt-click anything in your inventory and it is gone — into the Kist, where every
member of your hird can reach it. Press `F3` to open the Kist, take things back
out, and manage who belongs. Crafting stations draw on it too, so you stop
running home for two nails.

A Kist belongs to a **hird**, never to one person. A hird needs at least two
members before its Kist opens at all.

## What it does

| | |
|---|---|
| **Deposit** | Hold `Left Alt` and **left-click** an item in your inventory. It leaves with the game's own loot-spawn sound and a burst of light. |
| **Open** | `F3`. Two tabs: **Kist** (the contents) and **Hird** (who is in it). The search box takes focus immediately — just start typing. |
| **Take** | Buttons on every row: `1`, `10`, `All`, with the usual pickup sound. Items with no room in your pack drop at your feet rather than vanishing. |
| **Craft** | Every crafting station, the build hammer and repairs can spend materials straight out of the Kist. |

## Hirds

The founder of a hird manages it: invite, remove, move the Kist, disband. Anyone
online who is not already in a hird shows up in the invite list — no typing
names, no player IDs.

The Kist has a location — the spot the hird was founded, which the founder can
move at any time. Every radius below is measured from there.

## Configuration

`BepInEx/config/posixone_ThorsKist.cfg`

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `modEnable` | `true` | Master switch. |
| Keys | `depositModifier` | `LeftAlt` | Hold this and left-click to deposit. |
| Keys | `openKist` | `F3` | Opens and closes the Kist window. |
| Range | `unlimitedRange` | `false` | Ignore every radius; the Kist reaches the whole world. |
| Range | `depositRadius` | `100` | Metres from the Kist you may deposit within. |
| Range | `takeRadius` | `100` | Metres from the Kist you may take within. |
| Range | `craftRadius` | `100` | Metres from the Kist crafting stations may draw within. |
| Crafting | `craftFromKist` | `true` | Turn crafting from the Kist off entirely. |
| Vault | `kistRows` | `64` | Rows of 8 slots. 64 rows = 512 stacks. **Server-side.** |
| Feedback | `depositSound` | `true` | Sound on deposit. |
| Feedback | `depositVfx` | `true` | Effect on deposit. |
| Feedback | `depositSfxPrefab` | `sfx_lootspawn` | Any sound prefab in ZNetScene. |
| Feedback | `depositVfxPrefab` | `vfx_Potion_health_medium` | Any effect prefab in ZNetScene. |
| Feedback | `takeSound` | `true` | Pickup sound when an item arrives from the Kist. |
| Interface | `uiScale` | `1.0` | Scales the window's text and rows. Below 1 fits more on screen. |
| Interface | `scrollRowsPerNotch` | `5` | Rows moved per notch of the mouse wheel. |
| Server | `minHirdSize` | `2` | Members needed before a Kist unlocks. **Server-side.** |

Settings marked **server-side** are read from the host's config only. Everything
else is per-player.

## Installing

Needs BepInEx. Drop `ThorsKist.dll` into `BepInEx/plugins/`.

**Install it on the server as well as on every client.** The server owns the
vault; a client with the mod talking to a server without it will simply be told
there is no hird.

Vaults are stored per world in `BepInEx/config/ThorsKist/kist_<worldUID>.dat`
on the host, written atomically and never in the world save itself — a rollback
of the world does not roll back the Kist.

## Building

```
dotnet build ThorsKist.csproj -c Release
dotnet run --project tools/VerifyPatches -- bin/Release/net472/ThorsKist.dll lib
```

The verifier resolves every Harmony patch target against the vendored Valheim
assembly. A green build does not prove a patch will bind; that does.
