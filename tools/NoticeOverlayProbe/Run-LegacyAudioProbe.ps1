#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LwjglJar,
    [Parameter(Mandatory)][string]$NativeJar,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$JavaHome
)
$ErrorActionPreference = 'Stop'
$LwjglJar = [IO.Path]::GetFullPath($LwjglJar)
$NativeJar = [IO.Path]::GetFullPath($NativeJar)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$classes = Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $classes | Out-Null
& (Join-Path $JavaHome 'bin/javac.exe') --release 8 -encoding UTF-8 -cp $LwjglJar -d $classes (Join-Path $PSScriptRoot 'LegacyAudioProbe.java')
if ($LASTEXITCODE -ne 0) { throw 'Audio probe compilation failed.' }
foreach ($variant in @('ascii', 'проверка-звука', 'path with spaces', ('long-' + ('folder-' * 20)))) {
    $natives = Join-Path $OutputDirectory $variant
    New-Item -ItemType Directory -Force -Path $natives | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($NativeJar)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.Name.EndsWith('.dll')) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $natives $entry.Name), $true)
            }
        }
    } finally { $archive.Dispose() }
    & (Join-Path $JavaHome 'bin/java.exe') '-XX:-UsePerfData' "-Djava.io.tmpdir=$OutputDirectory" "-Duser.home=$OutputDirectory" "-Djava.library.path=$natives" '-Dorg.lwjgl.util.Debug=true' -cp "$classes;$LwjglJar" LegacyAudioProbe 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "$variant.log")
    if ($LASTEXITCODE -ne 0) { Write-Warning "Audio probe failed for $variant (exit $LASTEXITCODE)." }
}
