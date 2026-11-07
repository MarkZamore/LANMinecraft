#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$LauncherAssembly,
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$IndexPath,
    [Parameter(Mandatory)][string]$AssetsRoot,
    [Parameter(Mandatory)][string]$ResourcesRoot,
    [Parameter(Mandatory)][string]$ProofPath,
    [switch]$ResetCopies
)
$ErrorActionPreference = 'Stop'
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
if (-not (Test-Path -LiteralPath (Join-Path $RunRoot 'RUN.json'))) { throw 'An owned agent run is required.' }
$prefix = $RunRoot + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($IndexPath, $AssetsRoot, $ResourcesRoot, $ProofPath)) {
    if (-not ([IO.Path]::GetFullPath($path)).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Asset audit escaped its owned run.' }
}
$IndexPath = [IO.Path]::GetFullPath($IndexPath)
$AssetsRoot = [IO.Path]::GetFullPath($AssetsRoot)
$ResourcesRoot = [IO.Path]::GetFullPath($ResourcesRoot)
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($LauncherAssembly))
$layout = $assembly.GetType('Minecraft.DerivedAssetLayout', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$copies = $layout.GetMethod('Read', $flags).Invoke($null, @($IndexPath, $AssetsRoot, $ResourcesRoot))
foreach ($copy in $copies) {
    if (-not $copy.Destination.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Derived output escaped its owned run.' }
    for ($parent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($copy.Destination)); $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked asset output is forbidden.' }
    }
    if (Test-Path -LiteralPath $copy.Destination) {
        if ((Get-Item -LiteralPath $copy.Destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked asset output is forbidden.' }
        if ($ResetCopies) { [IO.File]::Delete($copy.Destination) }
    }
}
$repaired = $layout.GetMethod('Restore', $flags).Invoke($null, @($IndexPath, $AssetsRoot, $ResourcesRoot, [Threading.CancellationToken]::None))
foreach ($copy in $copies) {
    if ((Get-FileHash -LiteralPath $copy.Destination -Algorithm SHA1).Hash -ne $copy.Sha1) { throw 'Actual restored asset hash differs.' }
}
$again = $layout.GetMethod('Restore', $flags).Invoke($null, @($IndexPath, $AssetsRoot, $ResourcesRoot, [Threading.CancellationToken]::None))
if ($again -ne 0) { throw 'Unchanged assets were rewritten.' }
@{ status='PASS_ACTUAL_INDEX_BYTES'; index=[IO.Path]::GetFileName($IndexPath); declaredCopies=$copies.Count;
    restoredCopies=$repaired; unchangedSecondPass=$again; allDestinationSha1Matched=$true;
    networkUsed=$false; launcherAssemblySha256=(Get-FileHash -LiteralPath $LauncherAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath $ProofPath -Encoding utf8
Get-Content -LiteralPath $ProofPath
