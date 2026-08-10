#requires -Version 7.2

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$FromVersion = '2.0.0',

    [ValidateNotNullOrEmpty()]
    [string]$ToVersion = '2.0.4',

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Repository = 'RiRi-380/GAM'
$script:UninstallSubKey =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Gmod Addon Manager_is1'
$script:UninstallKeys = @{
    HKLM = "Registry::HKEY_LOCAL_MACHINE\$($script:UninstallSubKey)"
    HKCU = "Registry::HKEY_CURRENT_USER\$($script:UninstallSubKey)"
}
$script:GamProcessName = 'GmodAddonManager.UI'
$script:ExpectedSourceVersion = '2.0.0'
$script:ExpectedSourceSetupLength = [int64]39172870
$script:ExpectedSourceSetupSha256 =
    '2a2f19c41c97f709b6beac27cd8f236b0d3b742f5dc900299669f9569be14b07'
$script:ProcessTimeout = [TimeSpan]::FromMinutes(10)
$script:UiTimeout = [TimeSpan]::FromMinutes(4)
$script:UpdateTimeout = [TimeSpan]::FromMinutes(12)
$script:LaunchStabilityWindow = [TimeSpan]::FromSeconds(3)

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath
    }

    return $fullPath.TrimEnd([char[]]'\/')
}

function Test-PathsEqual {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    return [string]::Equals(
        (Get-NormalizedPath -Path $Left),
        (Get-NormalizedPath -Path $Right),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathIsWithin {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Parent
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

function Convert-ToStableVersion {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Label
    )

    $normalized = $Value.Trim()
    if ($normalized.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    if ($normalized -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Label must be an exact stable X.Y.Z version: $Value"
    }

    return [pscustomobject]@{
        Text = $normalized
        Parsed = [Version]::Parse($normalized)
        Tag = "v$normalized"
    }
}

function Assert-FreshWindows2022Runner {
    param([Parameter(Mandatory)][string]$ResolvedWorkRoot)

    if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
        throw 'This E2E requires 64-bit PowerShell on Windows.'
    }
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
        throw 'This destructive installer E2E is restricted to a fresh GitHub Actions Windows runner.'
    }
    if ($env:ImageOS -ne 'win22') {
        throw "This E2E is pinned to the windows-2022 image (ImageOS=win22); actual '$env:ImageOS'."
    }
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        throw 'RUNNER_TEMP is unavailable.'
    }

    $runnerTemp = Get-NormalizedPath -Path $env:RUNNER_TEMP
    if (-not (Test-PathIsWithin -Candidate $ResolvedWorkRoot -Parent $runnerTemp) -or
        (Test-PathsEqual -Left $ResolvedWorkRoot -Right $runnerTemp)) {
        throw "WorkRoot must be a new child of RUNNER_TEMP: $runnerTemp"
    }
    if (Test-Path -LiteralPath $ResolvedWorkRoot) {
        throw "WorkRoot must not already exist: $ResolvedWorkRoot"
    }

    foreach ($name in @(
            'GAM_GITHUB_TOKEN',
            'GAM_UPDATE_REPO',
            'GAM_UPDATE_API_URL',
            'GAM_UPDATE_INCLUDE_PRERELEASE')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            throw "$name must be unset so the production public updater path is exercised."
        }
        [Environment]::SetEnvironmentVariable($name, $null)
    }
}

function Get-Registration {
    param([Parameter(Mandatory)][ValidateSet('HKLM', 'HKCU')][string]$Hive)

    $path = $script:UninstallKeys[$Hive]
    if (-not (Test-Path -LiteralPath $path)) {
        return $null
    }

    return Get-ItemProperty -LiteralPath $path
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
        throw "A pre-existing GAM registration makes this runner unsafe: $($present -join ', ')"
    }
}

function Assert-Registration {
    param(
        [Parameter(Mandatory)][string]$ExpectedInstallDirectory,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )

    $registration = Get-Registration -Hive HKCU
    $machineRegistration = Get-Registration -Hive HKLM
    if ($null -eq $registration) {
        throw 'The expected current-user GAM registration is missing.'
    }
    if ($null -ne $machineRegistration) {
        throw 'GAM is unexpectedly registered for all users.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$registration.InstallLocation) -or
        -not (Test-PathsEqual `
            -Left ([string]$registration.InstallLocation) `
            -Right $ExpectedInstallDirectory)) {
        throw "InstallLocation mismatch: '$($registration.InstallLocation)'."
    }
    if (-not [string]::Equals(
            [string]$registration.DisplayVersion,
            $ExpectedVersion,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "DisplayVersion mismatch. Expected '$ExpectedVersion', actual '$($registration.DisplayVersion)'."
    }

    $uninstallString = [string]$registration.UninstallString
    if ([string]::IsNullOrWhiteSpace($uninstallString) -or
        $uninstallString.IndexOf(
            (Get-NormalizedPath -Path $ExpectedInstallDirectory),
            [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "UninstallString is not owned by the test install: $uninstallString"
    }

    $executable = Join-Path $ExpectedInstallDirectory 'GmodAddonManager.UI.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Installed GAM executable is missing: $executable"
    }
    $expected = [Version]::Parse($ExpectedVersion)
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($info.FileMajorPart -ne $expected.Major -or
        $info.FileMinorPart -ne $expected.Minor -or
        $info.FileBuildPart -ne $expected.Build) {
        throw "Installed FileVersion does not match $ExpectedVersion`: $($info.FileVersion)"
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Sentinel {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Sentinel was removed: $Path"
    }
    $actual = Get-FileSha256 -Path $Path
    if ($actual -ne $ExpectedSha256) {
        throw "Sentinel was modified: $Path"
    }
}

function Get-TreeFingerprint {
    param([Parameter(Mandatory)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Fingerprint root is missing: $Root"
    }
    $normalizedRoot = Get-NormalizedPath -Path $Root
    return @(
        Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($normalizedRoot, $_.FullName).
                    Replace('\', '/')
                "$relative|$($_.Length)|$(Get-FileSha256 -Path $_.FullName)"
            }
    ) -join "`n"
}

function Assert-TreeUnchanged {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ExpectedFingerprint
    )

    $actual = Get-TreeFingerprint -Root $Root
    if (-not [string]::Equals(
            $actual,
            $ExpectedFingerprint,
            [System.StringComparison]::Ordinal)) {
        throw "$Label changed during the updater E2E.`nExpected:`n$ExpectedFingerprint`nActual:`n$actual"
    }
}

function Invoke-Executable {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [TimeSpan]$Timeout = $script:ProcessTimeout
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = Split-Path -Parent $FilePath
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    Write-Host "Running: $FilePath $($Arguments -join ' ')"
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start: $FilePath"
    }

    if (-not $process.WaitForExit([int]$Timeout.TotalMilliseconds)) {
        try {
            $process.Kill($true)
        }
        catch {
            Write-Warning "Could not kill timed-out process $($process.Id): $($_.Exception.Message)"
        }
        throw "Process timed out after $([int]$Timeout.TotalMinutes) minutes: $FilePath"
    }
    if ($process.ExitCode -ne 0) {
        throw "Process exited with code $($process.ExitCode): $FilePath"
    }
}

function Get-PublicRelease {
    param([Parameter(Mandatory)][string]$Endpoint)

    $headers = @{
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'GAM-v2-public-updater-e2e'
    }
    return Invoke-RestMethod -Method Get -Uri $Endpoint -Headers $headers -TimeoutSec 30
}

function Get-ExactReleaseAsset {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$Name
    )

    $matches = @($Release.assets | Where-Object { [string]$_.name -ceq $Name })
    if ($matches.Count -ne 1) {
        throw "Release must contain exactly one '$Name' asset; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-PublicSourceRelease {
    param([Parameter(Mandatory)]$SourceVersion)

    $endpoint = "https://api.github.com/repos/$($script:Repository)/releases/tags/$($SourceVersion.Tag)"
    $release = Get-PublicRelease -Endpoint $endpoint
    if ([string]$release.tag_name -cne $SourceVersion.Tag -or
        [bool]$release.draft -or
        [bool]$release.prerelease) {
        throw "The audited source release is not an exact public stable release: $($SourceVersion.Tag)"
    }

    $assetName = "GAM-Setup-$($SourceVersion.Text).exe"
    $asset = Get-ExactReleaseAsset -Release $release -Name $assetName
    if ([string]$asset.state -cne 'uploaded' -or
        [int64]$asset.size -ne $script:ExpectedSourceSetupLength -or
        [string]$asset.digest -cne "sha256:$($script:ExpectedSourceSetupSha256)" -or
        -not ([string]$asset.browser_download_url).StartsWith(
            'https://',
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The public v2.0.0 Setup metadata no longer matches the audited fixture.'
    }

    return $asset
}

function Wait-ForPublicTargetRelease {
    param([Parameter(Mandatory)]$TargetVersion)

    $endpoint = "https://api.github.com/repos/$($script:Repository)/releases/latest"
    $lastReason = 'No response was received.'
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            $release = Get-PublicRelease -Endpoint $endpoint
            if ([string]$release.tag_name -cne $TargetVersion.Tag) {
                $lastReason = "latest tag is '$($release.tag_name)'"
            }
            elseif ([bool]$release.draft -or [bool]$release.prerelease) {
                $lastReason = 'the target release is draft or prerelease'
            }
            else {
                $stable = Get-ExactReleaseAsset -Release $release -Name 'GAM-Setup.exe'
                $versioned = Get-ExactReleaseAsset `
                    -Release $release `
                    -Name "GAM-Setup-$($TargetVersion.Text).exe"
                $digestPattern = '^sha256:[0-9a-f]{64}$'
                if ([string]$stable.state -cne 'uploaded' -or
                    [string]$versioned.state -cne 'uploaded') {
                    $lastReason = 'one or both Setup assets are not uploaded'
                }
                elseif ([int64]$stable.size -le 0 -or
                    [int64]$stable.size -ne [int64]$versioned.size) {
                    $lastReason = 'the stable/versioned Setup sizes are invalid or different'
                }
                elseif ([string]$stable.digest -cnotmatch $digestPattern -or
                    [string]$versioned.digest -cnotmatch $digestPattern -or
                    [string]$stable.digest -cne [string]$versioned.digest) {
                    $lastReason = 'the stable/versioned Setup digests are invalid or different'
                }
                elseif (-not ([string]$stable.browser_download_url).StartsWith(
                        'https://',
                        [System.StringComparison]::OrdinalIgnoreCase) -or
                    -not ([string]$versioned.browser_download_url).StartsWith(
                        'https://',
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    $lastReason = 'a Setup browser download URL is not HTTPS'
                }
                else {
                    Write-Host "Public target release is ready: $($TargetVersion.Tag), $($stable.digest)"
                    return $release
                }
            }
        }
        catch {
            $lastReason = $_.Exception.Message
        }

        if ($attempt -lt 20) {
            Write-Host "Waiting for public target release ($attempt/20): $lastReason"
            Start-Sleep -Seconds 15
        }
    }

    throw "Public target release did not become ready: $lastReason"
}

function Save-PublicFile {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][int64]$ExpectedLength,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $handler.UseDefaultCredentials = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromMinutes(5)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('GAM-v2-public-updater-e2e')
        $response = $client.GetAsync(
            $Uri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).
            GetAwaiter().GetResult()
        try {
            [void]$response.EnsureSuccessStatusCode()
            $finalUri = $response.RequestMessage.RequestUri
            if ($null -eq $finalUri -or
                $finalUri.Scheme -cne [Uri]::UriSchemeHttps) {
                throw 'The public Setup download redirected to a non-HTTPS URI.'
            }
            $declaredLength = $response.Content.Headers.ContentLength
            if ($null -ne $declaredLength -and
                [int64]$declaredLength -ne $ExpectedLength) {
                throw "Public Setup Content-Length mismatch: $declaredLength."
            }

            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $output = [System.IO.FileStream]::new(
                    $Destination,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None)
                try {
                    $buffer = [byte[]]::new(81920)
                    [int64]$totalBytes = 0
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        if ($read -gt $ExpectedLength - $totalBytes) {
                            throw 'The public Setup download exceeded its fixed audited size.'
                        }
                        $output.Write($buffer, 0, $read)
                        $totalBytes += $read
                    }
                    if ($totalBytes -ne $ExpectedLength) {
                        throw "Public Setup size mismatch: $totalBytes."
                    }
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $input.Dispose()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $file = Get-Item -LiteralPath $Destination
    $actualHash = Get-FileSha256 -Path $Destination
    if ($file.Length -ne $ExpectedLength -or $actualHash -ne $ExpectedSha256) {
        throw "Downloaded v2.0.0 Setup failed its fixed size/hash check: $($file.Length), $actualHash"
    }
}

function Get-UpdateArtifactPaths {
    param([Parameter(Mandatory)][string]$TemporaryDirectory)

    return @(
        Get-ChildItem -LiteralPath $TemporaryDirectory -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -like 'GAM-Update-Package-*' -or
                $_.Name -like 'GAM-Update-Launcher-*'
            } |
            ForEach-Object { Get-NormalizedPath -Path $_.FullName }
    )
}

function Update-ObservedArtifacts {
    param(
        [Parameter(Mandatory)][string]$TemporaryDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$BaselineArtifacts,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts
    )

    foreach ($path in @(Get-UpdateArtifactPaths -TemporaryDirectory $TemporaryDirectory)) {
        if (-not $BaselineArtifacts.Contains($path)) {
            $null = $ObservedArtifacts.Add($path)
        }
    }
}

function Convert-AutomationCollection {
    param([Parameter(Mandatory)]$Collection)

    return @(
        for ($index = 0; $index -lt $Collection.Count; $index++) {
            $Collection.Item($index)
        }
    )
}

function Get-ExactAutomationDescendant {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)]$ControlType,
        [Parameter(Mandatory)][string]$Name,
        [string]$AutomationId
    )

    $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    $typeMatches = Convert-AutomationCollection -Collection (
        $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $typeCondition))

    if (-not [string]::IsNullOrWhiteSpace($AutomationId)) {
        $idMatches = @(
            $typeMatches | Where-Object {
                $_.Current.AutomationId -ceq $AutomationId
            }
        )
        if ($idMatches.Count -gt 1) {
            throw "AutomationId '$AutomationId' is ambiguous."
        }
        if ($idMatches.Count -eq 1) {
            if ($idMatches[0].Current.Name -cne $Name) {
                throw "AutomationId '$AutomationId' has unexpected Name '$($idMatches[0].Current.Name)'."
            }
            return $idMatches[0]
        }
    }

    $nameMatches = @($typeMatches | Where-Object { $_.Current.Name -ceq $Name })
    if ($nameMatches.Count -ne 1) {
        throw "Expected exactly one $ControlType named '$Name'; found $($nameMatches.Count)."
    }
    return $nameMatches[0]
}

function Wait-ForUpdateDialogAndInvoke {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)]$SourceVersion,
        [Parameter(Mandatory)]$TargetVersion,
        [Parameter(Mandatory)][string]$TemporaryDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$BaselineArtifacts,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts
    )

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $condition = [System.Windows.Automation.AndCondition]::new(
        $processCondition,
        $windowCondition)
    $deadline = [DateTime]::UtcNow + $script:UiTimeout
    $dialog = $null

    do {
        Update-ObservedArtifacts `
            -TemporaryDirectory $TemporaryDirectory `
            -BaselineArtifacts $BaselineArtifacts `
            -ObservedArtifacts $ObservedArtifacts
        if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "The v$($SourceVersion.Text) process exited before its update dialog appeared."
        }

        $windows = Convert-AutomationCollection -Collection (
            $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
        $dialogs = @($windows | Where-Object { $_.Current.Name -ceq 'Update' })
        if ($dialogs.Count -gt 1) {
            throw 'More than one exact Update window belongs to the source GAM process.'
        }
        if ($dialogs.Count -eq 1) {
            $dialog = $dialogs[0]
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($null -eq $dialog) {
        throw "The v$($SourceVersion.Text) update dialog did not appear within $([int]$script:UiTimeout.TotalMinutes) minutes."
    }

    $currentText = Get-ExactAutomationDescendant `
        -Root $dialog `
        -ControlType ([System.Windows.Automation.ControlType]::Text) `
        -Name "Current version: v$($SourceVersion.Text)" `
        -AutomationId 'GAM.UpdateDialog.CurrentVersion'
    $newText = Get-ExactAutomationDescendant `
        -Root $dialog `
        -ControlType ([System.Windows.Automation.ControlType]::Text) `
        -Name "New version: v$($TargetVersion.Text)" `
        -AutomationId 'GAM.UpdateDialog.NewVersion'
    $button = Get-ExactAutomationDescendant `
        -Root $dialog `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -Name 'Update now' `
        -AutomationId 'GAM.UpdateDialog.UpdateNow'

    if ($currentText.Current.ProcessId -ne $ProcessId -or
        $newText.Current.ProcessId -ne $ProcessId -or
        $button.Current.ProcessId -ne $ProcessId -or
        -not $button.Current.IsEnabled -or
        $button.Current.IsOffscreen) {
        throw 'The exact update controls are not safely invokable in the source process.'
    }

    try {
        $invoke = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
    }
    catch {
        throw "The exact Update now button does not expose InvokePattern: $($_.Exception.Message)"
    }
    Write-Host "Invoking the real v$($SourceVersion.Text) Update now button via UI Automation."
    $invoke.Invoke()
}

function Get-OwnedGamProcesses {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    return @(
        foreach ($process in @(Get-Process -Name $script:GamProcessName -ErrorAction SilentlyContinue)) {
            try {
                $path = $process.Path
            }
            catch {
                continue
            }
            if (-not [string]::IsNullOrWhiteSpace($path) -and
                (Test-PathIsWithin -Candidate $path -Parent $InstallDirectory)) {
                $process
            }
        }
    )
}

function Stop-OwnedGamProcesses {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    foreach ($process in @(Get-OwnedGamProcesses -InstallDirectory $InstallDirectory)) {
        try {
            $process.Kill($true)
            [void]$process.WaitForExit(15000)
        }
        catch {
            throw "Could not stop owned GAM process $($process.Id): $($_.Exception.Message)"
        }
    }
}

function Wait-ForSourceExit {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$SourceProcess,
        [Parameter(Mandatory)][string]$TemporaryDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$BaselineArtifacts,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts
    )

    $deadline = [DateTime]::UtcNow + $script:UpdateTimeout
    do {
        Update-ObservedArtifacts `
            -TemporaryDirectory $TemporaryDirectory `
            -BaselineArtifacts $BaselineArtifacts `
            -ObservedArtifacts $ObservedArtifacts
        $SourceProcess.Refresh()
        if ($SourceProcess.HasExited) {
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The source GAM process did not exit after Update now was invoked.'
}

function Wait-ForUpdatedRelaunch {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][int]$OldProcessId,
        [Parameter(Mandatory)]$TargetVersion,
        [Parameter(Mandatory)][string]$TemporaryDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$BaselineArtifacts,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts
    )

    $deadline = [DateTime]::UtcNow + $script:UpdateTimeout
    $stableProcess = $null
    $stableSince = $null
    do {
        Update-ObservedArtifacts `
            -TemporaryDirectory $TemporaryDirectory `
            -BaselineArtifacts $BaselineArtifacts `
            -ObservedArtifacts $ObservedArtifacts

        $processes = @(Get-OwnedGamProcesses -InstallDirectory $InstallDirectory)
        if ($processes.Count -gt 1) {
            throw "More than one GAM process relaunched from the owned install: $($processes.Id -join ', ')"
        }
        if ($processes.Count -eq 1 -and $processes[0].Id -ne $OldProcessId) {
            $registration = Get-Registration -Hive HKCU
            $machineRegistration = Get-Registration -Hive HKLM
            if ($null -ne $machineRegistration) {
                throw 'The updater unexpectedly created an all-users registration.'
            }
            if ($null -ne $registration -and
                -not (Test-PathsEqual `
                    -Left ([string]$registration.InstallLocation) `
                    -Right $InstallDirectory)) {
                throw "The updater registration moved to an unexpected directory: $($registration.InstallLocation)"
            }

            $executable = Join-Path $InstallDirectory 'GmodAddonManager.UI.exe'
            $fileVersionReady = $false
            if (Test-Path -LiteralPath $executable -PathType Leaf) {
                $expectedFileVersion = $TargetVersion.Parsed
                $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
                $fileVersionReady =
                    $versionInfo.FileMajorPart -eq $expectedFileVersion.Major -and
                    $versionInfo.FileMinorPart -eq $expectedFileVersion.Minor -and
                    $versionInfo.FileBuildPart -eq $expectedFileVersion.Build
            }
            if ($null -ne $registration -and
                [string]$registration.DisplayVersion -ceq $TargetVersion.Text -and
                $fileVersionReady) {
                Assert-Registration `
                    -ExpectedInstallDirectory $InstallDirectory `
                    -ExpectedVersion $TargetVersion.Text
                $mainWindow = Find-AutomationWindow `
                    -ProcessId $processes[0].Id `
                    -Name 'Gmod Addon Manager'
                if ($null -ne $mainWindow) {
                    if ($null -eq $stableProcess -or $stableProcess.Id -ne $processes[0].Id) {
                        $stableProcess = $processes[0]
                        $stableSince = [DateTime]::UtcNow
                    }
                    elseif (([DateTime]::UtcNow - $stableSince) -ge $script:LaunchStabilityWindow) {
                        return $stableProcess
                    }
                }
                else {
                    $stableProcess = $null
                    $stableSince = $null
                }
            }
            else {
                $stableProcess = $null
                $stableSince = $null
            }
        }
        else {
            $stableProcess = $null
            $stableSince = $null
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "GAM did not relaunch stably as v$($TargetVersion.Text) from the same install directory."
}

function Find-AutomationWindow {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$Name
    )

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $condition = [System.Windows.Automation.AndCondition]::new(
        $processCondition,
        $windowCondition)
    $windows = Convert-AutomationCollection -Collection (
        $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
    $matches = @($windows | Where-Object { $_.Current.Name -ceq $Name })
    if ($matches.Count -gt 1) {
        throw "More than one exact '$Name' window belongs to process $ProcessId."
    }
    if ($matches.Count -eq 1) {
        return $matches[0]
    }
    return $null
}

function Wait-ForUpdaterArtifactsRemoved {
    param(
        [Parameter(Mandatory)][string]$TemporaryDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$BaselineArtifacts,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts
    )

    $deadline = [DateTime]::UtcNow + [TimeSpan]::FromMinutes(2)
    do {
        Update-ObservedArtifacts `
            -TemporaryDirectory $TemporaryDirectory `
            -BaselineArtifacts $BaselineArtifacts `
            -ObservedArtifacts $ObservedArtifacts
        $remaining = @($ObservedArtifacts | Where-Object { Test-Path -LiteralPath $_ })
        if ($remaining.Count -eq 0) {
            $packages = @($ObservedArtifacts | Where-Object {
                    [System.IO.Path]::GetFileName($_) -like 'GAM-Update-Package-*'
                })
            $launchers = @($ObservedArtifacts | Where-Object {
                    [System.IO.Path]::GetFileName($_) -like 'GAM-Update-Launcher-*'
                })
            if ($packages.Count -eq 0 -or $launchers.Count -eq 0) {
                throw 'The updater E2E did not observe both its real package and launcher artifacts.'
            }
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Updater temp artifacts were not cleaned: $($remaining -join ', ')"
}

function Stop-ProcessesReferencingArtifacts {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$ObservedArtifacts)

    if ($ObservedArtifacts.Count -eq 0) {
        return
    }
    foreach ($item in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
        if ($item.ProcessId -eq $PID -or [string]::IsNullOrWhiteSpace([string]$item.CommandLine)) {
            continue
        }
        $owned = $false
        foreach ($path in $ObservedArtifacts) {
            if (([string]$item.CommandLine).IndexOf(
                    $path,
                    [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $owned = $true
                break
            }
        }
        if (-not $owned) {
            continue
        }
        try {
            $process = Get-Process -Id ([int]$item.ProcessId) -ErrorAction Stop
            $process.Kill($true)
            [void]$process.WaitForExit(15000)
        }
        catch {
            Write-Warning "Could not stop owned updater process $($item.ProcessId): $($_.Exception.Message)"
        }
    }
}

function Get-ManagedApplicationFiles {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    $manifest = Join-Path $InstallDirectory 'GAM-ReleaseFiles.txt'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Managed release-file manifest is missing: $manifest"
    }
    $files = @(
        foreach ($relative in @(Get-Content -LiteralPath $manifest -Encoding UTF8)) {
            if ([string]::IsNullOrWhiteSpace($relative) -or
                [System.IO.Path]::IsPathRooted($relative)) {
                throw "Unsafe managed-file path: '$relative'"
            }
            $candidate = Get-NormalizedPath -Path (Join-Path $InstallDirectory $relative)
            if (-not (Test-PathIsWithin -Candidate $candidate -Parent $InstallDirectory)) {
                throw "Managed-file path escapes the install: '$relative'"
            }
            $candidate
        }
    )
    if ($files.Count -eq 0) {
        throw 'Managed release-file manifest is empty.'
    }
    return $files
}

function Invoke-OwnedUninstall {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    $registration = Get-Registration -Hive HKCU
    if ($null -eq $registration -or
        -not (Test-PathsEqual `
            -Left ([string]$registration.InstallLocation) `
            -Right $InstallDirectory)) {
        throw 'Refusing to uninstall because the current-user registration is not the owned install.'
    }
    if ($null -ne (Get-Registration -Hive HKLM)) {
        throw 'Refusing to uninstall while an unexpected all-users registration exists.'
    }

    $uninstallers = @(
        Get-ChildItem -LiteralPath $InstallDirectory -Filter 'unins*.exe' -File |
            Sort-Object Name
    )
    if ($uninstallers.Count -ne 1) {
        throw "Expected one owned uninstaller; found $($uninstallers.Count)."
    }
    Invoke-Executable `
        -FilePath $uninstallers[0].FullName `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
}

function Remove-OwnedRegistrationFallback {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    foreach ($hive in @('HKCU', 'HKLM')) {
        $registration = Get-Registration -Hive $hive
        if ($null -eq $registration) {
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$registration.InstallLocation) -and
            (Test-PathsEqual `
                -Left ([string]$registration.InstallLocation) `
                -Right $InstallDirectory)) {
            Remove-Item -LiteralPath $script:UninstallKeys[$hive] -Recurse -Force
        }
        else {
            throw "Refusing to remove non-owned $hive GAM registration."
        }
    }
}

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedExactPath
    )

    if (-not (Test-PathsEqual -Left $Path -Right $ExpectedExactPath)) {
        throw "Refusing to remove a directory outside its exact owned path: $Path"
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to recursively remove a reparse point: $Path"
    }
    $nestedReparsePoints = @(
        Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop |
            Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            }
    )
    if ($nestedReparsePoints.Count -ne 0) {
        throw (
            "Refusing to recursively remove an owned tree containing reparse points: " +
            ($nestedReparsePoints.FullName -join ', '))
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$sourceVersion = Convert-ToStableVersion -Value $FromVersion -Label 'FromVersion'
$targetVersion = Convert-ToStableVersion -Value $ToVersion -Label 'ToVersion'
if ($sourceVersion.Text -cne $script:ExpectedSourceVersion) {
    throw "Only the audited v$($script:ExpectedSourceVersion) source fixture is supported."
}
if ($targetVersion.Parsed -le $sourceVersion.Parsed) {
    throw 'ToVersion must be newer than FromVersion.'
}

$workRootPath = Get-NormalizedPath -Path $WorkRoot
Assert-FreshWindows2022Runner -ResolvedWorkRoot $workRootPath
Assert-NoRegistration
if (@(Get-Process -Name $script:GamProcessName -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'A pre-existing GAM process makes this runner unsafe.'
}

$appDataDirectory = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) `
    'GmodAddonManager'
$appDataDirectory = Get-NormalizedPath -Path $appDataDirectory
if (Test-Path -LiteralPath $appDataDirectory) {
    throw "GAM AppData already exists; use a fresh Windows runner: $appDataDirectory"
}
$temporaryDirectory = Get-NormalizedPath -Path ([System.IO.Path]::GetTempPath())
$baselineArtifacts = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($path in @(Get-UpdateArtifactPaths -TemporaryDirectory $temporaryDirectory)) {
    $null = $baselineArtifacts.Add($path)
}
$observedArtifacts = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

$installDirectory = Join-Path $workRootPath 'installed GAM'
$sourceSetupPath = Join-Path $workRootPath "GAM-Setup-$($sourceVersion.Text).exe"
$steamLibrary = Join-Path $workRootPath 'Steam Library'
$gmodDirectory = Join-Path $steamLibrary 'steamapps\common\GarrysMod'
$gmodCfgDirectory = Join-Path $gmodDirectory 'garrysmod\cfg'
$gmodAddonsDirectory = Join-Path $gmodDirectory 'garrysmod\addons'
$workshopDirectory = Join-Path $steamLibrary 'steamapps\workshop\content\4000'
$gmodSentinel = Join-Path $gmodDirectory 'updater-gmod-sentinel.bin'
$workshopSentinel = Join-Path $workshopDirectory 'updater-workshop-sentinel.bin'
$appDataSentinel = Join-Path $appDataDirectory 'updater-appdata-sentinel.bin'
$settingsPath = Join-Path $appDataDirectory 'settings.json'
$installSentinel = Join-Path $installDirectory 'user-owned-install-sentinel.bin'
$workRootCreated = $false
$appDataCreated = $false
$installCreated = $false
$managedApplicationFiles = @()
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
        $gmodSentinel,
        [System.Text.Encoding]::UTF8.GetBytes(
            "GAM updater E2E GMod sentinel $([Guid]::NewGuid().ToString('N'))"))
    [System.IO.File]::WriteAllBytes(
        $workshopSentinel,
        [System.Text.Encoding]::UTF8.GetBytes(
            "GAM updater E2E Workshop sentinel $([Guid]::NewGuid().ToString('N'))"))
    [System.IO.File]::WriteAllBytes(
        $appDataSentinel,
        [System.Text.Encoding]::UTF8.GetBytes(
            "GAM updater E2E AppData sentinel $([Guid]::NewGuid().ToString('N'))"))

    $settings = [ordered]@{
        Language = 'en-US'
        ShowConsoleOnStartup = $false
        CustomGmodInstallPath = $gmodDirectory
        CustomWorkshopPath = $workshopDirectory
        ConfirmedGmodInstallPath = $gmodDirectory
        ConfirmedWorkshopPath = $workshopDirectory
    }
    [System.IO.File]::WriteAllText(
        $settingsPath,
        ($settings | ConvertTo-Json -Depth 4),
        $utf8NoBom)

    $gmodBefore = Get-TreeFingerprint -Root $gmodDirectory
    $workshopBefore = Get-TreeFingerprint -Root $workshopDirectory
    $gmodSentinelHash = Get-FileSha256 -Path $gmodSentinel
    $workshopSentinelHash = Get-FileSha256 -Path $workshopSentinel
    $appDataSentinelHash = Get-FileSha256 -Path $appDataSentinel
    $settingsHash = Get-FileSha256 -Path $settingsPath

    $sourceAsset = Assert-PublicSourceRelease -SourceVersion $sourceVersion
    $null = Wait-ForPublicTargetRelease -TargetVersion $targetVersion
    Save-PublicFile `
        -Uri ([string]$sourceAsset.browser_download_url) `
        -Destination $sourceSetupPath `
        -ExpectedLength $script:ExpectedSourceSetupLength `
        -ExpectedSha256 $script:ExpectedSourceSetupSha256

    Invoke-Executable `
        -FilePath $sourceSetupPath `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            '/CURRENTUSER',
            '/NOICONS',
            "/DIR=$installDirectory",
            "/LOG=$(Join-Path $workRootPath 'source-install.log')")
    $installCreated = $true
    Assert-Registration `
        -ExpectedInstallDirectory $installDirectory `
        -ExpectedVersion $sourceVersion.Text

    [System.IO.File]::WriteAllBytes(
        $installSentinel,
        [System.Text.Encoding]::UTF8.GetBytes(
            "GAM updater E2E install sentinel $([Guid]::NewGuid().ToString('N'))"))
    $installSentinelHash = Get-FileSha256 -Path $installSentinel

    $executable = Join-Path $installDirectory 'GmodAddonManager.UI.exe'
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $installDirectory
    $startInfo.UseShellExecute = $false
    foreach ($name in @(
            'GAM_GITHUB_TOKEN',
            'GAM_UPDATE_REPO',
            'GAM_UPDATE_API_URL',
            'GAM_UPDATE_INCLUDE_PRERELEASE')) {
        $null = $startInfo.Environment.Remove($name)
    }
    $sourceProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $sourceProcess) {
        throw 'Could not launch the installed v2.0.0 application.'
    }

    Wait-ForUpdateDialogAndInvoke `
        -ProcessId $sourceProcess.Id `
        -SourceVersion $sourceVersion `
        -TargetVersion $targetVersion `
        -TemporaryDirectory $temporaryDirectory `
        -BaselineArtifacts $baselineArtifacts `
        -ObservedArtifacts $observedArtifacts
    Wait-ForSourceExit `
        -SourceProcess $sourceProcess `
        -TemporaryDirectory $temporaryDirectory `
        -BaselineArtifacts $baselineArtifacts `
        -ObservedArtifacts $observedArtifacts

    $updatedProcess = Wait-ForUpdatedRelaunch `
        -InstallDirectory $installDirectory `
        -OldProcessId $sourceProcess.Id `
        -TargetVersion $targetVersion `
        -TemporaryDirectory $temporaryDirectory `
        -BaselineArtifacts $baselineArtifacts `
        -ObservedArtifacts $observedArtifacts
    if ($updatedProcess.Id -eq $sourceProcess.Id) {
        throw 'The updated process reused the source PID; relaunch was not proven.'
    }
    Assert-Registration `
        -ExpectedInstallDirectory $installDirectory `
        -ExpectedVersion $targetVersion.Text
    # v2.0.0 used Start-Process -Wait, which waits for the relaunched GAM child
    # as well as the installer. Closing the already-proven stable relaunch lets
    # that legacy launcher finish and proves its eventual cleanup contract.
    Write-Host 'Stopping the stable updated GAM process to let the v2.0.0 launcher finish cleanup.'
    Stop-OwnedGamProcesses -InstallDirectory $installDirectory
    Wait-ForUpdaterArtifactsRemoved `
        -TemporaryDirectory $temporaryDirectory `
        -BaselineArtifacts $baselineArtifacts `
        -ObservedArtifacts $observedArtifacts

    Assert-Sentinel -Path $gmodSentinel -ExpectedSha256 $gmodSentinelHash
    Assert-Sentinel -Path $workshopSentinel -ExpectedSha256 $workshopSentinelHash
    Assert-Sentinel -Path $appDataSentinel -ExpectedSha256 $appDataSentinelHash
    Assert-Sentinel -Path $settingsPath -ExpectedSha256 $settingsHash
    Assert-Sentinel -Path $installSentinel -ExpectedSha256 $installSentinelHash
    Assert-TreeUnchanged -Label 'Fake GMod tree' -Root $gmodDirectory -ExpectedFingerprint $gmodBefore
    Assert-TreeUnchanged `
        -Label 'Fake Workshop tree' `
        -Root $workshopDirectory `
        -ExpectedFingerprint $workshopBefore

    $managedApplicationFiles = @(Get-ManagedApplicationFiles -InstallDirectory $installDirectory)
    Write-Host (
        "PASS: real public v$($sourceVersion.Text) GUI updated in place to " +
        "v$($targetVersion.Text), relaunched, and preserved all sentinels.")
}
catch {
    $primaryFailure = $_
}
finally {
    try {
        Update-ObservedArtifacts `
            -TemporaryDirectory $temporaryDirectory `
            -BaselineArtifacts $baselineArtifacts `
            -ObservedArtifacts $observedArtifacts
        Stop-ProcessesReferencingArtifacts -ObservedArtifacts $observedArtifacts
    }
    catch {
        $cleanupFailures.Add("owned updater process cleanup: $($_.Exception.Message)")
    }
    try {
        Stop-OwnedGamProcesses -InstallDirectory $installDirectory
    }
    catch {
        $cleanupFailures.Add("owned GAM process cleanup: $($_.Exception.Message)")
    }
    try {
        if ($installCreated -and $null -ne (Get-Registration -Hive HKCU)) {
            Invoke-OwnedUninstall -InstallDirectory $installDirectory
        }
    }
    catch {
        $cleanupFailures.Add("owned uninstall: $($_.Exception.Message)")
    }
    try {
        if ($managedApplicationFiles.Count -ne 0) {
            $remainingManagedFiles = @(
                $managedApplicationFiles | Where-Object { Test-Path -LiteralPath $_ }
            )
            if ($remainingManagedFiles.Count -ne 0) {
                throw "Uninstall left managed GAM files behind: $($remainingManagedFiles -join ', ')"
            }
        }
    }
    catch {
        $cleanupFailures.Add("managed application cleanup verification: $($_.Exception.Message)")
    }
    try {
        Remove-OwnedRegistrationFallback -InstallDirectory $installDirectory
        Assert-NoRegistration
    }
    catch {
        $cleanupFailures.Add("owned registration cleanup: $($_.Exception.Message)")
    }
    try {
        foreach ($path in $observedArtifacts) {
            if ((Test-PathIsWithin -Candidate $path -Parent $temporaryDirectory) -and
                (Test-Path -LiteralPath $path -PathType Leaf)) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }
    catch {
        $cleanupFailures.Add("owned updater artifact cleanup: $($_.Exception.Message)")
    }
    try {
        if ($appDataCreated) {
            Remove-OwnedDirectory `
                -Path $appDataDirectory `
                -ExpectedExactPath $appDataDirectory
        }
    }
    catch {
        $cleanupFailures.Add("owned AppData cleanup: $($_.Exception.Message)")
    }
    try {
        if ($workRootCreated) {
            Remove-OwnedDirectory -Path $workRootPath -ExpectedExactPath $workRootPath
        }
    }
    catch {
        $cleanupFailures.Add("owned WorkRoot cleanup: $($_.Exception.Message)")
    }
}

if ($null -ne $primaryFailure) {
    if ($cleanupFailures.Count -ne 0) {
        throw (
            "$($primaryFailure.Exception.Message)`nCleanup also failed:`n- " +
            ($cleanupFailures -join "`n- "))
    }
    throw $primaryFailure
}
if ($cleanupFailures.Count -ne 0) {
    throw "The updater passed, but cleanup failed:`n- $($cleanupFailures -join "`n- ")"
}
