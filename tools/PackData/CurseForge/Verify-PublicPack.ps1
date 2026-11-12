#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')][string] $Label,
    [Parameter(Mandatory)][string] $LauncherAssembly,
    [Parameter(Mandatory)][string] $ExpectedManifest,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*$')][string] $Owner,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string] $Repository,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string] $Tag,
    [Parameter(Mandatory)][string] $PackName,
    [string] $RunOwner = 'Codex'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$runs = Join-Path $workspace 'artifacts/agent-runs'
$run = [IO.Path]::GetFullPath($RunDirectory)
if (![IO.Path]::GetDirectoryName($run).Equals($runs, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunDirectory must be an immediate child of the workspace agent-runs folder.'
}
for ($path = $run; $path; $path = [IO.Path]::GetDirectoryName($path)) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked run path: $path" }
}
$receipt = Get-Content -LiteralPath (Join-Path $run 'RUN.json') -Raw | ConvertFrom-Json
if ($receipt.owner -cne $RunOwner) { throw 'The task run owner does not match.' }
$install = Join-Path $run $Label
if (Test-Path -LiteralPath $install) { throw 'Use a new installation directory.' }

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Receive-Asset([string] $Name) {
    if ($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "Invalid asset name: $Name" }
    $path = Join-Path $install $Name
    $uri = "https://github.com/$Owner/$Repository/releases/download/$Tag/$Name"
    & curl.exe --fail --location --silent --show-error --proto '=https' --proto-redir '=https' --max-time 90 --output $path $uri
    if ($LASTEXITCODE -ne 0) { throw "Public download failed for $Name" }
    return $path
}

$assemblyPath = [IO.Path]::GetFullPath($LauncherAssembly)
if ([IO.Path]::GetFileName($assemblyPath) -cne 'LANMinecraft.dll') { throw 'Supply the production LANMinecraft.dll.' }
foreach ($name in @('CmlLib.Core.Commons.dll', 'CmlLib.Core.dll', 'LANMinecraft.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path ([IO.Path]::GetDirectoryName($assemblyPath)) $name))
}

[void][IO.Directory]::CreateDirectory($install)
$manifestPath = Receive-Asset 'pack-manifest.json'
$manifestSha = Get-Sha256 $manifestPath
if ($manifestSha -cne (Get-Sha256 $ExpectedManifest)) { throw 'The public manifest differs from the approved candidate.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$library = $manifest.library
$libraryPath = Receive-Asset $library.name
if ((Get-Item -LiteralPath $libraryPath).Length -ne $library.sizeBytes -or (Get-Sha256 $libraryPath) -cne $library.sha256) {
    throw 'The public library archive differs from its manifest pin.'
}
$zip = [IO.Compression.ZipFile]::OpenRead($libraryPath)
try {
    if ($zip.Entries.Count -ne 2 -or !$zip.GetEntry('preview.jpg') -or !$zip.GetEntry('library.json')) {
        throw 'Unexpected library archive members.'
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('library.json').Open())
    try { $card = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($card.revision -cne $manifest.revision -or $card.id -cne $manifest.packId -or $card.relativePath -cne $PackName) {
        throw 'Library archive does not match the release and pack identity.'
    }
}
finally { $zip.Dispose() }

$paths = [Minecraft.AppPaths]::new($install)
$paths.Ensure()
$pack = $paths.CombineUnderPacks($PackName)
[void][IO.Directory]::CreateDirectory($pack)
$logger = [Minecraft.Logger]::new($paths.LogFile)
$sync = [Minecraft.PortablePackSyncService]::new($paths, $logger, $null, $null)
$source = [Minecraft.PackSyncSource]::new($Owner, $Repository, $Tag)
$writer = $sync.GetType().GetMethod('WriteSourceMarker', [Reflection.BindingFlags]'NonPublic, Static')
if (!$writer) { throw 'The launcher source marker writer was not found.' }
[void]$writer.Invoke($null, [object[]]@($pack, $source))
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(45))
try { $result = $sync.SyncAsync($PackName, $null, $deadline.Token).GetAwaiter().GetResult() }
finally { $deadline.Dispose() }
if ($result.Outcome.ToString() -cne 'Installed' -or $result.Revision -cne $manifest.revision) {
    throw "Clean public install returned $($result.Outcome) at $($result.Revision)."
}
$assetBytes = ($manifest.assets | Measure-Object sizeBytes -Sum).Sum
if ($result.BytesDownloaded -ne $assetBytes) { throw 'Clean install did not verify the full set of game assets.' }
foreach ($file in $manifest.files) {
    $target = Join-Path $pack $file.path
    if ((Get-Item -LiteralPath $target).Length -ne $file.sizeBytes -or (Get-Sha256 $target) -cne $file.sha256) {
        throw "Installed file differs from the public manifest: $($file.path)"
    }
}

[ordered]@{
    outcome = 'PASS'
    revision = $manifest.revision
    manifestSha256 = $manifestSha
    librarySha256 = $library.sha256
    launcherAssemblySha256 = Get-Sha256 $assemblyPath
    gameAssets = $manifest.assets.Count
    gameAssetBytes = $result.BytesDownloaded
    installedFiles = $manifest.files.Count
    installedBytes = ($manifest.files | Measure-Object sizeBytes -Sum).Sum
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $install 'public-acceptance.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $install 'public-acceptance.json')
