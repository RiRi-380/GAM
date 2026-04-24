import argparse
import json
import os
import shutil
import stat
import subprocess
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


def is_gmod_running() -> bool:
    # Prefer PowerShell Get-Process to avoid tasklist permission issues.
    try:
        output = subprocess.check_output(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-Process | Where-Object { $_.ProcessName -match 'gmod|hl2|garrysmod' } | Select-Object -First 1 -ExpandProperty ProcessName"
            ],
            text=True,
            stderr=subprocess.STDOUT,
        ).strip().lower()
        if output:
            return True
    except Exception:
        pass
    try:
        output = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq gmod.exe"], text=True, stderr=subprocess.STDOUT
        )
        if "gmod.exe" in output.lower():
            return True
    except Exception:
        pass
    try:
        output = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq hl2.exe"], text=True, stderr=subprocess.STDOUT
        )
        return "hl2.exe" in output.lower()
    except Exception:
        return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-run-index", required=True)
    parser.add_argument("--log-root", required=True)
    parser.add_argument("--env-name", default="")
    parser.add_argument("--ratio", default="")
    parser.add_argument("--mode", choices=["readonly", "tamper", "gmod_running"], required=True)
    parser.add_argument("--repeats", type=int, default=10)
    parser.add_argument("--powershell", default="powershell")
    args = parser.parse_args()

    log_root = args.log_root
    os.makedirs(log_root, exist_ok=True)

    run_index = load_json(args.source_run_index)
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

    selected_run = None
    for run in run_index["runs"]:
        manifest = load_json(run["manifest"])
        if args.env_name and manifest.get("envName") != args.env_name:
            continue
        if args.ratio and str(manifest.get("overlapRatio")) != args.ratio:
            continue
        selected_run = run
        break

    if not selected_run:
        raise SystemExit("No run matched env/ratio filters.")

    manifest = load_json(selected_run["manifest"])
    run_id = f"{manifest.get('runId')}_{args.mode}"
    env_name = manifest.get("envName")
    ratio = manifest.get("overlapRatio")
    m_size = manifest.get("mSize")
    seed = manifest.get("seed")

    # Build a reduced manifest for failure runs (T1 only).
    manifest["runId"] = run_id
    manifest["tasks"] = [{"id": "T1", "from": "Base", "to": "A"}]
    manifest["repeats"] = args.repeats
    manifest_path = os.path.join(log_root, f"manifest_{run_id}.json")
    write_json(manifest_path, manifest)

    new_run_index = {
        "runId": os.path.basename(log_root),
        "createdAt": datetime.now().isoformat(),
        "seed": seed,
        "sourceWorkshopPath": run_index.get("sourceWorkshopPath"),
        "sizes": run_index.get("sizes"),
        "ratios": run_index.get("ratios"),
        "envs": envs,
        "runs": [{"runId": run_id, "envName": env_name, "manifest": manifest_path}],
    }
    write_json(os.path.join(log_root, "run_index.json"), new_run_index)

    env = next(e for e in envs if e["envName"] == env_name)
    gmod_root = env["gmodRoot"]
    addonnomount_path = os.path.join(gmod_root, "garrysmod", "cfg", "addonnomount.txt")
    os.makedirs(os.path.dirname(addonnomount_path), exist_ok=True)

    if args.mode == "gmod_running" and not is_gmod_running():
        raise SystemExit("GMod is not running. Start GMod, then re-run this command.")

    events_path = os.path.join(log_root, f"events_{run_id}.jsonl")
    canonical_path = os.path.join(log_root, f"canonical_{run_id}.jsonl")

    if args.mode in ("readonly", "gmod_running"):
        # Ensure addonnomount exists.
        if not os.path.exists(addonnomount_path):
            with open(addonnomount_path, "w", encoding="utf-8") as f:
                f.write(build_addonnomount([]))

        readonly_applied = False
        try:
            if args.mode == "readonly":
                os.chmod(addonnomount_path, stat.S_IREAD)
                readonly_applied = True

            note = f"env={env_name};m={m_size};r={ratio};mode={args.mode};seed={seed}"
            if args.mode == "gmod_running":
                note += ";gmod_running=true"

            subprocess.run(
                [
                    "dotnet",
                    "run",
                    "--no-restore",
                    "--project",
                    os.path.join("tools", "GmodAddonManager.ExperimentRunner"),
                    "--",
                    "--manifest",
                    manifest_path,
                    "--event-log",
                    events_path,
                    "--canonical-log",
                    canonical_path,
                    "--note",
                    note,
                ],
                check=True,
            )
        finally:
            if readonly_applied:
                os.chmod(addonnomount_path, stat.S_IREAD | stat.S_IWRITE)

        return 0

    # Tamper mode: external apply + tamper + canonical check.
    m_ids = [str(x) for x in manifest["addonIds"]]
    m_ids_sorted = sorted(m_ids)
    asset_sets = manifest["assetSets"]
    tasks = manifest["tasks"]
    repeats = int(manifest["repeats"])

    session_id = uuid.uuid4().hex
    base_time = time.perf_counter()

    with open(events_path, "w", encoding="utf-8") as events, open(canonical_path, "w", encoding="utf-8") as canonical:
        canonical.write(json.dumps({
            "Event": "run_start",
            "TimestampUtc": utc_now(),
            "RunId": run_id,
            "EnvName": env_name,
            "MSize": m_size,
            "OverlapRatio": ratio,
            "Seed": seed,
            "ExperimentId": run_id,
            "Condition": f"Soft-Tamper",
            "EventLogPath": events_path,
            "AddonIds": m_ids_sorted,
        }, ensure_ascii=False) + "\n")

        events.write(json.dumps({
            "schema_version": "3",
            "strict_link_mode": False,
            "event_scope": "external",
            "monotonic_ms": int((time.perf_counter() - base_time) * 1000),
            "trial_index": None,
            "timestamp": utc_now(),
            "session_id": session_id,
            "experiment_id": run_id,
            "condition": "Soft-Tamper",
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
            "note": "external_tamper",
        }, ensure_ascii=False) + "\n")

        for repeat in range(1, repeats + 1):
            for task in tasks:
                task_id = task["id"]
                to_asset = task["to"]

                enabled_expected = set(asset_sets[to_asset].get("enabled", []))
                canonical_expected = canonical_hash(m_ids_sorted, enabled_expected)

                disabled = sorted([a for a in m_ids_sorted if a not in enabled_expected])
                with open(addonnomount_path, "w", encoding="utf-8") as f:
                    f.write(build_addonnomount(disabled))

                # Tamper: flip one enabled id to disabled.
                tamper_target = None
                for addon_id in m_ids_sorted:
                    if addon_id in enabled_expected:
                        tamper_target = addon_id
                        break
                if tamper_target:
                    tampered = set(disabled)
                    tampered.add(tamper_target)
                    with open(addonnomount_path, "w", encoding="utf-8") as f:
                        f.write(build_addonnomount(sorted(tampered)))

                disabled_after = parse_addonnomount(addonnomount_path)
                enabled_after = set([a for a in m_ids_sorted if a not in disabled_after])
                canonical_after = canonical_hash(m_ids_sorted, enabled_after)
                canonical_ok = canonical_after == canonical_expected

                note = f"env={env_name};m={m_size};r={ratio};repeat={repeat};seed={seed};tamper=1"

                canonical.write(json.dumps({
                    "Event": "task_result",
                    "TimestampUtc": utc_now(),
                    "RunId": run_id,
                    "EnvName": env_name,
                    "TaskId": task_id,
                    "From": task.get("from"),
                    "To": to_asset,
                    "Repeat": repeat,
                    "CanonicalBefore": "",
                    "CanonicalAfter": canonical_after,
                    "CanonicalExpected": canonical_expected,
                    "CanonicalOk": canonical_ok,
                    "Note": note,
                }, ensure_ascii=False) + "\n")

                events.write(json.dumps({
                    "schema_version": "3",
                    "strict_link_mode": False,
                    "event_scope": "external",
                    "monotonic_ms": int((time.perf_counter() - base_time) * 1000),
                    "trial_index": repeat,
                    "timestamp": utc_now(),
                    "session_id": session_id,
                    "experiment_id": run_id,
                    "condition": "Soft-Tamper",
                    "task_id": task_id,
                    "action_type": "AssetApplyExclusiveEnd",
                    "target_id": to_asset,
                    "result": "success" if canonical_ok else "fail",
                    "duration_ms": 0.0,
                    "before_hash": None,
                    "after_hash": canonical_after,
                    "expected_hash": canonical_expected,
                    "state_hash_scope": "actual:addonnomount.txt",
                    "expected_hash_scope": "expected:addonnomount.txt",
                    "error_code": None if canonical_ok else "state_mismatch",
                    "note": note,
                }, ensure_ascii=False) + "\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
