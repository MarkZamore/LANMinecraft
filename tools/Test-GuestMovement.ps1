param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AdapterPath = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($JavaHome)) { throw 'Set JAVA_HOME to a JDK.' }
if ([string]::IsNullOrWhiteSpace($AdapterPath)) {
    $AdapterPath = Join-Path $repositoryRoot 'Program\Build\IdentityAdapters\Minecraft-1.21.1-NeoForge\portable-identity-adapter.jar'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\guest-movement-probe'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $JavaHome 'bin\javac.exe'
$java = Join-Path $JavaHome 'bin\java.exe'
$source = Join-Path $PSScriptRoot 'GuestMovementProbe\GuestMovementProbe.java'
& $compiler -encoding UTF-8 -classpath $AdapterPath -d $OutputDirectory $source
if ($LASTEXITCODE -ne 0) { throw "Guest movement probe compilation failed: $LASTEXITCODE" }
& $java -classpath "$OutputDirectory;$AdapterPath" GuestMovementProbe
if ($LASTEXITCODE -ne 0) { throw "Guest movement probe failed: $LASTEXITCODE" }
