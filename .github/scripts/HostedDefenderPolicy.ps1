# The workflow may skip an unavailable Defender before a scan starts.
# A failed or ambiguous scan is never eligible for this exception.
function Assert-HostedDefenderUnavailable($Proof, [string]$Repository, [string]$Commit) {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $env:GITHUB_EVENT_NAME -cne 'push' -or $env:GITHUB_REF -cne 'refs/heads/main' -or
        $env:GITHUB_WORKFLOW_REF -cne "$Repository/.github/workflows/release.yml@refs/heads/main" -or
        $env:GITHUB_REPOSITORY -cne $Repository -or $env:GITHUB_SHA -cne $Commit) {
        throw 'Skipping an unavailable Defender is allowed only on the hosted main release workflow.'
    }
    if ($Proof.SchemaVersion -ne 1 -or $Proof.Result -cne 'Unknown' -or
        $Proof.DefenderUnavailableAtPreflight -isnot [bool] -or -not $Proof.DefenderUnavailableAtPreflight -or
        $null -eq $Proof.DefenderBefore -or $null -ne $Proof.NativeExitCode -or
        $null -ne $Proof.ScanStartedAtUtc -or $null -ne $Proof.ScanEndedAtUtc -or $null -ne $Proof.Tool -or
        $Proof.TimedOut -isnot [bool] -or $Proof.TimedOut -or $Proof.CompletionMethod -cne 'None' -or
        $Proof.MatchingDetections -isnot [array] -or $Proof.MatchingDetections.Count -ne 0 -or
        $Proof.Events -isnot [array] -or $Proof.Events.Count -ne 0) {
        throw 'Defender did not pass or explicitly report unavailability before the scan.'
    }
}
