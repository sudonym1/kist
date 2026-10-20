# ServerSync socket-wrapper fix

The new host log in `C:\shared\logs\Player.log` identifies the failure:
control authentication sees a ready, connected BufferingSocket; after initial
replication the character's actual ID and owner match the requesting peer, but
the account resolver still refuses it. The screenshot shows Beauj as founder,
three members, and an empty invite roster, ruling out the earlier minimum-size
hypothesis. The host also records a successful withdrawal in this session.

Both installed Build Camera and MistBeGone embed ServerSync's private
`ConfigSync.SendConfigsAfterLogin.BufferingSocket`. This inherits ZPlayFabSocket
even when Original is a Steam socket. The old resolver treated it as PlayFab,
whose wrapper account field was empty, and never authenticated the remote user.

`JournalSocketTransport` follows the known wrapper's readonly Original ISocket
field before resolving Steam or PlayFab identity. Independent embedded copies
can nest. Reflection metadata is cached by runtime type; traversal is bounded,
and malformed/null/cyclic chains fail closed. Unknown TCP endpoints remain
unauthenticated. The peer and RPC continue using ServerSync's wrapper, preserving
buffering and exact connection/character ownership checks. No persistence,
membership or profiling settings changed.

An isolated native test loads the actual installed wrapper implementations as
fixtures, without starting those mods. All **17 assertions pass**, covering
direct account identity, real Steam transport unwrapping, real PlayFab resolution,
independent nested copies, wrapped/direct TCP refusal, null transport and cycles.
Release build has zero warnings/errors; 131 Harmony targets and 138 runtime
references pass. Third-party fixture DLLs are not redistributed.

Published as requested:
`C:\shared\ThorsKist-0.9.0-serversync-fix.zip`, SHA256
`d5192f8d6d572183101bfffe9807b458a84046600623289b8fc42087c57f35bc`.
The consistent managed build and runtime checksums are updated; native libraries,
symbols, licenses and other release payload come from the original 0.9.0 ZIP.
ZIP integrity/runtime checksums and package artifact checks pass. The copied
shared archive hash matches. Original archives remain untouched. Matching
isolated Windows Setup/Update installers are available as v28 in C:\shared.

Raw host/client logs and the screenshot are preserved under
`.tools/m7/socket-wrapper-evidence`; native transcript is
`.tools/m7/socket-wrapper-probe/cutover-evidence.log`. User will retest the new
regular Gale package on both PCs. No normal installation or user save was
modified by the agent. Unrelated ongoing release changes remain unstaged.
