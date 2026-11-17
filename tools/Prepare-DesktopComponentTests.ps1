#requires -Version 7.2
[CmdletBinding()]
param([string]$LockPath = '', [switch]$NoDownload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$customLock = [bool]$LockPath
$candidateDirectory = ''
if (!$LockPath -and $env:LANMINECRAFT_DESKTOP_TEST_CANDIDATE) {
    $candidateDirectory = [IO.Path]::GetFullPath($env:LANMINECRAFT_DESKTOP_TEST_CANDIDATE)
    $LockPath = Join-Path $candidateDirectory 'desktop-components.json'
}
if (!$LockPath) { $LockPath = Join-Path $PSScriptRoot 'desktop-components.lock.json' }
$LockPath = [IO.Path]::GetFullPath($LockPath)
function Assert-DesktopRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [Text.Encoding]::UTF8.GetByteCount($Path) -gt 1024 -or
        $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.Contains(':') -or $Path -match '[\x00-\x1f\x7f]') { throw "Unsafe component path: $Path" }
    foreach ($part in $Path.Split('/')) {
        if (!$part -or $part -in @('.','..') -or $part.StartsWith(' ') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part.Length -gt 255 -or $part -match '[<>"|?*]' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])(?:\..*)?$') { throw "Unsafe component path: $Path" }
    }
}

function Get-DesktopSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Assert-DesktopOrdinaryPath([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while ($cursor) {
        try { if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw "Component path contains a reparse point: $cursor" } }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}

function Write-DesktopBytesAtomic([string]$Path,[byte[]]$Bytes) {
    Assert-DesktopOrdinaryPath $Path
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $output.Write($Bytes); $output.Flush($true) } finally { $output.Dispose() }
    Assert-DesktopOrdinaryPath $Path
    [IO.File]::Move($temporary,$Path,$true)
}

function Copy-DesktopFileAtomic([string]$Source,[string]$Target) {
    Assert-DesktopOrdinaryPath $Target
    $temporary=$Target+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $input=[IO.File]::OpenRead($Source)
    try { $input.CopyTo($output); $output.Flush($true) } finally { $input.Dispose(); $output.Dispose() }
    Assert-DesktopOrdinaryPath $Target
    [IO.File]::Move($temporary,$Target,$true)
}

function Test-DesktopPacket {
    param([Parameter(Mandatory)]$Component,[Parameter(Mandatory)][string]$PacketPath,[Parameter(Mandatory)][string]$ManifestPath,[string]$ExtractDirectory)
    Assert-DesktopOrdinaryPath $PacketPath
    Assert-DesktopOrdinaryPath $ManifestPath
    if ($ExtractDirectory) { Assert-DesktopOrdinaryPath $ExtractDirectory }
    if (([IO.FileInfo]$PacketPath).Length -ne [long]$Component.sizeBytes -or (Get-DesktopSha256 $PacketPath) -cne $Component.sha256) { throw 'Component packet pin mismatch.' }
    if ($Component.sizeBytes -gt 512MB -or $Component.manifestSizeBytes -gt 8MB -or $Component.manifestSizeBytes -lt 72) { throw 'Component size is out of bounds.' }
    $stream=[IO.File]::OpenRead($PacketPath); $reader=[IO.BinaryReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$true)
    try {
        $prefix=$reader.ReadBytes([int]$Component.manifestSizeBytes)
        if ($prefix.Length -ne $Component.manifestSizeBytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix)).ToLowerInvariant() -cne $Component.manifestSha256) { throw 'Component manifest pin mismatch.' }
        $header=[IO.MemoryStream]::new($prefix,$false); $table=[IO.BinaryReader]::new($header,[Text.UTF8Encoding]::new($false,$true),$true)
        try {
            if ([Text.Encoding]::ASCII.GetString($table.ReadBytes(8)) -cne 'LMHOST01' -or $table.ReadUInt32() -ne 1) { throw 'Invalid component format.' }
            $count=$table.ReadUInt32(); $entryLength=$table.ReadUInt32(); $reserved=$table.ReadUInt32(); $tableLength=$table.ReadUInt64(); $dataLength=$table.ReadUInt64(); $bodySha=$table.ReadBytes(32)
            if ($count -lt 1 -or $count -gt 4096 -or $entryLength -gt 1024 -or $reserved -ne 0 -or 72+$tableLength -ne $prefix.Length -or $prefix.Length+$dataLength -ne $stream.Length) { throw 'Invalid component header bounds.' }
            $stream.Position=72
            if (![Security.Cryptography.CryptographicOperations]::FixedTimeEquals($bodySha,[Security.Cryptography.SHA256]::HashData($stream))) { throw 'Invalid component body hash.' }
            $entry=[Text.UTF8Encoding]::new($false,$true).GetString($table.ReadBytes([int]$entryLength)); Assert-DesktopRelativePath $entry
            if ($entry -cne $Component.entryPath) { throw 'Component entry mismatch.' }
            $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $offset=[long]0; $lastPath=''; $found=$false
            for ($i=0;$i -lt $count;$i++) {
                $nameLength=$table.ReadUInt32()
                if ($nameLength -lt 1 -or $nameLength -gt 1024) { throw 'Invalid component path length.' }
                $name=[Text.UTF8Encoding]::new($false,$true).GetString($table.ReadBytes([int]$nameLength)); Assert-DesktopRelativePath $name
                $start=$table.ReadUInt64(); $length=$table.ReadUInt64(); $digest=$table.ReadBytes(32)
                if ($digest.Length -ne 32 -or !$seen.Add($name) -or ($i -gt 0 -and [StringComparer]::Ordinal.Compare($lastPath,$name) -ge 0) -or
                    $start -ne $offset -or $length -gt $dataLength-$offset) { throw 'Invalid component file table.' }
                $lastPath=$name; $offset+=[long]$length; if ($name -ceq $entry) { $found=$true }
                $stream.Position=$prefix.Length+[long]$start
                $fileHash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                $output=$null; $temporary=''; $remaining=[long]$length
                try {
                    if ($ExtractDirectory) {
                        $target=Join-Path $ExtractDirectory $name
                        Assert-DesktopOrdinaryPath $target
                        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                        if ((Test-Path -LiteralPath $target) -and ([IO.File]::GetAttributes($target) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Component extraction target is a reparse point.' }
                        $matches=(Test-Path -LiteralPath $target -PathType Leaf) -and ([IO.FileInfo]$target).Length -eq $length -and
                            (Get-DesktopSha256 $target) -ceq [Convert]::ToHexString($digest).ToLowerInvariant()
                        if (!$matches) { $temporary=$target+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'; $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None) }
                    }
                    $buffer=[byte[]]::new(65536)
                    while ($remaining -gt 0) {
                        $read=$stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining)); if (!$read) { throw 'Truncated component file.' }
                        $fileHash.AppendData($buffer,0,$read); if ($output) { $output.Write($buffer,0,$read) }; $remaining-=$read
                    }
                    if (![Security.Cryptography.CryptographicOperations]::FixedTimeEquals($digest,$fileHash.GetHashAndReset())) { throw "Component file hash mismatch: $name" }
                    if ($output) { $output.Flush($true); $output.Dispose(); $output=$null; Assert-DesktopOrdinaryPath $target; [IO.File]::Move($temporary,$target,$true) }
                } finally { $fileHash.Dispose(); if ($output) { $output.Dispose() } }
            }
            if (!$found -or $offset -ne $dataLength -or $header.Position -ne $prefix.Length) { throw 'Invalid complete component table.' }
        } finally { $table.Dispose(); $header.Dispose() }
        Write-DesktopBytesAtomic $ManifestPath $prefix
        [pscustomobject]@{ fileCount=$count; entryPath=$entry; dataLength=$dataLength }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.protocolVersion -ne 1 -or @($lock.components).Count -ne 3) { throw 'Unsupported desktop component lock.' }
$identities = [Collections.Generic.List[string]]::new()
foreach ($kind in @('desktop-runtime','assets','steam-native')) {
    $rows = @($lock.components | Where-Object kind -CEQ $kind)
    if ($rows.Count -ne 1) { throw "Missing or duplicate desktop component: $kind" }
    $component = $rows[0]
    if ($component.id -cnotmatch '^[a-z0-9][a-z0-9._-]{0,127}$' -or $component.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $component.manifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or $component.sizeBytes -gt 512MB -or $component.sizeBytes -lt 72 -or
        $component.manifestSizeBytes -lt 72 -or $component.manifestSizeBytes -gt 8MB) { throw 'Invalid desktop component pin.' }
    Assert-DesktopRelativePath $component.entryPath
    $uri = [Uri]$component.url
    $local = $customLock -and $uri.IsAbsoluteUri -and $uri.Scheme -eq 'http' -and $uri.Host -in @('localhost','127.0.0.1')
    if (!$local -and (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.Host -cne 'github.com' -or !$uri.IsDefaultPort -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cnotmatch '^/MarkZamore/LANMinecraft-Components/releases/download/(?!latest/)[A-Za-z0-9._-]+/[^/]+$')) { throw 'Desktop tests require an immutable approved publisher URL.' }
    if ([IO.Path]::GetFileName($uri.AbsolutePath) -cne ($component.id+'.pak')) { throw 'Component URL filename differs from its pinned id.' }
    if ($component.apiUrl) {
        $api = [Uri]$component.apiUrl
        if (!$api.IsAbsoluteUri -or $api.Scheme -ne 'https' -or $api.Host -cne 'api.github.com' -or !$api.IsDefaultPort -or $api.UserInfo -or
            $api.Query -or $api.Fragment -or $api.AbsolutePath -cnotmatch '^/repos/MarkZamore/LANMinecraft-Components/releases/assets/[0-9]+$') { throw 'Invalid component API fallback.' }
    }
    $identities.Add($kind+':'+$component.sha256+':'+$component.manifestSha256)
}
$identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($identities -join '\n')))).ToLowerInvariant()
$taskRoot = Join-Path $repository 'artifacts/agent-runs/desktop-component-tests'
$fixture = Join-Path $taskRoot $identity
Assert-DesktopOrdinaryPath $fixture
[void][IO.Directory]::CreateDirectory($fixture)
$run = [ordered]@{owner='desktop-component-tests';purpose='Reusable exact desktop runtime, UI and Steam test dependencies';createdUtc=[DateTime]::UtcNow.ToString('o');expectedBytes=512MB;deleteByUtc=[DateTime]::UtcNow.AddDays(7).ToString('o');requiredResults=@('Exact pinned packets and extracted fixture for repeated tests');reusableDependencyCache=$true}
Write-DesktopBytesAtomic (Join-Path $taskRoot 'RUN.json') ([Text.UTF8Encoding]::new($false).GetBytes(($run|ConvertTo-Json)+"`n"))
$lease = $null
$deadline = [DateTime]::UtcNow.AddMinutes(3)
while (!$lease) {
    try { $leasePath=Join-Path $taskRoot 'prepare.lock'; Assert-DesktopOrdinaryPath $leasePath; $lease = [IO.File]::Open($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw 'Another process is still preparing desktop component tests.' }; Start-Sleep -Milliseconds 200 }
}
try {
    foreach ($component in $lock.components) {
        $packet = Join-Path $fixture ($component.id+'.pak')
        Assert-DesktopOrdinaryPath $packet
        if ($candidateDirectory) {
            $candidate = Join-Path $candidateDirectory ($component.id+'.pak')
            if (!(Test-Path -LiteralPath $candidate -PathType Leaf) -or (Get-Item -LiteralPath $candidate).Length -ne $component.sizeBytes -or
                (Get-DesktopSha256 $candidate) -cne $component.sha256) { throw 'Candidate desktop packet differs from its exact catalog pin.' }
            if (!(Test-Path -LiteralPath $packet)) { Copy-DesktopFileAtomic $candidate $packet }
        }
        if (!(Test-Path -LiteralPath $packet -PathType Leaf)) {
            if ($NoDownload) { throw "Pinned desktop component is unavailable offline: $($component.id)" }
            $part = $packet+'.'+[Guid]::NewGuid().ToString('N')+'.part'
            try {
                try { Invoke-WebRequest -Uri $component.url -OutFile $part -TimeoutSec 300 }
                catch { if (!$component.apiUrl) { throw }; Invoke-WebRequest -Uri $component.apiUrl -Headers @{Accept='application/octet-stream';'User-Agent'='LANMinecraft-Desktop-Tests'} -OutFile $part -TimeoutSec 300 }
                if ((Get-Item -LiteralPath $part).Length -ne $component.sizeBytes -or (Get-DesktopSha256 $part) -cne $component.sha256) { throw 'Downloaded desktop component does not match its pin.' }
                Move-Item -LiteralPath $part -Destination $packet
            } finally { if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force } }
        }
        $null = Test-DesktopPacket -Component $component -PacketPath $packet -ManifestPath (Join-Path $fixture ($component.kind+'.manifest')) -ExtractDirectory (Join-Path $fixture $component.kind)
    }
    Copy-DesktopFileAtomic $LockPath (Join-Path $fixture 'desktop-components.json')
} finally { $lease.Dispose() }
$env:LANMINECRAFT_DESKTOP_TEST_COMPONENTS = $fixture
Write-Host "Desktop component test dependencies verified: $fixture"
