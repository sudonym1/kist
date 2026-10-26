set windows-shell := ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"]

# Show available commands.
default:
    @just --list

# Build this worktree's testing mod and run the automated checks.
build:
    & ./tools/Worktree-Test.ps1 -Action Build

# Build, install this worktree's mod, and open the shared isolated game.
launch:
    & ./tools/Worktree-Test.ps1 -Action Launch
