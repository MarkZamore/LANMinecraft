#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('2.9.3', '3.2.2', '3.3.3', '3.4.1')][string]$Version = '3.3.3',
    [string]$Repository = '',
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$Agent = '',
    [switch]$Fullscreen,
    [switch]$DownloadLegacy,
    [switch]$CompileOnly,
    [switch]$WithoutAgent
)
$ErrorActionPreference = 'Stop'
if (-not $Repository) { $Repository = Join-Path $PSScriptRoot '..\..' }
$Repository = [IO.Path]::GetFullPath($Repository)
if (-not $JavaHome) { throw 'Set JAVA_HOME or pass -JavaHome with a JDK 17 or newer.' }
$java = Join-Path $JavaHome 'bin\java.exe'
$javac = Join-Path $JavaHome 'bin\javac.exe'
if (-not (Test-Path -LiteralPath $javac)) { throw "javac was not found: $javac" }
if (-not $Agent -and -not $WithoutAgent) { $Agent = Join-Path $Repository 'Program\Build\NoticeOverlay\launcher-notice-agent.jar' }
if ($Agent) { $Agent = [IO.Path]::GetFullPath($Agent) }
if ($Agent -and -not (Test-Path -LiteralPath $Agent)) { throw "Build the notification agent first: $Agent" }

$artifacts = Join-Path $Repository '.claude\NoticeOverlayProbe'
$libraries = Join-Path $Repository 'Minecraft\Launcher\Shared\libraries'
$session = Join-Path $artifacts ('session-' + $Version + $(if ($Fullscreen) { '-fullscreen' } else { '' }))
$probeTemp = Join-Path $session 'temp'
$probeProfile = Join-Path $session 'profile'
$classes = Join-Path $artifacts ('classes-' + $Version)
$natives = Join-Path $artifacts ('natives-' + $Version)
foreach ($directory in @($session, $probeTemp, $probeProfile, $classes, $natives,
        (Join-Path $probeProfile 'AppData\Roaming'), (Join-Path $probeProfile 'AppData\Local'))) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
$previousEnvironment = @{}
$probeEnvironment = @{
    TEMP = $probeTemp; TMP = $probeTemp; USERPROFILE = $probeProfile; HOME = $probeProfile
    APPDATA = (Join-Path $probeProfile 'AppData\Roaming'); LOCALAPPDATA = (Join-Path $probeProfile 'AppData\Local')
}
foreach ($name in $probeEnvironment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $probeEnvironment[$name], 'Process')
}
try {
    $classpath = @()
    $nativeJars = @()
    $main = 'NoticeOverlayProbe'
    if ($Version -eq '2.9.3') {
        $main = 'LegacyNoticeOverlayProbe'
        $downloadDirectory = Join-Path $artifacts 'lwjgl2'
        New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
        $dependencies = @(
            @{ Name = 'lwjgl-2.9.3.jar'; Path = 'lwjgl'; SHA256 = '527d509f60132e5b2653c7fc0f8cf299d6f698f4a8013342bef47705dc57ed3f' },
            @{ Name = 'lwjgl-platform-2.9.3-natives-windows.jar'; Path = 'lwjgl-platform'; SHA256 = '7d4dd95bee064d91e1110918b0a7f63a5294e2fd9a48833a13d37d68c9c06d23' }
        )
        foreach ($dependency in $dependencies) {
            $file = Join-Path $downloadDirectory $dependency.Name
            if (-not (Test-Path -LiteralPath $file)) {
                if (-not $DownloadLegacy) { throw 'Legacy dependencies are missing; use -DownloadLegacy to fetch the pinned Maven Central artifacts.' }
                Invoke-WebRequest -Uri ('https://repo.maven.apache.org/maven2/org/lwjgl/lwjgl/{0}/2.9.3/{1}' -f $dependency.Path, $dependency.Name) -OutFile $file
            }
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $dependency.SHA256) { throw "Dependency SHA-256 mismatch: $file" }
        }
        $classpath += Join-Path $downloadDirectory $dependencies[0].Name
        $nativeJars += Join-Path $downloadDirectory $dependencies[1].Name
    } else {
        foreach ($module in @('lwjgl', 'lwjgl-glfw', 'lwjgl-opengl')) {
            $name = if ($Version -eq '3.4.1' -and $module -eq 'lwjgl') { "$module-$Version-unsafe.jar" } else { "$module-$Version.jar" }
            $classpath += Join-Path $libraries "org\lwjgl\$module\$Version\$name"
            $nativeJars += Join-Path $libraries "org\lwjgl\$module\$Version\$module-$Version-natives-windows.jar"
        }
    }
    $jnaDirectory = Join-Path $libraries 'net\java\dev\jna\jna'
    $jna = Get-ChildItem -LiteralPath $jnaDirectory -Recurse -File -Filter 'jna-*.jar' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $jna) { throw 'A cached JNA jar is required for foreground-window checks.' }
    $classpath += $jna.FullName
    foreach ($file in $classpath + $nativeJars) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Required cached dependency is missing: $file" }
    }
    & $javac "-J-Djava.io.tmpdir=$probeTemp" "-J-Duser.home=$probeProfile" "-J-XX:ErrorFile=$session/javac-crash-%p.log" --release 8 -encoding UTF-8 -cp ($classpath -join ';') -d $classes (Join-Path $PSScriptRoot ($main + '.java'))
    if ($LASTEXITCODE -ne 0) { throw 'Probe javac failed.' }
    if ($CompileOnly) { return }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($nativeJar in $nativeJars) {
        $archive = [IO.Compression.ZipFile]::OpenRead($nativeJar)
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.Name.EndsWith('.dll')) {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $natives $entry.Name), $true)
                }
            }
        } finally { $archive.Dispose() }
    }
    $arguments = @('-XX:-UsePerfData', "-XX:ErrorFile=$session/hs_err_pid%p.log",
        "-Djava.io.tmpdir=$probeTemp", "-Duser.home=$probeProfile", "-Djna.tmpdir=$probeTemp",
        "-Dorg.lwjgl.system.SharedLibraryExtractPath=$probeTemp", "-Djava.library.path=$natives",
        "-Dlanminecraft.notice.directory=$session")
    # JDK 17 accepts this flag; JDK 24+ requires it for the native LWJGL/JNA calls.
    $arguments += '--enable-native-access=ALL-UNNAMED'
    if ($Agent) {
        $sourceHash = (Get-FileHash -LiteralPath $Agent -Algorithm SHA256).Hash
        $sessionAgent = Join-Path $session ('launcher-notice-agent-' + [Guid]::NewGuid().ToString('N') + '.jar')
        Copy-Item -LiteralPath $Agent -Destination $sessionAgent
        $copyHash = (Get-FileHash -LiteralPath $sessionAgent -Algorithm SHA256).Hash
        if ($sourceHash -ne $copyHash) { throw 'The source agent changed while the probe copied it.' }
        @{
            sourceAgent = $Agent; sourceSha256 = $sourceHash; sessionAgent = $sessionAgent
            sessionSha256 = $copyHash; version = $Version; fullscreen = [bool]$Fullscreen
            startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session 'agent-source.json') -Encoding utf8
        $arguments += "-javaagent:$sessionAgent"
    }
    $arguments += @('-cp', (($classpath + @($classes)) -join ';'), $main)
    if ($Fullscreen) { $arguments += 'fullscreen' }
    & $java @arguments 2>&1 | Tee-Object -FilePath (Join-Path $session 'probe.log')
    if ($LASTEXITCODE -ne 0) { throw "Notification rendering probe failed for LWJGL $Version." }
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}
