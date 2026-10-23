"""Read a standalone ThorsKist SQLite snapshot and export SharedInventory counts.

Only current_stacks are stock. Checkpoints, journals, and request results are
history/protocol records and must never be added to the current quantities.
"""

import argparse
from collections import Counter
from contextlib import closing
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import struct
import tempfile
import uuid


class Reader:
    def __init__(self, data):
        self.data = data
        self.position = 0

    def take(self, size):
        if size < 0 or size > len(self.data) - self.position:
            raise ValueError("Truncated ThorsKist storage row")
        result = self.data[self.position : self.position + size]
        self.position += size
        return result

    def integer(self):
        return struct.unpack("<i", self.take(4))[0]

    def blob(self):
        return self.take(self.integer())

    def text(self):
        return self.blob().decode("utf-8", errors="strict")


def decode_stack(data, expected_id):
    # Mirrors StorageEncoding.Encode(Stack): Int32 envelope version, length-
    # prefixed GUID text, Int32 slot, prefab text, quantity, format, payload bytes.
    reader = Reader(data)
    if reader.integer() != 1:
        raise ValueError("Unsupported ThorsKist storage envelope")
    stack_id = reader.text()
    if uuid.UUID(stack_id).hex != expected_id:
        raise ValueError("Stack ID does not match its database row")
    reader.integer()  # Position is intentionally discarded.
    prefab = reader.text()
    quantity = reader.integer()
    item_format = reader.integer()
    reader.blob()  # All item metadata is intentionally discarded.
    if reader.position != len(data) or not prefab or quantity <= 0:
        raise ValueError("Invalid current stack record")
    return prefab, quantity, item_format


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inspect(source):
    source = source.resolve(strict=True)
    wal = Path(str(source) + "-wal")
    if wal.exists() and wal.stat().st_size:
        raise ValueError("Source has a WAL; first create a consistent standalone SQLite backup")
    original_hash = sha256(source)
    # Work on a byte-for-byte copy. This also avoids SQLite locking/sidecar writes
    # on a source mounted from Windows. The source is never opened by SQLite.
    with tempfile.TemporaryDirectory(prefix="sharedinventory-export-") as temporary:
        copy = Path(temporary) / "snapshot.sqlite"
        shutil.copyfile(source, copy)
        if sha256(copy) != original_hash or sha256(source) != original_hash:
            raise ValueError("Source changed while being copied")
        with closing(sqlite3.connect(copy.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
            if db.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
                raise ValueError("Source failed SQLite integrity_check")
            if db.execute("PRAGMA foreign_key_check").fetchall():
                raise ValueError("Source failed SQLite foreign_key_check")
            vaults = db.execute(
                "SELECT id,world,vault,head,capacity FROM vaults "
                "WHERE id NOT IN (SELECT vault_id FROM retired_vaults)"
            ).fetchall()
            if len(vaults) != 1:
                raise ValueError("Expected exactly one active vault; select/inspect a vault before exporting")
            vault_id, world, vault, head, capacity = vaults[0]
            counts = Counter()
            formats = Counter()
            stacks = 0
            for stack_id, body in db.execute(
                "SELECT stack_id,body FROM current_stacks WHERE vault_id=?", (vault_id,)
            ):
                prefab, quantity, item_format = decode_stack(body, stack_id)
                counts[prefab] += quantity
                if counts[prefab] > 2**63 - 1:
                    raise OverflowError("Count exceeds the mod's Int64 database format")
                formats[item_format] += 1
                stacks += 1
            info = {
                "source_path": str(source),
                "source_sha256": original_hash,
                "sqlite_integrity_check": "ok",
                "sqlite_foreign_key_check": "ok",
                "sqlite_user_version": db.execute("PRAGMA user_version").fetchone()[0],
                "world_uid": world,
                "vault_id": vault,
                "journal_head": head,
                "source_capacity": capacity,
                "source_current_stacks": stacks,
                "item_format_versions": dict(sorted(formats.items())),
                "distinct_prefabs": len(counts),
                "total_units": sum(counts.values()),
            }
    if sha256(source) != original_hash:
        raise ValueError("Source changed during inspection")
    return dict(sorted(counts.items())), info


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    counts, info = inspect(args.source)
    if args.baseline:
        before, baseline = inspect(args.baseline)
        if (info["world_uid"], info["vault_id"]) != (baseline["world_uid"], baseline["vault_id"]):
            raise ValueError("Baseline belongs to a different world or vault")
        info["baseline"] = baseline
        info["delta_from_baseline"] = {
            item: counts.get(item, 0) - before.get(item, 0)
            for item in sorted(counts.keys() | before.keys())
            if counts.get(item, 0) != before.get(item, 0)
        }
    args.output.mkdir(parents=True, exist_ok=True)
    inventory = args.output / "inventory.json"
    inventory.write_text(json.dumps(counts, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    assert json.loads(inventory.read_text(encoding="utf-8")) == counts
    info.update({
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "inventory_file": inventory.name,
        "inventory_sha256": sha256(inventory),
        "stock_source_table": "current_stacks",
        "metadata_policy": "Discarded; aggregate quantities by exact prefab ID",
    })
    (args.output / "inventory-source.json").write_text(
        json.dumps(info, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n"
    )
    with (args.output / "inventory-items.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(["prefab", "count"])
        writer.writerows(counts.items())
    print(json.dumps(info, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
