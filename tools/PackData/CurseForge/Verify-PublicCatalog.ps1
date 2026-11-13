#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Live', 'Cold')][string] $Mode,
    [Parameter(Mandatory)][string] $RunDirectory,
    [Parameter(Mandatory)][string] $LauncherDirectory,
    [Parameter(Mandatory)][string] $PreviousIndex,
    [Parameter(Mandatory)][string] $ExpectedManifest,
    [Parameter(Mandatory)][string] $PackName,
    [Parameter(Mandatory)][string] $Owner,
    [Parameter(Mandatory)][string] $Repository,
    [string] $Tag = 'pack-latest',
    [string] $RunOwner = 'Codex'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$runs = Join-Path $workspace 'artifacts/agent-runs'
$run = [IO.Path]::GetFullPath($RunDirectory)
$launcher = [IO.Path]::GetFullPath($LauncherDirectory)
if (![IO.Path]::GetDirectoryName($run).Equals($runs, [StringComparison]::OrdinalIgnoreCase) -or
    !$launcher.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use the existing owned run and a launcher directory inside it.'
}
for ($path = $launcher; $path; $path = [IO.Path]::GetDirectoryName($path)) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked launcher path: $path" }
}
$ownerReceipt = Get-Content -LiteralPath (Join-Path $run 'RUN.json') -Raw | ConvertFrom-Json
if ($ownerReceipt.owner -cne $RunOwner) { throw 'The task run owner does not match.' }

function Get-Digest([byte[]] $Bytes) {
    [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Get-FileDigest([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-PinnedFile([string] $Path, $Pin) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $item.Length -ne $Pin.sizeBytes -or (Get-FileDigest $Path) -cne $Pin.sha256) {
        throw "File differs from its verified public pin: $Path"
    }
}

function Get-CatalogEntry($Entries) {
    $matching = @($Entries | Where-Object id -CEQ $packId)
    if ($matching.Count -ne 1 -or $matching[0].relativePath -cne $PackName -or
        $matching[0].source.owner -cne $Owner -or $matching[0].source.repo -cne $Repository -or
        $matching[0].source.tag -cne $Tag) {
        throw 'The requested source is missing or disagrees with the verified index.'
    }
    return $matching[0]
}

function Assert-PreviousSources($Entries) {
    foreach ($old in $baseline.packs) {
        $matching = @($Entries | Where-Object { ($_.legacyId ?? $_.id) -ceq $old.id })
        if ($matching.Count -ne 1 -or $matching[0].source.owner -cne $old.source.owner -or
            $matching[0].source.repo -cne $old.source.repo -or $matching[0].source.tag -cne $old.source.tag) {
            throw "A pre-existing source differs: $($old.id)"
        }
    }
}

function Get-CheckedCache([string] $LibraryDirectory) {
    $entryPath = Join-Path $LibraryDirectory $cacheName
    $entry = Get-Content -LiteralPath $entryPath -Raw | ConvertFrom-Json
    $manifestSha = Get-Digest ([Text.Encoding]::UTF8.GetBytes([string]$entry.manifest))
    $bundle = [Convert]::FromBase64String($entry.bundle)
    $bundleSha = Get-Digest $bundle
    if ($manifestSha -cne $expectedManifestSha -or $bundleSha -cne $expected.library.sha256 -or
        $bundle.Length -ne $expected.library.sizeBytes) { throw 'The production cache differs from the approved release.' }
    $memory = [IO.MemoryStream]::new($bundle, $false)
    try {
        $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
        try {
            if ($archive.Entries.Count -ne 2 -or !$archive.GetEntry('preview.jpg') -or !$archive.GetEntry('library.json')) {
                throw 'Unexpected library archive members.'
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry('library.json').Open())
            try { $card = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }
    }
    finally { $memory.Dispose() }
    $loaded = @($card.mods | Where-Object isLoadedMod)
    if ($card.id -cne $packId -or $card.relativePath -cne $PackName -or $card.revision -cne $expected.revision -or
        $loaded.Count -ne $expected.modCount -or [string]::IsNullOrWhiteSpace($card.name)) {
        throw 'The verified card disagrees with the approved release.'
    }
    $badLinks = @($loaded | Where-Object { !$_.websiteAvailable -or $_.websiteUrl -cnotmatch '^https://' })
    if ($badLinks.Count) { throw 'A loaded mod has no usable verified official link.' }
    return $card
}

$expected = Get-Content -LiteralPath $ExpectedManifest -Raw | ConvertFrom-Json
$expectedManifestSha = Get-FileDigest $ExpectedManifest
$packId = $expected.packId
if ($packId -cnotmatch '^minecraft-[1-9][0-9]*$') { throw 'The approved manifest requires a numbered pack ID.' }
$baseline = Get-Content -LiteralPath $PreviousIndex -Raw | ConvertFrom-Json
if ($baseline.schemaVersion -ne 1 -or !$baseline.packs) { throw 'Supply the pre-publication catalog snapshot.' }
$cacheName = (Get-Digest ([Text.Encoding]::UTF8.GetBytes("$Owner/$Repository/$Tag"))) + '.json'
$report = [ordered]@{
    outcome = 'PASS'
    computerUse = $false
    actualExecutableStarted = $Mode -eq 'Cold'
    network = 'real public GitHub'
    packId = $packId
    revision = $expected.revision
    manifestSha256 = $expectedManifestSha
    librarySha256 = $expected.library.sha256
    loadedModCount = $expected.modCount
    priorEntriesPreserved = $baseline.packs.Count
}
$reportPath = Join-Path $launcher ('catalog-' + $Mode.ToLowerInvariant() + '-' + $packId + '.json')
if (Test-Path -LiteralPath $reportPath) { throw 'The acceptance receipt already exists.' }
$watch = [Diagnostics.Stopwatch]::StartNew()

if ($Mode -eq 'Live') {
    $fixture = Join-Path $launcher ('live-' + $packId)
    if (Test-Path -LiteralPath $fixture) { throw 'Use a clean catalog probe root.' }
    $managed = Join-Path $launcher 'managed'
    $managedDirectory = Get-Item -LiteralPath $managed -Force
    if (!$managedDirectory.PSIsContainer -or ($managedDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Use the extracted public managed assembly directory.'
    }
    $managedReceipt = Get-Content -LiteralPath (Join-Path $launcher 'managed.json') -Raw | ConvertFrom-Json
    $assemblySha = $null
    foreach ($name in @('CmlLib.Core.Commons.dll', 'CmlLib.Core.dll', 'LANMinecraft.dll')) {
        $pin = @($managedReceipt.extracted | Where-Object name -CEQ $name)
        if ($pin.Count -ne 1) { throw "Missing unique verified pin for $name." }
        $path = Join-Path $managed $name
        Assert-PinnedFile $path $pin[0]
        [void][Reflection.Assembly]::LoadFrom($path)
        if ($name -ceq 'LANMinecraft.dll') { $assemblySha = $pin[0].sha256 }
    }
    Add-Type -AssemblyName PresentationCore
    $paths = [Minecraft.AppPaths]::new($fixture)
    $paths.Ensure()
    $logger = [Minecraft.Logger]::new($paths.LogFile)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(2))
    try {
        $catalog = [Minecraft.PackCatalogService]::new($paths, $logger, $null, $deadline.Token, $true)
        $catalog.InitializeIndexAsync($deadline.Token).GetAwaiter().GetResult() | Out-Null
        if (!$catalog.IsIndexReady -or !$catalog.HasOnlineCatalog) { throw 'The current online catalog did not verify.' }
        $entry = Get-CatalogEntry $catalog.Sources
        Assert-PreviousSources $catalog.Sources
        $catalog.EnsureLaunchReadyAsync($PackName, $deadline.Token).GetAwaiter().GetResult() | Out-Null
        if (!$catalog.IsLaunchReady($PackName)) { throw 'The selected pack is not launch-ready.' }
        $raw = $catalog.SessionManifest($entry.Source)
        if ((Get-Digest ([Text.Encoding]::UTF8.GetBytes($raw))) -cne $expectedManifestSha) {
            throw 'The production catalog used unexpected launch-manifest bytes.'
        }
        $catalog.PrepareLibraryAsync($deadline.Token).GetAwaiter().GetResult() | Out-Null
        $card = $catalog.Find($PackName)
        $decoded = Get-CheckedCache (Join-Path $paths.Launcher 'Library')
        if (!$catalog.IsReady -or !$catalog.IsOverviewReady -or !$card -or $card.Name -cne $decoded.name -or
            $card.ModCount -ne $expected.modCount -or $card.Preview.PixelWidth -lt 1 -or $card.Preview.PixelHeight -lt 1) {
            throw 'The production presentation disagrees with the verified card.'
        }
        $report.launcherAssemblySha256 = $assemblySha
        $report.releaseNumber = $managedReceipt.releaseNumber
        $report.title = $card.Name
        $report.versionLabel = $card.VersionLabel
        $report.displayedModCount = $card.DisplayModCount
        $report.previewWidth = $card.Preview.PixelWidth
        $report.previewHeight = $card.Preview.PixelHeight
        $report.catalogEntries = $catalog.Sources.Count
    }
    finally { $deadline.Dispose() }
}
else {
    $exe = Join-Path $launcher 'LANMinecraft.exe'
    $pin = Get-Content -LiteralPath (Join-Path $launcher 'download.json') -Raw | ConvertFrom-Json
    Assert-PinnedFile $exe $pin
    $actual = Join-Path $launcher 'Minecraft'
    if (Test-Path -LiteralPath $actual) { throw 'Cold startup requires a portable root with no Minecraft data.' }
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting('Local\LANMinecraft.SingleInstance', [ref]$mutex)) {
        $mutex.Dispose()
        throw 'The Windows session already has an active LANMinecraft launcher.'
    }
    $library = Join-Path $actual 'Launcher/Library'
    $indexPath = Join-Path $library 'catalog.json'
    $sourcePath = Join-Path $library $cacheName
    $logPath = Join-Path $actual 'Personal/logs.log'
    $line = 'Launch ' + $PackName + ': verified startup manifest revision "' + $expected.revision + '", SHA256 ' + $expectedManifestSha + '.'
    $process = $null
    try {
        $process = Start-Process -FilePath $exe -WorkingDirectory $launcher -WindowStyle Hidden -PassThru
        $done = $false
        while ($watch.Elapsed.TotalSeconds -lt 100) {
            if ($process.HasExited) { throw "Public executable exited early with code $($process.ExitCode)." }
            if ((Test-Path -LiteralPath $indexPath -PathType Leaf) -and (Test-Path -LiteralPath $sourcePath -PathType Leaf) -and
                (Test-Path -LiteralPath $logPath -PathType Leaf) -and (Select-String -LiteralPath $logPath -Pattern $line -SimpleMatch -Quiet)) {
                $done = $true
                break
            }
            Start-Sleep -Milliseconds 300
        }
        if (!$done) { throw 'The public launcher did not verify the pack during cold startup.' }
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        Get-CatalogEntry $index.packs | Out-Null
        Assert-PreviousSources $index.packs
        $card = Get-CheckedCache $library
        $report.launcherSha256 = $pin.sha256
        $report.releaseNumber = $pin.releaseNumber
        $report.launchLog = $line
        $report.title = $card.name
        $report.catalogEntries = $index.packs.Count
    }
    finally {
        if ($process) {
            if (!$process.HasExited) {
                $process.Kill()
                $process.WaitForExit(4000) | Out-Null
            }
            $process.Dispose()
        }
    }
}
$report.elapsedMilliseconds = $watch.ElapsedMilliseconds
$report | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding utf8
Get-Content -LiteralPath $reportPath -Raw
