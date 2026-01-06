# State Hash Specification

## Purpose
Define deterministic addon state snapshots for experiment verification.

## Snapshot Input
- Addon IDs: Configuration.AddonMetadata keys, excluding "*".
- State source (actual):
  - Directory addons: enabled if the workshop path has a junction for addonId.
  - GMA addons: enabled if the Garry's Mod cache has addonId.gma.
- Each addon is mapped to enabled (1) or disabled (0).

## Normalization
- Sort by addonId in ordinal order.
- Format each line as: `<addonId>=<0|1>`.
- Join lines with LF (`\n`), no trailing newline.

## Hash
- SHA-256 of UTF-8 bytes of the normalized string.
- Hex output, lowercase.

## Expected State
- Expected snapshots are derived from `CalculateFinalAddonState` after configuration updates.

## Example
Normalized:
```
111=1
222=0
333=1
```

Hash (SHA-256, hex, lowercase):
```
d8ea0a108072fd8e8a841babd4d4b91715b1e0bdd72d7d824d59bde647afd8eb
```

Related samples:
- docs/normalized_state.txt
- docs/state_hash.txt
