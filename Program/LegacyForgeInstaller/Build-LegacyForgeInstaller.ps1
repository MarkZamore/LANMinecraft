param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$programRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $programRoot 'Build\LegacyForgeInstaller'))
$destination = [IO.Path]::GetFullPath($OutputPath)
$buildPrefix = $buildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Legacy Forge installer output must remain under Program/Build/LegacyForgeInstaller.'
}

$compiler = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\javac.exe' }
if (-not $compiler -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = (Get-Command javac.exe -ErrorAction SilentlyContinue).Source
}
if (-not $compiler) { throw 'Set JAVA_HOME to a JDK that supports --release 8.' }

$stage = [IO.Path]::GetFullPath((Join-Path $buildRoot ([Guid]::NewGuid().ToString('N'))))
if (-not $stage.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Legacy Forge installer staging directory escaped its build directory.'
}
try {
    [void][IO.Directory]::CreateDirectory($stage)
    $source = Join-Path $PSScriptRoot 'LegacyForgeClientInstaller.java'
    & $compiler "-J-Djava.io.tmpdir=$stage" "-J-Duser.home=$stage" '-J-XX:-UsePerfData' --release 8 -Xlint:-options -encoding UTF-8 -d $stage $source
    if ($LASTEXITCODE -ne 0) { throw "Legacy Forge installer javac failed with exit code $LASTEXITCODE." }
    $class = Join-Path $stage 'LegacyForgeClientInstaller.class'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [IO.File]::Replace($class, $destination, (Join-Path $stage 'previous.class'))
    } else {
        [IO.File]::Move($class, $destination)
    }
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
