<#
.SYNOPSIS
Finds the executables this release can be a delta against.

.DESCRIPTION
Downloads the published `latest` release and up to one archived base, proves
each executable is the one its manifest describes, and writes the set to the
bases file the delta step reads. Returns what the caller should put in the
job's environment; nothing here writes GITHUB_ENV, because this runs beside
other work and two writers to one file interleave.

Not finding a base is an answer, not a failure: the first release ever, or a
publish that died halfway, simply ships the full executable.
#>
param(
    [Parameter(Mandatory = $true)][int]$CommitNumber,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$BasesFile,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [Parameter(Mandatory = $true)][string]$Repository
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GhRetry.ps1"
. "$PSScriptRoot/CompressedHistoryRelease.ps1"
. "$PSScriptRoot/ReleaseBase.ps1"
# A background job starts wherever PowerShell puts it, so gh is told which
# repository it is talking about instead of being left to find a git remote.
$env:GH_REPO = $Repository

$currentCommit = $CommitSha.ToLowerInvariant()
$outcome = [ordered]@{
    HasPrevious = $false
    ReleaseNumber = $CommitNumber
    PreviousExe = ''
    PreviousManifest = ''
    PreviousCommit = ''
    PreviousSha256 = ''
    PreviousReleaseNumber = 0
}
'[]' | Set-Content -LiteralPath $BasesFile -Encoding UTF8

Invoke-Gh release view latest --json tagName *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "No previous latest release is available; a full-only release will be created."
    return [pscustomobject]$outcome
}

$previousDir = Join-Path $WorkDirectory 'minecraft-base-latest'
try {
    $previous = Get-ValidatedBase 'latest' $previousDir $Repository
}
catch {
    # A publish that died between the executable and its manifest leaves the
    # release without the pointer that names it. Launchers ignore such a
    # release by design, and so must this job: it has no base to build a delta
    # against, which is a reason to ship the full file, not a reason to stop
    # shipping.
    Write-Host "The published latest release cannot serve as a delta base ($($_.Exception.Message)); a full-only release will be created."
    return [pscustomobject]$outcome
}

$releaseNumber = if ($previous.commitSha -eq $currentCommit) {
    $previous.releaseNumber
} else {
    $CommitNumber
}
# Updates are offered by number, so it must never walk backwards - which is
# what a rewritten history would do.
if ($releaseNumber -le $previous.releaseNumber -and $previous.commitSha -ne $currentCommit -and
    !(Test-CompressedHistoryRelease $releaseNumber $previous)) {
    throw ("The commit count $CommitNumber is not above the published release " +
           "$($previous.releaseNumber); was the history rewritten?")
}

$bases = [System.Collections.Generic.List[object]]::new()
if ($previous.releaseNumber -lt $releaseNumber) { $bases.Add($previous) }
$releaseRows = Invoke-Gh release list --limit 100 --json tagName | ConvertFrom-Json
$archiveTags = $releaseRows.tagName |
    Where-Object { $_ -match '^update-base-([1-9][0-9]*)$' } |
    Sort-Object { [int]($_ -replace '^update-base-', '') } -Descending
foreach ($tag in $archiveTags) {
    if ($bases.Count -ge 2) { break }
    $number = [int]($tag -replace '^update-base-', '')
    if ($number -ge [Math]::Min($previous.releaseNumber, $releaseNumber)) { continue }
    $archive = Get-ValidatedBase $tag (Join-Path $WorkDirectory "minecraft-$tag") $Repository
    if (-not ($bases | Where-Object { $_.sha256 -eq $archive.sha256 })) {
        $bases.Add($archive)
    }
}
ConvertTo-Json -InputObject @($bases) -Depth 4 |
    Set-Content -LiteralPath $BasesFile -Encoding UTF8

$outcome.HasPrevious = $true
$outcome.ReleaseNumber = $releaseNumber
$outcome.PreviousExe = $previous.executablePath
$outcome.PreviousManifest = $previous.manifestPath
$outcome.PreviousCommit = $previous.commitSha
$outcome.PreviousSha256 = $previous.sha256
$outcome.PreviousReleaseNumber = $previous.releaseNumber
return [pscustomobject]$outcome
