#requires -Version 7.2
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HostedDefenderPolicy.ps1')

$repository = 'Owner/Launcher'
$commit = 'a' * 40
$context = @{
    GITHUB_ACTIONS = 'true'
    RUNNER_ENVIRONMENT = 'github-hosted'
    GITHUB_EVENT_NAME = 'push'
    GITHUB_REF = 'refs/heads/main'
    GITHUB_WORKFLOW_REF = "$repository/.github/workflows/release.yml@refs/heads/main"
    GITHUB_REPOSITORY = $repository
    GITHUB_SHA = $commit
}
$original = @{}
foreach ($key in $context.Keys) {
    $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

function New-Proof {
    return [pscustomobject]@{
        SchemaVersion = 1
        Result = 'Unknown'
        DefenderUnavailableAtPreflight = $true
        Before = [pscustomobject]@{ Sha256 = 'b' * 64; SizeBytes = 10 }
        After = $null
        DefenderBefore = [pscustomobject]@{
            AMServiceEnabled = $true
            AntivirusEnabled = $true
            RealTimeProtectionEnabled = $false
            AMRunningMode = 'Normal'
            DefenderSignaturesOutOfDate = $false
        }
        DefenderAfter = $null
        NativeExitCode = $null
        ScanStartedAtUtc = $null
        ScanEndedAtUtc = $null
        Tool = $null
        TimedOut = $false
        CompletionMethod = 'None'
        Events = @()
        MatchingDetections = @()
    }
}

function Assert-Rejected([string]$Name, $Proof) {
    try { Assert-HostedDefenderUnavailable $Proof $repository $commit }
    catch { return }
    throw "Hosted Defender policy accepted $Name."
}

$cases = 0
try {
    foreach ($key in $context.Keys) {
        [Environment]::SetEnvironmentVariable($key, $context[$key], 'Process')
    }
    Assert-HostedDefenderUnavailable (New-Proof) $repository $commit
    $cases++

    foreach ($key in $context.Keys) {
        [Environment]::SetEnvironmentVariable($key, 'wrong', 'Process')
        Assert-Rejected "wrong $key" (New-Proof)
        [Environment]::SetEnvironmentVariable($key, $context[$key], 'Process')
        $cases++
    }

    $invalidEvidence = @(
        @{ SchemaVersion = 2 },
        @{ Result = 'Detected' },
        @{ DefenderUnavailableAtPreflight = $false },
        @{ DefenderUnavailableAtPreflight = 'true' },
        @{ DefenderBefore = $null },
        @{ NativeExitCode = 2 },
        @{ NativeExitCode = 0 },
        @{ ScanStartedAtUtc = '2026-11-06T00:00:00Z' },
        @{ ScanEndedAtUtc = '2026-11-06T00:00:00Z' },
        @{ Tool = [pscustomobject]@{ Path = 'MpCmdRun.exe' } },
        @{ TimedOut = $true },
        @{ TimedOut = 'false' },
        @{ CompletionMethod = 'DefenderOperationalScanId' },
        @{ Events = @([pscustomobject]@{ Id = 1116 }) },
        @{ Events = $null },
        @{ MatchingDetections = $null },
        @{ MatchingDetections = @([pscustomobject]@{ ThreatId = 1 }) }
    )
    foreach ($change in $invalidEvidence) {
        $proof = New-Proof
        foreach ($key in $change.Keys) { $proof.$key = $change[$key] }
        Assert-Rejected "changed $($change.Keys -join ', ')" $proof
        $cases++
    }

    Assert-Rejected 'missing evidence' $null
    $cases++
}
finally {
    foreach ($key in $original.Keys) {
        [Environment]::SetEnvironmentVariable($key, $original[$key], 'Process')
    }
}
Write-Host "Hosted Defender policy: $cases cases passed."
