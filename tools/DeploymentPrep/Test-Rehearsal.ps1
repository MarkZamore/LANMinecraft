param(
    [Parameter(Mandatory=$true)][string]$Assembly,
    [Parameter(Mandatory=$true)][string]$OriginalRoot,
    [Parameter(Mandatory=$true)][string]$FixtureParent,
    [Parameter(Mandatory=$true)][string]$Candidates,
    [Parameter(Mandatory=$true)][string]$FixtureDist,
    [string]$BaselineRoot
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $OriginalRoot).Path
$parent = [IO.Path]::GetFullPath($FixtureParent)
$boundary = [IO.Path]::GetFullPath((Join-Path $source 'artifacts')).TrimEnd('\') + '\'
if (-not $parent.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $parent)) {
    throw 'FixtureParent must be a NEW directory under the workspace artifacts. Existing directories are never removed.'
}
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$components = (Resolve-Path -LiteralPath $Candidates).Path
$dist = (Resolve-Path -LiteralPath $FixtureDist).Path
$tool = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\DeploymentPrep.dll'
if (-not (Test-Path -LiteralPath $tool)) { throw 'Build the independent DeploymentPrep project first.' }
New-Item -ItemType Directory -Path (Join-Path $parent 'reports') | Out-Null
$results = [Collections.Generic.List[object]]::new()
function New-Fixture([string]$name) {
    $path = Join-Path $parent $name
    & (Join-Path $PSScriptRoot 'New-Rehearsal.ps1') -OriginalRoot $source -FixtureRoot $path -Candidates $components -BaselineRoot $BaselineRoot | Out-Host
    return $path
}
function Inventory([string]$root) {
    $rows = [Collections.Generic.List[string]]::new()
    function Visit([string]$directory) {
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            $relative = $item.FullName.Substring($root.Length).Replace('\','/')
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $rows.Add("LINK $relative -> $($item.Target -join ',')")
            } elseif ($item.PSIsContainer) { Visit $item.FullName }
            else { $rows.Add("FILE $relative $((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)") }
        }
    }
    Visit $root
    return (($rows | Sort-Object) -join "`n")
}
function Invoke-Prep([string]$root, [string]$name, [bool]$apply, [bool]$expectedSuccess) {
    $report = Join-Path $parent "reports\$name.json"
    $arguments = @($tool, '--assembly', $assemblyPath, '--root', $root, '--report', $report, '--mode', 'all', '--rehearsal')
    if ($apply) { $arguments += @('--apply', '--skip-runtime', '--fixture-dist', $dist, '--fixture-components', $components) }
    else { $arguments += '--dry-run' }
    & dotnet @arguments | Out-Host
    $exit = $LASTEXITCODE
    if (($exit -eq 0) -ne $expectedSuccess) { throw "Unexpected exit $exit in $name. Inspect $report" }
    $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $results.Add([pscustomobject]@{ name=$name; status='PASS'; toolStatus=$json.status; expectedSuccess=$expectedSuccess; report=$report })
    return $json
}
$positive = New-Fixture 'positive'
$before = Inventory $positive
$null = Invoke-Prep $positive 'dry-run-no-writes' $false $true
if ($before -cne (Inventory $positive)) { throw 'Dry-run changed fixture files or junctions.' }
$first = Invoke-Prep $positive 'apply' $true $true
$second = Invoke-Prep $positive 'repeat' $true $true
$updated = @($first.stages | Where-Object stage -eq 'production-managed-transport')
$repeated = @($second.stages | Where-Object stage -eq 'production-managed-transport')
if ($updated.Count -ne 7 -or $repeated.Count -ne 7 -or @($repeated | Where-Object { $_.downloaded -or $_.installed }).Count -ne 0) {
    throw 'Expected seven production transport preparations and zero download/install on repeat.'
}
foreach ($kind in @('unknown-canonical', 'corrupt-instance', 'locked-world', 'wrong-profile-link')) {
    $fixture = New-Fixture $kind
    if ($kind -eq 'unknown-canonical') {
        $mods = Join-Path $fixture 'Minecraft\Packs\All The Mods 9\mods'
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $mods 'e4steam-user.jar'), 'Unknown bytes must be preserved and refused before mutation.')
    } elseif ($kind -eq 'corrupt-instance') {
        $jar = Get-ChildItem -LiteralPath (Join-Path $fixture 'Minecraft\Personal\Instances\All The Mods 9\mods') -Filter 'e4steam*.jar' | Select-Object -First 1
        [IO.File]::WriteAllText($jar.FullName, 'Corrupt expected filename must not authorize removal.')
    } elseif ($kind -eq 'wrong-profile-link') {
        # Fixture-only junction replacement. Delete() removes the junction itself,
        # never recursively traverses or deletes the target world directory.
        $link = Join-Path $fixture 'Minecraft\Personal\Instances\All The Mods 9\saves'
        [IO.Directory]::Delete($link)
        New-Item -ItemType Junction -Path $link -Target (Join-Path $fixture 'Minecraft\Worlds\All The Mods 10') | Out-Null
    }
    $before = Inventory $fixture
    $held = $null
    try {
        if ($kind -eq 'locked-world') {
            $lock = Join-Path $fixture 'Minecraft\Worlds\All The Mods 9\DeploymentFixture\session.lock'
            $held = [IO.FileStream]::new($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        $null = Invoke-Prep $fixture $kind $true $false
    } finally { if ($null -ne $held) { $held.Dispose() } }
    if ($before -cne (Inventory $fixture)) { throw "Failed safety check mutated installation fixture: $kind" }
}
$summary = [pscustomobject]@{ status='PASS'; assemblySha256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant();
    tests=$results; gameStarted=$false; runtimeInstaller='NOT RUN: marked fixture deliberately skips official install';
    privateWorldDataCopied=$false; genuineRuntimeCompatibility='NOT TESTED' }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $parent 'summary.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 4
