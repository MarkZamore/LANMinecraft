#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$OutputExecutable,
    [Parameter(Mandatory = $true)][ValidateRange(1,65535)][int]$ReleaseNumber,
    [string]$SourceRevisionId = '',
    [string]$EntryAssembly = 'LANMinecraft.dll',
    [string]$ArtifactsDirectory,
    [string]$ComponentLockPath = '',
    [switch]$AllowTestComponentSources,
    [string]$NativeSourcePath = ''
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$ComponentLockPath) { $ComponentLockPath = Join-Path $repository 'tools/desktop-components.lock.json' }
$payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\','/')
$destination = [IO.Path]::GetFullPath($OutputExecutable)
$maximumPayload = 512MB
if (-not $ArtifactsDirectory) {
    $ArtifactsDirectory = Join-Path $repository ('artifacts\portable-host\' + $ReleaseNumber + '-' + [Guid]::NewGuid().ToString('N'))
}
$stage = [IO.Path]::GetFullPath($ArtifactsDirectory)
$artifactBoundary = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $stage.StartsWith($artifactBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native host build files must stay inside repository artifacts.'
}
if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) { throw 'The application payload folder is missing.' }
if ($SourceRevisionId -and $SourceRevisionId -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid source revision.' }
[void][IO.Directory]::CreateDirectory($stage)

function Assert-RelativePayloadPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [Text.Encoding]::UTF8.GetByteCount($Path) -gt 1024 -or
        $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.Contains(':') -or $Path -match '[\x00-\x1f\x7f]') {
        throw ('Invalid portable payload path: ' + $Path)
    }
    foreach ($component in $Path.Split('/')) {
        if (-not $component -or $component -in @('.', '..') -or $component.StartsWith(' ') -or $component.EndsWith('.') -or $component.EndsWith(' ') -or
            $component.Length -gt 255 -or $component -match '[<>"|?*]' -or
            $component -match '^(?i:CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])(?:\..*)?$') {
            throw ('Invalid portable payload component: ' + $Path)
        }
    }
}

Assert-RelativePayloadPath $EntryAssembly
if ($EntryAssembly.Contains('/') -or -not $EntryAssembly.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The entry assembly must be a top-level managed DLL.'
}
$entryAppHost = [IO.Path]::ChangeExtension($EntryAssembly, '.exe')
$pendingDirectories = [Collections.Generic.Stack[string]]::new()
$pendingDirectories.Push($payloadRoot)
$filesByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pendingDirectories.Count) {
    $directory = $pendingDirectories.Pop()
    if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw ('A published payload directory is a reparse point: ' + $directory)
    }
    foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $attributes = [IO.File]::GetAttributes($child)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('A published payload entry is a reparse point: ' + $child) }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pendingDirectories.Push($child); continue }
        $relative = [IO.Path]::GetRelativePath($payloadRoot, $child).Replace('\','/')
        Assert-RelativePayloadPath $relative
        # The SDK requires an apphost when publishing self-contained. Native
        # hosting uses the DLL entry point, so its unused EXE is not packaged.
        if ($relative.Equals($entryAppHost, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($filesByPath.ContainsKey($relative)) { throw ('Duplicate Windows payload path: ' + $relative) }
        $info = [IO.FileInfo]::new($child)
        $filesByPath.Add($relative, [pscustomobject]@{ Path = $relative; Source = $child; Length = $info.Length })
    }
}
if ($filesByPath.Count -lt 1 -or $filesByPath.Count -gt 4096) { throw 'The native payload file count is outside its bounded range.' }
foreach ($required in @($EntryAssembly)) {
    if (-not $filesByPath.ContainsKey($required)) { throw ('The self-contained payload is missing ' + $required) }
}
$orderedPaths = [string[]]@($filesByPath.Keys)
[Array]::Sort($orderedPaths, [StringComparer]::Ordinal)
$table = [IO.MemoryStream]::new()
$tableWriter = [IO.BinaryWriter]::new($table, [Text.Encoding]::UTF8, $true)
$entryBytes = [Text.Encoding]::UTF8.GetBytes($EntryAssembly)
$tableWriter.Write($entryBytes)
$dataLength = [long]0
$records = [Collections.Generic.List[object]]::new()
try {
    foreach ($relative in $orderedPaths) {
        $file = $filesByPath[$relative]
        if ($file.Length -gt $maximumPayload - $dataLength) { throw 'The native payload exceeds 512 MiB.' }
        $nameBytes = [Text.Encoding]::UTF8.GetBytes($relative)
        $digest = [Convert]::FromHexString((Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash)
        $tableWriter.Write([uint32]$nameBytes.Length)
        $tableWriter.Write($nameBytes)
        $tableWriter.Write([uint64]$dataLength)
        $tableWriter.Write([uint64]$file.Length)
        $tableWriter.Write($digest)
        $records.Add([pscustomobject]@{ Path = $relative; Source = $file.Source; Offset = $dataLength; Length = $file.Length; Sha256 = [Convert]::ToHexString($digest).ToLowerInvariant() })
        $dataLength += $file.Length
    }
    $tableWriter.Flush()
    if ($table.Length + $dataLength + 72 -gt $maximumPayload) { throw 'The complete native packet exceeds 512 MiB.' }
    $packetPath = Join-Path $stage 'payload.packet'
    $packet = [IO.FileStream]::new($packetPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($packet, [Text.Encoding]::UTF8, $true)
    $bodyHash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('LMHOST01'))
        $writer.Write([uint32]1)
        $writer.Write([uint32]$records.Count)
        $writer.Write([uint32]$entryBytes.Length)
        $writer.Write([uint32]0)
        $writer.Write([uint64]$table.Length)
        $writer.Write([uint64]$dataLength)
        $writer.Write([byte[]]::new(32))
        $tableBytes = $table.ToArray()
        $writer.Write($tableBytes)
        $bodyHash.AppendData($tableBytes)
        $buffer = [byte[]]::new(65536)
        foreach ($record in $records) {
            $input = [IO.FileStream]::new($record.Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $fileHash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $copied = [long]0
            try {
                while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($count -gt $record.Length - $copied) { throw 'A published payload file grew during packaging.' }
                    $writer.Write($buffer, 0, $count)
                    $bodyHash.AppendData($buffer, 0, $count)
                    $fileHash.AppendData($buffer, 0, $count)
                    $copied += $count
                }
                if ($copied -ne $record.Length -or [Convert]::ToHexString($fileHash.GetHashAndReset()).ToLowerInvariant() -ne $record.Sha256) {
                    throw ('A published payload file changed during packaging: ' + $record.Path)
                }
            }
            finally { $input.Dispose(); $fileHash.Dispose() }
        }
        $digest = $bodyHash.GetHashAndReset()
        $writer.Flush()
        $packet.Position = 40
        $writer.Write($digest)
        $writer.Flush()
        $packet.Flush($true)
        $payloadHash = [Convert]::ToHexString($digest).ToLowerInvariant()
    }
    finally { $bodyHash.Dispose(); $writer.Dispose(); $packet.Dispose() }
}
finally { $tableWriter.Dispose(); $table.Dispose() }

function Escape-ResourcePath([string]$Path) { $Path.Replace('\','\\').Replace('"','\"') }
$lock = Get-Content -LiteralPath $ComponentLockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.protocolVersion -ne 1 -or @($lock.components).Count -ne 3) {
    throw 'Unsupported desktop component lock.'
}
$dependencyPath = Join-Path $stage 'desktop-dependencies.bin'
$dependencyBuffer = [IO.MemoryStream]::new()
$dependencyWriter = [IO.BinaryWriter]::new($dependencyBuffer, [Text.UTF8Encoding]::new($false), $true)
function Write-DependencyText([string]$Value) {
    $encoded = [Text.Encoding]::UTF8.GetBytes($Value)
    if ($encoded.Length -gt 8192) { throw 'Oversized dependency descriptor value.' }
    $dependencyWriter.Write([uint32]$encoded.Length)
    $dependencyWriter.Write($encoded)
}
function Assert-ComponentUrl([string]$Value, [bool]$Api) {
    if (!$Value -and $Api) { return }
    $uri = [Uri]$Value
    if (!$uri.IsAbsoluteUri -or $uri.UserInfo -or $uri.Fragment) { throw 'Invalid desktop component address.' }
    if ($AllowTestComponentSources -and $uri.Scheme -eq 'http' -and $uri.Host -in @('localhost','127.0.0.1')) { return }
    $pattern = if ($Api) { '^/repos/MarkZamore/LANMinecraft-Components/releases/assets/[0-9]+$' } else {
        '^/MarkZamore/LANMinecraft-Components/releases/download/(?!latest/)[A-Za-z0-9._-]+/[^/]+$'
    }
    $hostName = if ($Api) { 'api.github.com' } else { 'github.com' }
    if ($uri.Scheme -ne 'https' -or !$uri.IsDefaultPort -or $uri.Host -cne $hostName -or $uri.Query -or $uri.AbsolutePath -cnotmatch $pattern) {
        throw 'Desktop components require pinned immutable publisher release addresses.'
    }
}
try {
    $dependencyWriter.Write([Text.Encoding]::ASCII.GetBytes('LMDESK01'))
    $dependencyWriter.Write([uint32]1)
    $dependencyWriter.Write([uint32]3)
    $kindNumber = 0
    foreach ($kind in @('desktop-runtime','assets','steam-native')) {
        $kindNumber++
        $matches = @($lock.components | Where-Object kind -CEQ $kind)
        if ($matches.Count -ne 1) { throw ('Missing or duplicate desktop component: ' + $kind) }
        $component = $matches[0]
        if ($component.id -cnotmatch '^[a-z0-9][a-z0-9._-]{0,127}$' -or
            $component.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $component.manifestSha256 -cnotmatch '^[a-f0-9]{64}$' -or
            [long]$component.sizeBytes -le 72 -or [long]$component.sizeBytes -gt $maximumPayload -or
            [long]$component.manifestSizeBytes -lt 72 -or [long]$component.manifestSizeBytes -gt 4MB -or
            [long]$component.manifestSizeBytes -ge [long]$component.sizeBytes) { throw 'Invalid desktop component pin.' }
        Assert-RelativePayloadPath ([string]$component.entryPath)
        Assert-ComponentUrl ([string]$component.url) $false
        Assert-ComponentUrl ([string]$component.apiUrl) $true
        $dependencyWriter.Write([uint32]$kindNumber)
        Write-DependencyText ([string]$component.id)
        Write-DependencyText ([string]$component.url)
        Write-DependencyText ([string]$component.apiUrl)
        Write-DependencyText ([string]$component.entryPath)
        $dependencyWriter.Write([uint64]$component.sizeBytes)
        $dependencyWriter.Write([Convert]::FromHexString($component.sha256))
        $dependencyWriter.Write([uint64]$component.manifestSizeBytes)
        $dependencyWriter.Write([Convert]::FromHexString($component.manifestSha256))
    }
    $dependencyWriter.Flush()
    if ($dependencyBuffer.Length -gt 64KB) { throw 'Oversized dependency resource.' }
    [IO.File]::WriteAllBytes($dependencyPath, $dependencyBuffer.ToArray())
} finally { $dependencyWriter.Dispose(); $dependencyBuffer.Dispose() }
$dependencyResource = Escape-ResourcePath $dependencyPath
$icon = Escape-ResourcePath ([IO.Path]::GetFullPath((Join-Path $repository 'Program\Assets\icon.ico')))
$regularFont = Escape-ResourcePath ([IO.Path]::GetFullPath((Join-Path $repository 'Program\Assets\Fonts\Roboto-Regular.ttf')))
$semiboldFont = Escape-ResourcePath ([IO.Path]::GetFullPath((Join-Path $repository 'Program\Assets\Fonts\Roboto-SemiBold.ttf')))
$packetResource = Escape-ResourcePath $packetPath
$productVersion = '1.0.' + $ReleaseNumber + $(if ($SourceRevisionId) { '+' + $SourceRevisionId.ToLowerInvariant() } else { '' })
$resources = @"
#include <windows.h>
1 ICON "$icon"
101 RCDATA "$packetResource"
102 RCDATA "$dependencyResource"
103 RCDATA "$regularFont"
104 RCDATA "$semiboldFont"
1 VERSIONINFO
 FILEVERSION 1,0,$ReleaseNumber,0
 PRODUCTVERSION 1,0,$ReleaseNumber,0
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "CompanyName", "LANMinecraft\0"
   VALUE "FileDescription", "LANMinecraft\0"
   VALUE "FileVersion", "1.0.$ReleaseNumber.0\0"
   VALUE "InternalName", "LANMinecraft\0"
   VALUE "OriginalFilename", "LANMinecraft.exe\0"
   VALUE "ProductName", "LANMinecraft\0"
   VALUE "ProductVersion", "$productVersion\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
[IO.File]::WriteAllText((Join-Path $stage 'resources.rc'), $resources, [Text.UTF8Encoding]::new($true))
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'The Visual Studio C++ toolchain discovery tool is missing.' }
$visualStudio = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if (-not $visualStudio) { throw 'Install the Visual Studio x64 C++ tools and Windows SDK before publishing the native host.' }
$developerCommand = Join-Path $visualStudio 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand) -or $developerCommand.Contains('%')) { throw 'Unsupported Visual Studio developer command path.' }
$nativeSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'native_host.cpp'))
if ($NativeSourcePath) {
    if (!$AllowTestComponentSources) { throw 'A baseline native source override requires the explicit test-only build mode.' }
    $nativeSource = [IO.Path]::GetFullPath($NativeSourcePath, $repository)
    $sourceBoundary = [IO.Path]::GetFullPath((Join-Path $repository 'LocalSources')) + [IO.Path]::DirectorySeparatorChar
    if (!$nativeSource.StartsWith($sourceBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        !(Test-Path -LiteralPath $nativeSource -PathType Leaf) -or
        ((Get-Item -LiteralPath $nativeSource).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'A baseline source must be an ordinary canonical LocalSources checkout file.'
    }
}
$manifest = [IO.Path]::GetFullPath((Join-Path $repository 'Program\app.manifest'))
$testSourcesDefine = if ($AllowTestComponentSources) { '/DLANMINECRAFT_BOOTSTRAP_TEST_SOURCES=1' } else { '' }
$response = @"
/nologo /O2 /W4 /EHsc /MT /std:c++17 /utf-8 /DUNICODE /D_UNICODE /guard:cf
$testSourcesDefine
"$nativeSource"
resources.res
/Fo:native_host.obj
/Fe:LANMinecraft.exe
/link /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf /Brepro /MANIFEST:EMBED /MANIFESTINPUT:"$manifest" kernel32.lib shell32.lib ole32.lib bcrypt.lib user32.lib gdi32.lib dwmapi.lib winhttp.lib comctl32.lib
"@
[IO.File]::WriteAllText((Join-Path $stage 'compile.rsp'), $response, [Text.UnicodeEncoding]::new($false, $true))
$buildCommand = @"
@echo off
call "%LANMINECRAFT_VSDEVCMD%" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
rc.exe /nologo /c65001 /fo resources.res resources.rc
if errorlevel 1 exit /b %errorlevel%
cl.exe @compile.rsp
exit /b %errorlevel%
"@
[IO.File]::WriteAllText((Join-Path $stage 'build-native.cmd'), $buildCommand, [Text.Encoding]::ASCII)
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
$start.ArgumentList.Add('/d'); $start.ArgumentList.Add('/c'); $start.ArgumentList.Add('build-native.cmd')
$start.WorkingDirectory = $stage
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['LANMINECRAFT_VSDEVCMD'] = $developerCommand
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'The native compiler did not start.' }
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
[IO.File]::WriteAllText((Join-Path $stage 'native-build.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
if ($process.ExitCode -ne 0) { throw ('Native host build failed. See ' + (Join-Path $stage 'native-build.log')) }
$built = Join-Path $stage 'LANMinecraft.exe'
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw 'The native compiler did not produce LANMinecraft.exe.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($built)
if ($version.FileBuildPart -ne $ReleaseNumber -or $version.FileMajorPart -ne 1 -or $version.FileMinorPart -ne 0) {
    throw 'The outer native host has incorrect version resources.'
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
[IO.File]::Copy($built, $destination, $true)
$summary = [ordered]@{
    Format = 2; EntryAssembly = $EntryAssembly; PayloadSha256 = $payloadHash; Files = $records.Count
    ComponentLock = [IO.Path]::GetFullPath($ComponentLockPath); TestComponentSources = [bool]$AllowTestComponentSources
    NativeSource = $nativeSource; NativeSourceSha256 = (Get-FileHash -LiteralPath $nativeSource -Algorithm SHA256).Hash.ToLowerInvariant()
    DataBytes = $dataLength; PacketBytes = ([IO.FileInfo]::new($packetPath)).Length
    Executable = $destination; ExecutableSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    ReleaseNumber = $ReleaseNumber; SourceRevisionId = $SourceRevisionId; FileRecords = @($records | Select-Object Path, Offset, Length, Sha256)
}
[IO.File]::WriteAllText((Join-Path $stage 'host-build.json'), ($summary | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host ('Published portable native host: ' + $destination)
