<#
.SYNOPSIS
Keeps the executable this release replaces, so the release after next can
still be a small delta for players who skipped one.

.DESCRIPTION
Runs beside the delta computation: this is upload bandwidth while that is
processor, and neither waits on the other. The publish still waits on this -
a base that was never archived is a chain that quietly breaks two releases
later.
#>
param(
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$Repository
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GhRetry.ps1"
# A background job starts wherever PowerShell puts it, so gh is told which
# repository it is talking about instead of being left to find a git remote.
$env:GH_REPO = $Repository

$tag = "update-base-$ReleaseNumber"
if (Invoke-Gh release view $tag 2>$null) {
    Invoke-Gh release delete $tag --cleanup-tag --yes
    if ($LASTEXITCODE -ne 0) { throw "Could not replace archive $tag." }
}
Invoke-Gh release create $tag `
    $ExecutablePath `
    $ManifestPath `
    --prerelease `
    --title "Update base $ReleaseNumber" `
    --notes "Binary base for commit $CommitSha retained for direct delta generation."
if ($LASTEXITCODE -ne 0) { throw "Could not create archive $tag." }
Write-Host "Archived $tag."
