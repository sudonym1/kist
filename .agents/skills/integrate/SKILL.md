---
name: integrate
description: Integrate a named Paseo workspace into this repository's main branch, validate the combined changes, then have that workspace's agent rebase onto main. Requires Paseo. Use for /integrate, $integrate, or requests to integrate a workspace.
---

# Integrate a workspace

Accept a workspace name, for example `$integrate puzzled-anaconda`, `/integrate unique-fish`, or "integrate the unique-fish workspace." If no workspace is specified and context does not identify one, ask for its name.

## Paseo dependency

This skill depends on the **Paseo MCP server** for workspace discovery and communication with the source workspace's agent. Discover the available Paseo tools rather than guessing tool schemas. The flow uses `list_workspaces`, `list_agents`, `get_agent_activity`, and `send_agent_prompt`. Shell Git commands handle repository operations.

If Paseo or a required capability is unavailable, report the missing dependency before changing Git state. Do not silently substitute a Git-only workflow: the source agent's rebase is part of completion. Never hardcode workspace IDs, agent IDs, server IDs, worktree paths, or model settings from a previous run.

Invoking this skill requests the local merge, necessary integration fixes, validation, commit, and the source agent's subsequent rebase. Do not push, publish, deploy, archive workspaces, or delete branches unless separately requested. Use existing source agents; creating another agent is not needed for this flow.

## Discover and review

1. Read applicable repository instructions. Inspect `git status --short --branch` and `git worktree list --porcelain`. Locate the worktree holding local `main`; perform integration there without switching another workspace's branch.
2. Use Paseo `list_workspaces` to match the requested name to a workspace in this repository, then `list_agents` scoped to its actual directory. Retain any returned server ID needed for subsequent calls. Use recent agent activity to understand the work and select its responsible agent. Ask only if multiple candidates remain ambiguous. If no source agent exists, report that before merging.
3. Inspect both worktrees' status, the source branch and HEAD, commit history, and the diff from the merge base with main. Read the implementation, tests, and relevant agent handoff. Do not rely solely on a commit title or diff stat.
4. Main must have no unrelated pending changes. Preserve all existing work: do not reset, clean, auto-stash, or accidentally commit someone else's changes. If the source agent is still working or has uncommitted changes, coordinate with that agent to finish and commit the intended work; wait for its report, then review the resulting commit. Do not integrate a partial snapshot as the completed workspace.
5. Review compatibility with current main, including player-facing text, tutorials, tests, version metadata, and release docs. This repository spells the deity's name **Oden**. Update guidance when merged mechanics change its meaning; preserve permanent tutorial seen keys and character history.
6. Before merging, recheck the source HEAD and main status. If the source moved since review, inspect the additional changes first. Keep the source workspace settled until integration finishes.

For Windows worktree ownership errors, use `git -c safe.directory=<verified-worktree-path> -C <verified-worktree-path> ...` for that command. Do not change global Git trust. Use the normal permission escalation mechanism if protected Git metadata requires it.

## Integrate and validate

- Integrate the reviewed local source branch into local `main`, preserving its commits. If main can fast-forward and no integration edits are needed, use `git merge --ff-only`. Otherwise use `git merge --no-ff --no-commit`, reconcile conflicts and necessary cross-feature edits, then commit after validation. If the source is already an ancestor of main, skip the redundant merge and continue to synchronization.
- Resolve conflicts by retaining the intended behavior of both sides, including both sets of validation checks. Do not blindly select an entire side. If a conflict needs an unresolved product decision, leave all work intact and ask about that concrete decision.
- Use `build.ps1` for the repository's gameplay, protocol, fault-harness, production build, release isolation, packaging, and game-hook checks. Discover a usable BepInEx directory containing `core/BepInEx.dll` and `core/Mono.Cecil.dll`; the default game installation may lack them. Existing marked test installations or `.work` payloads can supply references. Use `tools/Resolve-Dotnet.ps1` through the build script for the required .NET SDK.

  ```powershell
  .\build.ps1 -BepInExDir '<discovered BepInEx directory>'
  .\build.ps1 -BepInExDir '<discovered BepInEx directory>' -EnableTestHarness -SkipTests
  ```

  The second command verifies testing-only code compiles and remains opt-in, without rerunning the regression suites. Run additional acceptance only when the change requires it; automated checks do not establish in-game or multiplayer acceptance. Do not deploy or launch an interactive game merely to integrate a branch.
- Keep temporary logs under ignored `.work`. Check actual exit codes and validation summaries. Resolve in-scope failures and rerun affected checks; do not claim a failed or skipped check passed. If a failure prevents completion, report the exact Git state and remaining issue without sending the final rebase instruction.
- Inspect the final diff and run `git -c core.whitespace=cr-at-eol diff --check` plus the staged equivalent when committing. Stage only the reviewed integration files. Commit the merge and integration fixes on main, then confirm a clean working tree and that the reviewed source commit is an ancestor of main. Record the final main SHA. Do not change release versions solely because a merge occurred.

## Have the source agent rebase

Only after the integration is committed and validated, send the responsible Paseo agent a task using `send_agent_prompt` with completion notification enabled. Include the workspace/branch, final main SHA, relevant integration adjustments, and this instruction:

> Rebase your branch onto local main in your workspace. Preserve all work and handle any new uncommitted changes without discarding them. Do not push. Report the final HEAD, whether main is an ancestor, and the clean/dirty state. If a conflict requires a product decision, report it with the work preserved.

Use the tool's completion notification rather than repeatedly polling. Wait for the result; a queued prompt is not a completed rebase. If attention or permission is required, surface the actual blocker and never self-approve another agent's permission request. After success, verify branch ancestry and worktree status with read-only Git checks. The source HEAD may be ahead of main if the agent preserved newer commits; equality is not required, but the final integrated main must be its ancestor.

Finish with the main commit, concise validation results and material limits, the confirmed source rebase/status, and whether anything was pushed. Distinguish an integration that succeeded from a source rebase that remains pending or blocked.
