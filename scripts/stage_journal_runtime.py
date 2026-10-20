#!/usr/bin/env python3
"""Stage audited journal dependencies into a candidate package's plugins folder.

Usage: stage_journal_runtime.py BUILD_DIRECTORY NUGET_PACKAGES PLUGINS_DIRECTORY
       [--rids win-x64 linux-x64 osx-x64]

No restore, downloads, publication, or installed-game writes. The build directory
is normally tools/AdapterTests/bin/Release/net472; its test plugin is excluded.
Use separate packages for osx-x64 and osx-arm64 because their loader names match.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct

MANAGED = (
    "ThorsKist.dll", "ThorsKist.Sync.dll", "ThorsKist.Sync.Sqlite.dll",
    "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
)
NATIVE_PACKAGE = "sqlitepclraw.lib.e_sqlite3"
NATIVE_VERSION = "2.1.14"
NATIVE = {
    "win-x64": "e_sqlite3.dll",
    "linux-x64": "libe_sqlite3.so",
    "osx-x64": "libe_sqlite3.dylib",
    "osx-arm64": "libe_sqlite3.dylib",
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def native_architecture(path, rid):
    data = path.read_bytes()
    if rid == "win-x64":
        if data[:2] != b"MZ" or len(data) < 64:
            raise ValueError("Invalid Windows native image")
        offset = struct.unpack_from("<I", data, 60)[0]
        if data[offset:offset + 4] != b"PE\0\0" or struct.unpack_from("<H", data, offset + 4)[0] != 0x8664:
            raise ValueError("Expected Windows x64 native image")
    elif rid == "linux-x64":
        if data[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", data, 18)[0] != 62:
            raise ValueError("Expected Linux x64 native image")
    else:
        cpu = 0x01000007 if rid == "osx-x64" else 0x0100000C
        if data[:4] != b"\xcf\xfa\xed\xfe" or struct.unpack_from("<I", data, 4)[0] != cpu:
            raise ValueError("Expected " + rid + " native image")


def verify(plugins):
    manifest = json.loads((plugins / "journal-runtime.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != 1 or manifest.get("native_package") != NATIVE_PACKAGE or manifest.get("native_version") != NATIVE_VERSION:
        raise ValueError("Unexpected journal runtime manifest")
    rids = manifest["rids"]
    if not rids or len(set(rids)) != len(rids) or any(r not in NATIVE for r in rids):
        raise ValueError("Invalid runtime selection")
    expected = set(MANAGED) | {NATIVE[r] for r in rids}
    if len({NATIVE[r] for r in rids}) != len(rids):
        raise ValueError("Native loader names collide; use separate architecture packages")
    if set(manifest["sha256"]) != expected:
        raise ValueError("Incomplete journal dependency manifest")
    actual = {p.name for p in plugins.iterdir()}
    if actual != expected | {"journal-runtime.json"}:
        raise ValueError("Unexpected or missing plugin payload: " + repr(actual ^ (expected | {"journal-runtime.json"})))
    for name in expected:
        if digest(plugins / name) != manifest["sha256"][name]:
            raise ValueError("Journal dependency checksum mismatch: " + name)
    for rid in rids:
        native_architecture(plugins / NATIVE[rid], rid)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("build", type=Path)
    parser.add_argument("nuget_packages", type=Path)
    parser.add_argument("plugins", type=Path)
    parser.add_argument("--rids", nargs="+", choices=NATIVE, default=["win-x64", "linux-x64", "osx-x64"])
    args = parser.parse_args()
    if len(set(NATIVE[r] for r in args.rids)) != len(args.rids):
        parser.error("Native loader names collide; stage separate architecture packages")
    sources = {name: args.build / name for name in MANAGED}
    native_root = args.nuget_packages / NATIVE_PACKAGE / NATIVE_VERSION / "runtimes"
    for rid in args.rids:
        source = native_root / rid / "native" / NATIVE[rid]
        native_architecture(source, rid)
        sources[NATIVE[rid]] = source
    for source in sources.values():
        if not source.is_file():
            parser.error("Missing built dependency: " + str(source))
    if args.plugins.exists() and any(args.plugins.iterdir()):
        parser.error("Plugins destination must be empty; refusing to overwrite a candidate")
    args.plugins.mkdir(parents=True, exist_ok=True)
    for name, source in sources.items():
        shutil.copyfile(source, args.plugins / name)
    manifest = {"schema": 1, "native_package": NATIVE_PACKAGE, "native_version": NATIVE_VERSION,
                "rids": args.rids, "sha256": {name: digest(path) for name, path in sources.items()}}
    (args.plugins / "journal-runtime.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    verify(args.plugins)
    print("Staged and verified journal dependencies: " + ", ".join(args.rids))
    print("Architecture and checksum verification does not establish in-game provider compatibility.")


if __name__ == "__main__":
    main()
