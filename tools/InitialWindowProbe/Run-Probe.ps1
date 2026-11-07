#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LibraryRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:\+legacyfabric\.[0-9]+)?$')][string]$Version,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$JavaExecutable = '',
    [ValidateRange(8,99)][int]$JavaMajorVersion = 25,
    [string]$IconArchive = ''
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$LibraryRoot = [IO.Path]::GetFullPath($LibraryRoot)
$output = Join-Path $repository ('artifacts/agent-runs/initial-window-probe/' + $Version)
$agent = Join-Path $repository 'Program/Build/NoticeOverlay/launcher-notice-agent.jar'
if (-not $IconArchive) { $IconArchive = Join-Path $repository 'Program/MinecraftWindowIconAssets.jar' }
$IconArchive = [IO.Path]::GetFullPath($IconArchive)
$java = if ($JavaExecutable) { $JavaExecutable } else { Join-Path $JavaHome 'bin/java.exe' }
$javac = Join-Path $JavaHome 'bin/javac.exe'
$legacy = $Version.StartsWith('2.')
$main = if ($legacy) { 'LegacyInitialWindow' } else { 'GlfwInitialWindow' }
$libraries = @(if ($legacy) {
    @((Join-Path $LibraryRoot "org/lwjgl/lwjgl/lwjgl/$Version/lwjgl-$Version.jar"))
} else {
    @((Join-Path $LibraryRoot "org/lwjgl/lwjgl/$Version/lwjgl-$Version.jar"),
      (Join-Path $LibraryRoot "org/lwjgl/lwjgl-glfw/$Version/lwjgl-glfw-$Version.jar"))
})
if (-not $legacy -and -not (Test-Path -LiteralPath $libraries[0])) {
    $libraries[0] = Join-Path $LibraryRoot "org/lwjgl/lwjgl/$Version/lwjgl-$Version-unsafe.jar"
}
$nativeJars = @(if ($legacy) {
    @((Join-Path $LibraryRoot "org/lwjgl/lwjgl/lwjgl-platform/$Version/lwjgl-platform-$Version-natives-windows.jar"))
} else {
    @((Join-Path $LibraryRoot "org/lwjgl/lwjgl/$Version/lwjgl-$Version-natives-windows.jar"),
      (Join-Path $LibraryRoot "org/lwjgl/lwjgl-glfw/$Version/lwjgl-glfw-$Version-natives-windows.jar"))
})
$jna = Join-Path $LibraryRoot 'net/java/dev/jna/jna/5.17.0/jna-5.17.0.jar'
$libraries += $jna
$inputs = @($libraries + $nativeJars + @($agent, $IconArchive))
$before = @{}
foreach ($file in $inputs) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required prepared input missing: $file" }
    $before[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
@{ owner='tools/InitialWindowProbe'; purpose='Native first-window title and icon check without Minecraft';
   createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); expectedBytes=16777216;
   deleteAfterUtc=[DateTimeOffset]::UtcNow.AddDays(1).ToString('O');
   requiredResults=@('probe.log','receipt.json') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'RUN.json')
$classes = Join-Path $output 'classes'
$temp = Join-Path $output 'temp'
$profile = Join-Path $output 'profile'
$natives = Join-Path $output 'natives'
foreach ($directory in @($classes,$temp,$profile,$natives)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
function Run-Tool([string]$Executable,[string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.WorkingDirectory=$output
    foreach ($pair in @{ TEMP=$temp; TMP=$temp; USERPROFILE=$profile; APPDATA=(Join-Path $profile 'AppData/Roaming');
                        LOCALAPPDATA=(Join-Path $profile 'AppData/Local') }.GetEnumerator()) {
        [IO.Directory]::CreateDirectory($pair.Value) | Out-Null
        $start.Environment[$pair.Key]=$pair.Value
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) { $process.Kill($true); $process.WaitForExit(); throw 'Owned window probe timed out.' }
        $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        Add-Content -LiteralPath (Join-Path $output 'probe.log') -Value $text
        if ($process.ExitCode -ne 0) { throw $text }
        return $text
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
try {
    Set-Content -LiteralPath (Join-Path $output 'probe.log') -Value ''
    $classpath=$libraries -join ';'
    Run-Tool $javac @("-J-Djava.io.tmpdir=$temp", '--release','8','-encoding','UTF-8','-cp',$classpath,'-d',$classes,
        (Join-Path $PSScriptRoot 'WindowReceipt.java'),(Join-Path $PSScriptRoot ($main+'.java'))) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($file in $nativeJars) {
        $archive=[IO.Compression.ZipFile]::OpenRead($file)
        try { foreach ($entry in $archive.Entries) {
            if ($entry.Name.EndsWith('.dll')) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $natives $entry.Name),$true) }
        } } finally { $archive.Dispose() }
    }
    $nativeAccess = if ($JavaMajorVersion -ge 17) { @('--enable-native-access=ALL-UNNAMED') } else { @() }
    $text = Run-Tool $java (@('-Xverify:all') + $nativeAccess + @(
        "-Djava.io.tmpdir=$temp","-Duser.home=$profile","-Djna.tmpdir=$temp","-Djava.library.path=$natives",
        "-Dorg.lwjgl.system.SharedLibraryExtractPath=$temp","-Dlanminecraft.notice.directory=$output",
        "-Dlanminecraft.window.title=Initial window $Version","-Dlanminecraft.window.icons=$IconArchive","-javaagent:$agent",
        '-cp',($classpath+';'+$classes),$main))
    if (@($text -split "`n" | Where-Object { $_ -match '^NATIVE_WINDOW_PASS\|' }).Count -ne 2 -or
        $text.Contains('[LauncherWindow] Initial window presentation failed') -or $text.Contains('Window creation hook failed')) {
        throw 'The native window probe did not produce two valid receipts.'
    }
    foreach ($file in $inputs) {
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $before[$file]) { throw "Prepared source changed: $file" }
    }
    @{ version=$Version; java=$java; javaMajorVersion=$JavaMajorVersion; inputSha256=$before; result='PASS';
       receipts=@($text -split "`n" | Where-Object { $_ -match '^NATIVE_WINDOW_PASS\|' }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'receipt.json')
    Write-Host "PASS native window $Version"
} finally {
    foreach ($directory in @($classes,$temp,$profile,$natives)) {
        $resolved=[IO.Path]::GetFullPath($directory)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath($output)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Probe cleanup escaped its owned directory.'
        }
        if (Test-Path -LiteralPath $resolved) {
            if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
                throw 'Probe cleanup encountered a filesystem link.'
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
