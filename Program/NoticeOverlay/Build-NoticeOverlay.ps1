param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
try { Add-Type -AssemblyName System.IO.Compression.FileSystem } catch { }

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
$buildRoot = Join-Path $buildBase 'NoticeOverlay'
$destination = [System.IO.Path]::GetFullPath($OutputPath)
$buildPrefix = $buildBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($buildPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Notice overlay output must remain under Program/Build: $destination"
}
$manifest = Join-Path $sourceRoot 'MANIFEST.MF'
$libRoot = Join-Path $programRoot 'IdentityAdapters\Common\lib'
$license = Join-Path $libRoot 'LICENSE.asm.txt'
$artifacts = @(
    [pscustomobject]@{
        Name = 'asm-9.8.jar'; Length = 126113
        Sha256 = '876eab6a83daecad5ca67eb9fcabb063c97b5aeb8cf1fca7a989ecde17522051'
    },
    [pscustomobject]@{
        Name = 'asm-tree-9.8.jar'; Length = 51934
        Sha256 = '14b7880cb7c85eed101e2710432fc3ffb83275532a6a894dc4c4095d49ad59f1'
    }
)
$asmJars = @(foreach ($artifact in $artifacts) {
    $path = Join-Path $libRoot $artifact.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        [System.IO.FileInfo]::new($path).Length -ne $artifact.Length -or
        (Get-Sha256 $path) -ne $artifact.Sha256) {
        throw "Vendored ASM does not match its pinned bytes: $($artifact.Name)"
    }
    $path
})
foreach ($required in @($manifest, $license)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file missing: $required" }
}
$javaFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src') -Recurse -File -Filter '*.java' |
    Sort-Object FullName | ForEach-Object FullName)
if ($javaFiles.Count -eq 0) { throw 'Notice overlay Java sources were not found.' }

# A real unchanged build leaves the existing JAR untouched. In particular,
# rebuilding the WPF project must not invalidate every installed agent cache.
$inputs = @($PSCommandPath, $manifest, $license) + $asmJars + $javaFiles
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
            Write-Host "Notice overlay unchanged: $destination"
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
$hooks = Join-Path $stageRoot 'hooks'
$asm = Join-Path $stageRoot 'asm'
$temporaryJar = Join-Path $stageRoot 'launcher-notice-overlay.jar'
$backupJar = Join-Path $stageRoot 'previous-launcher-notice-overlay.jar'
$toolTemporary = Join-Path $stageRoot 'temp'
$toolProfile = Join-Path $stageRoot 'profile'
$toolRoaming = Join-Path $toolProfile 'AppData\Roaming'
$toolLocal = Join-Path $toolProfile 'AppData\Local'
$originalEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    New-Item -ItemType Directory -Path $classes, $hooks, $asm, $toolTemporary, $toolRoaming, $toolLocal, (Split-Path -Parent $destination) -Force | Out-Null
    [Environment]::SetEnvironmentVariable('TEMP', $toolTemporary, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $toolTemporary, 'Process')
    [Environment]::SetEnvironmentVariable('USERPROFILE', $toolProfile, 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $toolRoaming, 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $toolLocal, 'Process')
    & $javac "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" --release 8 -Xlint:-options -encoding UTF-8 -cp ($asmJars -join [System.IO.Path]::PathSeparator) -d $classes @javaFiles
    if ($LASTEXITCODE -ne 0) { throw "Notice overlay javac failed with exit code $LASTEXITCODE." }

    foreach ($asmJar in $asmJars) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($asmJar)
        try {
            foreach ($entry in $archive.Entries) {
                # Keep ASM out of the bootstrap hooks and skip module-info.
                if ($entry.FullName -notlike 'org/objectweb/asm/*' -or -not $entry.FullName.EndsWith('.class')) { continue }
                $target = Join-Path $asm ($entry.FullName -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally { $archive.Dispose() }
    }
    $licenseTarget = Join-Path $asm 'META-INF\LICENSE.asm.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $licenseTarget) -Force | Out-Null
    Copy-Item -LiteralPath $license -Destination $licenseTarget

    $package = 'minecraft\portable\notice'
    $compiledPackage = Join-Path $classes $package
    $hookPackage = Join-Path $hooks $package
    New-Item -ItemType Directory -Path $hookPackage -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $compiledPackage -File -Filter '*.class') {
        if ($file.BaseName -match '^PortableNotice(Agent|Transformer)(\$|$)') { continue }
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $hookPackage $file.Name)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $hookPackage 'PortableNoticeHooks.class') -PathType Leaf)) {
        throw 'PortableNoticeHooks was not compiled.'
    }
    & $jar "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" cf (Join-Path $classes 'portable-notice-hooks.jar') -C $hooks .
    if ($LASTEXITCODE -ne 0) { throw "Notice hooks jar failed with exit code $LASTEXITCODE." }
    & $jar "-J-Djava.io.tmpdir=$toolTemporary" "-J-Duser.home=$toolProfile" cfm $temporaryJar $manifest -C $classes . -C $asm .
    if ($LASTEXITCODE -ne 0) { throw "Notice agent jar failed with exit code $LASTEXITCODE." }

    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
    } else {
        [System.IO.File]::Move($temporaryJar, $destination)
    }
    @{ InputHash = $inputHash; JarHash = Get-Sha256 $destination } | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Host "Notice overlay: $destination"
} finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
    # stageRoot is the exact generated child, never a path accepted from input.
    $stagePrefix = [System.IO.Path]::GetFullPath($buildRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $stageRoot.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the notice build directory: $stageRoot"
    }
    if (Test-Path -LiteralPath $stageRoot -PathType Container) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
