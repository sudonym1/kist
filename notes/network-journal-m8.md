# M8: Game validation and release

Status: **planned**, after [M7 live journal integration](network-journal-m7.md).
This is the former M7 scope, renumbered when the user requested moving pending
M6 integration work into a later milestone on 2026-10-19.

## Validation matrix

- Run dedicated-server and listen-host sessions with multiple independent
  authenticated clients, supported game assemblies and representative mods.
- Exercise last-stack races, bursts, process termination, connection interruption,
  uncertain outcomes, stale-session traffic, mismatch/suspension/rejoin and
  retained overflow. Verify the documented accepted character loss/duplication
  cases and ensure unrelated replacement items are never removed.
- Confirm Steam, crossplay and TCP transport conclusions under disruption and
  reconnect after M7's live RPC wiring. Validate listen-host session parity.
  Add application retransmission/gap recovery only if transport evidence requires it.
- Verify independent asynchronous replication and bounded game-thread inventory,
  UI and delta work under slow-client and burst conditions. Measure frame/request
  latency and observed stutter against the recorded baseline. Choose numeric
  performance targets or optimizations from evidence.
- Recheck packaged supported-platform providers and representative mod conflicts
  using the actual release candidates. Retain reproducible logs and fault artifacts.

## Completion gate

The matrix passes with dedicated servers and listen hosts, supported runtimes,
assemblies and representative mods. Report measured latency/stutter and accepted
loss/duplication cases. All M7 completion gates remain satisfied. A .NET harness,
Windows-only adapter run or TCP loopback alone cannot establish this result.

Use disposable worlds/characters and verified backups. Independent authenticated
clients and supported OS runtimes are external prerequisites; record unavailable
coverage explicitly. Release readiness does not authorize publication, purchases,
messages or account/security changes.
