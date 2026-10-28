# Verifies the production transformer against executable fixtures and selected
# installed class bytes. This tool never starts Minecraft or initializes Steam.
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetsJson,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AdapterJar,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $JavaHome) { throw 'Pass -JavaHome or set JAVA_HOME to a JDK 17 or newer.' }
$java = Join-Path $JavaHome 'bin\java.exe'
$javac = Join-Path $JavaHome 'bin\javac.exe'
foreach ($executable in @($java, $javac)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "JDK tool not found: $executable" }
}
if (-not $AdapterJar) {
    $AdapterJar = Join-Path $projectRoot 'Program\Build\IdentityAdapters\Minecraft-1.21.1-NeoForge\portable-identity-adapter.jar'
}
$AdapterJar = [IO.Path]::GetFullPath($AdapterJar)
if (-not (Test-Path -LiteralPath $AdapterJar -PathType Leaf)) { throw 'Build the current production adapter before running the probe.' }
$TargetsJson = [IO.Path]::GetFullPath($TargetsJson)
$targets = @(Get-Content -LiteralPath $TargetsJson -Raw | ConvertFrom-Json)
if ($targets.Count -eq 0) { throw 'The target list must not be empty.' }
if (-not $OutputDirectory) {
    $runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $projectRoot ('artifacts\WindowTitleProbe\' + $runName)
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory for each proof.' }
$classes = Join-Path $OutputDirectory 'classes'
[void][IO.Directory]::CreateDirectory($classes)
$classpath = $classes + [IO.Path]::PathSeparator + $AdapterJar
$fixture = Join-Path $projectRoot 'Program.Tests\Fixtures\WindowTitleTransformerProbe.java'

function Invoke-JavaTool([string]$Executable, [string[]]$ToolArguments, [string]$LogName) {
    $lines = @(& $Executable @ToolArguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $OutputDirectory $LogName), $text)
    if ($exitCode -ne 0) {
        $tail = ($lines | Select-Object -Last 20 | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "Java probe exited with $exitCode. $tail"
    }
    return $text
}

$compileArguments = @('--release', '17', '-cp', $AdapterJar, '-d', $classes, $fixture)
$null = Invoke-JavaTool $javac $compileArguments 'compile.log'
$syntheticOutput = Invoke-JavaTool $java @('-Xverify:all', '-cp', $classpath, 'WindowTitleTransformerProbe') 'synthetic.log'
if (-not $syntheticOutput.Contains('WINDOW_TITLE_SYNTHETIC_PASS')) { throw 'The simulated lifecycle proof did not report success.' }
$premainOutput = Invoke-JavaTool $java @('-Xverify:all', ('-javaagent:' + $AdapterJar),
    '-Dminecraft.portable.identity.enabled=false', '-cp', $classpath, 'WindowTitleTransformerProbe', '--premain') 'premain.log'
if (-not $premainOutput.Contains('WINDOW_TITLE_PREMAIN_PASS')) { throw 'The built title agent did not work with the identity family disabled.' }

$results = [Collections.Generic.List[object]]::new()
$failed = 0
foreach ($target in $targets) {
    $index = $results.Count
    $row = [ordered]@{ name = [string]$target.name; negativeControl = [bool]$target.negativeControl; passed = $false }
    try {
        foreach ($field in @('jarPath', 'className', 'methodName')) {
            if ([string]::IsNullOrWhiteSpace([string]$target.$field)) { throw "Target $index requires $field." }
        }
        if ($target.enabled -isnot [bool]) { throw "Target $index requires a JSON boolean enabled." }
        if ($target.enabled -and [string]::IsNullOrWhiteSpace([string]$target.title)) { throw "Target $index requires the configured title." }
        $jar = [string]$target.jarPath
        if (-not [IO.Path]::IsPathRooted($jar)) { $jar = Join-Path (Split-Path -Parent $TargetsJson) $jar }
        $jar = [IO.Path]::GetFullPath($jar)
        if (-not (Test-Path -LiteralPath $jar -PathType Leaf)) { throw "Runtime jar was not found for target $index." }
        $row.minecraftVersion = [string]$target.minecraftVersion
        $row.loaderVersion = [string]$target.loaderVersion
        $row.jarPath = $jar
        $row.jarSha256 = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash.ToLowerInvariant()
        $row.className = [string]$target.className
        $row.methodName = [string]$target.methodName
        $row.descriptor = '()Ljava/lang/String;'
        $row.enabled = [bool]$target.enabled
        $nativeArguments = @('-Xverify:all', '-cp', $classpath, 'WindowTitleTransformerProbe', $jar,
            [string]$target.className, [string]$target.methodName)
        $nativeArguments += if ($target.enabled) { [string]$target.title } else { '--disabled' }
        $nativeOutput = Invoke-JavaTool $java $nativeArguments ('native-' + $index + '.log')
        $marker = if ($target.enabled) { 'WINDOW_TITLE_NATIVE_PASS' } else { 'WINDOW_TITLE_NATIVE_DISABLED_PASS' }
        if (-not $nativeOutput.Contains($marker)) { throw "Target $index did not report the expected native proof marker." }
        $hashMatch = [regex]::Match($nativeOutput, '(?m)^NATIVE_CLASS_SHA256=([a-fA-F0-9]{64})\s*$')
        $countMatch = [regex]::Match($nativeOutput, '(?m)^CHANGED_METHOD_COUNT=(\d+)\s*$')
        $expectedCount = if ($target.enabled) { 1 } else { 0 }
        if (-not $hashMatch.Success -or -not $countMatch.Success -or [int]$countMatch.Groups[1].Value -ne $expectedCount) {
            throw "Target $index did not prove the exact method-change count and class hash."
        }
        $row.classSha256 = $hashMatch.Groups[1].Value.ToLowerInvariant()
        $row.changedMethodCount = $expectedCount
        $row.passed = $true
    }
    catch {
        $row.error = $_.Exception.Message
        $failed++
    }
    $results.Add([pscustomobject]$row)
}
$report = [ordered]@{
    schemaVersion = 1
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    adapterSha256 = (Get-FileHash -LiteralPath $AdapterJar -Algorithm SHA256).Hash.ToLowerInvariant()
    targetInputSha256 = (Get-FileHash -LiteralPath $TargetsJson -Algorithm SHA256).Hash.ToLowerInvariant()
    syntheticLifecyclePassed = $true
    builtAgentPremainPassed = $true
    proofScope = 'JVM fixtures and installed-class bytecode; no Minecraft execution'
    gameplayVerified = $false
    visualVerified = $false
    steamPeerVerified = $false
    targets = $results.ToArray()
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8))
if ($failed -ne 0) { throw "$failed native window-title proof(s) failed. See report.json." }
Write-Host ("WINDOW_TITLE_OFFLINE_PROBE_PASS targets=" + $results.Count)
Write-Host (Join-Path $OutputDirectory 'report.json')
