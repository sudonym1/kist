#!/bin/sh
#
# Publish a built ThorsKist to the GitLab package registry and create (or
# refresh) its release.
#
# Run by CI's publish:release job, and runnable by hand:
#
#     GITLAB_TOKEN=... CI_API_V4_URL=https://gitlab.phospher.com/api/v4 \
#     CI_PROJECT_ID=87 sh scripts/publish_release.sh
#
# Authentication is whichever of these is set, in order:
#   CI_JOB_TOKEN  -> JOB-TOKEN header     (what CI uses; needs no stored secret)
#   GITLAB_TOKEN  -> PRIVATE-TOKEN header (what a human uses)
#
# Every upload is read back and compared by checksum before this exits 0. That
# is not belt-and-braces: publishing here is a separate step from building, so
# the registry CAN hold something other than what the repo says, and it has --
# the 0.8.0 payload sat there for two commits with a stale README and nobody
# could tell from a green pipeline.
set -eu

VERSION="$(cat version.txt)"
DLL="bin/Release/net472/ThorsKist.dll"
ZIP="dist/ThorsKist-${VERSION}.zip"
PKG="${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages/generic/thorskist/${VERSION}"

if [ -n "${CI_JOB_TOKEN:-}" ]; then
    AUTH="JOB-TOKEN: ${CI_JOB_TOKEN}"
elif [ -n "${GITLAB_TOKEN:-}" ]; then
    AUTH="PRIVATE-TOKEN: ${GITLAB_TOKEN}"
else
    echo "ERROR: neither CI_JOB_TOKEN nor GITLAB_TOKEN is set." >&2
    exit 1
fi

echo "Publishing ThorsKist ${VERSION} to project ${CI_PROJECT_ID}"

# ---- the artefacts must exist -----------------------------------------------
# A missing file here means the build job did not hand its artefacts over.
# Uploading nothing and reporting success is the failure mode worth preventing.
for f in "$DLL" "$ZIP"; do
    if [ ! -f "$f" ]; then
        echo "ERROR: $f does not exist. Did test:build's artifacts reach this job?" >&2
        exit 1
    fi
done

# ---- upload, then read back and compare -------------------------------------
upload() {
    src="$1"
    name="$2"

    code=$(curl -sS -o /tmp/upload.json -w "%{http_code}" \
        --upload-file "$src" -H "$AUTH" "${PKG}/${name}")
    if [ "$code" != "201" ] && [ "$code" != "200" ]; then
        echo "ERROR: uploading $name returned HTTP $code" >&2
        cat /tmp/upload.json >&2
        exit 1
    fi

    curl -sS -H "$AUTH" -o "/tmp/readback-${name}" "${PKG}/${name}"
    want=$(sha256sum "$src" | cut -d' ' -f1)
    got=$(sha256sum "/tmp/readback-${name}" | cut -d' ' -f1)
    if [ "$want" != "$got" ]; then
        echo "ERROR: $name in the registry does not match what was uploaded." >&2
        echo "  local:    $want" >&2
        echo "  registry: $got" >&2
        exit 1
    fi
    echo "  ok  ${name}  $(wc -c < "$src") bytes  sha256 $(echo "$want" | cut -c1-16)"
}

upload "$DLL" "ThorsKist.dll"
upload "$ZIP" "ThorsKist-${VERSION}.zip"

# ---- the release ------------------------------------------------------------
# Description comes from the top section of CHANGELOG.md, so the release notes
# and the changelog cannot disagree.
python3 - "$VERSION" "$PKG" > /tmp/release.json <<'PY'
import json
import re
import sys

version, pkg = sys.argv[1], sys.argv[2]

body = ""
with open("CHANGELOG.md", encoding="utf-8") as f:
    text = f.read()
match = re.search(r"^## +" + re.escape(version) + r"\s*$(.*?)(?=^## |\Z)",
                  text, re.M | re.S)
if match:
    body = match.group(1).strip()
if not body:
    # Not fatal, but say so plainly rather than shipping an empty release.
    body = ("No CHANGELOG.md section found for " + version +
            ". See the repository for what changed.")
    print("WARNING: no '## " + version + "' section in CHANGELOG.md",
          file=sys.stderr)

print(json.dumps({
    "name": "ThorsKist " + version,
    "tag_name": "v" + version,
    "ref": "main",
    "description": body,
    "assets": {"links": [
        {"name": "ThorsKist.dll",
         "url": pkg + "/ThorsKist.dll", "link_type": "package"},
        {"name": "ThorsKist-" + version + ".zip (Thunderstore/Hexium payload)",
         "url": pkg + "/ThorsKist-" + version + ".zip", "link_type": "package"},
    ]},
}))
PY

REL="${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/releases"
code=$(curl -sS -o /tmp/release-out.json -w "%{http_code}" -X POST \
    -H "$AUTH" -H "Content-Type: application/json" \
    --data @/tmp/release.json "$REL")

if [ "$code" = "409" ]; then
    # Already released. Refreshing it is the point of re-running this: it is
    # how a docs-only fix reaches the store page without burning a version.
    echo "  release v${VERSION} exists; updating it"
    code=$(curl -sS -o /tmp/release-out.json -w "%{http_code}" -X PUT \
        -H "$AUTH" -H "Content-Type: application/json" \
        --data @/tmp/release.json "${REL}/v${VERSION}")
fi

if [ "$code" != "200" ] && [ "$code" != "201" ]; then
    echo "ERROR: release v${VERSION} returned HTTP $code" >&2
    cat /tmp/release-out.json >&2
    exit 1
fi

echo "  ok  release v${VERSION}"
echo
echo "DLL:     ${PKG}/ThorsKist.dll"
echo "Payload: ${PKG}/ThorsKist-${VERSION}.zip"
echo "Release: ${CI_PROJECT_URL:-}/-/releases/v${VERSION}"
echo "SUCCESS: published and verified by read-back."
