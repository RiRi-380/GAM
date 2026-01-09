#!/usr/bin/env python3
"""Aggregate experiment JSONL logs into a session-level CSV."""
import argparse
import csv
import json
from datetime import datetime
from pathlib import Path
import sys

APPLY_START = "AssetApplyExclusiveStart"
APPLY_END = "AssetApplyExclusiveEnd"
UPDATE_END = "UpdateAddonStatesEnd"

USER_ACTION_TYPES = {
    "AssetApplyExclusiveStart",
    "AddonToggle",
    "AssetAddAddon",
    "AssetRemoveAddon",
    "UndoStart",
    "TaskStart",
    "TaskEnd",
}


def parse_timestamp(value: str):
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log_path", help="Path to JSONL log file")
    parser.add_argument("-o", "--out", default="-", help="Output CSV path (default: stdout)")
    args = parser.parse_args()

    log_path = Path(args.log_path)
    if not log_path.exists():
        raise SystemExit(f"Log file not found: {log_path}")

    start_times = {}
    stats = {}

    with log_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue

            session_id = event.get("session_id", "")
            experiment_id = event.get("experiment_id", "")
            condition = event.get("condition", "")
            task_id = event.get("task_id", "")
            key = (session_id, experiment_id, condition, task_id)

            entry = stats.setdefault(
                key,
                {
                    "has_apply": False,
                    "success": True,
                    "apply_durations": [],
                    "operation_count": 0,
                    "undo_count": 0,
                },
            )

            action_type = event.get("action_type")
            result = event.get("result")
            operation_id = event.get("operation_id")

            if action_type == APPLY_START and operation_id:
                monotonic_ms = event.get("monotonic_ms")
                timestamp = parse_timestamp(event.get("timestamp", ""))
                start_times[(key, operation_id)] = monotonic_ms if monotonic_ms is not None else timestamp

            if action_type == APPLY_END:
                entry["has_apply"] = True
                duration = event.get("duration_ms")
                if duration is None and operation_id:
                    started_at = start_times.get((key, operation_id))
                    monotonic_ms = event.get("monotonic_ms")
                    ended_at = monotonic_ms if monotonic_ms is not None else parse_timestamp(event.get("timestamp", ""))
                    if started_at is not None and ended_at is not None:
                        if isinstance(started_at, datetime) and isinstance(ended_at, datetime):
                            duration = int((ended_at - started_at).total_seconds() * 1000)
                        elif isinstance(started_at, (int, float)) and isinstance(ended_at, (int, float)):
                            duration = int(ended_at - started_at)
                if duration is not None:
                    entry["apply_durations"].append(int(duration))

            event_scope = event.get("event_scope")
            if event_scope == "user" or (event_scope is None and action_type in USER_ACTION_TYPES):
                entry["operation_count"] += 1

            if action_type == "UndoStart":
                entry["undo_count"] += 1

            if action_type == "TaskEnd":
                task_success = event.get("task_success")
                if isinstance(task_success, bool):
                    entry["success"] = task_success
                elif result in {"success", "fail"}:
                    entry["success"] = result == "success"

            if action_type in {APPLY_END, UPDATE_END} and result == "fail":
                entry["success"] = False

    rows = []
    for key, entry in stats.items():
        session_id, experiment_id, condition, task_id = key
        if not entry["has_apply"]:
            entry["success"] = False
        switch_time_ms = sum(entry["apply_durations"]) if entry["apply_durations"] else ""
        rows.append(
            {
                "session_id": session_id,
                "experiment_id": experiment_id,
                "condition": condition,
                "task_id": task_id,
                "success": str(entry["success"]).lower(),
                "switch_time_ms": switch_time_ms,
                "operation_count": entry["operation_count"],
                "undo_count": entry["undo_count"],
            }
        )

    fieldnames = [
        "session_id",
        "experiment_id",
        "condition",
        "task_id",
        "success",
        "switch_time_ms",
        "operation_count",
        "undo_count",
    ]

    if args.out == "-":
        writer = csv.DictWriter(sys.stdout, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)
        return 0

    with Path(args.out).open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    return 0


if __name__ == "__main__":
    sys.exit(main())
