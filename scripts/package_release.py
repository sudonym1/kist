#!/usr/bin/env python3
"""Package and validate a Thunderstore release for ThorsKist.

Usage:
    python3 scripts/package_release.py <staging_dir> <version>

    <staging_dir> is the thunderstore package folder, named <ModName>-<version>
    and laid out as:

        <ModName>-<version>/
          manifest.json
          README.md
          CHANGELOG.md      (optional; Hexium renders it on the versions page)
          icon.png
          plugins/ThorsKist.dll
          plugins/ThorsKist.Sync.dll

    The script validates the payload and creates
    <staging_dir>/../<ModName>-<version>.zip containing the package CONTENTS
    (manifest.json, README.md, icon.png, plugins/) at the zip root (the
    standard Thunderstore layout), not wrapped in a version-named folder.

    Exits non-zero on any validation failure.
"""
import json
import os
import sys
import zipfile

MOD_NAME = "ThorsKist"
MAX_ICON_BYTES = 512000
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def die(msg):
    print(f"ERROR: {msg}")
    return False


def validate(staging_dir, version):
    ok = True

    if not os.path.isdir(staging_dir):
        ok = die(f"staging dir '{staging_dir}' is not a directory") and ok
        return ok

    # manifest.json
    manifest_path = os.path.join(staging_dir, "manifest.json")
    if not os.path.isfile(manifest_path):
        ok = die(f"{manifest_path} does not exist") and ok
    else:
        try:
            with open(manifest_path, "r", encoding="utf-8-sig") as f:
                manifest = json.load(f)
            if not isinstance(manifest, dict):
                ok = die("manifest.json is not a JSON object") and ok
            else:
                name = manifest.get("name")
                ver = manifest.get("version_number")
                if name != MOD_NAME:
                    ok = die(f"manifest.json name is '{name}', expected '{MOD_NAME}'") and ok
                if ver != version:
                    ok = die(f"manifest.json version_number is '{ver}', expected '{version}'") and ok
        except json.JSONDecodeError as e:
            ok = die(f"manifest.json is not valid JSON: {e}") and ok

    # icon.png
    icon_path = os.path.join(staging_dir, "icon.png")
    if not os.path.isfile(icon_path):
        ok = die(f"{icon_path} does not exist") and ok
    else:
        with open(icon_path, "rb") as f:
            head = f.read(8)
        if not head.startswith(PNG_MAGIC):
            ok = die("icon.png is not a valid PNG (bad magic bytes)") and ok
        size = os.path.getsize(icon_path)
        if size > MAX_ICON_BYTES:
            ok = die(f"icon.png is {size} bytes, exceeds {MAX_ICON_BYTES}") and ok
        else:
            print(f"  icon.png: valid PNG, {size} bytes")

    # Both the plugin and its state-sync dependency must be installed together.
    for dll_name in (f"{MOD_NAME}.dll", "ThorsKist.Sync.dll"):
        dll_path = os.path.join(staging_dir, "plugins", dll_name)
        if not os.path.isfile(dll_path):
            ok = die(f"{dll_path} does not exist") and ok
        else:
            print(f"  {os.path.relpath(dll_path)}: present")

    plugins = os.path.join(staging_dir, "plugins")
    if os.path.isfile(os.path.join(plugins, "ThorsKist.Sync.Sqlite.dll")) or os.path.isfile(os.path.join(plugins, "journal-runtime.json")):
        try:
            from pathlib import Path
            from stage_journal_runtime import verify
            runtime = verify(Path(plugins))
            print("  journal dependencies: checksums and native architectures verified (" + ", ".join(runtime["rids"]) + ")")
        except (ValueError, OSError, KeyError) as error:
            ok = die(str(error)) and ok

    # README.md
    readme_path = os.path.join(staging_dir, "README.md")
    if not os.path.isfile(readme_path):
        ok = die(f"{readme_path} does not exist") and ok

    return ok


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip())
        return 2
    staging_dir, version = argv[0], argv[1]

    if not validate(staging_dir, version):
        return 1

    zip_name = f"{MOD_NAME}-{version}.zip"
    zip_path = os.path.join(os.path.dirname(os.path.abspath(staging_dir)), zip_name)

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _dirs, files in os.walk(staging_dir):
            for fname in sorted(files):
                full = os.path.join(root, fname)
                rel = os.path.relpath(full, staging_dir)
                zf.write(full, rel)

    print("  package contents placed at the zip root (not wrapped in a folder)")
    print(f"  wrote {zip_path}")
    print("SUCCESS: payload validated and packaged.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
