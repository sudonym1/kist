# Shared Inventory

A small BepInEx 5 mod for a session-wide shared item inventory. Install the same DLL on the server and every client. It works from anywhere, including for the host's player.

Use synthetic stock for isolated testing; see [the Windows setup instructions](WINDOWS-CODEX-SETUP.md).

Press **F3** to open the searchable shared inventory window. **Alt-click** an item in your normal inventory to deposit that entire stack. The withdrawal buttons take **1** or **one normal stack**. Nonstackable items can only be withdrawn one at a time.

The shared inventory stores one count per prefab: 400 coal is one entry of 400, and ten iron swords are one entry of ten. **Depositing discards all per-item metadata**, including upgrades, durability, variant, crafter information, world level, and custom mod data. Withdrawn items have quality 1, full default durability, and empty custom data.

The client checks room before withdrawing. If inventory capacity changes during the request, anything that could not actually be inserted is sent back through `Deposit`. A stack action requests up to one normal stack, limited by available stock; if that amount does not fit initially, the action makes no request.

## Server and threads

The server is only a `prefab ID -> Int64 count` database. Clients are fully trusted. All gameplay rules live on clients. The only requests are [`List`, `Deposit`, and `Withdrawal`](API.md).

The mod uses Valheim's existing reliable RPC transport. A dedicated worker constructs and parses packages, serializes database requests from all clients, and reads/writes saves. Valheim's packet dispatch stays on Unity's main thread, along with local inventory changes and UI rendering. Main-thread completions are pumped in Update and LateUpdate with a one-millisecond budget checked between actions; gameplay never waits for the database worker.

The host's player uses the same RPCs and worker queue as remote players. There is no direct database shortcut for that player. There are no subscriptions or server-side transfer sessions. The UI refreshes through `List` every two seconds while open and after pressing Refresh. Transfer replies update the affected displayed count immediately.

## Build

Requires a .NET 8 SDK or newer, Valheim, and BepInEx 5. References come from your game installation and BepInEx profile; no game DLLs are distributed with this source.

From Windows PowerShell in this folder:

```powershell
.\build.ps1 -ValheimDir 'C:\Program Files (x86)\Steam\steamapps\common\Valheim' `
  -BepInExDir "$env:APPDATA\com.kesomannen.gale\valheim\profiles\My Valheim\BepInEx"
```

If BepInEx is inside your Valheim installation, omit `-BepInExDir`. The script runs regression tests, builds the mod, and packages `dist/SharedInventory.zip`. Copy the DLL to `BepInEx/plugins/SharedInventory/` on the server and all clients. No other DLLs from the build references should be copied.

From Linux/WSL with a Linux .NET SDK, run:

```sh
dotnet run --project tests/SharedInventory.Tests.csproj -c Release
dotnet build SharedInventory.csproj -c Release \
  -p:ValheimDir='/mnt/c/Program Files (x86)/Steam/steamapps/common/Valheim' \
  -p:BepInExDir='/mnt/c/Users/seamu/AppData/Roaming/com.kesomannen.gale/valheim/profiles/My Valheim/BepInEx'
```

The mod output is `bin/Release/netstandard2.1/SharedInventory.dll`. The toggle key is configurable in `BepInEx/config/local.sharedinventory.cfg`.

## Saves and limits

The server saves each world's counts to `BepInEx/config/SharedInventory/<world UID>.json` every two seconds while dirty and when the session stops. Saving uses a temporary file and atomic replacement, retaining the previous file as `.bak`. A corrupt save causes requests to fail instead of replacing the inventory with an empty one.

This deliberately simple protocol has no durable transfer receipts or automatic retries. An abrupt process failure can lose the most recent unsaved counts, and a connection lost during a transfer can leave the client and database out of sync. Character saves and shared inventory saves are independent.

## Validation

`tests/` exercises the production count database, client transfer logic, item normalization, and background worker using small game/transport doubles. It covers concurrent-style overdraw, counts beyond Int32, absence of server gameplay caps, incompatible inventory stacks, capacity races, partial insertions, ordinary-deposit returns, death during withdrawal, rejected deposits, and worker thread/order/shutdown behavior.

In the authoring environment, C# syntax and referenced game API signatures were checked against the installed Valheim assemblies. **Compilation, execution of the C# tests, and in-game multiplayer testing remain unverified:** that environment has no Linux .NET SDK and cannot launch Windows .NET through WSL.
