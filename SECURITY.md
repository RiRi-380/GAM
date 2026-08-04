# Security Policy

## Supported version

Security fixes are provided for the latest `2.x` release only. Older builds,
including the public `1.x` line, are unsupported and should not be used.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private
security-advisory form instead:

https://github.com/RiRi-380/GAM/security/advisories/new

Include the affected version, Windows version, a minimal reproduction, the
expected impact, and whether the issue requires a malicious `.gam` file,
Workshop payload, update response, or local filesystem access. Remove Steam
tokens, GitHub tokens, personal paths, and other private data from logs or
screenshots.

The maintainer will acknowledge a reproducible report, assess severity, and
coordinate a fix and disclosure. Please allow time for a corrected private
build to be tested before publishing technical details.

## Security boundaries

- GAM does not subscribe to or unsubscribe from Steam Workshop items.
- Imported `.gam` files are untrusted input and are validated before use.
- Release downloads must use HTTPS and a GitHub-provided SHA-256 digest.
- Application state belongs under `%APPDATA%\GmodAddonManager`; the installer
  does not recursively delete that directory during upgrades or uninstall.
- Release executables are currently not Authenticode-signed. Windows
  SmartScreen may therefore warn even when the SHA-256 checksum matches.
