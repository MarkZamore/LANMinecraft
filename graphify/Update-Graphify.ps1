param([switch]$Check)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$scope = @('Program', 'Program.Tests', 'tools', '.github')
$output = Join-Path $repo 'graphify-out'
$graphPath = Join-Path $output 'graph.json'
$metadataPath = Join-Path $output 'source.json'

function Invoke-Git([string[]]$GitArgs) {
    & git -C $repo @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed with exit code $LASTEXITCODE." }
}

function Read-Inventory([string]$Revision) {
    $records = (Invoke-Git (@('ls-tree', '-rz', '--full-tree', $Revision, '--') + $scope)) -join "`n"
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $codeEntries = [Collections.Generic.List[string]]::new()
    foreach ($record in $records.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $entry = [regex]::Match($record, '^100(?:644|755) blob [a-f0-9]+\t([^\r\n]+)$')
        if (!$entry.Success) {
            throw 'The source snapshot contains a link, submodule, or unsupported Git path.'
        }
        $path = $entry.Groups[1].Value
        [void]$paths.Add($path)
        if ($path -cne 'Program/Changelog.md') { $codeEntries.Add($record) }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($codeEntries -join "`n")
    [pscustomobject]@{ Paths = $paths; Fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
}

function Assert-OrdinaryPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $Path" }
}

if ($Check) {
    if (!(Test-Path -LiteralPath $graphPath -PathType Leaf) -or !(Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw 'The Graphify map is missing.'
    }
    $saved = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $current = Read-Inventory (Invoke-Git @('rev-parse', 'HEAD'))
    & git -C $repo diff --quiet HEAD -- @scope ':(exclude)Program/Changelog.md'
    if ($LASTEXITCODE -ne 0 -or $saved.scopeFingerprint -cne $current.Fingerprint -or
        $saved.graphSha256 -ine (Get-FileHash -LiteralPath $graphPath -Algorithm SHA256).Hash) {
        throw 'The Graphify map does not match this checkout.'
    }
    "Graphify $($saved.graphifyVersion) map matches the indexed sources."
    return
}

function Confirm-SourceReferences($Value) {
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Contains('source_file') -and $Value['source_file']) {
            $path = $Value['source_file']
            if ($path -is [string] -and $inventory.Paths.Contains($path)) { [void]$indexedFiles.Add($path) }
            else { [void]$unverifiedPaths.Add([string]$path) }
        }
        foreach ($child in $Value.Values) { Confirm-SourceReferences $child }
    }
    elseif ($Value -is [array]) {
        foreach ($child in $Value) { Confirm-SourceReferences $child }
    }
}

$cli = (Get-Command graphify -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$versionOutput = & $cli --version
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the Graphify CLI version.' }
$versionMatch = [regex]::Match($versionOutput, '^graphify (\S+)$')
if (!$versionMatch.Success) { throw 'Could not identify the Graphify CLI version.' }
$version = $versionMatch.Groups[1].Value
$revision = Invoke-Git @('rev-parse', '--verify', 'origin/main^{commit}')
$inventory = Read-Inventory $revision
$trees = [ordered]@{}
foreach ($directory in $scope) { $trees[$directory] = Invoke-Git @('rev-parse', "${revision}:$directory") }

& python (Join-Path $repo 'tools/workspace_storage.py') --check | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Workspace storage check failed.' }
$artifacts = Join-Path $repo 'artifacts'
$runs = Join-Path $artifacts 'agent-runs'
$stage = Join-Path $runs ('graphify-' + [Guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $stage 'sources'
foreach ($path in @($repo, $artifacts, $runs, $output, $graphPath, $metadataPath)) { Assert-OrdinaryPath $path }
$oldOutput = $env:GRAPHIFY_OUT
$oldCeiling = $env:GIT_CEILING_DIRECTORIES
try {
    [void][IO.Directory]::CreateDirectory($snapshot)
    @{
        owner = 'graphify/Update-Graphify.ps1'
        purpose = 'Build an offline map of the published Git snapshot'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        expectedBytes = 134217728
        deleteAfterUtc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O')
        requiredResults = @('graphify-out/graph.json', 'graphify-out/source.json')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'RUN.json') -Encoding utf8

    $archive = Join-Path $stage 'sources.tar'
    Invoke-Git (@('archive', '--format=tar', "--output=$archive", $revision, '--') + $scope)
    & tar -xf $archive -C $snapshot
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the Git source snapshot.' }

    $env:GRAPHIFY_OUT = $null
    $env:GIT_CEILING_DIRECTORIES = $stage
    & $cli extract $snapshot --code-only --no-gitignore --max-workers 2 --out $stage
    if ($LASTEXITCODE -ne 0) { throw 'Graphify extraction failed.' }

    $candidate = Join-Path $stage 'graphify-out/graph.json'
    $raw = [IO.File]::ReadAllText($candidate)
    $graph = ConvertFrom-Json -InputObject $raw -AsHashtable -Depth 100
    if ($graph['graph']['schema_version'] -ne 1 -or $graph['graph']['graphify_version'] -ne $version -or !$graph['nodes'] -or !$graph['links']) {
        throw 'Graphify produced an unsupported or empty graph.'
    }
    foreach ($path in @($repo, $stage)) {
        foreach ($spelling in @($path.Replace('\', '/'), (($path | ConvertTo-Json -Compress).Trim('"')))) {
            if ($raw.Contains($spelling, [StringComparison]::OrdinalIgnoreCase)) { throw 'The graph contains an absolute build path.' }
        }
    }
    # Point virtual solution folders and unevaluated MSBuild references to their declarations.
    $folderOwners = @{}
    $referenceOwners = @{}
    foreach ($link in $graph['links']) {
        if ($link['relation'] -eq 'contains' -and $link['source_file'] -like '*.sln') {
            $folderOwners[$link['target']] = $link['source_file']
        }
        if ($link['relation'] -eq 'imports' -and $link['source_file'] -match '\.(cs|fs|vb)proj$') {
            $referenceOwners[$link['target']] = $link['source_file']
        }
    }
    $normalized = $false
    foreach ($node in $graph['nodes']) {
        $path = $node['source_file']
        if (!$path -or $node['source_location'] -or $inventory.Paths.Contains($path)) { continue }
        if ($path -ceq $node['label'] -and $folderOwners.ContainsKey($node['id'])) {
            $node['source_file'] = $folderOwners[$node['id']]
            $normalized = $true
        }
        elseif ($node['label'] -cmatch '^\$\([A-Za-z_][A-Za-z0-9_.-]*\)$' -and $path.EndsWith('/' + $node['label']) -and
            $referenceOwners.ContainsKey($node['id'])) {
            $node['source_file'] = $referenceOwners[$node['id']]
            $normalized = $true
        }
    }
    $indexedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unverifiedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Confirm-SourceReferences $graph
    if ($unverifiedPaths.Count) { throw "Source paths outside the Git snapshot: $($unverifiedPaths -join ', ')" }
    if ($indexedFiles.Count -eq 0) { throw 'The graph has no verified source paths.' }
    $nodeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $graph['nodes']) {
        if (!$node['id'] -or !$nodeIds.Add($node['id'])) { throw 'The graph contains an empty or duplicate node ID.' }
    }
    foreach ($link in $graph['links']) {
        if (!$nodeIds.Contains($link['source']) -or !$nodeIds.Contains($link['target'])) { throw 'The graph contains an unresolved link.' }
    }
    if ($normalized) {
        $raw = ($graph | ConvertTo-Json -Depth 100) + "`n"
    }
    [IO.File]::WriteAllText($candidate, $raw.Replace("`r`n", "`n"))

    $metadata = [ordered]@{
        sourceCommit = $revision
        graphifyVersion = $version
        scopeTrees = $trees
        scopeFingerprint = $inventory.Fingerprint
        trackedFiles = $inventory.Paths.Count
        indexedFiles = $indexedFiles.Count
        nodes = $graph['nodes'].Count
        links = $graph['links'].Count
        graphSha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $candidateMetadata = Join-Path $stage 'source.json'
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $candidateMetadata -Encoding utf8
    [void][IO.Directory]::CreateDirectory($output)
    foreach ($path in @($output, $graphPath, $metadataPath)) { Assert-OrdinaryPath $path }
    [IO.File]::Move($candidate, $graphPath, $true)
    [IO.File]::Move($candidateMetadata, $metadataPath, $true)
    $metadata | ConvertTo-Json -Depth 4
}
finally {
    $env:GRAPHIFY_OUT = $oldOutput
    $env:GIT_CEILING_DIRECTORIES = $oldCeiling
    if (Test-Path -LiteralPath $stage) {
        $resolved = (Resolve-Path -LiteralPath $stage).Path
        if ($resolved -ne $stage -or !(Split-Path -Parent $resolved).Equals($runs, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Graphify staging path no longer matches the task directory.'
        }
        foreach ($path in @($repo, $artifacts, $runs, $resolved)) { Assert-OrdinaryPath $path }
        if (Get-ChildItem -LiteralPath $resolved -Force -Recurse -Attributes ReparsePoint | Select-Object -First 1) {
            throw 'The Graphify staging directory contains a link.'
        }
        [IO.Directory]::Delete($resolved, $true)
    }
}
