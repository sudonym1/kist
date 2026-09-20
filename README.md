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
| **Find** | The Kist is pinned on every member's map with a chest icon. The pin cannot be deleted by hand — it moves when the Kist moves and vanishes when you leave the hird. |

## Hirds

The founder of a hird manages it: rename it, invite, remove, move the Kist,
disband. Renaming is free and safe at any time — the name is only a label, and
membership, the vault and the map pin all key off the hird's id. Anyone
online who is not already in a hird shows up in the invite list — no typing
names, no player IDs.

**Moving the Kist is deliberately hard.** Re-anchoring costs 25 surtling cores
from the founder's own pack, and every member of the hird who is online must be
standing within `anchorRadius` of the new spot. Offline members cannot be asked
to attend, so they are not counted — but at least `minHirdSize` members must be
online, which is what stops one person relocating the vault alone. Founding a
hird is free; only moving it costs.

**There is no limit on hird size.** The practical ceiling is however many
players your server allows, and members count whether they are online or not.

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
| Range | `anchorRadius` | `100` | Every online member must be this close before the Kist can be re-anchored. **Server-side.** |
| Crafting | `craftFromKist` | `true` | Turn crafting from the Kist off entirely. |
| Vault | `kistRows` | `64` | Rows of 8 slots. 64 rows = 512 stacks. **Server-side.** |
| Feedback | `depositSound` | `true` | Sound on deposit. |
| Feedback | `depositVfx` | `true` | Effect on deposit. |
| Feedback | `depositSfxPrefab` | `sfx_lootspawn` | Any sound prefab in ZNetScene. |
| Feedback | `depositVfxPrefab` | `vfx_Potion_eitr_minor` | Any effect prefab. Eitr is blue; `_health_` red, `_stamina_` yellow. |
| Feedback | `depositVolume` | `1.5` | Multiplies the deposit sound. |
| Feedback | `takeSound` | `true` | Pickup sound when an item arrives from the Kist. |
| Feedback | `takeVolume` | `4.0` | Multiplies the pickup sound. |
| Feedback | `anchorEffects` | `true` | The slam when a Kist is planted or re-anchored. |
| Feedback | `anchorSfxPrefab` | `sfx_frozenking_doubleslam_explosion` | Sound of the landing. |
| Feedback | `anchorVfxPrefab` | `vfx_trollsnow_groundslam` | Impact effect at the landing point. |
| Feedback | `anchorFallVfxPrefab` | `vfx_Potion_eitr_minor` | What falls out of the sky. |
| Feedback | `anchorVolume` | `1.5` | Multiplies the slam. |
| Feedback | `anchorDropHeight` | `12` | Metres the Kist falls from. |
| Feedback | `anchorDropSeconds` | `0.45` | How long the fall takes. |
| Interface | `mapPin` | `true` | Mark the Kist on your map. |
| Interface | `mapPinPrefab` | `piece_chest_wood` | Build piece whose icon the pin borrows. |
| Interface | `uiScale` | `1.0` | Scales the window's text and rows. Below 1 fits more on screen. |
| Interface | `scrollRowsPerNotch` | `5` | Rows moved per notch of the mouse wheel. |
| Server | `minHirdSize` | `2` | Members needed before a Kist unlocks. **Server-side.** |
| Server | `anchorCostPrefab` | `SurtlingCore` | Item spent to re-anchor. A prefab name. Empty means free. **Server-side.** |
| Server | `anchorCostAmount` | `25` | How many of it. **Server-side.** |

Settings marked **server-side** are read from the host's config only. Everything
else is per-player.

Every prefab name above is a real entry in the game's own asset manifest. The
full list of every prefab name the game ships — 43,346 of them — is in
`notes/prefab-names.txt` if you want to try something else. No rebuild needed;
they are read from config.

## Installing

Needs BepInEx. Drop `ThorsKist.dll` into `BepInEx/plugins/`.

**Install it on the server as well as on every client.** The server owns the
vault; a client with the mod talking to a server without it will simply be told
there is no hird.

## Where the vaults live

One file per world, in the server's **save directory** — the path it was started
with via `-savedir`:

```
<savedir>/ThorsKist/kist_<worldUID>.dat
```

Not `BepInEx/config`. Server managers rebuild the mod tree on update — PhValheim
runs `rm -rf BepInEx/config/*` on every mod sync — so a vault kept there would be
one world update away from gone. The save directory is the one place a host has
to preserve, because the world is in it. A vault written by an older build is
moved across automatically on first load.

It is still outside the world save itself, so rolling the world back to an older
`.db` does not roll the Kist back with it — and Valheim's own backup rotation
does not cover it. Back up `<savedir>/ThorsKist/` alongside your worlds.

## Building

```
dotnet build ThorsKist.csproj -c Release
dotnet run --project tools/VerifyPatches -- bin/Release/net472/ThorsKist.dll lib
```

The verifier resolves every Harmony patch target against the vendored Valheim
assembly. A green build does not prove a patch will bind; that does.
