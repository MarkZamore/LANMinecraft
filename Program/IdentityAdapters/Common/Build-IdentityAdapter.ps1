param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    [string]$AdapterName,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
try { Add-Type -AssemblyName System.IO.Compression.FileSystem } catch { }

# Hashing through .NET keeps this working where Get-FileHash is unavailable,
# which is the case on the build agent.
function Get-Sha256([string]$path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $algorithm.Dispose()
    }
}
$commonRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath))
$adaptersRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $commonRoot))
$programRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $adaptersRoot))
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $programRoot))
$adaptersPrefix = $adaptersRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$adapterRoot = [System.IO.Path]::GetFullPath((Join-Path $adaptersRoot $AdapterName))
if ($AdapterName -eq "Common" -or
    -not $adapterRoot.StartsWith($adaptersPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Identity adapter name escapes the adapters directory: $AdapterName"
}
if (-not (Test-Path -LiteralPath $adapterRoot -PathType Container)) {
    throw "Identity adapter source directory was not found: $adapterRoot"
}

$manifest = Join-Path $commonRoot "MANIFEST.MF"
$libRoot = Join-Path $commonRoot "lib"
$asmLicense = Join-Path $libRoot "LICENSE.asm.txt"
# The transformers run on the bootstrap class loader, which cannot reach the
# module-path ASM that NeoForge loads, so ASM is merged into the agent jar.
# JDK 25 removed jdk.internal.org.objectweb.asm, hence the real library.
$asmArtifacts = @(
    [pscustomobject]@{
        Name   = "asm-9.8.jar"
        Length = 126113
        Sha256 = "876eab6a83daecad5ca67eb9fcabb063c97b5aeb8cf1fca7a989ecde17522051"
    },
    [pscustomobject]@{
        Name   = "asm-tree-9.8.jar"
        Length = 51934
        Sha256 = "14b7880cb7c85eed101e2710432fc3ffb83275532a6a894dc4c4095d49ad59f1"
    }
)
$buildRoot = Join-Path $programRoot "Build\IdentityAdapters\$AdapterName"
$stageRoot = Join-Path $buildRoot ("stage-" + [Environment]::ProcessId + "-" + [Guid]::NewGuid().ToString("N"))
$classes = Join-Path $stageRoot "classes"
$asmClasses = Join-Path $stageRoot "asm"
$temporaryJar = Join-Path $stageRoot "portable-identity-adapter.jar"
$backupJar = Join-Path $stageRoot "previous-portable-identity-adapter.jar"

function Find-JavaTool([string]$name) {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidate = Join-Path $env:JAVA_HOME "bin\$name.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    $command = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $runtimeRoot = Join-Path $projectRoot "Minecraft\Launcher\Runtimes"
    if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Filter "$name.exe" |
            Where-Object { $_.FullName -match '\\runtime\\' } |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "$name.exe was not found. Install JDK 21 or newer (JAVA_HOME or PATH), or prepare a local pack runtime first."
}

foreach ($requiredFile in @(
    (Join-Path $commonRoot "PortableIdentityAgent.java"),
    (Join-Path $commonRoot "PortableIdentityReflection.java"),
    (Join-Path $commonRoot "PortableFtbTeleportTransformer.java"),
    (Join-Path $commonRoot "PortableLanAutoPublishHooks.java"),
    (Join-Path $commonRoot "PortableLanAutoPublishTransformer.java"),
    (Join-Path $commonRoot "PortableLanTitleHooks.java"),
    (Join-Path $commonRoot "PortableSolarFluxSyncTransformer.java"),
    (Join-Path $commonRoot "PortableLanTitleTransformer.java"),
    (Join-Path $commonRoot "PortableXaeroWaypointHooks.java"),
    (Join-Path $commonRoot "PortableXaeroWaypointTransformer.java"),
    (Join-Path $adapterRoot "PortableIdentityHooks.java"),
    (Join-Path $adapterRoot "PortableIdentityTransformer.java"),
    $manifest,
    $asmLicense
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required identity adapter file was not found: $requiredFile"
    }
}

$asmJars = foreach ($artifact in $asmArtifacts) {
    $path = Join-Path $libRoot $artifact.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Vendored ASM library was not found: $path"
    }
    $length = [System.IO.FileInfo]::new($path).Length
    $hash = Get-Sha256 $path
    if ($length -ne $artifact.Length -or $hash -ne $artifact.Sha256) {
        throw "Vendored ASM library does not match its pinned bytes: $($artifact.Name)"
    }
    $path
}

$javac = Find-JavaTool "javac"
$jar = Find-JavaTool "jar"
$javaFiles = @(
    Get-ChildItem -LiteralPath $commonRoot -Recurse -File -Filter "*.java"
    Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter "*.java"
) | Sort-Object FullName | ForEach-Object FullName
if ($javaFiles.Count -eq 0) { throw "Identity adapter Java sources were not found." }

try {
    New-Item -ItemType Directory -Path $classes -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

    # --release (not -source/-target) keeps a JDK 25 javac quiet while still
    # emitting class files that load on Java 21 and later.
    & $javac `
        --release 21 `
        -cp ($asmJars -join [System.IO.Path]::PathSeparator) `
        -d $classes `
        @javaFiles
    if ($LASTEXITCODE -ne 0) { throw "Identity adapter javac failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Path $asmClasses -Force | Out-Null
    foreach ($asmJar in $asmJars) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($asmJar)
        try {
            foreach ($entry in $archive.Entries) {
                # module-info.class and META-INF/MANIFEST.MF would collide with
                # the agent's own manifest and turn the jar into a module.
                if ($entry.FullName -notlike "org/objectweb/asm/*" -or
                    -not $entry.FullName.EndsWith(".class")) {
                    continue
                }
                $target = Join-Path $asmClasses ($entry.FullName -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally {
            $archive.Dispose()
        }
    }
    $licenseTarget = Join-Path $asmClasses "META-INF\LICENSE.asm.txt"
    New-Item -ItemType Directory -Path (Split-Path -Parent $licenseTarget) -Force | Out-Null
    Copy-Item -LiteralPath $asmLicense -Destination $licenseTarget -Force

    & $jar cfm $temporaryJar $manifest -C $classes . -C $asmClasses .
    if ($LASTEXITCODE -ne 0) { throw "Identity adapter jar failed with exit code $LASTEXITCODE." }

    $destination = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
        Remove-Item -LiteralPath $backupJar -Force -ErrorAction SilentlyContinue
    } else {
        try {
            [System.IO.File]::Move($temporaryJar, $destination)
        } catch [System.IO.IOException] {
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw }
            [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
            Remove-Item -LiteralPath $backupJar -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Identity adapter ${AdapterName}: $OutputPath"
} finally {
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
