set windows-shell := ["pwsh.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"]

# Run the existing production build, tests, and package checks.
build: create-isolated-environment
    & ./tools/Dev.ps1 -Action Build

# Remove build outputs, keeping the isolated game and saves.
clean:
    & ./tools/Dev.ps1 -Action Clean

# Remove build outputs and the isolated game, including its test saves.
clean-all:
    & ./tools/Dev.ps1 -Action CleanAll

# Copy the game/loader and restore preserved test characters, worlds, and hoards.
create-isolated-environment:
    & ./tools/Dev.ps1 -Action Create

# Build and test before installing and launching the isolated game.
run: build
    & ./tools/Dev.ps1 -Action Run
