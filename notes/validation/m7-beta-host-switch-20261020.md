# Regular 0.9.0 hosted-world connection investigation

The user reports no client Kist or invitation path when the other PC hosts,
and disabled withdrawal buttons on that host. Both games are now closed. The
user also reports incorrect password attempts before the successful join.

Observed evidence:

- Supplied host log: `C:\shared\logz\serverbeta\Player.log`, regular Steam
  game with Gale's Default profile, Thor's Kist 0.9.0 loaded successfully.
- Host opens world 1410349507's SQLite authority, reports one hird and starts
  its local journal session. No Kist exception or SQLite failure is logged.
- The local matching Player.log covers prior hosting of world 580196674,
  multiple join attempts, and joining the supplied host at 00:02:38. The
  remote character spawns around 00:03:00 and appears in the host's vanilla
  CharacterID log at 00:03:01. Both plugins report 0.9.0.
- Client logs contain no Kist session start for the remote world and no Kist
  exception. Shutdown hooks are invoked between prior worlds/failed attempts.
- Local Gale config enables the mod, uses F3, disables profiling and sets
  minHirdSize=2. Host membership/ownership/minimum cannot be determined from
  its current log. The two-member minimum can explain disabled buttons if the
  other character never joined the hird, but this is not a confirmed diagnosis.
- Wrong-password errors explain failed earlier joins, not necessarily the
  missing hird after the successful join. Stale session/connection behavior
  remains a hypothesis, not a proven cause.

Original logs are preserved under `.tools/m7/beta-host-switch-evidence` and
archived in `.tools/m7/checkpoints/beta-host-switch-20261020.zip`.

The ordinary logs omit the metadata introduction boundary. Added focused,
deduplicated `KIST CONNECT` messages for RPC registration, initial client
world/role/character/introduction state, ready server selection, ingress/reply
refusals, server authority waits, exact replicated character identity facts on
refusal, authenticated Hello, and received hird/member/founder/minimum/roster
state. These are connection transitions, not profiling. No authentication,
membership, SQLite, protocol or inventory behavior changed. No game/profile
installation or saves were modified.

Temporary diagnostic bundle:
`C:\shared\ThorsKist-0.9.0-connection-diagnostics.zip`, SHA256
`ac304f0e09bc3ee5ea1915b0dcc80966789c1e53807819b4f52c18aa84aa7a9e`.
It preserves the supplied 0.9.0 package's dependencies, native builds, licenses
and release files, replacing only ThorsKist.dll and its journal-runtime checksum
and adding diagnostic instructions. Original release ZIP is unchanged. Package
checksums and Hexium artifact checks pass. Release build has zero warnings/errors;
131 Harmony targets and 138 public runtime references pass verification.

This is a logging build, not a confirmed fix or published release. Install its
plugins contents over the existing Gale ThorsKist plugin folder on both PCs
while games are closed. Start the host, join once, open F3, save/exit, and retain
both newly generated Player.log files, labeled host/client, before restarting.
No performance capture or database copy is required for this next diagnosis.
Unrelated ongoing release/packaging edits remain untouched.
