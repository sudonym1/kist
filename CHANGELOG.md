# Changelog

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
