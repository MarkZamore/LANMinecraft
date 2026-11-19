#requires -Version 5.1
[CmdletBinding()]
param([string]$LockPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $PSScriptRoot 'components-test-lock.json'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Invoke-CheckedGit([string[]]$GitArguments) {
    & git @GitArguments
    if ($LASTEXITCODE -ne 0) { throw 'The pinned component sources could not be prepared.' }
}

function Confirm-File([string]$Path, [long]$Size, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path
    return -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        ($Size -le 0 -or $item.Length -eq $Size) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256
}

function Assert-OrdinaryPath([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "The component test path is redirected: $current"
        }
    }
}

function Get-ComponentCheckoutHead([string]$Source) {
    Assert-OrdinaryPath (Join-Path $Source '.git')
    if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) {
        throw "The component test checkout is missing: $Source"
    }
    $topLevel = & git -C $Source rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or
        -not [string]::Equals([IO.Path]::GetFullPath($topLevel), $Source, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Not the root of a component test checkout: $Source"
    }
    $head = & git -C $Source rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw "Cannot identify the component test checkout: $Source" }
    return $head
}

function Confirm-ComponentCheckout([string]$Source, [string]$Commit) {
    if ((Get-ComponentCheckoutHead $Source) -cne $Commit) {
        throw "The component test checkout must be at $($Commit): $Source"
    }
    $changes = @(& git --no-optional-locks -C $Source status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
        throw "The component test checkout has unfinished changes: $Source"
    }
}

function Get-ComponentSourceCache {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'Local application data is unavailable.' }
    return [IO.Path]::GetFullPath((Join-Path $localAppData 'LANMinecraft\DevCache\ComponentSources'))
}

function Initialize-PinnedComponentCheckout([string]$Source, [string]$Commit, [string]$Origin) {
    $parent = Split-Path -Parent $Source
    Assert-OrdinaryPath $parent
    [void][IO.Directory]::CreateDirectory($parent)
    $leasePath = $Source + '.lock'
    Assert-OrdinaryPath $leasePath
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while ($true) {
        try {
            $lease = [IO.File]::Open($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            break
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    try {
        Assert-OrdinaryPath $Source
        if (Test-Path -LiteralPath $Source) {
            Confirm-ComponentCheckout $Source $Commit
            return
        }

        # Readers only see a completed checkout. A failed download can resume the same stage.
        $stage = $Source + '.preparing'
        Assert-OrdinaryPath $stage
        if (-not (Test-Path -LiteralPath $stage)) {
            Invoke-CheckedGit @('clone', '--filter=blob:none', '--no-checkout', $Origin, $stage)
        }
        $stageHead = Get-ComponentCheckoutHead $stage
        $stageOrigin = & git -C $stage remote get-url origin
        if ($LASTEXITCODE -ne 0 -or $stageOrigin -cne $Origin) { throw "Unexpected component test staging origin: $stage" }
        $indexPaths = @(& git -C $stage ls-files)
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect the component test staging index: $stage" }
        if ($indexPaths.Count -eq 0 -and -not (Get-ChildItem -LiteralPath $stage -Force | Where-Object Name -ne '.git')) {
            if ($stageHead -cne $Commit) {
                Invoke-CheckedGit @('-C', $stage, 'fetch', '--no-tags', 'origin', $Commit)
            }
            Invoke-CheckedGit @('-C', $stage, 'checkout', '--detach', $Commit)
        }
        Confirm-ComponentCheckout $stage $Commit
        [IO.Directory]::Move($stage, $Source)
    }
    finally {
        $lease.Dispose()
    }
}

$candidateRoot = [Environment]::GetEnvironmentVariable('LANMINECRAFT_COMPONENT_TEST_CANDIDATE', 'Process')
if ($candidateRoot) {
    $candidateRoot = [IO.Path]::GetFullPath($candidateRoot)
    $candidateCatalog = Join-Path $candidateRoot 'catalog.json'
    if (-not (Test-Path -LiteralPath $candidateCatalog -PathType Leaf)) { throw 'Candidate component catalog is missing.' }
    $catalog = Get-Content -LiteralPath $candidateCatalog -Raw | ConvertFrom-Json
    if ($catalog.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $catalog.schemaVersion -ne 1 -or
        $catalog.componentApiVersion -ne 1 -or [int]$catalog.minimumLauncherRelease -lt 1) {
        throw 'The candidate catalog does not describe a component source and supported contract.'
    }
    $sourceRoot = [Environment]::GetEnvironmentVariable('LANMINECRAFT_COMPONENT_TEST_CANDIDATE_SOURCES', 'Process')
    if ([string]::IsNullOrWhiteSpace($sourceRoot)) {
        # Older component CI pre-seeds this checkout. It must never be created or changed here.
        $sourceRoot = Join-Path $repositoryRoot 'LocalSources/LANMinecraft-Components-Tests'
    }
    $sourceRoot = [IO.Path]::GetFullPath($sourceRoot)
    Confirm-ComponentCheckout $sourceRoot $catalog.sourceCommit
    $expected = @{
        'identity-adapter' = 'portable-identity-adapter.jar'
        'notice-overlay' = 'launcher-notice-agent.jar'
        'native-interop' = 'launcher-native-interop-agent.jar'
        'legacy-forge-installer' = 'LegacyForgeClientInstaller.class'
    }
    if (@($catalog.artifacts).Count -ne $expected.Count -or
        @($catalog.artifacts.id | Sort-Object -Unique).Count -ne $expected.Count) { throw 'The candidate artifact set is incomplete or duplicated.' }
    foreach ($artifact in $catalog.artifacts) {
        if (-not $expected.ContainsKey($artifact.id) -or $artifact.fileName -cne $expected[$artifact.id] -or
            $artifact.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$artifact.sizeBytes -le 0) {
            throw 'Invalid candidate component identity.'
        }
        $uri = [Uri]$artifact.url
        $parts = $uri.AbsolutePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
        if ($uri.Scheme -cne 'https' -or $uri.Host -cne 'github.com' -or -not $uri.IsDefaultPort -or
            $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $parts.Count -ne 6 -or
            $parts[0] -cne 'MarkZamore' -or $parts[1] -cne 'LANMinecraft-Components' -or
            $parts[2] -cne 'releases' -or $parts[3] -cne 'download' -or $parts[4] -ceq 'latest' -or
            [Uri]::UnescapeDataString($parts[5]) -cne $artifact.fileName) {
            throw 'Candidate component requires an immutable approved release address.'
        }
        if (-not (Confirm-File (Join-Path $candidateRoot $artifact.fileName) $artifact.sizeBytes $artifact.sha256)) {
            throw ('Candidate component bytes differ from their catalog: ' + $artifact.fileName)
        }
    }
    $env:LANMINECRAFT_COMPONENT_TEST_ASSETS = $candidateRoot
    $env:LANMINECRAFT_COMPONENT_TEST_SOURCES = $sourceRoot
    Write-Host ('Using checked candidate component dependencies from ' + $catalog.sourceCommit)
    return
}

$pin = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.repository -cne 'MarkZamore/LANMinecraft-Components' -or
    $pin.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $pin.catalogSha256 -cnotmatch '^[a-f0-9]{64}$' -or
    $pin.releaseTag -notmatch '^components-[A-Za-z0-9._-]+$') {
    throw 'The external component test lock is invalid.'
}

$sourceRoot = Join-Path (Get-ComponentSourceCache) $pin.sourceCommit
Initialize-PinnedComponentCheckout $sourceRoot $pin.sourceCommit ('https://github.com/' + $pin.repository + '.git')

$assetRoot = Join-Path $repositoryRoot ('artifacts/agent-runs/component-test-dependencies/' + $pin.catalogSha256)
[void][IO.Directory]::CreateDirectory($assetRoot)
@{
    owner = 'Component contract test preparation'
    purpose = 'Pinned external Java artifacts for offline deterministic tests'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    expectedBytes = 4MB
    deleteByUtc = [DateTime]::UtcNow.AddDays(7).ToString('o')
    requiredResults = @('catalog.json', 'published component artifacts')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assetRoot 'RUN.json') -Encoding utf8
$catalogPath = Join-Path $assetRoot 'catalog.json'
if (-not (Confirm-File $catalogPath 0 $pin.catalogSha256)) {
    & gh release download $pin.releaseTag --repo $pin.repository --pattern catalog.json --dir $assetRoot --clobber
    if ($LASTEXITCODE -ne 0 -or -not (Confirm-File $catalogPath 0 $pin.catalogSha256)) {
        throw 'The pinned component test catalog could not be verified.'
    }
}
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($catalog.sourceCommit -cne $pin.sourceCommit) { throw 'Component sources differ from the published catalog.' }
foreach ($artifact in $catalog.artifacts) {
    if ($artifact.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $artifact.sha256 -notmatch '^[a-f0-9]{64}$' -or
        [long]$artifact.sizeBytes -le 0) { throw 'Invalid pinned component artifact.' }
    $target = Join-Path $assetRoot $artifact.fileName
    if (-not (Confirm-File $target $artifact.sizeBytes $artifact.sha256)) {
        & gh release download $pin.releaseTag --repo $pin.repository --pattern $artifact.fileName --dir $assetRoot --clobber
        if ($LASTEXITCODE -ne 0 -or -not (Confirm-File $target $artifact.sizeBytes $artifact.sha256)) {
            throw ('The published test component could not be verified: ' + $artifact.fileName)
        }
    }
}
$env:LANMINECRAFT_COMPONENT_TEST_ASSETS = $assetRoot
$env:LANMINECRAFT_COMPONENT_TEST_SOURCES = $sourceRoot
Write-Host ('Using checked component test dependencies from ' + $pin.releaseTag)
