"""Analyze journal stages and join submissions to effects/acks without opening saves.

Usage: python analyze.py CAPTURE_DIRECTORY OUTPUT_DIRECTORY
Raw inputs are preserved in the local checkpoint archive recorded in the M7 log.
"""
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys


def stats(values):
    values = sorted(values)
    if not values:
        return {"count": 0}
    return {"count": len(values), "mean_ms": round(statistics.mean(values), 6),
            "p50_ms": round(values[math.ceil(len(values) * .5) - 1], 6),
            "p95_ms": round(values[math.ceil(len(values) * .95) - 1], 6),
            "max_ms": round(values[-1], 6)}


def analyze(source, output):
    output.mkdir(parents=True, exist_ok=True)
    events = list(csv.DictReader((source / "journal-events.csv").open()))
    for event in events:
        for key in ["start_ms", "duration_ms"]:
            event[key] = float(event[key])
        event["end_ms"] = event["start_ms"] + event["duration_ms"]
    submissions = [e for e in events if e["stage"] in ["client.withdraw.submitted", "client.deposit.submitted"]]
    latencies, missing = [], []
    for submit in submissions:
        matched = [e for e in events if e["request"] == submit["request"]]
        effects = [e for e in matched if e["stage"] == "unity.inventory_effect.work"]
        acks = [e for e in matched if e["stage"] == "client.ack.round_trip"]
        if len(effects) != 1 or len(acks) != 1:
            missing.append(submit["request"])
            continue
        latencies.append({"request": submit["request"], "kind": "withdraw" if "withdraw" in submit["stage"] else "deposit",
                          "sequence": effects[0]["sequence"],
                          "effect_complete_ms": round(effects[0]["end_ms"] - submit["start_ms"], 6),
                          "ack_complete_ms": round(acks[0]["end_ms"] - submit["start_ms"], 6)})
    summary = {"capture": source.name,
               "input_sha256": {name: hashlib.sha256((source / name).read_bytes()).hexdigest()
                                for name in ["journal-events.csv", "methods.csv"]},
               "events": len(events), "submissions": len(submissions), "unmatched_requests": missing,
               "stages": {stage: stats(e["duration_ms"] for e in events if e["stage"] == stage)
                          for stage in sorted({e["stage"] for e in events})},
               "command_round_trip": stats(e["duration_ms"] for e in events
                                           if e["stage"] == "client.request.round_trip" and e["operation"] == "command"),
               "click_to_effect": stats(e["effect_complete_ms"] for e in latencies),
               "click_to_ack_complete": stats(e["ack_complete_ms"] for e in latencies),
               "synchronous_main_work": stats(e["duration_ms"] for e in events if e["kind"] == "Work" and e["start_on_main"] == "1"),
               "full_recovery_or_replacement_events": [e["stage"] for e in events if e["stage"] in
                   ["sqlite.full_recovery", "sqlite.candidate.validate", "sqlite.previous.read_verify", "sqlite.successor.validate", "sqlite.rows.delete_all", "sqlite.bootstrap.write"]]}
    methods = list(csv.DictReader((source / "methods.csv").open()))
    methods = [m for m in methods if m["method"] != "Main.Update" and not m["method"].startswith(("KistUI.", "KistSort.", "UnityUI."))]
    summary["largest_non_drawing_method"] = max(methods, key=lambda m: float(m["max_ms"]))
    (output / "analysis.json").write_text(json.dumps(summary, indent=2) + "\n")
    with (output / "request-latencies.csv").open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["request", "kind", "sequence", "effect_complete_ms", "ack_complete_ms"])
        writer.writeheader()
        writer.writerows(latencies)
    print(json.dumps({k: summary[k] for k in ["capture", "events", "submissions", "unmatched_requests", "command_round_trip", "click_to_effect", "click_to_ack_complete", "synchronous_main_work"]}, indent=2))


if __name__ == "__main__":
    analyze(Path(sys.argv[1]), Path(sys.argv[2]))
