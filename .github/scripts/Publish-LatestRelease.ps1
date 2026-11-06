#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssetDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CommitSha,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$ReleaseNumber,
    [string]$AntivirusEvidencePath,
    [string]$CiAntivirusEvidencePath,
    [switch]$AllowUnavailableHostedDefender,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GhRetry.ps1')
. (Join-Path $PSScriptRoot 'HostedDefenderPolicy.ps1')
$assetRoot = [IO.Path]::GetFullPath($AssetDirectory, (Get-Location).ProviderPath)
$commit = $CommitSha.ToLowerInvariant()
$locks = [Collections.Generic.List[IO.FileStream]]::new()

function Get-CheckedFile([string]$Name) {
    if ($Name -notmatch '^(?:LANMinecraft\.exe|update\.json|LANMinecraft(?:\.from-[1-9][0-9]*)?\.bsdiff)$') { throw ('Unsafe release asset: ' + $Name) }
    $path = Join-Path $assetRoot $Name
    $attributes = [IO.File]::GetAttributes($path)
    if (($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0) { throw ('Release asset is not an ordinary file: ' + $Name) }
    $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $locks.Add($stream)
    return $path
}

function Assert-HashSize([string]$Path, $Expected) {
    if ([string]$Expected.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$Expected.sizeBytes -le 0 -or
        ([IO.FileInfo]::new($Path)).Length -ne [long]$Expected.sizeBytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne [string]$Expected.sha256) {
        throw ('Release asset does not match its manifest: ' + [IO.Path]::GetFileName($Path))
    }
}

function Invoke-CheckedGh {
    $result = Invoke-Gh @args
    if ($LASTEXITCODE -ne 0) { throw 'GitHub operation failed; no absence or success is inferred from that failure.' }
    return $result
}

function Read-ScanFinishedAt([string]$Json) {
    # ConvertFrom-Json may materialize DateTime; converting that object back to
    # text loses its ISO timezone and can swap month/day under another culture.
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $text = $document.RootElement.GetProperty('FinishedAtUtc').GetString()
        if ([string]::IsNullOrWhiteSpace($text) -or $text -notmatch '(?:Z|[+-][0-9]{2}:[0-9]{2})$') {
            throw 'Antivirus completion timestamp requires an explicit ISO timezone.'
        }
        return [DateTimeOffset]::Parse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
    }
    finally { $document.Dispose() }
}

function Assert-PublicationHead {
    $head = Invoke-CheckedGh api "repos/$Repository/git/ref/heads/main" | ConvertFrom-Json
    if ($head.object.sha -ne $commit) { throw 'The candidate is no longer the main head; coordinate the next release before publishing.' }
}

function Read-Latest {
    # Listing distinguishes a genuinely absent tag from a failed lookup.
    $tags = @(Invoke-CheckedGh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[].tag_name')
    if ('latest' -notin $tags) { return $null }
    $release = Invoke-CheckedGh api "repos/$Repository/releases/tags/latest" | ConvertFrom-Json
    $pointers = @($release.assets | Where-Object name -eq 'update.json')
    if ($pointers.Count -ne 1) { throw 'The existing latest release has no unambiguous update pointer.' }
    $pointer = Invoke-CheckedGh api "repos/$Repository/releases/assets/$($pointers[0].id)" -H 'Accept: application/octet-stream' | ConvertFrom-Json
    if ($pointer.releaseNumber -gt $ReleaseNumber -or
        ($pointer.releaseNumber -eq $ReleaseNumber -and $pointer.commitSha -ne $commit)) {
        throw 'A newer or conflicting latest release already exists; refusing to replace it.'
    }
    return $release
}

try {
    if (([IO.File]::GetAttributes($assetRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release asset root is a reparse point.' }
    $manifestPath = Get-CheckedFile 'update.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 2 -or $manifest.assetName -cne 'LANMinecraft.exe' -or
        $manifest.commitSha -ne $commit -or [int]$manifest.releaseNumber -ne $ReleaseNumber -or
        [string]$manifest.version -ne [string]$ReleaseNumber) { throw 'Update manifest identity does not match the approved candidate.' }
    $exe = Get-CheckedFile 'LANMinecraft.exe'
    Assert-HashSize $exe $manifest
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($version.FileVersion -ne "1.0.$ReleaseNumber.0" -or $version.ProductVersion -ne "1.0.$ReleaseNumber+$commit") { throw 'Outer EXE version/commit does not match its manifest.' }
    $assetPaths = [Collections.Generic.List[string]]::new()
    $assetPaths.Add($exe)
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$expected.Add('LANMinecraft.exe'); [void]$expected.Add('update.json')
    $patches = @($manifest.deltaPatches)
    if ($patches.Count -gt 2) { throw 'Too many release delta patches.' }
    foreach ($patch in $patches) {
        if ($patch.algorithm -ne 'bsdiff' -or $patch.algorithmVersion -ne 1 -or
            $patch.assetName -notmatch '^LANMinecraft(?:\.from-[1-9][0-9]*)?\.bsdiff$' -or
            $patch.baseCommitSha -notmatch '^[a-fA-F0-9]{40}$' -or $patch.baseSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            [int]$patch.baseReleaseNumber -lt 1 -or [int]$patch.baseReleaseNumber -ge $ReleaseNumber -or
            -not $expected.Add([string]$patch.assetName)) { throw 'Invalid or duplicate delta patch identity.' }
        $path = Get-CheckedFile $patch.assetName
        Assert-HashSize $path $patch
        $assetPaths.Add($path)
    }
    $files = @([IO.Directory]::EnumerateFiles($assetRoot, '*', [IO.SearchOption]::TopDirectoryOnly))
    if ($files.Count -ne $expected.Count -or @($files | Where-Object { -not $expected.Contains([IO.Path]::GetFileName($_)) }).Count) { throw 'Unexpected or missing release files.' }
    if (-not $Publish) {
        return [pscustomobject]@{ Manifest = $manifest; AssetPaths = @($assetPaths) + @($manifestPath); Executable = $exe }
    }
    if (-not $AntivirusEvidencePath) { throw 'Publishing requires a fresh antivirus record for these exact bytes.' }
    if ($CiAntivirusEvidencePath) {
        $ciProof = Get-Content -LiteralPath $CiAntivirusEvidencePath -Raw | ConvertFrom-Json
        if ($ciProof.Result -eq 'Detected' -or @($ciProof.MatchingDetections).Count -gt 0 -or
            ($null -ne $ciProof.NativeExitCode -and [int]$ciProof.NativeExitCode -ne 0)) { throw 'A CI detection/nonzero scan result cannot be overridden by a local publication.' }
    }
    $proofJson = Get-Content -LiteralPath $AntivirusEvidencePath -Raw
    $proof = $proofJson | ConvertFrom-Json
    $finished = Read-ScanFinishedAt $proofJson
    if ($proof.Before.Sha256 -ne $manifest.sha256 -or $proof.After.Sha256 -ne $manifest.sha256 -or
        [long]$proof.Before.SizeBytes -ne [long]$manifest.sizeBytes -or [long]$proof.After.SizeBytes -ne [long]$manifest.sizeBytes -or
        -not ([IO.Path]::GetFullPath([string]$proof.Executable)).Equals($exe, [StringComparison]::OrdinalIgnoreCase) -or
        $finished -lt [DateTimeOffset]::UtcNow.AddHours(-4) -or $finished -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'Antivirus evidence is absent, stale or belongs to different executable bytes.'
    }
    if ($proof.Result -eq 'Passed') {
        if ($proof.NativeExitCode -ne 0 -or @($proof.MatchingDetections).Count -gt 0 -or
            $proof.CompletionMethod -notin @('DefenderOperationalScanId','CalibratedEnglishExactTargetOutput')) {
            throw 'The antivirus record does not prove a completed clean scan.'
        }
        foreach ($status in @($proof.DefenderBefore, $proof.DefenderAfter)) {
            if ($status.AMServiceEnabled -ne $true -or $status.AntivirusEnabled -ne $true -or $status.RealTimeProtectionEnabled -ne $true -or
                $status.AMRunningMode -ne 'Normal' -or $status.DefenderSignaturesOutOfDate -ne $false) { throw 'Antivirus evidence does not show active current protection.' }
        }
        $releaseNotes = "Automated build for commit $commit. Microsoft Defender checked the published executable."
    }
    elseif ($AllowUnavailableHostedDefender) {
        Assert-HostedDefenderUnavailable $proof $Repository $commit
        Write-Warning 'Microsoft Defender was unavailable before scanning. No antivirus verdict is available for this release.'
        $releaseNotes = "Automated build for commit $commit. Microsoft Defender was unavailable on the GitHub-hosted runner, so this executable was not scanned."
    }
    else { throw 'Publication outside the hosted workflow requires a completed clean antivirus scan.' }
    Assert-PublicationHead
    $latest = Read-Latest
    if ($null -eq $latest) {
        $refs = @(Invoke-CheckedGh api "repos/$Repository/git/matching-refs/tags/latest" | ConvertFrom-Json)
        if (@($refs | Where-Object ref -eq 'refs/tags/latest').Count) { [void](Invoke-CheckedGh api --method DELETE "repos/$Repository/git/refs/tags/latest") }
        [void](Invoke-CheckedGh release create latest @assetPaths $manifestPath --repo $Repository --target $commit --latest --title 'Latest LANMinecraft.exe' --notes $releaseNotes)
    }
    else {
        [void](Invoke-CheckedGh release upload latest @assetPaths --repo $Repository --clobber)
        Assert-PublicationHead
        [void](Read-Latest)
        # The pointer changes last, after every checked payload was uploaded.
        [void](Invoke-CheckedGh release upload latest $manifestPath --repo $Repository --clobber)
        $current = Invoke-CheckedGh release view latest --repo $Repository --json assets | ConvertFrom-Json
        foreach ($asset in @($current.assets | Where-Object { -not $expected.Contains($_.name) })) {
            [void](Invoke-CheckedGh release delete-asset latest $asset.name --repo $Repository --yes)
        }
        [void](Invoke-CheckedGh release edit latest --repo $Repository --target $commit --title 'Latest LANMinecraft.exe' --notes $releaseNotes)
    }
    $archiveTags = (Invoke-CheckedGh release list --repo $Repository --limit 100 --json tagName | ConvertFrom-Json).tagName |
        Where-Object { $_ -match '^update-base-([1-9][0-9]*)$' } | Sort-Object { [int]($_ -replace '^update-base-', '') } -Descending
    foreach ($tag in @($archiveTags | Select-Object -Skip 2)) { [void](Invoke-CheckedGh release delete $tag --repo $Repository --cleanup-tag --yes) }
    Write-Host ("Published verified release $ReleaseNumber ($commit).")
}
finally { foreach ($held in $locks) { $held.Dispose() } }
