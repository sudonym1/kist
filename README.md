# Thor's Kist

### One chest. Every viking. Anywhere in the world.

Your iron is in a barrel nine thousand metres away. Your friend is in the Plains
with forty black metal scrap and nowhere to put it.

**Alt-click. It's in the Kist. Everyone has it. Get back to work.**

One shared vault for your whole **hird**. Deposit from anywhere, take from
anywhere, and every workbench, forge and hammer quietly reaches into it while
you build.

---

![The Kist][shot-kist]

*One window, one search box. Take `1`, `10` or `All` off any row.*

![The Hird][shot-hird]

*Who's in, who's out, and who is 116 metres too far away for you to move the
vault today.*

---

## What it does

| | |
|---|---|
| ⚒️ **Deposit** | Hold `Left Alt` and **left-click** anything in your inventory. It goes with a burst of blue light that everyone in the hird sees. |
| 📖 **Open** | `F3`. Two tabs: **Kist** (the loot) and **Hird** (the people). Just start typing to search. |
| 🗂️ **Sort** | **A-Z**, **Amount**, **Progress** or **Type**. Click the same one twice to flip it. |
| 🫴 **Take** | `1`, `10`, `All` on every row. No room in your pack? It lands at your feet instead of vanishing. |
| 🔨 **Craft** | Stations, the build hammer and repairs spend straight out of the Kist. Stop running home for two nails. |
| 🔥 **Feed** | Smelters, kilns, fireplaces, cooking stations and fermenters take their coal, ore and food from the Kist too. |
| 🗺️ **Find** | A chest pin on every member's map that can't be deleted by hand. It follows the Kist and disappears when you leave the hird. |

---

## Sorting

- **A-Z** — the boring one. It works.
- **Amount** — biggest hoards first, counted across the whole vault, so 300
  wood doesn't scatter down the list in 50s.
- **Progress** — **Meadows → Black Forest → Swamp → Mountain → Plains →
  Mistlands → Ashlands → Deep North.** Your vault in the order you earned it.
- **Type** — wood with wood, ore with ore, hides with hides.

Progress and Type print what they sorted on at the end of each row, so if
something looks misfiled you can see it and say so.

<details>
<summary><b>How does it know what's Mistlands?</b></summary>

It's read out of the game's own data, not a list typed from memory: which biome
the game plants each berry and ore deposit in, which biome the creatures that
drop a thing live in, and a walk over every recipe so `copper ore → bronze →
bronze sword` settles correctly.

That means **other mods' items get placed too**, without Thor's Kist knowing
anything about them. Anything still unplaced sorts last and says *unknown*,
rather than being given a number it didn't earn.

</details>

---

## Hirds

A Kist belongs to a hird, never to one person — and needs **two members** before
it opens at all. The founder can rename, invite, remove and disband. Anyone
online who isn't already in a hird shows up in the invite list; no typing names,
no player IDs. There's no limit on hird size.

> ### 🧍 Playing alone?
> Set **`minHirdSize = 1`** in the config, or your Kist will never unlock —
> you'll found a hird of one and it'll stay shut. On a single-player world
> that's your own config file; you're the host. It also frees you to move the
> Kist by yourself.

### Moving the Kist is meant to hurt

Founding a hird is free. *Moving* it costs **25 surtling cores** from the
founder's pack, with **every online member standing within 100m**. The button
greys out and tells you who's too far away or how many cores you're short.

Then it lands — a blue mass falls out of the sky and hits the ground with a
slam every member hears.

---

## Installing

Needs [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/).
Download the release ZIP and copy both DLLs from its `plugins/` directory
(`ThorsKist.dll` and `ThorsKist.Sync.dll`) into `BepInEx/plugins/`.

> **On the server too, not just the clients.** The server owns the vault. A
> client with the mod talking to a server without it is simply told there's no
> hird.

---

## Configuration

`BepInEx/config/posixone_ThorsKist.cfg`

Settings marked **server-side** are read from the host's config only; everything
else is yours alone. Every radius is measured from the Kist itself.

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `modEnable` | `true` | Master switch. |
| Performance | `enableProfiling` | `false` | Enables optional diagnostic-plugin profiling hooks and F4 captures. Restart after changing. |
| Keys | `depositModifier` | `LeftAlt` | Hold this and left-click to deposit. |
| Keys | `openKist` | `F3` | Opens and closes the Kist window. |
| Range | `unlimitedRange` | `false` | Ignore every radius; the Kist reaches the whole world. |
| Range | `depositRadius` | `100` | Metres you may deposit within. |
| Range | `takeRadius` | `100` | Metres you may take within. |
| Range | `craftRadius` | `100` | Metres crafting stations may draw within. |
| Range | `anchorRadius` | `100` | How close every online member must be to re-anchor. **Server-side.** |
| Crafting | `craftFromKist` | `true` | Turn crafting from the Kist off entirely. |
| Vault | `kistRows` | `64` | Rows of 8 slots. 64 rows = 512 stacks. **Server-side.** |
| Feedback | `depositSound` / `depositVfx` | `true` | Sound and effect on deposit. |
| Feedback | `depositSfxPrefab` | `sfx_lootspawn` | Any sound prefab in ZNetScene. |
| Feedback | `depositVfxPrefab` | `vfx_Potion_eitr_minor` | Eitr is blue; `_health_` red, `_stamina_` yellow. |
| Feedback | `takeSound` | `true` | Pickup sound when an item arrives. |
| Feedback | `depositVolume` / `takeVolume` | `1.5` / `4.0` | Volume multipliers. |
| Feedback | `anchorEffects` | `true` | The slam when a Kist is planted or moved. |
| Feedback | `anchorSfxPrefab` | `sfx_frozenking_doubleslam_explosion` | Sound of the landing. |
| Feedback | `anchorVfxPrefab` | `vfx_trollsnow_groundslam` | Impact effect. |
| Feedback | `anchorFallVfxPrefab` | `vfx_Potion_eitr_minor` | What falls out of the sky. |
| Feedback | `anchorVolume` | `1.5` | Multiplies the slam. |
| Feedback | `anchorDropHeight` / `anchorDropSeconds` | `12` / `0.45` | How far it falls, and for how long. |
| Interface | `mapPin` | `true` | Mark the Kist on your map. |
| Interface | `mapPinPrefab` | `piece_chest_wood` | Icon source. Try `piece_chest_treasure` or `piece_chest_blackmetal`. |
| Interface | `uiScale` | `1.0` | Scales text and rows. Below 1 fits more on screen. |
| Interface | `scrollRowsPerNotch` | `5` | Rows per notch of the mouse wheel. |
| Interface | `sortMode` | `Name` | Set from the buttons; stored so it survives a session. |
| Server | `minHirdSize` | `2` | Members needed before a Kist unlocks. **Set to 1 for single-player. Server-side.** |
| Server | `anchorCostPrefab` | `SurtlingCore` | Item spent to re-anchor. Empty means free. **Server-side.** |
| Server | `anchorCostAmount` | `25` | How many of it. **Server-side.** |

Every prefab name above is real — all 43,346 the game ships are in
`notes/prefab-names.txt` if you fancy a different sound. No rebuild needed.

---

## Where your loot actually lives

One file per world, in the server's save directory:

```
<savedir>/ThorsKist/kist_<worldUID>.dat
```

Deliberately **not** `BepInEx/config`, which server managers wipe on every mod
sync — a vault kept there would be one update away from gone. Older builds are
migrated automatically.

It sits outside the world save, so rolling the world back doesn't roll the Kist
back with it, and Valheim's backups don't cover it. **Back up
`<savedir>/ThorsKist/` alongside your worlds.**

---

## Building

The Valheim and Unity reference assemblies are **not in this repository** —
they belong to Iron Gate and Unity, and you already own a copy. This collects
them from your own install:

```bash
dotnet tool install -g Krafs.AssemblyPublicizer.Tool
./scripts/setup-refs.sh /path/to/steamapps/common/Valheim
```

(It finds the usual Steam locations on its own if you don't pass a path, and
needs BepInEx installed into the game for `BepInEx.dll` and `0Harmony.dll`.)

Then:

```bash
dotnet build ThorsKist.csproj -c Release
dotnet run --project tools/VerifyPatches -c Release -- bin/Release/net472/ThorsKist.dll lib .refs/assembly_valheim.dll
dotnet run --project tools/CategoryCheck -c Release -- .refs
```

A green build proves almost nothing about a Valheim mod, so CI doesn't stop
there. **VerifyPatches** resolves every Harmony target against the real game
assembly and checks every reference is public at runtime — mods compile against
a *publicized* copy where everything is, so calling something private compiles
fine and throws on the first frame. **CategoryCheck** runs the real
sort-by-type rules against known traps, because keyword matching fails
silently: `leatherscraps` contains "scrap", `barleyflour` contains "bar".

---

## Licence and credits

Source code is **MIT** — see [LICENSE](LICENSE). Do what you like with it.

Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and
[HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT). Neither is redistributed
here. The deposit sound and effects were learned from reading
**SmarterContainers**.

An unofficial mod, not affiliated with or endorsed by Iron Gate Studio.
Valheim and its assets are theirs.

---

*Built for people who play together. Skål.* 🍺

[shot-kist]: https://gitlab.phospher.com/brianmiller/thorskist/-/raw/main/kist1.png
[shot-hird]: https://gitlab.phospher.com/brianmiller/thorskist/-/raw/main/hird1.png
