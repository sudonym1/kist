# Remote invite failure, v22

The first real two-PC v19 QA session connected M7TestRemote to M7TEST, but the
host's invite roster omitted the remote character. Remote startup logs show both
plugins loaded and test-save isolation active. Its installation manifest matches
all 29 v19 payload hashes. Reinstalling the same payload is not a remedy.

Both production metadata authentication and journal item identification required
`ZNetPeer.m_playerID`. The host logs contain CharacterID RPCs but no PlayerID
RPC. Inspection of the matching real Valheim assembly confirms SetCharacterID
sends only CharacterID; RPC_CharacterID sets m_characterID without m_playerID.
Consequently the required comparison to the remote character ID rejects the
Hello before NameByPlayer is populated. This is an M7 remote integration defect.

Both paths now share TryCharacter/ReadCharacter: select the exact peer's
m_characterID ZDO, verify its owner equals the current peer UID, then derive the
nonzero player ID and finite position from that object. Metadata still compares
the claimed character to that derived ID. Existing ready/current RPC, connected
authenticated socket, server direction, membership and session checks remain.
No fallback to a claimed header or roster identity was introduced. No SQLite,
protocol or persistence changes were made.

The remote Player.log also shows AuthenticatedPeerProbe.Tick null references
while connecting, before ZNet has received its World. The probe now guards
GetWorldUID with GetWorld readiness and rebuilds its inspector when the world ID
arrives. This separate test-plugin error did not explain the production ingress
comparison, but needed correction too.

Validation: Release build has zero warnings/errors; 126 Harmony targets resolve
and 123 real-game runtime references are public. A separate native menu process
passes nine existing control framing assertions plus ten new replicated-object
identity assertions. The latter cover absent ZDO/owner/ID, correct identity and
position, wrong/zero peer, three nonfinite positions and ownership reassignment.
Native fixtures write independent replicated extra data and never touch saves.
An initial fixture incorrectly called SetOwnerInternal before a ZDOMan existed;
it was corrected to seed replica extra data directly. The final transcript is
[native-checks.txt](native-checks.txt). Real connected two-PC invitation, acceptance
and journal transfers still require user QA; synthetic checks do not close that
gate.

Final bundle: `.tools/m7/ThorsKist-M7-Windows-Journal-v22.zip`, SHA256
`f3962483f28057fef30425bd45648d3674a6b354de662612cbf900922c041f0b`.
Setup and Update share 29 payload files. v20/v21 retain intermediate test fixture
iterations; use v22 on both PCs. The v22 ZIP is also in `C:\shared`.
With all Valheim processes closed, the host saves were archived to
`.tools/m7/checkpoints/journal-before-invite-v22.zip` before updating its isolated
installation. All 29 installed payload hashes match and all 22 saved files remain
byte-identical. Raw remote and host diagnostic logs are preserved in
`.tools/m7/checkpoints/journal-invite-v19-evidence.zip`.
