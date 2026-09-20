#!/usr/bin/env python3
"""Single source of truth for the ThorsKist version.

Usage:
    python3 scripts/version.py sync <NEW_VERSION>
        Set manifest.json version_number, the VERSION const in Main.cs, and
        version.txt to the given semver-ish 1.2.3 version.

    python3 scripts/version.py check
        Verify manifest.json version_number == Main.cs VERSION == version.txt.
        Exits non-zero (printing which value mismatches) if they differ.
"""
import json
import re
import sys

SEMVER_RE = re.compile(r"^\d+\.\d+\.\d+$")
MANIFEST = "manifest.json"
MAIN_CS = "Main.cs"
VERSION_TXT = "version.txt"
VERSION_CONST_RE = re.compile(
    r'^(\s*public\s+const\s+string\s+VERSION\s*=\s*)"([^"]*)"(\s*;)', re.MULTILINE
)


def read_text(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return f.read()


def write_text(path, text):
    # utf-8-sig re-adds the BOM on write, preserving whatever BOM was present.
    with open(path, "w", encoding="utf-8-sig") as f:
        f.write(text)


def read_manifest():
    return json.loads(read_text(MANIFEST))


def read_version_const():
    match = VERSION_CONST_RE.search(read_text(MAIN_CS))
    if not match:
        raise RuntimeError("Could not find VERSION const in Main.cs")
    return match.group(1), match.group(2), match.group(3)


def current_values():
    manifest = read_manifest()
    _, const, _ = read_version_const()
    with open(VERSION_TXT, "r", encoding="utf-8-sig") as f:
        txt = f.read().strip()
    return {
        "manifest.json": manifest.get("version_number"),
        "Main.cs VERSION": const,
        "version.txt": txt,
    }


def do_check():
    values = current_values()
    ok = True
    for name, value in sorted(values.items()):
        print(f"  {name}: {value if value is not None else '<missing>'}")
    keys = list(values.keys())
    baseline = values[keys[0]]
    for name, value in values.items():
        if value != baseline:
            print(f"ERROR: {name} ({value}) does not match {keys[0]} ({baseline})")
            ok = False
    return ok


def do_sync(new_version):
    if not SEMVER_RE.match(new_version):
        print(f"ERROR: '{new_version}' is not a valid version (expected X.Y.Z)")
        return False

    # manifest.json
    manifest_path = MANIFEST
    manifest_text = read_text(manifest_path)
    manifest = json.loads(manifest_text)
    manifest["version_number"] = new_version
    # Preserve key ordering by rewriting from the parsed dict; keep indentation
    # matching the current file (2 spaces).
    write_text(manifest_path, json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
    print(f"  manifest.json version_number -> {new_version}")

    # Main.cs VERSION const
    main_text = read_text(MAIN_CS)
    match = VERSION_CONST_RE.search(main_text)
    if not match:
        print("ERROR: could not find VERSION const in Main.cs")
        return False
    prefix, _old, suffix = match.group(1), match.group(2), match.group(3)
    new_text = main_text[: match.start()] + prefix + f'"{new_version}"' + suffix + main_text[match.end():]
    write_text(MAIN_CS, new_text)
    print(f"  Main.cs VERSION -> {new_version}")

    # version.txt has no BOM; it is read as plain text (e.g. `cat` in CI).
    with open(VERSION_TXT, "w", encoding="utf-8") as f:
        f.write(new_version + "\n")
    print(f"  version.txt -> {new_version}")
    return True


def main(argv):
    if not argv:
        print(__doc__.strip())
        return 2
    cmd = argv[0]
    if cmd == "check":
        return 0 if do_check() else 1
    if cmd == "sync":
        if len(argv) < 2:
            print("Usage: python3 scripts/version.py sync <NEW_VERSION>")
            return 2
        return 0 if do_sync(argv[1]) else 1
    print(f"Unknown command: {cmd}")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
