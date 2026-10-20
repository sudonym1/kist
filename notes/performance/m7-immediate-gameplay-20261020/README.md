# Immediate crafting and station inputs, v27

The user confirmed two-PC invitations work on v22, then reported station inputs
bouncing through the player inventory. They requested the same immediate,
optimistic behavior as hammer building, extended it to crafting, and asked for
unused code removal and profiling disabled by default. Rare free/double-spent
results are acceptable; Kist payment must follow an action, never a stock check
or a refused station insertion.

`JournalImmediatePayments.cs` replaces the building-only payment file. Building,
crafting and station inputs share pending stock holds and exact verified payment
effects. These effects settle already-used resources without adding them to the
player inventory. Ordinary withdrawals retain metadata-safe inventory stacking.
Station payments retain ItemCommandPurpose.Station for server range checks.

Crafting runs vanilla DoCrafting immediately with widened cached material
queries. Its native output/capacity/upgrade checks stay in place. The string
Inventory.RemoveItem payment hook runs only inside DoCrafting, where vanilla
consumes ingredients after producing the result (or resolving an upgrader).
Vanilla removes the local share; only the missing share is paid from Kist. This
covers normal, upgraded, multi-craft and single-ingredient payment branches.
DoCrafting's finalizer restores the previous scope flag, including exceptions.
No-cost and failed-output branches do not consume ingredients and therefore do
not trigger a journal payment. These vanilla UI branches still need manual QA.

Station interactions prefer real local inputs. Kist inputs check cached stock,
access and capacity, then update the station through its network owner without
a pack item. A locally owned station applies its existing native RPC handler;
for a remote owner a compact request/success reply runs asynchronously. The
owner rechecks capacity and supported input and confirms an actual fuel, queue,
slot or fermenter-content change before payment. Wrong-owner, refused, duplicate,
expired and unknown replies cannot charge. Lost success replies and refused
background payments can leave free station inputs. No persistent receipt ledger,
new SQLite schema, retry transaction system or rendering-thread IO wait was
added. Station effects, smelter animation and cooking skill gains are retained.
This is apply-before-payment, not atomic world-save/Kist rollback coupling.

Removed code: Procure, procurement task/flag/continuation queue and delivery
polling, crafting/station replay callbacks, Executing guard, CraftDraw,
PendingDraws/Record/FlushDraws and the obsolete ReportCraftConsume endpoint.
The original building payment marker remains for successful placements only.

Profiling setting: `BepInEx/config/posixone_ThorsKist.cfg`:

```ini
[Performance]
enableProfiling = false
```

False is the default. The diagnostic plugin installs no timing Harmony patches,
resolves no profiling targets, prepares no worker trace buffer and does not poll
F4. Set true and restart to restore profiling; F4 then starts/stops captures.
Explicit automated trace-verification flags remain opt-in test fixtures.

Validation:

- Release build: zero warnings/errors; 131 Harmony targets and 137 runtime
  references checked against the actual game assembly; category/version checks pass.
- Final profiling-off native run: **80 assertions**, including 18 stacking checks,
  existing hammer/cutover coverage and **28 new immediate-gameplay checks**.
  Real inactive Unity player/station components, patched ConsumeResources,
  Inventory.RemoveItem and inventory methods run. Station transport and native
  owner update callbacks are captured/stubbed; this does not prove real two-PC
  owner routing. Tests check full/local-preferred stations, exact wire inputs,
  owner identity, refused/duplicate replies, actual state-change confirmation,
  single-ingredient selection and full-pack payment without ingredient delivery.
- Exact isolated settlement: crafting consumes two local Wood plus five Kist
  Wood; two successful station inputs cost two Kist Wood. Failed/duplicate/
  unconfirmed inputs charge nothing. Logout and SQLite reopen retain the result;
  source legacy bytes stay unchanged.
- Profiling true: separate **22-assertion** control/identity/configuration run
  verifies timing hooks, targets and worker buffer preparation return.
- Full native adapter suite passes, including 168 cumulative inventory/provider/
  decoder/dispatch assertions, 63 reservation checks, projection, TCP ordering,
  journal/control TCP and the persistent 16-check session TCP probe. Counts overlap.
- Trace: 1,148 complete events, zero drops/incomplete spans/thread violations.
  Cold first-use work still exceeds the requested 1 ms budget: hammer submission
  max 1.950 ms and station submission/dispatch scope max 1.931 ms. Station payment
  submission max is 0.294 ms. This synthetic run does not close loaded-game timing
  QA. Drawing is outside these measurements.

Earlier fixture runs exposed an incorrect 512-stack test capacity, fake managed
Unity player and uninitialized test ZDO prefab. The final fixture uses the
preserved 1,000-stack capacity, real inactive components and valid test replicas.
No production stock/persistence change was needed for those fixture corrections.

Final Windows Setup/Update bundle:
`.tools/m7/ThorsKist-M7-Windows-Journal-v27.zip`, SHA256
`ee62e7fefb6e71051a9b078df07640bab2303d926bc9a8efba66d0d3b4f9811f`.
It is also in `C:\shared`. All 29 payload files are installed in the isolated
host. Latest closed user saves were archived before deployment in
`.tools/m7/checkpoints/journal-before-immediate-v27.zip`; all 22 save files remain
byte-identical afterward. The isolated config explicitly disables profiling.
Raw native evidence is archived in
`.tools/m7/checkpoints/journal-immediate-gameplay-v27.zip`.
Manual QA remains: two-PC station owner routing/full stations/inputs, ordinary
crafting/upgrades/single-ingredient/multi-craft, and enabling F4 when desired.
