import argparse
import json
import os
import shutil
import time
import uuid
from datetime import datetime, timezone
from typing import Dict, List, Set


def load_json(path: str):
    for enc in ("utf-8", "utf-8-sig"):
        try:
            with open(path, "r", encoding=enc) as f:
                return json.load(f)
        except json.JSONDecodeError:
            continue
    raise ValueError(f"Failed to decode JSON: {path}")


def write_json(path: str, obj) -> None:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=4)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def build_addonnomount(disabled_ids: List[str]) -> str:
    lines = ["\"addonnomount\"", "{"]
    for idx, addon_id in enumerate(disabled_ids, start=1):
        lines.append(f"\t\"{idx}\"\t\t\"{addon_id}\"")
    lines.append("}")
    return "\n".join(lines) + "\n"


def parse_addonnomount(path: str) -> Set[str]:
    if not os.path.exists(path):
        return set()
    disabled = set()
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("\"") and "\"\t\t\"" in line:
                parts = line.split("\"")
                if len(parts) >= 4 and parts[3].isdigit():
                    disabled.add(parts[3])
    return disabled


def canonical_hash(m_ids: List[str], enabled_ids: Set[str]) -> str:
    import hashlib
    lines = []
    for addon_id in m_ids:
        lines.append(f"{addon_id}={'1' if addon_id in enabled_ids else '0'}")
    payload = "\n".join(lines).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-run-index", required=True)
    parser.add_argument("--log-root", required=True)
    parser.add_argument("--condition", default="BL-Soft-Copy")
    args = parser.parse_args()

    source_run_index = args.source_run_index
    log_root = args.log_root
    condition = args.condition

    os.makedirs(log_root, exist_ok=True)

    run_index = load_json(source_run_index)
    envs = []
    for env in run_index["envs"]:
        combined = dict(env)
        manifest_path = env.get("envManifest")
        if manifest_path and os.path.exists(manifest_path):
            try:
                env_manifest = load_json(manifest_path)
                combined.update(env_manifest)
            except Exception:
                pass
        envs.append(combined)
    runs = run_index["runs"]
    seed = run_index.get("seed")

    # Copy manifests into new log root.
    new_runs = []
    for run in runs:
        src_manifest = run["manifest"]
        dst_manifest = os.path.join(log_root, os.path.basename(src_manifest))
        shutil.copy2(src_manifest, dst_manifest)
        new_runs.append({
            "runId": run["runId"],
            "envName": run["envName"],
            "manifest": dst_manifest,
        })

    new_run_index = {
        "runId": os.path.basename(log_root),
        "createdAt": datetime.now().isoformat(),
        "seed": seed,
        "sourceWorkshopPath": run_index.get("sourceWorkshopPath"),
        "sizes": run_index.get("sizes"),
        "ratios": run_index.get("ratios"),
        "envs": envs,
        "runs": new_runs,
    }
    write_json(os.path.join(log_root, "run_index.json"), new_run_index)

    # Run baseline copy for each manifest.
    for run in new_runs:
        manifest = load_json(run["manifest"])
        run_id = run["runId"]
        env_name = run["envName"]

        env = next(e for e in envs if e["envName"] == env_name)
        gmod_root = env["gmodRoot"]
        addonnomount_path = os.path.join(gmod_root, "garrysmod", "cfg", "addonnomount.txt")
        os.makedirs(os.path.dirname(addonnomount_path), exist_ok=True)

        m_ids = [str(x) for x in manifest["addonIds"]]
        m_ids_sorted = sorted(m_ids)
        asset_sets = manifest["assetSets"]
        tasks = manifest["tasks"]
        repeats = int(manifest["repeats"])
        overlap_ratio = manifest.get("overlapRatio")

        # Precompute addonnomount content per asset.
        asset_files: Dict[str, str] = {}
        for asset_name, asset in asset_sets.items():
            enabled = set(asset.get("enabled", []))
            disabled = sorted([a for a in m_ids_sorted if a not in enabled])
            asset_files[asset_name] = build_addonnomount(disabled)

        events_path = os.path.join(log_root, f"events_{run_id}.jsonl")
        canonical_path = os.path.join(log_root, f"canonical_{run_id}.jsonl")
        session_id = uuid.uuid4().hex
        base_time = time.perf_counter()

        with open(events_path, "w", encoding="utf-8") as events, open(canonical_path, "w", encoding="utf-8") as canonical:
            canonical.write(json.dumps({
                "Event": "run_start",
                "TimestampUtc": utc_now(),
                "RunId": run_id,
                "EnvName": env_name,
                "MSize": manifest.get("mSize"),
                "OverlapRatio": overlap_ratio,
                "Seed": seed,
                "ExperimentId": run_id,
                "Condition": condition,
                "EventLogPath": events_path,
                "AddonIds": m_ids_sorted,
            }, ensure_ascii=False) + "\n")

            # SessionStart
            events.write(json.dumps({
                "schema_version": "3",
                "strict_link_mode": False,
                "event_scope": "external",
                "monotonic_ms": int((time.perf_counter() - base_time) * 1000),
                "trial_index": None,
                "timestamp": utc_now(),
                "session_id": session_id,
                "experiment_id": run_id,
                "condition": condition,
                "task_id": "",
                "action_type": "SessionStart",
                "target_id": None,
                "result": "success",
                "duration_ms": None,
                "before_hash": None,
                "after_hash": None,
                "expected_hash": None,
                "state_hash_scope": None,
                "expected_hash_scope": None,
            }, ensure_ascii=False) + "\n")

            for repeat in range(1, repeats + 1):
                for task in tasks:
                    task_id = task["id"]
                    from_asset = task["from"]
                    to_asset = task["to"]

                    # Read before state.
                    disabled_before = parse_addonnomount(addonnomount_path)
                    enabled_before = set([a for a in m_ids_sorted if a not in disabled_before])
                    canonical_before = canonical_hash(m_ids_sorted, enabled_before)

                    # Expected state from asset.
                    enabled_expected = set(asset_sets[to_asset].get("enabled", []))
                    canonical_expected = canonical_hash(m_ids_sorted, enabled_expected)

                    # Copy addonnomount for target asset.
                    t0_ns = time.perf_counter_ns()
                    with open(addonnomount_path, "w", encoding="utf-8") as f:
                        f.write(asset_files[to_asset])
                    t1_ns = time.perf_counter_ns()

                    # Read after state.
                    disabled_after = parse_addonnomount(addonnomount_path)
                    enabled_after = set([a for a in m_ids_sorted if a not in disabled_after])
                    canonical_after = canonical_hash(m_ids_sorted, enabled_after)
                    canonical_ok = canonical_after == canonical_expected

                    note = f"env={env_name};m={manifest.get('mSize')};r={overlap_ratio};repeat={repeat};seed={seed};run_id={run_id}"

                    canonical.write(json.dumps({
                        "Event": "task_result",
                        "TimestampUtc": utc_now(),
                        "RunId": run_id,
                        "EnvName": env_name,
                        "TaskId": task_id,
                        "From": from_asset,
                        "To": to_asset,
                        "Repeat": repeat,
                        "CanonicalBefore": canonical_before,
                        "CanonicalAfter": canonical_after,
                        "CanonicalExpected": canonical_expected,
                        "CanonicalOk": canonical_ok,
                        "Note": note,
                    }, ensure_ascii=False) + "\n")

                    # Log baseline copy as AssetApplyExclusiveEnd for aggregation compatibility.
                    duration_ms = (t1_ns - t0_ns) / 1_000_000.0
                    events.write(json.dumps({
                        "schema_version": "3",
                        "strict_link_mode": False,
                        "event_scope": "external",
                        "monotonic_ms": int((time.perf_counter() - base_time) * 1000),
                        "trial_index": repeat,
                        "timestamp": utc_now(),
                        "session_id": session_id,
                        "experiment_id": run_id,
                        "condition": condition,
                        "task_id": task_id,
                        "action_type": "AssetApplyExclusiveEnd",
                        "target_id": to_asset,
                        "result": "success" if canonical_ok else "fail",
                        "duration_ms": duration_ms,
                        "before_hash": canonical_before,
                        "after_hash": canonical_after,
                        "expected_hash": canonical_expected,
                        "state_hash_scope": "actual:addonnomount.txt",
                        "expected_hash_scope": "expected:addonnomount.txt",
                        "error_code": None if canonical_ok else "state_mismatch",
                    }, ensure_ascii=False) + "\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
