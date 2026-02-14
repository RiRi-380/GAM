# Asset Exclusive Apply

Note: Current releases run in soft-only mode (addonnomount.txt). Junction asset behavior is legacy/experimental.

## Purpose
Provide a deterministic "apply preset" operation that converges to a single asset.

## Definition
`ApplyAssetExclusive(assetId)` performs the following:
- Disables all assets except the target asset.
- Enables the target asset.
- Applies the target asset's addon states.
- Addons not included in the target asset are forced OFF.

Notes:
- `Excluded` is treated as OFF.
- The Subscribe and Junction assets are disabled unless they are the target.

## Idempotence
Applying the same asset multiple times yields the same final state.

## Logging
Start/End events:
- `AssetApplyExclusiveStart`
- `AssetApplyExclusiveEnd`

State verification is performed via state hash matching (`docs/STATE_HASH.md`).
