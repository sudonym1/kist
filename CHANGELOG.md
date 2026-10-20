# Changelog

## Unreleased

- Verify withdrawals of restored hash-prefab records without falsely rejecting
  delivered items and compensating them back into the vault.
- Fill compatible vault stacks before allocating new stacks, preserving item
  metadata and enforcing stack limits for concurrent deposits. Use the same
  merge rules as withdrawals and preflight capacity for the whole deposit.
- Apply `kistRows` to existing vaults when the world reopens, preserving excess
  stacks as overflow when shrinking. Upgrade SQLite storage to schema 6 for
  recoverable resizing; older builds require a pre-upgrade backup.

## 0.9.1

- Update the release version to 0.9.1 and rebuild the publishing package.
- Retain the SQLite/journal migration and runtime validation notes from 0.9.0.

## 0.9.0

Performance fork of Thor's Kist by Brian Miller (posixone).

- Replace whole-vault save/sync work with SQLite-backed journal storage and
  incremental replication. Existing `.dat` vaults are imported once with a
  verified backup; subsequent saves use SQLite.
- Use immediate Kist payments for crafting, building and station inputs.
- Improve inventory stacking and remote peer identity handling.
- Add a per-row delete button that discards items through the withdrawal path.
- Disable optional diagnostic profiling by default.
- Bundle the SQLite managed dependencies and Windows/Linux x64 native libraries.
- Build Windows SQLite 3.54.0 from official source and include its matching
  native PDB and build provenance for symbol-based upload review.

Windows Unity/Mono provider checks exist. Linux runtime validation and the full
authenticated multiplayer release matrix remain pending. Back up host vault,
world and player saves before upgrading; see README.md for migration and rollback.

## 0.8.6

**Fixed: the Invite button had no label.**

It was two pixels shorter than the buttons beside it, which left its text a
hair less room than one line needs -- and a line that does not fit is dropped
whole rather than trimmed, so the button went blank while still working. Button
labels are now allowed to overflow instead of vanishing, which also fixes every
button in the window losing its text at small interface scales.

**Fixed: item names were printed on top of their icons.**

The name started a few pixels inside the icon's right edge rather than beside
it, and the two got closer as the interface scale went up. There is now a clear
gap at every scale.

**Quieter logs.** The station diagnostics added in 0.8.5 wrote a line every
time a station so much as asked the Kist a question -- and a cooking station
asks once a frame while you stand near it. A station now says something only
when it actually takes from the Kist, or when it tried and could not.

## 0.8.5

**Fixed: recipes that take any one of several ingredients were never charged.**

A recipe flagged "only one ingredient" pays with a direct inventory call and
never reaches the usual payment step, so the Kist was counted toward the cost
and then never asked for anything. The whole crafting window is now covered,
not just the payment step inside it, so every branch that can pay does.

**Fixed: the server spent the vault through the world-level filter.**

0.8.4 removed that rule from every place the Kist is read or spent — except the
server's own copy, which is the authoritative one. The client would spend its
mirror, the server would quietly decline, and the next sync put the material
back. Same trap as 0.8.4, caught on the other side of the wire.

**Fixed: the shared-hooks warning overstated what a rewrite can do.**

Crafting is now claimed with a prefix, and a prefix cannot be removed by
another mod rewriting the method around it. Backpacks appears under "shares
the same code" rather than as a crafting risk, which is what it always was —
its rewrite only records an item and removes nothing.

**Fixed: a station could take material the Kist was never charged for.**

When a station only *asked* whether something was available, the Kist handed
back its own stored stack rather than a copy. A charcoal kiln takes that answer
and queues the wood without checking whether the take succeeded -- so the kiln
was fed and the vault was never billed. It now answers with a detached copy,
which cannot be spent out of the vault by accident.

**Added: the Kist now says when it was asked to pay and could not.** Together
with the existing `Kist paid ...` line, "it did not deduct" and "it was never
asked to" are finally two different messages in the log.

**Added: stations report what they decided, to the server's log.** Station
decisions happen on your own machine, so when the Kist quietly declined there
was no way for anyone but you to see why. Each one now says which station
asked, what it asked for, how much the Kist held, and whether anything moved.

## 0.8.4

**Fixed: the Kist would show items it then refused to spend.**

Valheim stamps every item with the world level of the moment it entered an
inventory, and its inventory lookups ignore anything stamped below the world
level you are on now. For your own pack that is the intended difficulty rule.
For a shared vault it was a trap: **wood banked before a boss fell is still
wood**, but it silently stopped counting toward anything once the world level
rose.

Worse, the Kist window lists its contents with no filter at all — so those
materials sat plainly visible on screen while every craft, build and station
refused to touch them. A charcoal kiln would take your wood and never charge
the Kist for it.

Every read and every spend of the vault now ignores the world-level rule.
Which stack you happen to have stored, and when, no longer decides whether it
works.

**The Kist now says when it pays.** A line like `Kist paid 1 x $item_wood`
appears in the log whenever the vault is actually charged. "It did not deduct"
and "it was never asked to" are otherwise impossible to tell apart.

**The mod-conflict warning is honest about severity.** It previously filed the
Alt-click deposit hook under crafting, so mods that only share the deposit
click — and cannot touch payment at all — were reported as though they handed
out free materials. Overlaps are now separated into what could affect crafting
and what could affect depositing, and described as what *may* happen rather
than what has: most of these patches are conditional and decline far more often
than they fire. A mod that merely adds to a method, rather than replacing it,
is no threat and is no longer mentioned.

**Deposit clicks are considered first.** Other inventory-extending mods hook
the same click and can consume it. The Kist now gets first look, and passes
every click that is not a modifier-held deposit straight through.

## 0.8.3

**Smelters, kilns, fireplaces, cooking stations and fermenters now feed from
the Kist.**

Stations never touched the recipe machinery, so none of the crafting work
reached them — coal and ore had to be carried by hand even with a full vault
one step away. They now draw on the Kist like everything else:

- **Fuel** — coal into smelters, kilns and blast furnaces; wood into fireplaces
  and cooking stations.
- **Ore and food** — ore into smelters, raw food onto the cooking station,
  fermenter ingredients.

Items are moved one at a time, as the station consumes them, so nothing is left
stranded in your pack afterwards. Nothing is moved while a station is merely
being looked at — only when you actually feed it.

Known rough edge: a smelter decides what to take before it checks whether it is
full, so feeding a full smelter moves one ore into your inventory rather than
refusing outright. It is not lost, just carried.

## 0.8.2

**Thor's Kist now tells you when another mod has taken its crafting hooks.**

The Kist pays for a recipe by widening two of the game's own answers — "how
many have I got?" and "take this many" — while the game is asking them about a
craft. Another mod that *replaces* the game's payment step instead of adding to
it removes the Kist from the transaction: the game still counts the Kist's
stock toward the recipe, but the deduction never runs. The result is materials
that look affordable and are never actually spent, with nothing in the log to
say so.

That is now detected and reported in a dialog naming the other mod, the method
it took over, and what to do about it — remove the mod, or set
`craftFromKist = false` so the Kist stops offering materials it cannot charge
you for.

The check names no mod and depends on no mod. It asks Harmony which patches sit
on the seams the Kist relies on, and reports only the ones that can displace
the original — prefixes that can skip it, and transpilers that rewrite it. A
mod that merely adds to those methods is harmless and stays quiet. The seams
themselves are read back from the Kist's own patches rather than kept as a
list, so a seam added later is watched from the day it is added.

Known conflict: **AzuCraftyBoxes** replaces `Player.ConsumeResources`. With both
installed, crafting and building from the Kist is free.

## 0.8.1

Documentation only — the mod itself is unchanged from 0.8.0.

- **The screenshots render on the store pages.** They were written as
  repository-relative paths, which resolve on GitLab and nowhere else: both
  stores render the README from their own page, so a relative path pointed at
  themselves and came up empty. They are absolute URLs now.
- Source is now **open, MIT licensed**, at
  https://gitlab.phospher.com/brianmiller/thorskist — with the Valheim and
  Unity reference assemblies kept out of it, since those are Iron Gate's and
  Unity's to distribute, not ours. `scripts/setup-refs.sh` collects them from
  your own install.

A store page's README is fixed at upload and cannot be replaced in place, so a
version number is the only way to correct one.

## 0.8.0

**The Kist list can be sorted four ways**, from a new row of buttons under the
tabs. Clicking the mode already in use reverses it, and the arrow on the button
says which way round the list is. The choice is remembered between sessions.

- **A-Z** — as before.
- **Amount** — biggest hoards first, ranked by the item's TOTAL across the
  vault rather than by stack, so 300 wood does not scatter down the list in 50s.
- **Progress** — Meadows through to Deep North.
- **Type** — wood with wood, ore with ore, hides with hides.

Progress and Type both print what they sorted on at the end of each row. That
is there to be checked: a biome or a category that reads wrong is a line worth
reporting, and without it a wrong order just looks arbitrary.

**Where the progression order comes from.** It is read out of the game's own
data, not a list typed from memory: which biome the game plants each berry,
deposit and pickable in; which biome the creatures that drop a thing live in;
and what every recipe is made of, settled to a fixed point so ore → bar → sword
lands in the right place whatever order the recipes are in. That means a mod's
items are placed correctly too, without this mod knowing anything about them.
Only the materials none of those cover — dungeon loot, mostly — are named by
hand, and every one of those prefab names was checked against the 43,346 the
game actually ships. Deep North's materials are deliberately not named by hand;
they are left to the derived passes rather than guessed at.

Anything the table has no answer for sorts last and is labelled unknown, rather
than being quietly given a number. Each of the four passes is separately
guarded, so a game update that renames something costs that pass and nothing
else, and the plugin log says how many items each one accounted for.

**Type is a guess, and is tested like one.** Valheim files every raw material
under one ItemType, so wood, ore and hides can only be told apart by name —
and keyword matching fails silently: `leatherscraps` contains "scrap",
`barleyflour` contains "bar", `eyescream` contains "eye". All three were wrong
in the first draft. The rules now live in a file with no Unity dependency so
CI can compile and run the real ones against those traps, and the same check
re-reads ItemType's numbering out of the game assembly, because the classifier
hard-codes those numbers and a renumbering would not fail to compile.

## 0.7.1

**Keys and clicks no longer reach the game while the Kist window is open.**

Gating `Player.TakeInput()` was never enough, which is what I got wrong when I
first claimed this was handled. `Player.Update` does respect it, so movement and
attacks were blocked -- but `InventoryGui.Update` and `Minimap.Update` read
`ZInput` directly and never consult it. Tab still opened the inventory and M
still opened the map underneath the window, and the scroll wheel scrolled the
item list *and* cycled the hotbar.

Every game system reads input through `ZInput`'s static getters, so those now
report nothing pressed and nothing moved while the window is up: buttons, mouse
buttons, keys, mouse delta, the scroll wheel, radial taps, and the gamepad
sticks and triggers.

The window itself is unaffected -- it reads `UnityEngine.Input`, and its clicks
arrive through Unity's `InputSystemUIInputModule`. Neither goes near `ZInput`,
which was checked rather than assumed. There are still three independent ways
out: F3, Esc, and the X button.

The block is keyed on the panel actually existing and being active, not just on
a flag, so a stale flag cannot leave the game permanently deaf with nothing on
screen to explain it.

## 0.7.0

- **The founder can rename the hird**, from a new HIRD NAME box on the Hird tab.
  Renaming is safe at any time: the name is only a label, and membership, the
  vault file and the map pin all key off the hird's id, which is a GUID and
  never changes. The map pin's label follows the rename on its next sync, and
  everyone in the hird is told what it was called before.

Requires matching versions on client and server — the wire protocol gained a
message, so a 0.7.0 client talking to an older server is told plainly rather
than being quietly ignored.

## 0.6.0

**Vaults have moved out of `BepInEx/config`.** They now live in the server's
save directory, beside the world files:

```
<savedir>/ThorsKist/kist_<worldUID>.dat
```

The old location was a data-loss bug waiting to happen. Server managers rebuild
the mod tree on update — PhValheim runs `rm -rf BepInEx/config/*` on every mod
sync — so every Kist would have been destroyed by the next world update, with
no warning and nothing to recover from.

The path comes from `Utils.GetSaveDataPath`, which resolves the `-savedir` the
server was started with, so it follows the host's persistent volume wherever
that is. A vault written by an earlier build is moved across on first load —
moved, not copied, because two files claiming to be the same vault is how a
rollback silently resurrects last week's inventory.

Still outside the world save itself, so a world rollback does not roll the Kist
back. Back up `<savedir>/ThorsKist/` with your worlds.

## 0.5.0

- **The Kist is pinned on every member's map**, with a chest icon lifted from
  the wood chest's build-menu sprite — Valheim ships no chest among its own pin
  icons, and borrowing the piece's beats shipping an asset bundle for one image.
- **The pin cannot be deleted by hand.** Every way a player removes a pin ends
  at one method, so a single guard covers right-click, the radius sweep and the
  pin list.
- **The pin is a view of server state, not a saved pin.** It moves when the
  founder re-anchors, and disappears the moment you leave, are kicked, or the
  hird disbands — because there is no longer any state for it to draw. Nothing
  is written into the map save, so a stale pin cannot outlive its hird.
- `notes/prefab-names.txt` now lists all **43,346** prefab names the game
  ships, not just sounds and effects. `mapPinPrefab` accepts any of them that
  has a build icon — `piece_chest_treasure` and `piece_chest_blackmetal` are
  real alternatives.

## 0.4.1

- **Text typed into the hird-name box is visible again.** `AddComponent` runs
  `Awake` and `OnEnable` immediately, and `TMP_InputField` binds its label
  there — so assigning `textComponent` afterwards left a field that stored the
  value but never drew it. That is why the name worked while the box looked
  empty. Input fields are now built inactive and switched on once every
  reference is set. The search box had the same defect.
- Input fields get a visible caret and a selection highlight, so it is obvious
  when one has focus.
- The hird name survives a redraw. That field is inside the rebuilt row list,
  so anything typed was one refresh away from being wiped.
- Input text no longer parses rich text — a typed `<` would have eaten the rest
  of the line.

## 0.4.0

- **Rows are finally small.** `RowH` never did anything: the list's
  `VerticalLayoutGroup` had `childControlHeight = false`, which makes Unity
  ignore `LayoutElement` heights and lay every row out at its own RectTransform
  height — the default 100 units. Three releases of shrinking that number
  changed nothing. Rows are now the height of their tallest element (18 units),
  and notices size themselves to their wrapped text.
- **The deposit effect is actually blue now.** 0.3.0 changed the *default* to
  `vfx_Potion_eitr_minor`, which does nothing for anyone whose config file
  already existed — BepInEx keeps what is on disk. A one-time migration, guarded
  by a new `configVersion`, moves the old red value across. A deliberate choice
  made afterwards is never overwritten.
- **Everyone sees everyone's deposits.** Effect prefabs carry no `ZNetView`, so
  nothing about them replicated; the server now tells every hird member where to
  draw it. The depositor still plays theirs locally so the click stays instant.
- **Re-anchoring is now expensive and public.** It costs 25 surtling cores from
  the founder's pack, and every online member must be standing within the new
  `anchorRadius` (default 100m). Both are server settings. The button greys out
  with the reason spelled out above it — who is too far away, or how many cores
  you are short.

Offline members cannot be asked to attend, so they are not counted toward the
gathering rule; instead at least `minHirdSize` members must be online, which is
what stops one person relocating the vault by themselves.

## 0.3.0

- **The stack count is back.** 0.2.0 drew it underneath the take buttons: the
  strip width was measured to the leftmost button's right edge instead of its
  left, so the number rendered behind the `1` button. Button positions and the
  strip width now come from one place.
- **Rows are much tighter** — 18px instead of 24px, with smaller text and
  icons. Roughly 45 rows at 1080p. The Hird tab was condensed the same way.
- **Take is far louder.** `AudioSource.volume` was never the right knob:
  Valheim's `ZSFX` recomputes it every frame, so setting it directly survives
  one frame and is silently overwritten. It now uses `ZSFX.SetVolumeModifier`,
  which is a factor inside that same calculation. Deposit, take and anchor each
  have their own volume multiplier.
- **Deposit effect is blue**, not red — `vfx_Potion_eitr_minor` instead of
  `vfx_Potion_health_medium`.
- **Planting a Kist now slams.** Founding a hird or re-anchoring drops a blue
  effect from 12 metres up, accelerating, then lands with
  `vfx_trollsnow_groundslam` and a heavy impact sound. Everyone in the hird
  sees and hears it at the anchor, so distance attenuation does the rest.
- "Move the Kist here" is now **"Anchor Kist to this location"**.
- `notes/prefab-names.txt` lists all 1,487 sound and effect prefabs the game
  ships, pulled from its own asset manifest. Every default above was checked
  against it rather than guessed, and all of them are config.

Internal: the on-disk vault format now has its own version number, separate
from the wire protocol. They shared one constant, so adding this release's new
message would have made the server refuse to load existing vaults.

## 0.2.0

First round of feedback from a live game.

- **Deposit feedback is now the game's own.** `sfx_lootspawn` and
  `vfx_Potion_health_medium`, spawned through `ZNetScene` exactly the way
  SmarterContainers spawns them. The synthesised chime is gone — it never
  sounded like Valheim. Both prefab names are config, so swapping the sound
  does not need a new build.
- **Taking an item plays the pickup sound**, via the player's own
  `m_pickupEffects` rather than a hard-coded prefab name, so it is always
  exactly what picking something off the ground sounds like.
- **The Kist list is far denser.** The window now takes 86% of screen height
  and rows are 24px instead of 46px: roughly 33 rows at 1080p, up from 9. New
  `uiScale` setting scales text and rows together.
- **Scrolling is fixed.** It was moving 30 pixels per notch — two thirds of one
  row. The wheel is now handled directly and moves a configurable number of
  ROWS per notch (default 5), which also makes it behave the same across
  platforms; Unity's `scrollSensitivity` multiplies a raw delta that differs
  per device.
- **Search moved into the top bar**, always visible, focused the moment the
  window opens, with a live "N of M stacks" count. Esc clears the filter on the
  first press and closes the window on the second.

## 0.1.1

Fixes the publicizer trap. 0.1.0 threw `MethodAccessException` on the first
frame and never got as far as doing anything.

The mod compiles against a *publicized* copy of `assembly_valheim` where every
member is public; the game ships the real one, where many are not. The compiler
cannot tell the difference — Mono throws at the call site. Eight references were
affected, not just the one that happened to run first:

- `ZRoutedRpc.GetServerPeerID` / `GetPeer` / `m_id` — now the public
  `InvokeRoutedRPC(name, args)` overload, `ZNet.GetPeer` and `ZNet.GetUID`.
- `Inventory.Changed` — dropped; `AddItem` and `RemoveItem` already raise it.
- `ItemDrop.Save` — now `ItemDrop.DropItem`, vanilla's own drop path.
- `InventoryGui.m_dragItem` — now read through Harmony's `___` field injection.
- `InventoryGrid.GetElement` / `m_width` — deposit streaks now start at the
  cursor, which is a truer origin for the gesture anyway.

`tools/VerifyPatches` now resolves every member reference against the real
assembly and fails the build on any that are not public at runtime. This class
of bug is invisible to the compiler and to a green build; it is now caught in CI.

## 0.1.0

First build. Untested in a live game — this is the version to try, not to ship.

- Shared Kist vault, one per hird, owned and persisted by the server.
- Deposit with `Left Alt` + left-click from the inventory, with a chime and
  light streaks. Deposits roll back into your pack if the server refuses or
  never answers.
- `F3` Kist window: searchable contents with `1` / `10` / `All` take buttons,
  and a Hird tab for founding, inviting, removing and moving the Kist.
- A hird needs two members before its Kist unlocks.
- Crafting, building and repairs draw materials from the Kist, spending your own
  inventory first.
- Deposit, take and craft each have their own radius, measured from the Kist's
  location; 100 m by default, or unlimited.
