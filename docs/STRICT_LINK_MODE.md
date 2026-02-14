# StrictLinkMode

Note: Current releases run in soft-only mode (addonnomount.txt). StrictLinkMode is legacy/experimental for hard/junction operations.

## Purpose
Prevent link-based experiments from being contaminated by copy fallbacks.

## Behavior
When StrictLinkMode is enabled:
- Any attempt to fall back to `File.Copy` for link creation is blocked.
- A `StrictLinkViolation` event is logged.
- The operation fails with `StrictLinkModeException`.

When StrictLinkMode is disabled:
- Copy fallbacks are allowed.
- A `LinkFallbackCopy` event is logged with `error_code=copy_used:<context>`.

## Enable
Use either method:
- Settings file: `%APPDATA%/GmodAddonManager/settings.json`
  - `"StrictLinkMode": true`
- Environment variable:
  - `GAM_STRICT_LINK_MODE=1`

## Notes
- StrictLinkMode requires same-drive workshop/cache paths and sufficient privileges.
- If a copy fallback would occur, the experiment run should be treated as failed.
