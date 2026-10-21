param(
    [string]$JarPath = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath))
$programRoot = [IO.Path]::GetFullPath((Split-Path -Parent $sourceRoot))
if ([string]::IsNullOrWhiteSpace($JarPath)) { $JarPath = Join-Path $programRoot 'Build\NoticeOverlay\launcher-notice-overlay.jar' }
$JarPath = [IO.Path]::GetFullPath($JarPath)
$buildPrefix = [IO.Path]::GetFullPath((Join-Path $programRoot 'Build')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $JarPath.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Notice test JAR must remain under Program/Build.' }
$scratch = [IO.Path]::GetFullPath((Join-Path $programRoot ('Build\NoticeOverlay\test-' + [Guid]::NewGuid().ToString('N'))))
if (-not $scratch.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Notice test scratch escaped Program/Build.' }
$toolTemporary = Join-Path $scratch 'temp'
$toolProfile = Join-Path $scratch 'profile'
$toolRoaming = Join-Path $toolProfile 'AppData\Roaming'
$toolLocal = Join-Path $toolProfile 'AppData\Local'
$originalEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
New-Item -ItemType Directory -Path $scratch, $toolTemporary, $toolRoaming, $toolLocal -Force | Out-Null
[Environment]::SetEnvironmentVariable('TEMP', $toolTemporary, 'Process')
[Environment]::SetEnvironmentVariable('TMP', $toolTemporary, 'Process')
[Environment]::SetEnvironmentVariable('USERPROFILE', $toolProfile, 'Process')
[Environment]::SetEnvironmentVariable('APPDATA', $toolRoaming, 'Process')
[Environment]::SetEnvironmentVariable('LOCALAPPDATA', $toolLocal, 'Process')
& (Join-Path $sourceRoot 'Build-NoticeOverlay.ps1') -OutputPath $JarPath
$jarWriteTime = (Get-Item -LiteralPath $JarPath).LastWriteTimeUtc
& (Join-Path $sourceRoot 'Build-NoticeOverlay.ps1') -OutputPath $JarPath
if ((Get-Item -LiteralPath $JarPath).LastWriteTimeUtc -ne $jarWriteTime) { throw 'Unchanged agent was rebuilt.' }
$hooksPath = Join-Path $scratch 'portable-notice-hooks.jar'
function Assert-Java8Classes($archive) {
    foreach ($entry in $archive.Entries) {
        if (-not $entry.FullName.EndsWith('.class')) { continue }
        $stream = $entry.Open()
        try {
            $header = New-Object byte[] 8
            if ($stream.Read($header, 0, 8) -ne 8 -or $header[0] -ne 0xca -or $header[1] -ne 0xfe) {
                throw "Invalid class header: $($entry.FullName)"
            }
            $major = ([int]$header[6] * 256) + $header[7]
            if ($major -gt 52) { throw "Class needs newer than Java8: $($entry.FullName) major=$major" }
        } finally { $stream.Dispose() }
    }
}
$archive = [IO.Compression.ZipFile]::OpenRead($JarPath)
try {
    Assert-Java8Classes $archive
    if ($archive.GetEntry('minecraft/portable/notice/PortableNoticeHooks.class')) { throw 'Hooks leaked onto system class path.' }
    $hooks = $archive.GetEntry('portable-notice-hooks.jar')
    if (-not $hooks) { throw 'Nested hooks missing.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($hooks, $hooksPath, $true)
} finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($hooksPath)
try {
    Assert-Java8Classes $archive
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -like 'org/objectweb/asm/*') { throw 'ASM leaked onto bootstrap class path.' }
        if ($entry.FullName -match '/PortableNotice(Agent|Transformer)(\$|\.)') { throw 'Agent leaked onto bootstrap class path.' }
    }
} finally { $archive.Dispose() }
if ([string]::IsNullOrWhiteSpace($env:JAVA_HOME)) { throw 'JAVA_HOME must point at a JDK for the bytecode audit.' }
$javac = Join-Path $env:JAVA_HOME 'bin\javac.exe'
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'
$classPath = $JarPath + [IO.Path]::PathSeparator + $hooksPath
& $javac "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" --release 8 -Xlint:-options -encoding UTF-8 -cp $classPath -d $scratch (Join-Path $sourceRoot 'tests\TransformerAudit.java')
if ($LASTEXITCODE -ne 0) { throw 'Presentation audit did not compile.' }
& $java "-Djava.io.tmpdir=$toolTemporary" "-Duser.home=$toolProfile" -cp ($scratch + [IO.Path]::PathSeparator + $classPath) minecraft.portable.notice.TransformerAudit
if ($LASTEXITCODE -ne 0) { throw 'Presentation audit failed.' }
Write-Host 'Notice agent packaging, Java8 floor, unchanged build and bytecode contracts passed.'
} finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}
