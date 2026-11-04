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
$common = Join-Path $repositoryRoot 'Program\IdentityAdapters\Common'
# The probe's generated fixtures use a public ASM of their own. The runtime
# agent carries a private copy and must not export the loader's org.objectweb.asm.
$classPath = @($AdapterPath,
    (Join-Path $common 'lib\asm-9.8.jar'),
    (Join-Path $common 'lib\asm-tree-9.8.jar')) -join [IO.Path]::PathSeparator
& $compiler -encoding UTF-8 -classpath $classPath -d $OutputDirectory $source
if ($LASTEXITCODE -ne 0) { throw "Guest movement probe compilation failed: $LASTEXITCODE" }
& $java -Xverify:all -classpath ($OutputDirectory + [IO.Path]::PathSeparator + $classPath) GuestMovementProbe
if ($LASTEXITCODE -ne 0) { throw "Guest movement probe failed: $LASTEXITCODE" }
