# Downloads a previous release and proves its executable is the one its
# manifest describes, so a delta is only ever built against bytes players
# actually have.
function Get-ValidatedBase([string]$Tag, [string]$Directory, [string]$Repository) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Invoke-Gh release download $Tag `
        --pattern 'update.json' `
        --repo $Repository `
        --dir $Directory `
        --clobber | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Release $Tag could not be downloaded." }

    $manifestPath = Join-Path $Directory 'update.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Release $Tag is incomplete."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $commit = ([string]$manifest.commitSha).ToLowerInvariant()
    $sha = ([string]$manifest.sha256).ToLowerInvariant()
    $size = [long]$manifest.sizeBytes
    $releaseNumber = [int]$manifest.releaseNumber
    if ($commit -notmatch '^[0-9a-f]{40}$' -or
        $sha -notmatch '^[0-9a-f]{64}$' -or
        $size -le 0 -or
        $releaseNumber -lt 1) {
        throw "Release $Tag manifest is invalid."
    }

    Invoke-Gh release download $Tag `
        --pattern 'LANMinecraft.exe' `
        --repo $Repository `
        --dir $Directory `
        --clobber | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Release $Tag executable could not be downloaded." }
    $exe = Join-Path $Directory 'LANMinecraft.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Release $Tag is incomplete."
    }
    $file = Get-Item -LiteralPath $exe
    $actualSha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne $size -or $actualSha -ne $sha) {
        throw "Release $Tag executable does not match its manifest."
    }

    return [pscustomobject]@{
        tag = $Tag
        executablePath = $exe
        manifestPath = $manifestPath
        releaseNumber = $releaseNumber
        commitSha = $commit
        sha256 = $sha
        sizeBytes = $size
    }
}
