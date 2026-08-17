# One retry rule for every call the release makes to GitHub.
#
# Publishing is a dozen requests to the same API, and any one of them meeting a
# 503 used to end the job - which is how an incident on GitHub's side turns into
# a version that never ships. Only a server stumbling is retried: a 404 is an
# answer, and several steps here are built on getting one.
#
# Dot-source this and call Invoke-Gh where gh used to be called. The exit code
# the caller reads is gh's own, from its last attempt, so the checks around
# every call keep working unchanged.

# Seconds to wait between attempts; the count of attempts is one more than this.
# Settable so a test can watch the retries without sitting through two minutes.
$script:GhRetryDelays =
    if ($env:GH_RETRY_DELAYS) { @($env:GH_RETRY_DELAYS -split ',' | ForEach-Object { [int]$_ }) }
    else { @(5, 15, 30, 60, 120) }

<#
.SYNOPSIS
Tells a stumble from an answer in what gh printed when it failed.
#>
function Test-GhTransientFailure {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    # Both shapes gh reports a server error in: its own "HTTP 503: ..." and the
    # raw "non-200 OK status code: 503 ..." from an asset download.
    if ($Text -match '(?im)^\s*(HTTP\s+)?(429|500|502|503|504)\b') { return $true }
    if ($Text -match '(?i)(HTTP|status code:?)\s+(429|500|502|503|504)\b') { return $true }
    if ($Text -match '(?i)no server is currently available') { return $true }
    if ($Text -match '(?i)service unavailable|bad gateway|gateway time-?out|internal server error') { return $true }
    if ($Text -match '(?i)we couldn.t respond to your request in time') { return $true }
    if ($Text -match '(?i)secondary rate limit|rate limit exceeded|abuse detection') { return $true }
    if ($Text -match '(?i)connection (reset|refused|closed)|unexpected EOF|i/o timeout|tls handshake timeout') { return $true }
    return $false
}

<#
.SYNOPSIS
Runs gh, and runs it again while it is GitHub that is failing.
#>
function Invoke-Gh {
    $arguments = $args
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $attempts = $script:GhRetryDelays.Count + 1
    try {
        for ($attempt = 1; ; $attempt++) {
            # Standard output stays a clean stream for the caller to parse; what
            # gh says about the failure is read here and echoed to the log.
            $output = & gh @arguments 2> $stderrFile
            $code = $LASTEXITCODE
            $problem = Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue
            if ($problem) { Write-Host $problem.TrimEnd() }

            if ($code -eq 0 -or $attempt -ge $attempts -or -not (Test-GhTransientFailure $problem)) {
                # $LASTEXITCODE is still gh's: only cmdlets have run since.
                return $output
            }

            $delay = $script:GhRetryDelays[$attempt - 1]
            Write-Host ("gh $arguments failed while GitHub was unwell; " +
                        "retrying in $delay s (attempt $attempt of $attempts).")
            Start-Sleep -Seconds $delay
        }
    }
    finally {
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
}
