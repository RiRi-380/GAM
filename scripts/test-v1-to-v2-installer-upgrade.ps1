#requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Alias('V1LegacySetupPath')]
    [ValidateNotNullOrEmpty()]
    [string]$V100SetupPath,

    [Parameter(Mandatory)]
    [Alias('V1PerUserSetupPath')]
    [ValidateNotNullOrEmpty()]
    [string]$V1026SetupPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$V2SetupPath,

    [Parameter(Mandatory)]
    [Alias('ExpectedVersion')]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:UninstallSubKey =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Gmod Addon Manager_is1'
$script:UninstallKeys = @{
    HKLM = "Registry::HKEY_LOCAL_MACHINE\$($script:UninstallSubKey)"
    HKCU = "Registry::HKEY_CURRENT_USER\$($script:UninstallSubKey)"
}
$script:GamProcessName = 'GmodAddonManager.UI'
$script:SetupTimeout = [TimeSpan]::FromMinutes(10)
$script:LaunchTimeout = [TimeSpan]::FromSeconds(30)
$script:LaunchStabilityWindow = [TimeSpan]::FromSeconds(2)
$script:ExpectedV1InstallerSha256 = @{
    '1.0.0' = '083fb68a4fce57f3f01282f68946c71da48e40a98b77a00bd0886710c704aa79'
    '1.0.26' = 'a6f61f971cf96c4c9d3bc79e81bd7a4edebbaa74e51cff42be136e500628a81d'
}
$script:KnownLegacyPayloads = @(
    [pscustomobject]@{
        Name = 'steam_api64.dll'
        Length = [int64]296408
        Sha256 = '46688ecd8849a86bf8b807c5de1adbb8b8dddaa48583d68b3518b72c77c15bd0'
    },
    [pscustomobject]@{
        Name = 'steam_appid.txt'
        Length = [int64]4
        Sha256 = 'b090147020e033534635010c4f7eb6fc270d44e5df67ea9e744a8087df9ca106'
    }
)

function Get-NormalizedPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $pathRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath
    }

    return $fullPath.TrimEnd([char[]]'\/')
}

function Test-PathsEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right
    )

    return [string]::Equals(
        (Get-NormalizedPath -Path $Left),
        (Get-NormalizedPath -Path $Right),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathIsWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    $normalizedCandidate = Get-NormalizedPath -Path $Candidate
    $normalizedParent = Get-NormalizedPath -Path $Parent
    if (Test-PathsEqual -Left $normalizedCandidate -Right $normalizedParent) {
        return $true
    }

    return $normalizedCandidate.StartsWith(
        $normalizedParent + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-SetupFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label does not exist: $Path"
    }

    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    if ([System.IO.Path]::GetExtension($resolved) -ne '.exe') {
        throw "$Label is not an .exe file: $resolved"
    }

    return Get-NormalizedPath -Path $resolved
}

function Assert-Administrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This installer E2E must run from an elevated 64-bit PowerShell process.'
    }

    if (-not [Environment]::Is64BitProcess) {
        throw 'This installer E2E must run in 64-bit PowerShell so HKLM64 is inspected.'
    }
}

function Assert-SafeNewWorkRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $normalized = Get-NormalizedPath -Path $Path
    $forbidden = @(
        [System.IO.Path]::GetPathRoot($normalized),
        $env:USERPROFILE,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
        [System.IO.Path]::GetTempPath()
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($forbiddenPath in $forbidden) {
        if (Test-PathsEqual -Left $normalized -Right $forbiddenPath) {
            throw "Refusing to use a broad or sensitive work root: $normalized"
        }
    }

    if (Test-Path -LiteralPath $normalized) {
        throw "WorkRoot must not already exist; the test only cleans a directory it created: $normalized"
    }

    return $normalized
}

function Get-Registration {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('HKLM', 'HKCU')]
        [string]$Hive
    )

    $keyPath = $script:UninstallKeys[$Hive]
    if (-not (Test-Path -LiteralPath $keyPath)) {
        return $null
    }

    return Get-ItemProperty -LiteralPath $keyPath
}

function Move-CurrentUserRegistrationToAllUsers {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedInstallDirectory
    )

    $currentRegistration = Get-Registration -Hive HKCU
    if ($null -eq $currentRegistration) {
        throw 'Cannot create the all-users compatibility fixture because the current-user registration is missing.'
    }
    if ($null -ne (Get-Registration -Hive HKLM)) {
        throw 'Cannot create the all-users compatibility fixture because an HKLM registration already exists.'
    }
    if (-not (Test-PathsEqual `
            -Left ([string]$currentRegistration.InstallLocation) `
            -Right $ExpectedInstallDirectory)) {
        throw "The current-user registration does not belong to the compatibility fixture: $($currentRegistration.InstallLocation)"
    }

    $currentUserBase = $null
    $localMachineBase = $null
    $sourceKey = $null
    $targetKey = $null
    $targetCreated = $false
    try {
        $currentUserBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::CurrentUser,
            [Microsoft.Win32.RegistryView]::Registry64)
        $localMachineBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $sourceKey = $currentUserBase.OpenSubKey($script:UninstallSubKey, $false)
        if ($null -eq $sourceKey) {
            throw 'The current-user uninstall key disappeared while creating the all-users compatibility fixture.'
        }
        if ($sourceKey.SubKeyCount -ne 0) {
            throw 'The current-user uninstall key unexpectedly contains subkeys; refusing to create a partial fixture.'
        }

        $targetKey = $localMachineBase.CreateSubKey($script:UninstallSubKey, $true)
        if ($null -eq $targetKey) {
            throw 'Could not create the all-users uninstall key for the compatibility fixture.'
        }
        $targetCreated = $true
        foreach ($valueName in $sourceKey.GetValueNames()) {
            $value = $sourceKey.GetValue(
                $valueName,
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $targetKey.SetValue($valueName, $value, $sourceKey.GetValueKind($valueName))
        }
        $targetKey.Flush()
        $targetKey.Dispose()
        $targetKey = $null
        $sourceKey.Dispose()
        $sourceKey = $null

        $currentUserBase.DeleteSubKey($script:UninstallSubKey, $false)
    }
    catch {
        if ($null -ne $targetKey) {
            $targetKey.Dispose()
            $targetKey = $null
        }
        if ($targetCreated -and $null -ne $localMachineBase) {
            $localMachineBase.DeleteSubKeyTree($script:UninstallSubKey, $false)
        }
        throw
    }
    finally {
        if ($null -ne $targetKey) {
            $targetKey.Dispose()
        }
        if ($null -ne $sourceKey) {
            $sourceKey.Dispose()
        }
        if ($null -ne $localMachineBase) {
            $localMachineBase.Dispose()
        }
        if ($null -ne $currentUserBase) {
            $currentUserBase.Dispose()
        }
    }

    if ($null -ne (Get-Registration -Hive HKCU)) {
        throw 'The current-user registration still exists after creating the all-users compatibility fixture.'
    }
    $allUsersRegistration = Get-Registration -Hive HKLM
    if ($null -eq $allUsersRegistration -or
        -not (Test-PathsEqual `
            -Left ([string]$allUsersRegistration.InstallLocation) `
            -Right $ExpectedInstallDirectory)) {
        throw 'The all-users compatibility registration was not created correctly.'
    }
}

function Assert-NoRegistration {
    $present = @(
        foreach ($hive in @('HKLM', 'HKCU')) {
            if ($null -ne (Get-Registration -Hive $hive)) {
                $hive
            }
        }
    )

    if ($present.Count -ne 0) {
        throw "Expected no GAM uninstall registration, but found: $($present -join ', ')"
    }
}

function Assert-Registration {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('HKLM', 'HKCU')]
        [string]$ExpectedHive,

        [Parameter(Mandatory)]
        [string]$ExpectedInstallDirectory,

        [Parameter(Mandatory)]
        [string]$ExpectedDisplayVersion
    )

    $otherHive = if ($ExpectedHive -eq 'HKLM') { 'HKCU' } else { 'HKLM' }
    $registration = Get-Registration -Hive $ExpectedHive
    $otherRegistration = Get-Registration -Hive $otherHive
    if ($null -eq $registration) {
        if ($null -ne $otherRegistration) {
            throw "Expected a GAM uninstall registration in $ExpectedHive, but it exists in $otherHive at '$($otherRegistration.InstallLocation)' with version '$($otherRegistration.DisplayVersion)'."
        }
        throw "Expected a GAM uninstall registration in $ExpectedHive, but none exists."
    }

    if ($null -ne $otherRegistration) {
        throw "GAM is registered in both $ExpectedHive and $otherHive; exactly one registration is required."
    }

    $actualInstallDirectory = [string]$registration.InstallLocation
    if ([string]::IsNullOrWhiteSpace($actualInstallDirectory) -or
        -not (Test-PathsEqual -Left $actualInstallDirectory -Right $ExpectedInstallDirectory)) {
        throw "InstallLocation mismatch. Expected '$ExpectedInstallDirectory', actual '$actualInstallDirectory'."
    }

    $actualVersion = [string]$registration.DisplayVersion
    if (-not [string]::Equals(
            $actualVersion,
            $ExpectedDisplayVersion,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "DisplayVersion mismatch. Expected '$ExpectedDisplayVersion', actual '$actualVersion'."
    }

    $uninstallString = [string]$registration.UninstallString
    if ([string]::IsNullOrWhiteSpace($uninstallString) -or
        $uninstallString.IndexOf(
            (Get-NormalizedPath -Path $ExpectedInstallDirectory),
            [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "UninstallString does not point into the expected install directory: $uninstallString"
    }

    $installedExecutable = Join-Path $ExpectedInstallDirectory 'GmodAddonManager.UI.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw "The registered GAM executable is missing: $installedExecutable"
    }

    $expectedExecutableVersion = [Version]::Parse($ExpectedDisplayVersion)
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    if (($versionInfo.FileMajorPart -ne $expectedExecutableVersion.Major) -or
        ($versionInfo.FileMinorPart -ne $expectedExecutableVersion.Minor) -or
        ($versionInfo.FileBuildPart -ne $expectedExecutableVersion.Build)) {
        throw "Installed executable FileVersion does not match $ExpectedDisplayVersion`: $($versionInfo.FileVersion) ($installedExecutable)"
    }
}

function Get-ManagedApplicationFiles {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory
    )

    $manifestPath = Join-Path $InstallDirectory 'GAM-ReleaseFiles.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Installed managed-file manifest is missing: $manifestPath"
    }

    $managedFiles = @(
        foreach ($relativePath in @(Get-Content -LiteralPath $manifestPath -Encoding UTF8)) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                [System.IO.Path]::IsPathRooted($relativePath)) {
                throw "Unsafe installed managed-file path: '$relativePath'"
            }

            $candidate = [System.IO.Path]::GetFullPath(
                (Join-Path $InstallDirectory $relativePath))
            if (-not (Test-PathIsWithin -Candidate $candidate -Parent $InstallDirectory)) {
                throw "Installed managed-file path escapes the install directory: '$relativePath'"
            }

            $candidate
        }
    )

    if ($managedFiles.Count -eq 0) {
        throw "Installed managed-file manifest is empty: $manifestPath"
    }

    return $managedFiles
}

function Assert-ManagedApplicationRemoved {
    param(
        [Parameter(Mandatory)]
        [string[]]$ManagedFiles
    )

    $remaining = @($ManagedFiles | Where-Object { Test-Path -LiteralPath $_ })
    if ($remaining.Count -ne 0) {
        throw "Uninstall left managed GAM files behind: $($remaining -join ', ')"
    }
}

function Get-OwnedGamProcesses {
    param(
        [Parameter(Mandatory)]
        [string[]]$OwnedInstallDirectories
    )

    $owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($process in @(Get-Process -Name $script:GamProcessName -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $process.Path
        }
        catch {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($processPath)) {
            continue
        }

        foreach ($installDirectory in $OwnedInstallDirectories) {
            if (Test-PathIsWithin -Candidate $processPath -Parent $installDirectory) {
                $owned.Add($process)
                break
            }
        }
    }

    return $owned.ToArray()
}

function Test-OwnedGamProcessRunning {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory
    )

    return @(Get-OwnedGamProcesses -OwnedInstallDirectories @($InstallDirectory)).Count -gt 0
}

function Wait-ForOwnedGamLaunch {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory,

        [TimeSpan]$Timeout = $script:LaunchTimeout,

        [switch]$Required
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $stableSince = $null
    do {
        if (Test-OwnedGamProcessRunning -InstallDirectory $InstallDirectory) {
            if ($null -eq $stableSince) {
                $stableSince = [DateTime]::UtcNow
            }
            elseif (([DateTime]::UtcNow - $stableSince) -ge $script:LaunchStabilityWindow) {
                return $true
            }
        }
        else {
            $stableSince = $null
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($Required) {
        throw "GAM did not launch from the expected directory within $([int]$Timeout.TotalSeconds) seconds: $InstallDirectory"
    }

    return $false
}

function Stop-OwnedGamProcesses {
    param(
        [Parameter(Mandatory)]
        [string[]]$OwnedInstallDirectories
    )

    $deadline = [DateTime]::UtcNow + [TimeSpan]::FromSeconds(15)
    do {
        $processes = @(Get-OwnedGamProcesses -OwnedInstallDirectories $OwnedInstallDirectories)
        if ($processes.Count -eq 0) {
            return
        }

        foreach ($process in $processes) {
            try {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Could not stop owned GAM process $($process.Id): $($_.Exception.Message)"
            }
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    $remaining = @(Get-OwnedGamProcesses -OwnedInstallDirectories $OwnedInstallDirectories)
    if ($remaining.Count -gt 0) {
        throw "Owned GAM processes are still running: $($remaining.Id -join ', ')"
    }
}

function Invoke-SetupExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [string]$LaunchInstallDirectory,

        [switch]$RequireLaunch
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = Split-Path -Parent $FilePath
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    Write-Host "Running: $FilePath $($Arguments -join ' ')"
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start executable: $FilePath"
    }

    $deadline = [DateTime]::UtcNow + $script:SetupTimeout
    $launchObserved = $false
    while (-not $process.HasExited) {
        if (-not [string]::IsNullOrWhiteSpace($LaunchInstallDirectory) -and
            (Test-OwnedGamProcessRunning -InstallDirectory $LaunchInstallDirectory)) {
            $launchObserved = $true
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            try {
                $process.Kill($true)
            }
            catch {
                Write-Warning "Could not terminate timed-out process $($process.Id): $($_.Exception.Message)"
            }
            throw "Executable timed out after $([int]$script:SetupTimeout.TotalMinutes) minutes: $FilePath"
        }

        Start-Sleep -Milliseconds 100
    }

    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Executable failed with exit code $($process.ExitCode): $FilePath"
    }

    if ($RequireLaunch) {
        $launchObserved = Wait-ForOwnedGamLaunch `
            -InstallDirectory $LaunchInstallDirectory `
            -Timeout $script:LaunchTimeout `
            -Required
    }

    return $launchObserved
}

function Invoke-OwnedUninstall {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory,

        [Parameter(Mandatory)]
        [string[]]$AllOwnedInstallDirectories
    )

    Stop-OwnedGamProcesses -OwnedInstallDirectories $AllOwnedInstallDirectories
    $uninstallers = @(
        Get-ChildItem -LiteralPath $InstallDirectory -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue |
            Sort-Object Name
    )
    if ($uninstallers.Count -eq 0) {
        throw "No Inno Setup uninstaller exists in: $InstallDirectory"
    }

    $null = Invoke-SetupExecutable `
        -FilePath $uninstallers[0].FullName `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
}

function Get-TreeFingerprint {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Fingerprint root is missing: $Root"
    }

    $normalizedRoot = Get-NormalizedPath -Path $Root
    $lines = @(
        Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relativePath = [System.IO.Path]::GetRelativePath($normalizedRoot, $_.FullName).
                    Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$relativePath|$($_.Length)|$hash"
            }
    )

    return $lines -join "`n"
}

function Assert-TreeUnchanged {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ExpectedFingerprint
    )

    $actualFingerprint = Get-TreeFingerprint -Root $Root
    if (-not [string]::Equals(
            $actualFingerprint,
            $ExpectedFingerprint,
            [System.StringComparison]::Ordinal)) {
        throw "$Label changed unexpectedly during installer processing.`nExpected:`n$ExpectedFingerprint`nActual:`n$actualFingerprint"
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-InstallerFixtureHash {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$FixtureVersion
    )

    $expected = $script:ExpectedV1InstallerSha256[$FixtureVersion]
    $actual = Get-FileSha256 -Path $Path
    if ($actual -ne $expected) {
        throw "The v$FixtureVersion installer is not the audited GitHub Release asset. Expected SHA-256 '$expected', actual '$actual'."
    }
}

function Test-KnownLegacyPayloadPresent {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory
    )

    $presentPayloads = @(
        foreach ($payload in $script:KnownLegacyPayloads) {
            if (Test-Path -LiteralPath (Join-Path $InstallDirectory $payload.Name) -PathType Leaf) {
                $payload
            }
        }
    )
    if ($presentPayloads.Count -eq 0) {
        return $false
    }
    if ($presentPayloads.Count -ne $script:KnownLegacyPayloads.Count) {
        throw 'The v1.0.0 fixture contains only part of the known legacy Steam payload pair.'
    }

    foreach ($payload in $presentPayloads) {
        $path = Join-Path $InstallDirectory $payload.Name
        $file = Get-Item -LiteralPath $path
        $hash = Get-FileSha256 -Path $path
        if ($file.Length -ne $payload.Length -or $hash -ne $payload.Sha256) {
            throw "Known legacy payload '$($payload.Name)' does not match the audited v1.0.0 size/hash."
        }
    }

    return $true
}

function Assert-KnownLegacyPayloadRemoved {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory
    )

    foreach ($payload in $script:KnownLegacyPayloads) {
        $path = Join-Path $InstallDirectory $payload.Name
        if (Test-Path -LiteralPath $path) {
            throw "Verified obsolete v1 payload was not removed: $path"
        }
    }
}

function Assert-Sentinel {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Sentinel was removed: $Path"
    }

    $actual = Get-FileSha256 -Path $Path
    if ($actual -ne $ExpectedSha256) {
        throw "Sentinel was modified: $Path"
    }
}

function Remove-OwnedRegistrations {
    param(
        [Parameter(Mandatory)]
        [string[]]$OwnedInstallDirectories
    )

    foreach ($hive in @('HKLM', 'HKCU')) {
        $registration = Get-Registration -Hive $hive
        if ($null -eq $registration) {
            continue
        }

        $registeredPath = [string]$registration.InstallLocation
        $owned = $false
        if (-not [string]::IsNullOrWhiteSpace($registeredPath)) {
            foreach ($installDirectory in $OwnedInstallDirectories) {
                if (Test-PathsEqual -Left $registeredPath -Right $installDirectory) {
                    $owned = $true
                    break
                }
            }
        }

        if ($owned) {
            Remove-Item -LiteralPath $script:UninstallKeys[$hive] -Recurse -Force
        }
        else {
            Write-Warning "Refusing to remove a GAM registration that is not owned by this test: $hive '$registeredPath'"
        }
    }
}

$v100Setup = Resolve-SetupFile -Path $V100SetupPath -Label 'v1.0.0 Setup'
$v1026Setup = Resolve-SetupFile -Path $V1026SetupPath -Label 'v1.0.26 Setup'
$v2Setup = Resolve-SetupFile -Path $V2SetupPath -Label 'v2 Setup'
Assert-InstallerFixtureHash -Path $v100Setup -FixtureVersion '1.0.0'
Assert-InstallerFixtureHash -Path $v1026Setup -FixtureVersion '1.0.26'
$expectedVersion = $Version.Trim()
if ($expectedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $expectedVersion = $expectedVersion.Substring(1)
}
if ($expectedVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version must be an exact stable X.Y.Z value: $Version"
}

Assert-Administrator
$workRootPath = Assert-SafeNewWorkRoot -Path $WorkRoot
Assert-NoRegistration
if (@(Get-Process -Name $script:GamProcessName -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'GAM is already running. This isolated E2E refuses to stop a pre-existing process.'
}

$appDataDirectory = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) `
    'GmodAddonManager'
if ((Test-PathIsWithin -Candidate $workRootPath -Parent $appDataDirectory) -or
    (Test-PathIsWithin -Candidate $appDataDirectory -Parent $workRootPath)) {
    throw 'WorkRoot and the GAM AppData test directory must not overlap.'
}
if (Test-Path -LiteralPath $appDataDirectory) {
    throw "GAM AppData already exists. Run this test only in a fresh Windows runner: $appDataDirectory"
}

$v100InstallDirectory = Join-Path $workRootPath 'v1.0.0 current-user install'
$adminInstallDirectory = Join-Path $workRootPath 'legacy all-users install'
$perUserInstallDirectory = Join-Path $workRootPath 'per-user install'
$cleanInstallDirectory = Join-Path $workRootPath 'clean install'
$ownedInstallDirectories = @(
    $v100InstallDirectory,
    $adminInstallDirectory,
    $perUserInstallDirectory,
    $cleanInstallDirectory
)
$steamLibrary = Join-Path $workRootPath 'Steam Library'
$gmodDirectory = Join-Path $steamLibrary 'steamapps\common\GarrysMod'
$gmodCfgDirectory = Join-Path $gmodDirectory 'garrysmod\cfg'
$gmodAddonsDirectory = Join-Path $gmodDirectory 'garrysmod\addons'
$workshopDirectory = Join-Path $steamLibrary 'steamapps\workshop\content\4000'
$appDataSentinel = Join-Path $appDataDirectory 'installer-upgrade-sentinel.bin'
$v100InstallSentinel = Join-Path $v100InstallDirectory 'user-owned-install-sentinel.bin'
$adminInstallSentinel = Join-Path $adminInstallDirectory 'user-owned-install-sentinel.bin'
$updaterSetupPath = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "GAM-Update-Setup-$([Guid]::NewGuid().ToString('N')).exe"
$workRootCreated = $false
$appDataCreated = $false
$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[string]]::new()

try {
    [System.IO.Directory]::CreateDirectory($workRootPath) | Out-Null
    $workRootCreated = $true
    [System.IO.Directory]::CreateDirectory($gmodCfgDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($gmodAddonsDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($workshopDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($appDataDirectory) | Out-Null
    $appDataCreated = $true

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $gmodCfgDirectory 'addonnomount.txt'),
        "`"addonnomount`"`r`n{`r`n}`r`n",
        $utf8NoBom)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $gmodDirectory 'installer-gmod-sentinel.bin'),
        [System.Text.Encoding]::UTF8.GetBytes('GAM installer E2E GMod sentinel'))
    [System.IO.File]::WriteAllBytes(
        (Join-Path $workshopDirectory 'installer-workshop-sentinel.bin'),
        [System.Text.Encoding]::UTF8.GetBytes('GAM installer E2E Workshop sentinel'))
    [System.IO.File]::WriteAllBytes(
        $appDataSentinel,
        [System.Text.Encoding]::UTF8.GetBytes(
            "GAM installer E2E AppData sentinel $([Guid]::NewGuid().ToString('N'))"))
    $appDataSentinelHash = Get-FileSha256 -Path $appDataSentinel

    $settings = [ordered]@{
        Language = 'en-US'
        ShowConsoleOnStartup = $false
        DisableMode = 0
        CustomGmodInstallPath = $gmodDirectory
        CustomWorkshopPath = $workshopDirectory
        ConfirmedGmodInstallPath = $gmodDirectory
        ConfirmedWorkshopPath = $workshopDirectory
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $appDataDirectory 'settings.json'),
        ($settings | ConvertTo-Json -Depth 4),
        $utf8NoBom)

    Write-Host 'Scenario A: official v1.0.0 current-user installation -> direct silent v2 in-place upgrade.'
    $null = Invoke-SetupExecutable `
        -FilePath $v100Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/NOICONS',
            "/DIR=$v100InstallDirectory",
            "/LOG=$(Join-Path $workRootPath 'v1.0.0-install.log')")
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $v100InstallDirectory `
        -ExpectedDisplayVersion '1.0.0'
    $knownLegacyPayloadPresent = Test-KnownLegacyPayloadPresent `
        -InstallDirectory $v100InstallDirectory
    if (-not $knownLegacyPayloadPresent) {
        Write-Host 'The official v1.0.0 fixture does not contain the optional historical Steam payload pair.'
    }
    [System.IO.File]::WriteAllBytes(
        $v100InstallSentinel,
        [System.Text.Encoding]::UTF8.GetBytes('user-owned install sentinel'))
    $v100InstallSentinelHash = Get-FileSha256 -Path $v100InstallSentinel
    $gmodBefore = Get-TreeFingerprint -Root $gmodDirectory
    $workshopBefore = Get-TreeFingerprint -Root $workshopDirectory

    $null = Invoke-SetupExecutable `
        -FilePath $v2Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CLOSEAPPLICATIONS',
            "/LOG=$(Join-Path $workRootPath 'v2-v100-current-user-upgrade.log')") `
        -LaunchInstallDirectory $v100InstallDirectory `
        -RequireLaunch
    Stop-OwnedGamProcesses -OwnedInstallDirectories $ownedInstallDirectories
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $v100InstallDirectory `
        -ExpectedDisplayVersion $expectedVersion
    if ($knownLegacyPayloadPresent) {
        Assert-KnownLegacyPayloadRemoved -InstallDirectory $v100InstallDirectory
    }
    Assert-Sentinel -Path $v100InstallSentinel -ExpectedSha256 $v100InstallSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore

    $unknownSteamApi = Join-Path $v100InstallDirectory 'steam_api64.dll'
    $unknownSteamAppId = Join-Path $v100InstallDirectory 'steam_appid.txt'
    [System.IO.File]::WriteAllBytes(
        $unknownSteamApi,
        [System.Text.Encoding]::UTF8.GetBytes('not the audited legacy steam_api64 payload'))
    [System.IO.File]::WriteAllBytes(
        $unknownSteamAppId,
        [System.Text.Encoding]::UTF8.GetBytes('not-4000'))
    $unknownSteamApiHash = Get-FileSha256 -Path $unknownSteamApi
    $unknownSteamAppIdHash = Get-FileSha256 -Path $unknownSteamAppId

    $null = Invoke-SetupExecutable `
        -FilePath $v2Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CLOSEAPPLICATIONS',
            "/LOG=$(Join-Path $workRootPath 'v2-v100-current-user-reinstall.log')")
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $v100InstallDirectory `
        -ExpectedDisplayVersion $expectedVersion
    Assert-Sentinel -Path $unknownSteamApi -ExpectedSha256 $unknownSteamApiHash
    Assert-Sentinel -Path $unknownSteamAppId -ExpectedSha256 $unknownSteamAppIdHash
    Assert-Sentinel -Path $v100InstallSentinel -ExpectedSha256 $v100InstallSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore
    $v100ManagedFiles = Get-ManagedApplicationFiles -InstallDirectory $v100InstallDirectory
    Remove-Item -LiteralPath $unknownSteamApi, $unknownSteamAppId -Force

    Invoke-OwnedUninstall `
        -InstallDirectory $v100InstallDirectory `
        -AllOwnedInstallDirectories $ownedInstallDirectories
    Assert-NoRegistration
    Assert-ManagedApplicationRemoved -ManagedFiles $v100ManagedFiles
    Assert-Sentinel -Path $v100InstallSentinel -ExpectedSha256 $v100InstallSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore

    # The official v1.0.0 release is current-user. Historical administrative
    # builds are no longer downloadable, so preserve the exact verified v1
    # registration values and move only their uninstall key to HKLM64.
    Write-Host 'Scenario B: verified v1.0.0 registration promoted to all-users -> direct silent v2 in-place upgrade.'
    $null = Invoke-SetupExecutable `
        -FilePath $v100Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/NOICONS',
            "/DIR=$adminInstallDirectory",
            "/LOG=$(Join-Path $workRootPath 'v1.0.0-admin-fixture-install.log')")
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $adminInstallDirectory `
        -ExpectedDisplayVersion '1.0.0'
    Move-CurrentUserRegistrationToAllUsers -ExpectedInstallDirectory $adminInstallDirectory
    Assert-Registration `
        -ExpectedHive HKLM `
        -ExpectedInstallDirectory $adminInstallDirectory `
        -ExpectedDisplayVersion '1.0.0'
    [System.IO.File]::WriteAllBytes(
        $adminInstallSentinel,
        [System.Text.Encoding]::UTF8.GetBytes('user-owned all-users install sentinel'))
    $adminInstallSentinelHash = Get-FileSha256 -Path $adminInstallSentinel
    $gmodBefore = Get-TreeFingerprint -Root $gmodDirectory
    $workshopBefore = Get-TreeFingerprint -Root $workshopDirectory

    $null = Invoke-SetupExecutable `
        -FilePath $v2Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CLOSEAPPLICATIONS',
            "/LOG=$(Join-Path $workRootPath 'v2-legacy-all-users-upgrade.log')") `
        -LaunchInstallDirectory $adminInstallDirectory `
        -RequireLaunch
    Stop-OwnedGamProcesses -OwnedInstallDirectories $ownedInstallDirectories
    Assert-Registration `
        -ExpectedHive HKLM `
        -ExpectedInstallDirectory $adminInstallDirectory `
        -ExpectedDisplayVersion $expectedVersion
    Assert-Sentinel -Path $adminInstallSentinel -ExpectedSha256 $adminInstallSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore
    $adminManagedFiles = Get-ManagedApplicationFiles -InstallDirectory $adminInstallDirectory

    Invoke-OwnedUninstall `
        -InstallDirectory $adminInstallDirectory `
        -AllOwnedInstallDirectories $ownedInstallDirectories
    Assert-NoRegistration
    Assert-ManagedApplicationRemoved -ManagedFiles $adminManagedFiles
    Assert-Sentinel -Path $adminInstallSentinel -ExpectedSha256 $adminInstallSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore

    Write-Host 'Scenario C: v1.0.26 current-user installation -> exact legacy updater arguments -> v2.'
    $null = Invoke-SetupExecutable `
        -FilePath $v1026Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/NOICONS',
            "/DIR=$perUserInstallDirectory",
            "/LOG=$(Join-Path $workRootPath 'v1.0.26-install.log')")
    Stop-OwnedGamProcesses -OwnedInstallDirectories $ownedInstallDirectories
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $perUserInstallDirectory `
        -ExpectedDisplayVersion '1.0.26'
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    $gmodBefore = Get-TreeFingerprint -Root $gmodDirectory
    $workshopBefore = Get-TreeFingerprint -Root $workshopDirectory

    Copy-Item -LiteralPath $v2Setup -Destination $updaterSetupPath
    $legacyUpdaterArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        '/CLOSEAPPLICATIONS'
    )
    $null = Invoke-SetupExecutable `
        -FilePath $updaterSetupPath `
        -Arguments $legacyUpdaterArguments `
        -LaunchInstallDirectory $perUserInstallDirectory `
        -RequireLaunch
    Stop-OwnedGamProcesses -OwnedInstallDirectories $ownedInstallDirectories
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $perUserInstallDirectory `
        -ExpectedDisplayVersion $expectedVersion
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore
    $perUserManagedFiles = Get-ManagedApplicationFiles -InstallDirectory $perUserInstallDirectory

    Invoke-OwnedUninstall `
        -InstallDirectory $perUserInstallDirectory `
        -AllOwnedInstallDirectories $ownedInstallDirectories
    Assert-NoRegistration
    Assert-ManagedApplicationRemoved -ManagedFiles $perUserManagedFiles
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore

    Write-Host 'Scenario D: clean v2 current-user installation.'
    $gmodBefore = Get-TreeFingerprint -Root $gmodDirectory
    $workshopBefore = Get-TreeFingerprint -Root $workshopDirectory
    $cleanLaunchObserved = Invoke-SetupExecutable `
        -FilePath $v2Setup `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CURRENTUSER',
            '/NOICONS',
            "/DIR=$cleanInstallDirectory",
            "/LOG=$(Join-Path $workRootPath 'v2-clean-current-user.log')")
    Assert-Registration `
        -ExpectedHive HKCU `
        -ExpectedInstallDirectory $cleanInstallDirectory `
        -ExpectedDisplayVersion $expectedVersion
    if ($cleanLaunchObserved -or
        (Test-OwnedGamProcessRunning -InstallDirectory $cleanInstallDirectory)) {
        throw 'A clean silent v2 install launched GAM without /LAUNCHAFTERINSTALL=1.'
    }
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore
    $cleanManagedFiles = Get-ManagedApplicationFiles -InstallDirectory $cleanInstallDirectory

    Invoke-OwnedUninstall `
        -InstallDirectory $cleanInstallDirectory `
        -AllOwnedInstallDirectories $ownedInstallDirectories
    Assert-NoRegistration
    Assert-ManagedApplicationRemoved -ManagedFiles $cleanManagedFiles
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-TreeUnchanged -Label 'GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged -Label 'Workshop tree' -Root $workshopDirectory -ExpectedFingerprint $workshopBefore

    Write-Host 'All v1-to-v2 installer upgrade scenarios passed.' -ForegroundColor Green
}
catch {
    $primaryFailure = $_
}
finally {
    try {
        Stop-OwnedGamProcesses -OwnedInstallDirectories $ownedInstallDirectories
    }
    catch {
        $cleanupFailures.Add("process cleanup: $($_.Exception.Message)")
    }

    foreach ($installDirectory in $ownedInstallDirectories) {
        try {
            if (Test-Path -LiteralPath $installDirectory -PathType Container) {
                $uninstaller = @(
                    Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue |
                        Sort-Object Name |
                        Select-Object -First 1
                )
                if ($uninstaller.Count -gt 0) {
                    $null = Invoke-SetupExecutable `
                        -FilePath $uninstaller[0].FullName `
                        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
                }
            }
        }
        catch {
            $cleanupFailures.Add("uninstall cleanup for '$installDirectory': $($_.Exception.Message)")
        }
    }

    try {
        Remove-OwnedRegistrations -OwnedInstallDirectories $ownedInstallDirectories
    }
    catch {
        $cleanupFailures.Add("registry cleanup: $($_.Exception.Message)")
    }

    try {
        if (Test-Path -LiteralPath $updaterSetupPath -PathType Leaf) {
            Remove-Item -LiteralPath $updaterSetupPath -Force
        }
    }
    catch {
        $cleanupFailures.Add("updater Setup cleanup: $($_.Exception.Message)")
    }

    try {
        if ($appDataCreated -and (Test-Path -LiteralPath $appDataDirectory)) {
            $appDataItem = Get-Item -LiteralPath $appDataDirectory -Force
            if (($appDataItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to recursively remove a reparse-point AppData path: $appDataDirectory"
            }
            Remove-Item -LiteralPath $appDataDirectory -Recurse -Force
        }
    }
    catch {
        $cleanupFailures.Add("AppData cleanup: $($_.Exception.Message)")
    }

    try {
        if ($workRootCreated -and (Test-Path -LiteralPath $workRootPath)) {
            if (-not (Test-PathsEqual -Left $workRootPath -Right $WorkRoot)) {
                throw "Normalized work root no longer matches the requested path: $workRootPath"
            }
            $workRootItem = Get-Item -LiteralPath $workRootPath -Force
            if (($workRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to recursively remove a reparse-point work root: $workRootPath"
            }
            Remove-Item -LiteralPath $workRootPath -Recurse -Force
        }
    }
    catch {
        $cleanupFailures.Add("work-root cleanup: $($_.Exception.Message)")
    }
}

if ($null -ne $primaryFailure) {
    if ($cleanupFailures.Count -gt 0) {
        Write-Warning "Cleanup also reported: $($cleanupFailures -join '; ')"
    }
    throw $primaryFailure
}

if ($cleanupFailures.Count -gt 0) {
    throw "Installer E2E passed, but cleanup failed: $($cleanupFailures -join '; ')"
}
