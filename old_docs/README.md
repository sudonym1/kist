# Historical documentation

These documents preserve earlier implementation plans, validation evidence,
machine-specific setup procedures, and publishing instructions. They are
historical records, not current setup or release directions. Their local file
paths may refer to retired environments; test counts and versions describe the
original runs. Legacy scripts mentioned in them may have been removed.

Use [DEVELOPMENT.md](../DEVELOPMENT.md), [TESTING.md](../TESTING.md),
[PUBLISHING.md](../PUBLISHING.md), [README.md](../README.md), and [API.md](../API.md)
for the current workflow and behavior.

| Archived files | Original purpose |
| --- | --- |
| [DEVELOPMENT.md](DEVELOPMENT.md), [TESTING.md](TESTING.md), [PUBLISHING.md](PUBLISHING.md) | Superseded guides, retained before the justfile/documentation cleanup. |
| [WINDOWS-CODEX-SETUP.md](WINDOWS-CODEX-SETUP.md) | Initial manual isolated game setup and historical seed import. |
| [PLAN.md](PLAN.md) | Refresh recovery implementation and original acceptance evidence. |
| [EINHERJAR-HANDOFF.md](EINHERJAR-HANDOFF.md) | Phased gameplay implementation plan and completion records. |
| [Phase 2](EINHERJAR-PHASE2-RESULTS.md), [Phase 3](EINHERJAR-PHASE3-RESULTS.md), [Phase 4](EINHERJAR-PHASE4-RESULTS.md), [Phase 5](EINHERJAR-PHASE5-RESULTS.md) | Historical asset, chest access, gathering, and outcome results. |
| [PYRAMID-TEST-CLIENT.md](PYRAMID-TEST-CLIENT.md), [results](PYRAMID-TEST-CLIENT-RESULTS.md) | Machine/account-specific second-client deployment. |
| [seed/](seed/README.md) | Original inventory export and provenance; not the current playtest snapshot. |

The one-off `tools/Update-TestClient.ps1` and `tools/export_thorskist.py` scripts
were retired with those procedures. `tools/Worktree-Test.ps1` and its `just launch`
recipe were replaced by the current justfile and `tools/Dev.ps1`; the old helper
depended on a retired shared game copy and built testing packages by default.
Their originals remain in Git history.
The active `tools/Dev.ps1`, build/packaging/SDK scripts, load runner, asset-test
runner, protocol checks, and isolation helper remain supported.

Private saved characters/worlds, shared hoards, and backups are preserved locally
outside the disposable game copies, not in this archive or release ZIPs. See
[preserved test data](../DEVELOPMENT.md#preserved-test-data).
