# Experiment Constraints and Known Limitations (Archived)

This document records the retired v1 link-based experiments. GAM v2.0.0 runs
only in Soft mode through `addonnomount.txt`; the product UI and startup path do
not provide Hard mode, junction, hard-link, copy-fallback, or unsubscribe-based
addon management. Do not use the old experiment environment variables with a
release build.

## Link-Based Mode (LM)
- The following points describe the historical experiment only; they are not
  supported v2 operating instructions.
- Hard links require the workshop and cache paths to be on the same NTFS volume.
- Junction creation requires admin rights or Windows Developer Mode.
- StrictLinkMode blocks any File.Copy fallback and fails the operation with `StrictLinkModeException`.
- If Steam or GMod modifies workshop/cache during a run, state hashes can diverge and `state_mismatch` can occur.

## BL / Unsubscribe Flow
- If `DisableMode=Hard` and `UnsubscribeOnHardDisable=true`, Steam unsubscribe/resubscribe can trigger resync/redownload.
- For LM experiments, keep `UnsubscribeOnHardDisable=false` and avoid Hard disable.

## State Hash Coverage
- Hash inputs include only addon IDs in `Configuration.AddonMetadata` (keys excluding `"*"`).
- Unmanaged workshop folders are ignored by the hash.
- Hash is based on junctions in the workshop folder and `.gma` files in the cache; background updates can change these.

## Logging Reliability
- Logs are appended locally in JSONL; if storage is full or write permissions fail, events may be dropped.
- `experiment_id`, `condition`, `task_id`, `session_id` are supplied via environment variables.

## Operational
- Do not run these historical experiments against a live v2 profile. The only
  supported runtime state mechanism is `addonnomount.txt`.
