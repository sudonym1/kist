#!/usr/bin/env python3
"""Validate a built package against Hexium's documented requirements.

Usage:
    python3 scripts/verify_hexium.py <package.zip> [expected_version]

Hexium (https://hexium.gg/packaging) accepts Thunderstore-compatible ZIPs, but its
documented rules are STRICTER than what scripts/package_release.py checks -- most
notably the icon must be EXACTLY 256x256, which package_release.py does not verify
at all (it only checks PNG magic bytes and file size). A wrong-sized icon therefore
passes the Thunderstore packaging step and is rejected at upload.

This validates the real artifact -- the zip that actually gets uploaded -- rather
than a staging directory, so it cannot pass while the shipped file is wrong.

Exits non-zero on any violation.
"""
import json
import os
import re
import struct
import sys
import zipfile

REQUIRED_ROOT = ("manifest.json", "icon.png", "README.md")
REQUIRED_FIELDS = ("name", "description", "version_number", "website_url", "dependencies")

NAME_RE = re.compile(r"[A-Za-z0-9_]{1,128}")
VERSION_RE = re.compile(r"\d+\.\d+\.\d+(-(alpha|beta|rc)\.\d+)?")
# TeamName-ModName-Version; the version may itself contain a '-' suffix, so anchor at the end.
DEP_RE = re.compile(r"^.+-[A-Za-z0-9_]+-\d+\.\d+\.\d+(-(alpha|beta|rc)\.\d+)?$")

MAX_BYTES = 512 * 1024 * 1024        # 512 MB
MAX_DESCRIPTION = 256
MAX_FAQ_ENTRIES = 100
MODPACK_DEP_THRESHOLD = 5
ICON_PX = 256

errors, warnings, notes = [], [], []


def err(m):
    errors.append(m)


def warn(m):
    warnings.append(m)


def note(m):
    notes.append(m)


def check_icon(blob):
    if not blob.startswith(b"\x89PNG\r\n\x1a\n"):
        return err("icon.png is not a valid PNG (bad magic bytes)")
    # IHDR width/height are the 8 bytes at offset 16.
    width, height = struct.unpack(">II", blob[16:24])
    if (width, height) != (ICON_PX, ICON_PX):
        err(f"icon.png is {width}x{height}; Hexium requires exactly {ICON_PX}x{ICON_PX}")
    else:
        note(f"icon.png {width}x{height}")


def check_manifest(blob, expected_version):
    try:
        # version.py writes this file utf-8-sig, so the BOM must be tolerated.
        manifest = json.loads(blob.decode("utf-8-sig"))
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        return err(f"manifest.json is not valid JSON: {e}")
    if not isinstance(manifest, dict):
        return err("manifest.json is not a JSON object")

    missing = [f for f in REQUIRED_FIELDS if f not in manifest]
    if missing:
        err(f"manifest.json missing required field(s): {', '.join(missing)} "
            f"(all five are required; use \"\" or [] when empty)")

    name = manifest.get("name")
    if isinstance(name, str) and not NAME_RE.fullmatch(name):
        err(f"manifest name {name!r}: must be 1-128 chars of letters, digits and "
            f"underscores only -- no spaces or dashes")

    desc = manifest.get("description")
    if isinstance(desc, str) and len(desc) > MAX_DESCRIPTION:
        err(f"description is {len(desc)} chars; max {MAX_DESCRIPTION}")

    version = manifest.get("version_number")
    if isinstance(version, str):
        if not VERSION_RE.fullmatch(version):
            err(f"version_number {version!r}: must be Major.Minor.Patch, optionally "
                f"suffixed -alpha.N / -beta.N / -rc.N")
        elif expected_version and version != expected_version:
            err(f"version_number is {version!r}, expected {expected_version!r}")

    if not isinstance(manifest.get("website_url"), str):
        err("website_url must be a string (may be empty)")

    deps = manifest.get("dependencies")
    if not isinstance(deps, list):
        err("dependencies must be an array (may be empty)")
        return manifest
    for d in deps:
        if not isinstance(d, str) or not DEP_RE.match(d):
            err(f"dependency {d!r} is not a valid TeamName-ModName-Version string")
        elif "BepInExPack_Valheim" in d:
            note(f"{d} will be stripped automatically on upload (BepInExPack_Valheim "
                 f"is assumed) -- kept because Thunderstore still needs it")
    if len(deps) > MODPACK_DEP_THRESHOLD:
        warn(f"{len(deps)} dependencies declared; more than {MODPACK_DEP_THRESHOLD} "
             f"gets auto-tagged as a 'modpack'")
    return manifest


def check_faq(names):
    entries = [n for n in names if n.startswith("faq/") and n.lower().endswith(".md")]
    if not entries:
        return
    if len(entries) > MAX_FAQ_ENTRIES:
        err(f"{len(entries)} FAQ entries; max {MAX_FAQ_ENTRIES}")
    for n in entries:
        # faq/page.md -> depth 1 (ungrouped); faq/Section/page.md -> depth 2 (grouped).
        if len(n.split("/")) > 3:
            err(f"FAQ entry {n!r} nests deeper than one subfolder level")
    bad = [n for n in entries if re.search(r'[<>:"\\|?*]', n)]
    for n in bad:
        err(f"FAQ path {n!r} contains characters that do not extract cleanly on Windows")
    note(f"{len(entries)} FAQ entr{'y' if len(entries) == 1 else 'ies'}")


def main(argv):
    if not argv:
        print(__doc__.strip())
        return 2
    path = argv[0]
    expected_version = argv[1] if len(argv) > 1 else None

    if not os.path.isfile(path):
        print(f"ERROR: {path} does not exist")
        return 1

    size = os.path.getsize(path)
    if size > MAX_BYTES:
        err(f"package is {size} bytes; Hexium's limit is {MAX_BYTES} (512 MB)")

    print(f"Package: {path} ({size} bytes)")

    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        root = {n for n in names if "/" not in n}

        for required in REQUIRED_ROOT:
            if required not in root:
                where = next((n for n in names if n.endswith("/" + required)), None)
                if where:
                    err(f"{required} is at {where!r}; it must be at the ROOT of the "
                        f"archive, not inside a subfolder")
                else:
                    err(f"{required} is missing (required at the archive root)")

        if "manifest.json" in root:
            check_manifest(z.read("manifest.json"), expected_version)
        if "icon.png" in root:
            check_icon(z.read("icon.png"))

        if "CHANGELOG.md" in root:
            note("CHANGELOG.md present (shown on the versions page)")
        else:
            warn("no CHANGELOG.md -- optional, but Hexium renders it on the versions page")

        if not any(n.lower().endswith(".dll") for n in names):
            warn("no .dll in the package -- is that intended?")

        check_faq(names)

    for m in notes:
        print(f"  note: {m}")
    for m in warnings:
        print(f"  WARN: {m}")
    for m in errors:
        print(f"  ERROR: {m}")

    if errors:
        print(f"FAILED: {len(errors)} Hexium rule violation(s).")
        return 1
    print("SUCCESS: package satisfies Hexium's documented requirements.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
