# Thor's Kist

### One chest. Every viking. Anywhere in the world.

You are in the Mistlands. Your iron is in a barrel behind the smelter, nine
thousand metres and one longship ride away. Your friend is in the Plains with
forty black metal scrap and nowhere to put it.

**Alt-click. It's in the Kist. Everyone has it. Get back to work.**

Thor's Kist is one shared vault for your whole **hird** — a warband, a crew,
whatever you call the people you actually play with. Deposit from anywhere,
take from anywhere, and every workbench, forge and hammer in the world quietly
reaches into it while you build.

And no, you cannot have one to yourself. A hird needs **two** vikings before
the Kist will open at all. Hoarding is a solo problem.

---

## Behold

![The Kist][shot-kist]

*Sixty-one stacks, one window, one search box. Take `1`, `10` or `All` off any
row. Top right tells you how far you are standing from the Kist and what each
radius is set to.*

![The Hird][shot-hird]

*Who's in, who's out, who is 116 metres too far away for you to move the vault
today.*

---

## What it does

| | |
|---|---|
| ⚒️ **Deposit** | Hold `Left Alt` and **left-click** anything in your inventory. It goes with the game's own loot-spawn sound and a burst of blue light — and everyone in the hird sees it happen. |
| 📖 **Open** | `F3`. Two tabs: **Kist** (the loot) and **Hird** (the people). The search box grabs focus the moment it opens, so just start typing. |
| 🗂️ **Sort** | **A-Z**, **Amount**, **Progress** or **Type** — one click each. Click the same one again to flip it. |
| 🫴 **Take** | `1`, `10`, `All` on every row, with the proper pickup sound. No room in your pack? It lands at your feet instead of vanishing into Ginnungagap. |
| 🔨 **Craft** | Every crafting station, the build hammer and repairs spend straight out of the Kist. Stop running home for two nails. |
| 🗺️ **Find** | The Kist sits on every member's map under a chest pin that *cannot* be deleted by hand. It moves when the Kist moves and vanishes the day you leave the hird. |

---

## Sorting, and why "Progress" is the good one

Four buttons under the tabs. The arrow on the active button tells you which way
round the list is, and your choice survives between sessions.

- **A-Z** — the boring one. It works.
- **Amount** — biggest hoards first, ranked by the *total* across the vault
  rather than by stack, so 300 wood doesn't scatter down the list in 50s.
- **Progress** — **Meadows → Black Forest → Swamp → Mountain → Plains →
  Mistlands → Ashlands → Deep North.** Your whole vault, laid out in the order
  you earned it.
- **Type** — wood with wood, ore with ore, hides with hides, seeds with seeds.

**Progress and Type print what they sorted on at the end of each row.** If
something says "Swamp" and you know it's a Plains drop, that's a bug you can
see and report — rather than an order that just looks vaguely wrong.

<details>
<summary><b>How does it know what's Mistlands?</b> (it isn't a list somebody typed from memory)</summary>

It's read out of the game's own data, three ways, every time you load a world:

1. **Where things grow.** Valheim's vegetation tables say which biome it plants
   every berry bush, flint pile and copper deposit in. Walk what each one
   yields and you have real tiers for most raw materials.
2. **Where things live.** Spawn tables pair each creature with its biome, and
   each creature's drop table says what it leaves behind — hides, meat, glands,
   trophies.
3. **What things are made of.** Every recipe, walked to a fixed point, so
   `copper ore → bronze → bronze sword` settles correctly no matter what order
   the recipes happen to be in.

Which means **other mods' items get placed correctly too**, without Thor's Kist
knowing the first thing about them.

Only the leftovers — dungeon loot, mostly — are named by hand, and every prefab
name in that table was checked against the 43,346 the game actually ships.
Deep North's materials are deliberately *not* named by hand; they're left to
the three passes above rather than guessed at.

Anything still unplaced sorts last and is **labelled unknown**, rather than
being quietly given a number it didn't earn.

</details>

---

## Hirds

The founder runs the hird: **rename, invite, remove, re-anchor, disband.**
Anyone online who isn't already in a hird appears in the invite list — no typing
names, no player IDs, no Steam64 nonsense.

Renaming is free and safe whenever you like. The name is just a label;
membership, the vault and the map pin all key off the hird's id.

**There is no limit on hird size.** The ceiling is however many players your
server allows, and members count whether they're online or not.

### Moving the Kist is meant to hurt

Founding a hird is free. *Moving* it costs:

- **25 surtling cores** out of the founder's own pack, and
- **every online member standing within 100m** of the new spot.

Offline members can't be summoned, so they aren't counted — but at least
`minHirdSize` members must be online, which is what stops one person quietly
relocating everybody's vault while they sleep. The button greys out and tells
you exactly who's too far away or how many cores you're short.

Then it lands: a blue mass falls out of the sky, accelerating, and hits the
ground with a frost-king slam that every member hears from wherever they are.

---

## Installing

Needs [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/).
Drop `ThorsKist.dll` into `BepInEx/plugins/`.

> **Install it on the server as well as on every client.** The server owns the
> vault. A client with the mod talking to a server without it will simply be
> told there is no hird.

Every radius below is measured from the Kist's own location — the spot the hird
was founded at, which the founder can move.

---

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
| Interface | `mapPinPrefab` | `piece_chest_wood` | Build piece whose icon the pin borrows. Try `piece_chest_treasure` or `piece_chest_blackmetal`. |
| Interface | `uiScale` | `1.0` | Scales the window's text and rows. Below 1 fits more on screen. |
| Interface | `scrollRowsPerNotch` | `5` | Rows moved per notch of the mouse wheel. |
| Interface | `sortMode` | `Name` | How the list is ordered. Set from the buttons; stored so it survives between sessions. |
| Server | `minHirdSize` | `2` | Members needed before a Kist unlocks. **Server-side.** |
| Server | `anchorCostPrefab` | `SurtlingCore` | Item spent to re-anchor. A prefab name. Empty means free. **Server-side.** |
| Server | `anchorCostAmount` | `25` | How many of it. **Server-side.** |

Settings marked **server-side** are read from the host's config only. Everything
else is per-player — set your own sounds, your own scale, your own sort.

Every prefab name above is a real entry in the game's asset manifest. All 43,346
names the game ships are in `notes/prefab-names.txt` if you fancy a different
sound or a different pin. No rebuild needed — they're read from config.

---

## Where your loot actually lives

One file per world, in the server's **save directory** — the path it was
started with via `-savedir`:

```
<savedir>/ThorsKist/kist_<worldUID>.dat
```

Deliberately **not** `BepInEx/config`. Server managers rebuild the mod tree on
update — PhValheim runs `rm -rf BepInEx/config/*` on every mod sync — so a
vault kept there would be one world update away from gone. The save directory
is the one place a host *has* to preserve, because the world is in it. A vault
written by an older build is moved across automatically on first load.

It sits outside the world save itself, so rolling the world back to an older
`.db` does **not** roll the Kist back with it — and Valheim's own backup
rotation doesn't cover it. **Back up `<savedir>/ThorsKist/` alongside your
worlds.**

---

## Building

```bash
dotnet build ThorsKist.csproj -c Release
dotnet run --project tools/VerifyPatches -c Release -- bin/Release/net472/ThorsKist.dll lib .refs/assembly_valheim.dll
dotnet run --project tools/CategoryCheck -c Release -- .refs
```

A green build proves almost nothing about a Valheim mod, so CI doesn't stop
there:

- **VerifyPatches** resolves every Harmony target against the real game
  assembly, and checks every member reference is actually public at runtime.
  Mods are compiled against a *publicized* copy of the game where everything is
  public — call something the shipped game keeps private and it compiles
  perfectly, then throws `MethodAccessException` the first frame it runs.
- **CategoryCheck** compiles the real sort-by-type rules and runs them against
  known traps, because keyword matching fails *silently*: `leatherscraps`
  contains "scrap", `barleyflour` contains "bar", `eyescream` contains "eye".
  All three were wrong in the first draft and nothing crashed.

---

*Built for people who play together. Skål.* 🍺

[shot-kist]: kist1.png
[shot-hird]: hird1.png
