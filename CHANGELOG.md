# Changelog

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
