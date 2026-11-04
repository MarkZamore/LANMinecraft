#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$ExpectedRelease,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$RunId,
    [ValidateRange(1,1000)][int]$RunAttempt = 1,
    [string]$CandidateDirectory,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot '.github\scripts\GhRetry.ps1')
$publisher = Join-Path $repositoryRoot '.github\scripts\Publish-LatestRelease.ps1'
$commit = $ExpectedCommit.ToLowerInvariant()
function Assert-PlainAncestors([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate staging escaped the repository.' }
    $cursor = $repositoryRoot
    $parts = @('') + @([IO.Path]::GetRelativePath($repositoryRoot, $full).Split([IO.Path]::DirectorySeparatorChar))
    foreach ($part in $parts) {
        if ($part -and $part -ne '.') { $cursor = Join-Path $cursor $part }
        if (-not [IO.Directory]::Exists($cursor)) { break }
        if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Candidate staging has a reparse ancestor.' }
    }
}
$runRoot = Join-Path $repositoryRoot ('artifacts\agent-runs\verify-candidate-' + $RunId + '-' + [Guid]::NewGuid().ToString('N'))
Assert-PlainAncestors $runRoot
[void][IO.Directory]::CreateDirectory($runRoot)
[IO.File]::WriteAllText((Join-Path $runRoot 'RUN.json'), (@{
    owner = 'Publish-VerifiedCandidate.ps1'; task = 'Verify exact GitHub Actions candidate before publication'
    runId = $RunId; repository = $Repository; commitSha = $commit
    createdAtUtc = [DateTime]::UtcNow.ToString('O'); status = 'active'; expectedMaximumBytes = 4GB
    deleteByUtc = [DateTime]::UtcNow.AddDays(1).ToString('O')
    necessaryResults = @('verification.json','local-antivirus/*/scan.json','RUN.json')
} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$proof = [ordered]@{ Repository = $Repository; RunId = $RunId; RunAttempt = $RunAttempt; Commit = $commit; Release = $ExpectedRelease; Result = 'Unknown'; Published = $false }

function Invoke-CandidateGh {
    $result = Invoke-Gh @args
    if ($LASTEXITCODE -ne 0) { throw 'GitHub candidate provenance could not be verified.' }
    return $result
}

function Assert-OrdinaryTree([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Root)
    while ($pending.Count) {
        $current = $pending.Pop()
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Candidate tree contains a reparse point.' }
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Candidate tree contains a reparse point.' }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
        }
    }
}

function Remove-OwnedTree([string]$Path, [string]$OwnerRoot = $runRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    $runsBoundary = (Join-Path $repositoryRoot 'artifacts\agent-runs') + [IO.Path]::DirectorySeparatorChar
    if (-not $OwnerRoot.StartsWith($runsBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        -not $full.StartsWith($OwnerRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped this owned candidate run.' }
    Assert-PlainAncestors $full
    if ([IO.Directory]::Exists($full)) { Assert-OrdinaryTree $full; [IO.Directory]::Delete($full, $true) }
}

try {
    # Supersede only this tool's completed staging for the same exact task.
    # Never remove another active verifier or an unclassified artifacts tree.
    foreach ($previous in [IO.Directory]::EnumerateDirectories((Join-Path $repositoryRoot 'artifacts\agent-runs'), "verify-candidate-$RunId-*")) {
        if ($previous.Equals($runRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
        Assert-PlainAncestors $previous
        $recordPath = Join-Path $previous 'RUN.json'
        if (-not [IO.File]::Exists($recordPath)) { continue }
        if (([IO.File]::GetAttributes($recordPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Previous ownership record is a reparse point.' }
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        if ($record.owner -ne 'Publish-VerifiedCandidate.ps1' -or $record.commitSha -ne $commit -or $record.repository -ne $Repository -or [long]$record.runId -ne $RunId) { continue }
        if ($record.status -eq 'active') { throw 'Another verifier for this candidate is active; do not create a second full staging copy.' }
        Remove-OwnedTree (Join-Path $previous 'candidate') $previous
        $oldArchive = Join-Path $previous 'candidate.zip'
        if ([IO.File]::Exists($oldArchive)) { [IO.File]::Delete($oldArchive) }
        # Keep the old compact proof; this run records the superseding cleanup.
        $proof.SupersededRun = $previous
    }
    $run = Invoke-CandidateGh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
    $workflow = Invoke-CandidateGh api "repos/$Repository/actions/workflows/release.yml" | ConvertFrom-Json
    if ($run.head_sha -ne $commit -or $run.repository.full_name -ne $Repository -or $run.head_repository.full_name -ne $Repository -or
        $run.event -ne 'push' -or $run.head_branch -ne 'main' -or $run.workflow_id -ne $workflow.id -or
        $run.status -ne 'completed' -or [int]$run.run_attempt -ne $RunAttempt) { throw 'GitHub run is not the approved completed main release workflow/attempt.' }
    $jobs = Invoke-CandidateGh api "repos/$Repository/actions/runs/$RunId/jobs?filter=latest&per_page=100" | ConvertFrom-Json
    $releaseJobs = @($jobs.jobs | Where-Object name -eq 'release')
    if ($releaseJobs.Count -ne 1) { throw 'The candidate has no unambiguous release job.' }
    foreach ($name in @('Test and publish','Create delta and update manifest','Verify release contents','Prepare exact candidate','Upload exact candidate')) {
        $steps = @($releaseJobs[0].steps | Where-Object name -eq $name)
        if ($steps.Count -ne 1 -or $steps[0].conclusion -ne 'success') { throw ('Required CI candidate step did not succeed: ' + $name) }
    }
    $artifactName = "portable-host-candidate-$RunId-$RunAttempt"
    $artifacts = Invoke-CandidateGh api "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100" | ConvertFrom-Json
    $matches = @($artifacts.artifacts | Where-Object name -eq $artifactName)
    if ($matches.Count -ne 1 -or $matches[0].expired -or $matches[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$' -or
        [long]$matches[0].size_in_bytes -le 0 -or [long]$matches[0].size_in_bytes -gt 2GB) { throw 'An exact bounded immutable candidate artifact with a GitHub SHA256 digest is required.' }
    $artifact = $matches[0]
    $proof.ArtifactId = $artifact.id; $proof.ArtifactDigest = $artifact.digest
    $archive = Join-Path $runRoot 'candidate.zip'
    $authToken = if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { [string](Invoke-CandidateGh auth token --hostname github.com) }
    try {
        # Authorization is sent only to GitHub's fixed API endpoint; default
        # redirect behavior strips it from the signed storage redirect.
        Invoke-WebRequest -Uri "https://api.github.com/repos/$Repository/actions/artifacts/$($artifact.id)/zip" `
            -Headers @{ Authorization = 'Bearer ' + $authToken; Accept = 'application/vnd.github+json' } `
            -OutFile $archive -TimeoutSec 180
    }
    finally { $authToken = $null }
    if (([IO.FileInfo]::new($archive)).Length -gt 2GB -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $artifact.digest.Substring(7)) { throw 'Downloaded artifact ZIP does not match GitHub authoritative digest/budget.' }
    $extracted = Join-Path $runRoot 'candidate'; [void][IO.Directory]::CreateDirectory($extracted)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $unpackedBytes = [long]0
    try {
        if ($zip.Entries.Count -gt 16) { throw 'Candidate archive contains too many entries.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name -in @('assets/','antivirus/')) { continue }
            if ($name -cnotmatch '^(?:candidate\.json|antivirus/scan\.json|assets/(?:LANMinecraft\.exe|update\.json|LANMinecraft(?:\.from-[1-9][0-9]*)?\.bsdiff))$' -or
                -not $paths.Add($name) -or $entry.Length -lt 0 -or $entry.Length -gt 512MB -or
                $unpackedBytes -gt 2GB - $entry.Length) { throw 'Candidate ZIP contains an unsafe, duplicate or oversized entry.' }
            $unpackedBytes += $entry.Length
            $target = Join-Path $extracted $name.Replace('/', [IO.Path]::DirectorySeparatorChar)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $input = $entry.Open(); $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try {
                $buffer = [byte[]]::new(65536); $copied = [long]0
                while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($count -gt $entry.Length - $copied) { throw 'Candidate ZIP exceeded its declared extraction budget.' }
                    $output.Write($buffer, 0, $count); $copied += $count
                }
                if ($copied -ne $entry.Length) { throw 'Candidate ZIP length changed during extraction.' }
            }
            finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    foreach ($required in @('candidate.json','antivirus/scan.json','assets/LANMinecraft.exe','assets/update.json')) {
        if (-not $paths.Contains($required)) { throw ('Candidate archive is missing ' + $required) }
    }
    Assert-OrdinaryTree $extracted
    if ($CandidateDirectory) {
        $provided = [IO.Path]::GetFullPath($CandidateDirectory, (Get-Location).ProviderPath)
        Assert-OrdinaryTree $provided
        $providedFiles = @([IO.Directory]::EnumerateFiles($provided, '*', [IO.SearchOption]::AllDirectories))
        if ($providedFiles.Count -ne $paths.Count) { throw 'Provided candidate directory differs from the official CI artifact.' }
        foreach ($path in $paths) {
            $relative = $path.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ((Get-FileHash -LiteralPath (Join-Path $provided $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $extracted $relative)).Hash) { throw 'Provided candidate bytes differ from the official CI artifact.' }
        }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $extracted 'candidate.json') -Raw | ConvertFrom-Json
    $ciScanPath = Join-Path $extracted 'antivirus\scan.json'
    $ciScan = Get-Content -LiteralPath $ciScanPath -Raw | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.repository -ne $Repository -or $metadata.commitSha -ne $commit -or
        [int]$metadata.releaseNumber -ne $ExpectedRelease -or [long]$metadata.runId -ne $RunId -or [int]$metadata.runAttempt -ne $RunAttempt -or
        $metadata.ciScanSha256 -ne (Get-FileHash -LiteralPath $ciScanPath).Hash) { throw 'Candidate metadata does not match its approved GitHub provenance.' }
    if ($ciScan.Result -eq 'Detected' -or @($ciScan.MatchingDetections).Count -gt 0 -or
        ($null -ne $ciScan.NativeExitCode -and [int]$ciScan.NativeExitCode -ne 0) -or $ciScan.Result -notin @('Unknown','Passed')) {
        throw 'CI reported a detection or an ambiguous nonzero scan result; this artifact cannot be locally promoted.'
    }
    $assetRoot = Join-Path $extracted 'assets'
    $checked = & $publisher -AssetDirectory $assetRoot -Repository $Repository -CommitSha $commit -ReleaseNumber $ExpectedRelease
    $declaredAssets = @($metadata.assets)
    if ($declaredAssets.Count -ne $checked.AssetPaths.Count) { throw 'Candidate asset inventory differs from the update manifest.' }
    foreach ($asset in $checked.AssetPaths) {
        $name = [IO.Path]::GetFileName($asset)
        $declaration = @($declaredAssets | Where-Object name -ceq $name)
        if ($declaration.Count -ne 1 -or [long]$declaration[0].sizeBytes -ne ([IO.FileInfo]::new($asset)).Length -or
            $declaration[0].sha256 -ne (Get-FileHash -LiteralPath $asset).Hash) { throw 'Candidate metadata file hash/size does not match its exact CI bytes.' }
    }
    $avRoot = Join-Path $runRoot 'local-antivirus'
    & (Join-Path $PSScriptRoot 'Test-PortableHostAntivirus.ps1') -ExecutablePath $checked.Executable -ArtifactsDirectory $avRoot
    $avProofs = @([IO.Directory]::EnumerateFiles($avRoot, 'scan.json', [IO.SearchOption]::AllDirectories))
    if ($avProofs.Count -ne 1) { throw 'Local antivirus proof is missing or ambiguous.' }
    $avProof = Get-Content -LiteralPath $avProofs[0] -Raw | ConvertFrom-Json
    if ($avProof.Result -ne 'Passed' -or $avProof.After.Sha256 -ne $checked.Manifest.sha256) { throw 'Exact downloaded CI bytes did not pass local antivirus verification.' }
    $proof.Result = 'Passed'; $proof.ExecutableSha256 = $avProof.After.Sha256; $proof.AntivirusEvidence = $avProofs[0]
    if ($Publish) {
        & $publisher -AssetDirectory $assetRoot -Repository $Repository -CommitSha $commit -ReleaseNumber $ExpectedRelease -AntivirusEvidencePath $avProofs[0] -CiAntivirusEvidencePath $ciScanPath -Publish
        $proof.Published = $true
    }
    else { Write-Host 'Candidate verified; no GitHub release was changed. Explicit -Publish is required for publication.' }
}
catch { $proof.Result = 'Failed'; $proof.Reason = $_.Exception.Message; throw }
finally {
    $proof.FinishedAtUtc = [DateTime]::UtcNow.ToString('O')
    if ($archive -and [IO.File]::Exists($archive)) { [IO.File]::Delete($archive) }
    if ($extracted -and ($proof.Published -or $proof.Result -ne 'Passed')) { Remove-OwnedTree $extracted }
    $runStatus = if ($proof.Published) { 'published-cleaned' } elseif ($proof.Result -eq 'Passed') { 'verified-awaiting-publication' } else { 'failed-cleaned' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'RUN.json'), (@{
        owner = 'Publish-VerifiedCandidate.ps1'; task = 'Verify exact GitHub Actions candidate before publication'
        runId = $RunId; repository = $Repository; commitSha = $commit; createdAtUtc = $proof.FinishedAtUtc
        status = $runStatus; expectedMaximumBytes = $(if ($runStatus -eq 'verified-awaiting-publication') { 2GB } else { 50MB })
        deleteByUtc = [DateTime]::UtcNow.AddDays(1).ToString('O')
        necessaryResults = @('verification.json','local-antivirus/*/scan.json','RUN.json')
        retainedCandidateReason = $(if ($runStatus -eq 'verified-awaiting-publication') { 'Exact verified bytes retained for explicit publication review; remove or publish within24hours.' } else { '' })
    } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $runRoot 'verification.json'), ($proof | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
