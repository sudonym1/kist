#!/usr/bin/env bash
#
# Collect the reference assemblies ThorsKist compiles against, from YOUR OWN
# copy of Valheim.
#
#     ./scripts/setup-refs.sh [/path/to/Valheim]
#
# These assemblies belong to Iron Gate and Unity Technologies, so they are not
# in this repository and never will be -- you already own a copy, and this
# fetches it from there. Nothing here is downloaded from the internet.
#
# Needs a BepInEx install under the game directory for BepInEx.dll and
# 0Harmony.dll, and the `assembly-publicizer` dotnet tool:
#
#     dotnet tool install -g Krafs.AssemblyPublicizer.Tool
set -euo pipefail

GAME="${1:-${VALHEIM_DIR:-}}"

if [ -z "$GAME" ]; then
    for guess in \
        "$HOME/.steam/steam/steamapps/common/Valheim" \
        "$HOME/.local/share/Steam/steamapps/common/Valheim" \
        "/c/Program Files (x86)/Steam/steamapps/common/Valheim" \
        "$HOME/Library/Application Support/Steam/steamapps/common/Valheim"
    do
        if [ -d "$guess" ]; then GAME="$guess"; break; fi
    done
fi

if [ -z "$GAME" ] || [ ! -d "$GAME" ]; then
    echo "Could not find Valheim. Pass its directory:" >&2
    echo "    ./scripts/setup-refs.sh /path/to/steamapps/common/Valheim" >&2
    exit 1
fi

MANAGED=""
for d in "$GAME/valheim_Data/Managed" "$GAME/valheim_server_Data/Managed"; do
    if [ -d "$d" ]; then MANAGED="$d"; break; fi
done
if [ -z "$MANAGED" ]; then
    echo "No Managed/ directory under $GAME -- is that really the game folder?" >&2
    exit 1
fi

echo "Valheim:  $GAME"
echo "Managed:  $MANAGED"

mkdir -p lib .refs

GAME_DLLS="
assembly_valheim.dll
assembly_utils.dll
assembly_guiutils.dll
Splatform.dll
netstandard.dll
System.Memory.dll
System.Runtime.CompilerServices.Unsafe.dll
Unity.TextMeshPro.dll
UnityEngine.dll
UnityEngine.CoreModule.dll
UnityEngine.AnimationModule.dll
UnityEngine.AudioModule.dll
UnityEngine.ImageConversionModule.dll
UnityEngine.IMGUIModule.dll
UnityEngine.InputLegacyModule.dll
UnityEngine.PhysicsModule.dll
UnityEngine.TextRenderingModule.dll
UnityEngine.UI.dll
UnityEngine.UIModule.dll
"

missing=0
for dll in $GAME_DLLS; do
    if [ -f "$MANAGED/$dll" ]; then
        cp -f "$MANAGED/$dll" "lib/$dll"
    else
        echo "  MISSING  $dll" >&2
        missing=$((missing + 1))
    fi
done

# BepInEx ships its own loader and HarmonyX. Both are open source (LGPL-2.1 and
# MIT), but they still are not ours to vendor here.
BEPINEX=""
for d in "$GAME/BepInEx/core" "$GAME/BepInEx/Core"; do
    if [ -d "$d" ]; then BEPINEX="$d"; break; fi
done
if [ -n "$BEPINEX" ]; then
    for dll in BepInEx.dll 0Harmony.dll; do
        if [ -f "$BEPINEX/$dll" ]; then
            cp -f "$BEPINEX/$dll" "lib/$dll"
        else
            echo "  MISSING  $dll (in $BEPINEX)" >&2
            missing=$((missing + 1))
        fi
    done
else
    echo "  MISSING  BepInEx/core -- install BepInEx into the game first" >&2
    missing=$((missing + 2))
fi

# The UNPUBLICIZED assembly, kept separately. tools/VerifyPatches checks every
# member reference against this one: the build compiles against a publicized
# copy where everything is public, so calling something the shipped game keeps
# private compiles clean and throws MethodAccessException on the first frame.
# Checking against the publicized copy would prove nothing.
cp -f "$MANAGED/assembly_valheim.dll" ".refs/assembly_valheim.dll"

echo
if command -v assembly-publicizer >/dev/null 2>&1; then
    assembly-publicizer --input "$MANAGED/assembly_valheim.dll" \
        --output "lib/assembly_valheim_publicized.dll" >/dev/null
    echo "  publicized assembly_valheim.dll -> lib/assembly_valheim_publicized.dll"
else
    echo "  MISSING  assembly-publicizer. Install it with:" >&2
    echo "             dotnet tool install -g Krafs.AssemblyPublicizer.Tool" >&2
    missing=$((missing + 1))
fi

echo
if [ "$missing" -gt 0 ]; then
    echo "$missing reference(s) missing -- the build will fail until they are there." >&2
    exit 1
fi

echo "All references in place. Now:"
echo "    dotnet build ThorsKist.csproj -c Release"
