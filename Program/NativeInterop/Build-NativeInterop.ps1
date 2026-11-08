param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        } finally { $stream.Dispose() }
    } finally { $algorithm.Dispose() }
}

$sourceRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath))
$programRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $sourceRoot))
$buildBase = [System.IO.Path]::GetFullPath((Join-Path $programRoot 'Build'))
$buildRoot = Join-Path $buildBase 'NativeInterop'
$destination = [System.IO.Path]::GetFullPath($OutputPath)
$buildPrefix = $buildBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($buildPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Native interoperability output must remain under Program/Build: $destination"
}
$manifest = Join-Path $sourceRoot 'MANIFEST.MF'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Native interoperability manifest was not found.' }
$javaFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src') -Recurse -File -Filter '*.java' |
    Sort-Object FullName | ForEach-Object FullName)
if ($javaFiles.Count -eq 0) { throw 'Native interoperability Java sources were not found.' }

# Repeated WPF builds retain the exact existing runtime JAR and cache identity.
$inputs = @($PSCommandPath, $manifest) + $javaFiles
$inputDescription = ($inputs | ForEach-Object { $_.Substring($programRoot.Length) + ':' + (Get-Sha256 $_) }) -join "`n"
$inputHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $inputHash = ([System.BitConverter]::ToString($inputHashAlgorithm.ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($inputDescription))) -replace '-', '').ToLowerInvariant()
} finally { $inputHashAlgorithm.Dispose() }
$statePath = $destination + '.build.json'
if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    try {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.InputHash -eq $inputHash -and $state.JarHash -eq (Get-Sha256 $destination)) {
            Write-Host "Native interoperability unchanged: $destination"
            return
        }
    } catch { }
}

function Find-JavaTool([string]$name) {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidate = Join-Path $env:JAVA_HOME "bin\$name.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $command = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "$name.exe was not found. Set JAVA_HOME to a JDK supporting --release 8."
}
$javac = Find-JavaTool 'javac'
$jar = Find-JavaTool 'jar'
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $buildRoot ('stage-' + [Guid]::NewGuid().ToString('N'))))
$classes = Join-Path $stageRoot 'classes'
$temporaryJar = Join-Path $stageRoot 'launcher-native-interop-agent.jar'
$backupJar = Join-Path $stageRoot 'previous-launcher-native-interop-agent.jar'
$toolTemporary = Join-Path $stageRoot 'temp'
$toolProfile = Join-Path $stageRoot 'profile'
$toolRoaming = Join-Path $toolProfile 'AppData\Roaming'
$toolLocal = Join-Path $toolProfile 'AppData\Local'
$originalEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    New-Item -ItemType Directory -Path $classes, $toolTemporary, $toolRoaming, $toolLocal, (Split-Path -Parent $destination) -Force | Out-Null
    [Environment]::SetEnvironmentVariable('TEMP', $toolTemporary, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $toolTemporary, 'Process')
    [Environment]::SetEnvironmentVariable('USERPROFILE', $toolProfile, 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $toolRoaming, 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $toolLocal, 'Process')
    & $javac "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" --release 8 -Xlint:-options -encoding UTF-8 -d $classes @javaFiles
    if ($LASTEXITCODE -ne 0) { throw "Native interoperability javac failed with exit code $LASTEXITCODE." }
    # A fixed ZIP timestamp makes a clean rebuild reproduce the same agent bytes.
    & $jar "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" --create --date=2026-01-01T00:00:00Z --file $temporaryJar --manifest $manifest -C $classes .
    if ($LASTEXITCODE -ne 0) { throw "Native interoperability jar failed with exit code $LASTEXITCODE." }
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
    } else {
        [System.IO.File]::Move($temporaryJar, $destination)
    }
    @{ InputHash = $inputHash; JarHash = Get-Sha256 $destination } | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Host "Native interoperability: $destination"
} finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
    $stagePrefix = [System.IO.Path]::GetFullPath($buildRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $stageRoot.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the native interoperability build directory: $stageRoot"
    }
    if (Test-Path -LiteralPath $stageRoot -PathType Container) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
