# AUTO UPDATE RELEASE PLAYBOOK

## Purpose
This document is the repo-local handoff for future agents working on GAM's update flow.

Scope:
- explain how the current updater works
- define the current release contract
- list what was fixed locally in this task
- show how to verify that installed clients can detect a newer installer release

Non-goal:
- this document does not publish anything to GitHub

## Current Updater Behavior
GAM does not do a silent in-place binary swap.

What it does today:
1. after startup, GAM waits about 5 seconds
2. it requests `https://api.github.com/repos/RiRi-380/GAM/releases/latest`
3. it compares the release tag with the running app version
4. if a newer release exists and an installer asset is found, it shows an update dialog
5. if the user accepts, GAM downloads the installer, launches it, and exits

Important consequence:
- this is "automatic update check + user-confirmed installer upgrade"
- pushing a branch does nothing for installed clients
- uploading only a ZIP does nothing for installed clients

## Files That Define The Contract
- `src/GmodAddonManager.Core/Services/UpdateService.cs`
- `src/GmodAddonManager.UI/ViewModels/MainWindowViewModel.cs`
- `build-release.ps1`
- `installer/setup.iss`

## Contract Fixed In This Task
### 1. Installer asset matching
`UpdateService` now accepts installer assets by meaning, not by one exact suffix.

Accepted examples:
- `GAM-Setup-1.0.1.exe`
- `GAM-Setup-v1.0.1.exe`
- `GAM-installer.exe`

Rejected examples:
- `GAM-Portable-1.0.1.zip`
- `VC_redist.x64.exe`

Why this matters:
- the old updater logic was stricter than the repo's release naming
- a valid installer could exist on GitHub and still be ignored

### 2. Update check timestamp behavior
`last_update_check.txt` is now written after a successful `latest release` fetch, even when no update is available.

Why this matters:
- without this, a no-update state could hit GitHub again every launch
- the check cadence is now consistent with the intended "once per day" behavior

### 3. Release version propagation
`build-release.ps1` now normalizes `-Version` and passes these properties into `dotnet publish`:
- `Version`
- `AssemblyVersion`
- `FileVersion`
- `InformationalVersion`

Why this matters:
- installed clients compare the remote tag to the running app version
- if release builds keep stale `1.0.0` metadata, update detection becomes unreliable

### 4. Installer metadata version
`installer/setup.iss` now receives `MyAppVersion` from the release script and uses it for `AppVersion`.

Why this matters:
- installer metadata and built binary version now move together

## What A Real Release Must Satisfy
For installed GAM clients to receive an update, all of these must be true:

1. a GitHub Release exists
2. that release is the repository's `latest` release
3. the release contains an installer `.exe` asset
4. the installer asset name contains `setup` or `installer`
5. the built app version is lower than the release tag on existing clients
6. the new release build embeds the new version into the binaries

If any one of these is false, installed clients will not update.

## Recommended Release Procedure
1. prepare a clean release branch
2. run `./build-release.ps1 -Version vX.Y.Z`
3. verify binary version metadata before publishing anything
4. verify the installer output name matches the updater contract
5. create the GitHub Release
6. upload the installer `.exe` asset
7. confirm an existing install detects the release

## Local Verification Commands
### Verify version propagation
```powershell
dotnet clean src\GmodAddonManager.UI\GmodAddonManager.UI.csproj -c Release
dotnet build src\GmodAddonManager.UI\GmodAddonManager.UI.csproj `
  -c Release `
  -p:Version=1.0.1 `
  -p:AssemblyVersion=1.0.1.0 `
  -p:FileVersion=1.0.1.0 `
  -p:InformationalVersion=v1.0.1

$dll = "src\GmodAddonManager.UI\bin\Release\net6.0\GmodAddonManager.UI.dll"
[System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
(Get-Item $dll).VersionInfo.FileVersion
(Get-Item $dll).VersionInfo.ProductVersion
```

Pass condition:
- assembly version is `1.0.1.0`
- file version is `1.0.1.0`
- product version contains `v1.0.1`

### Verify installer asset matching
Run the core tests:

```powershell
dotnet test tests\GmodAddonManager.Core.Tests\GmodAddonManager.Core.Tests.csproj -c Debug
```

Coverage includes this truth table:
- `GAM-Setup-1.0.1.exe` => `True`
- `GAM-Setup-v1.0.1.exe` => `True`
- `GAM-installer.exe` => `True`
- `GAM-Portable-1.0.1.zip` => `False`
- `VC_redist.x64.exe` => `False`

### Verify no whitespace/content hygiene issues
```powershell
git diff --check -- `
  src\GmodAddonManager.Core\Services\UpdateService.cs `
  build-release.ps1 `
  installer\setup.iss `
  docs\AUTO_UPDATE_RELEASE_PLAYBOOK.md
```

Pass condition:
- no whitespace/content errors
- CRLF warnings are acceptable on this repo

## Known Limits
- the updater is GitHub-release based, not appcast based
- there is no stable/beta channel separation
- the updater launches an installer; it is not a silent patcher
- rollback is not automated
- release authenticity currently depends on GitHub release hygiene, not a dedicated manifest/signature flow

## Do Not Assume
- do not assume `git push` is enough
- do not assume ZIP assets are enough
- do not assume fixed `1.0.0` project metadata is harmless
- do not assume `scripts/release.ps1` is the truth source without checking its version and asset behavior

## If A Future Agent Wants "Reliable Auto Update"
The next level of hardening is:
1. add a dedicated update manifest or appcast
2. separate installer discovery from filename heuristics
3. verify hash/signature before launch
4. support release channels such as stable and beta
5. add release/download telemetry and failure logging

## Local Result Of This Task
No network-side action was taken in this task.

Local changes only:
- updater installer matching was hardened
- update check cadence was fixed
- release version propagation was fixed
- installer metadata version wiring was fixed
- this handoff document was added
