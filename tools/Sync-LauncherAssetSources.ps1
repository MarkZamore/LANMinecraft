#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ComponentRepository)

$ErrorActionPreference = 'Stop'
$launcher = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$component = (Resolve-Path -LiteralPath $ComponentRepository).Path
$remote = (& git -C $component remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remote -cnotmatch '^(https://github\.com/|git@github\.com:)MarkZamore/LANMinecraft-Components(?:\.git)?$') {
    throw 'The destination is not the canonical component repository.'
}
. (Join-Path $component 'tools/DesktopPacket.ps1')
$destination = Join-Path $component 'Program/Assets/LauncherDesktop'
$seedRoot = Join-Path $launcher 'Program/LibrarySeed'
$index = Get-Content -LiteralPath (Join-Path $seedRoot 'catalog.json') -Raw | ConvertFrom-Json
$sourceFiles = [ordered]@{}
foreach ($icon in @('back','chevron','requirements-dot','trash')) {
    $sourceFiles.Add("Icons/$icon.svg", (Join-Path $launcher "Program/Assets/Icons/$icon.svg"))
}
foreach ($entry in $index.packs) {
    $id = if ($entry.PSObject.Properties['legacyId'] -and $entry.legacyId) { $entry.legacyId } else { $entry.id }
    if ($id -cnotmatch '^[a-z0-9][a-z0-9_-]{0,79}$') { throw 'Unexpected library snapshot identity.' }
    $sourceFiles.Add("LibrarySeed/$id.zip", (Join-Path $seedRoot "$id.zip"))
}
foreach ($directory in @('Icons','LibrarySeed')) {
    $target = Join-Path $destination $directory
    Assert-DesktopOrdinaryPath $target
    [void][IO.Directory]::CreateDirectory($target)
    foreach ($path in Get-ChildItem -LiteralPath $target -Force) {
        if ($path.PSIsContainer -or !$sourceFiles.Contains("$directory/$($path.Name)")) {
            throw "Review stale component sources before syncing: $($path.FullName)"
        }
    }
}
$keys = [string[]]@($sourceFiles.Keys); [Array]::Sort($keys,[StringComparer]::Ordinal)
$inventory = foreach ($key in $keys) {
    $source = $sourceFiles[$key]
    Assert-DesktopOrdinaryPath $source
    $info = Get-Item -LiteralPath $source
    if ($info.PSIsContainer) { throw "Expected an ordinary source file: $source" }
    $sha256 = Get-DesktopSha256 $source
    $target = Join-Path $destination $key
    if (!(Test-Path -LiteralPath $target) -or (Get-DesktopSha256 $target) -cne $sha256) { Copy-DesktopFileAtomic $source $target }
    if ((Get-DesktopSha256 $target) -cne $sha256) { throw "Launcher asset changed while copying: $key" }
    [ordered]@{ path=$key; sizeBytes=$info.Length; sha256=$sha256 }
}
$manifest = [ordered]@{ schemaVersion=1; files=@($inventory) }
$manifestPath = Join-Path $component 'data/launcher-assets-sources.json'
Write-DesktopBytesAtomic $manifestPath ([Text.UTF8Encoding]::new($false).GetBytes(($manifest|ConvertTo-Json -Depth 5)+"`n"))
Write-Host "Recorded $($inventory.Count) exact SVG and preview inputs in $manifestPath."
