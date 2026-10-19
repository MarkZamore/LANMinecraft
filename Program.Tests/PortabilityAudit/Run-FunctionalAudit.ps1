#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('baseline', 'after', 'smoke')][string]$Phase,
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot 'Artifacts'),
    [string]$CaptureSessionDirectory,
    [string]$TestAssemblyPath,
    [ValidateRange(0, 2147483647)][int]$ReleaseNumber = 0,
    [switch]$UseExistingProductionBuild,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$RunName,
    [switch]$PrepareOnly,
    [switch]$SkipPrepare,
    [string[]]$Groups = @('steam', 'worlds', 'reports', 'packs', 'skins', 'java17', 'java21', 'java25', 'updater')
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = [IO.Path]::GetFullPath($ArtifactsRoot)
$installName = if ($RunName) { "$Phase-functional-$RunName-install" } else { "$Phase-functional-install" }
$installRoot = Join-Path $artifacts $installName
$prepared = Join-Path $artifacts $(if ($Phase -eq 'after') { 'functional-prepared-after' } else { 'functional-prepared' })
$hostProject = Join-Path $repo 'tools/PortabilityAuditHost/PortabilityAuditHost.csproj'
$hostOutput = Join-Path $prepared 'host'
$hostExecutable = Join-Path $hostOutput 'PortabilityAuditHost.exe'
$testsProject = Join-Path $repo 'Program.Tests/Program.Tests.csproj'
New-Item -ItemType Directory -Path $prepared -Force | Out-Null

function Invoke-Dotnet([string[]]$Arguments, [string]$Name, [hashtable]$EnvironmentOverrides = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) { $start.Environment[$entry.Key] = [string]$entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    $started = [DateTimeOffset]::UtcNow
    if ($script:markers) {
        Write-Marker 'group-process-start' @{ group = $Name; processId = $process.Id }
        if ($CaptureSessionDirectory) {
            & (Join-Path $PSScriptRoot 'AuditSession.ps1') -Action Register -SessionDirectory $CaptureSessionDirectory -ProcessId $process.Id | Out-Null
        }
    }
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    $seen = [Collections.Generic.HashSet[int]]::new()
    while (-not $process.WaitForExit(200)) {
        if ($script:markers) {
            # Observe the testhost PID while it is alive; Procmon's process tree
            # is the authoritative source for descendants which finish sooner.
            $children = Get-CimInstance Win32_Process -Filter "Name = 'testhost.exe'" -ErrorAction SilentlyContinue
            foreach ($child in $children) {
                if ($child.CreationDate.ToUniversalTime() -ge $started.UtcDateTime -and $seen.Add([int]$child.ProcessId)) {
                    Write-Marker 'testhost-observed' @{ group = $Name; processId = $child.ProcessId; parentProcessId = $child.ParentProcessId }
                }
            }
        }
    }
    $output = $outputTask.GetAwaiter().GetResult()
    $errorOutput = $errorTask.GetAwaiter().GetResult()
    $directory = if ($script:markers) { $installRoot } else { $prepared }
    [IO.File]::WriteAllText((Join-Path $directory "$Name.stdout.log"), $output)
    [IO.File]::WriteAllText((Join-Path $directory "$Name.stderr.log"), $errorOutput)
    $result = [ordered]@{ group = $Name; processId = $process.Id; startedUtc = $started; exitedUtc = [DateTimeOffset]::UtcNow; processExitCode = $process.ExitCode; exitCode = $process.ExitCode; arguments = $Arguments }
    if ($script:markers) {
        $trx = Join-Path $installRoot "TestResults/$Name.trx"
        $validationError = $null
        if (Test-Path -LiteralPath $trx) {
            [xml]$document = [IO.File]::ReadAllText($trx)
            $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
            if ($counters) {
                $result['testCounts'] = [ordered]@{
                    total = [int]$counters.GetAttribute('total')
                    passed = [int]$counters.GetAttribute('passed')
                    failed = [int]$counters.GetAttribute('failed')
                    skipped = [int]$counters.GetAttribute('notExecuted')
                }
                if ((Get-Item -LiteralPath $trx).LastWriteTimeUtc -lt $started.UtcDateTime.AddSeconds(-2)) {
                    $validationError = 'TRX predates this runner process.'
                } elseif ($result.testCounts.total -lt $expectedMinimumTests[$Name]) {
                    $validationError = "Only $($result.testCounts.total) tests ran; at least $($expectedMinimumTests[$Name]) are required for $Name."
                } elseif ($result.testCounts.failed -ne 0 -or $result.testCounts.skipped -ne 0 -or $result.testCounts.passed -ne $result.testCounts.total) {
                    $validationError = 'Requested audit tests include failures, skips, or unexecuted cases.'
                }
            } else {
                $validationError = 'TRX has no test counters.'
            }
        } else {
            $validationError = 'Runner did not produce a fresh TRX result.'
        }
        if ($validationError) {
            $result['validationError'] = $validationError
            $result['exitCode'] = 1
        }
    }
    if ($script:markers) { Write-Marker 'group-exit' $result }
    if ($script:markers -and $CaptureSessionDirectory) {
        $status = if ($result.exitCode -eq 0) { 'pass' } else { 'fail' }
        & (Join-Path $PSScriptRoot 'AuditSession.ps1') -Action Mark -SessionDirectory $CaptureSessionDirectory -Scenario $Name -Status $status -Detail "Process exit code $($process.ExitCode); audit result $($result.exitCode); logs and TRX in $installRoot" | Out-Null
    }
    Write-Host "$Name exited $($process.ExitCode), audit result $($result.exitCode)"
    if ($result.exitCode -ne 0) { Write-Warning ($output + $errorOutput + $validationError) }
    return $result
}
function Write-Marker([string]$Name, [object]$Details) {
    [IO.File]::AppendAllText($script:markers,
        (([ordered]@{ utc = [DateTimeOffset]::UtcNow; processId = $PID; name = $Name; details = $Details } | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine))
}

if (-not $SkipPrepare) {
    $env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
    $properties = @('-p:SkipPortableIdentityAdapter=true')
    if ($ReleaseNumber -gt 0) { $properties += "-p:ReleaseNumber=$ReleaseNumber" }
    if ($UseExistingProductionBuild) { $properties += '-p:BuildProjectReferences=false' }
    $build = Invoke-Dotnet (@('build', $testsProject, '-c', 'Release', '--nologo') + $properties) 'prepare-tests'
    if ($build.exitCode -ne 0) { throw 'Functional audit test build failed.' }
    $publish = Invoke-Dotnet (@('publish', $hostProject, '-c', 'Release', '--nologo', '-o', $hostOutput) + $properties) 'prepare-host'
    if ($publish.exitCode -ne 0) { throw 'Functional audit host publish failed.' }
    [ordered]@{
        preparedUtc = [DateTimeOffset]::UtcNow
        releaseNumber = $ReleaseNumber
        buildProjectReferences = (-not $UseExistingProductionBuild)
        skipPortableIdentityAdapter = $true
        productionSource = 'Program/bin/Release/net10.0-windows/win-x64/LANMinecraft.dll'
        productionReferenceSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'Program/bin/Release/net10.0-windows/win-x64/LANMinecraft.dll') -Algorithm SHA256).Hash
        testAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'Program.Tests/bin/Release/net10.0-windows/win-x64/Minecraft.Tests.dll') -Algorithm SHA256).Hash
        hostSha256 = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash
        note = if ($UseExistingProductionBuild) {
            'Production references were not rebuilt. Tests and the audit host use the final precompiled production DLL and its existing adapter resources.'
        } else {
            'Production references were built with SkipPortableIdentityAdapter=true. Existing adapter resources were reused.'
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Encoding utf8
}
if ($PrepareOnly) { Write-Host "Prepared host: $hostExecutable"; return }
if (-not (Test-Path -LiteralPath $hostExecutable)) { throw 'Run -PrepareOnly once before capture.' }
$assembly = if ($TestAssemblyPath) { [IO.Path]::GetFullPath($TestAssemblyPath) } else {
    Join-Path $repo 'Program.Tests/bin/Release/net10.0-windows/win-x64/Minecraft.Tests.dll'
}
$productionAssembly = Join-Path (Split-Path -Parent $assembly) 'LANMinecraft.dll'
$preparationPath = Join-Path $prepared 'preparation.json'
if (-not (Test-Path -LiteralPath $preparationPath)) { throw 'Missing preparation manifest; run -PrepareOnly before capture.' }
$preparation = Get-Content -LiteralPath $preparationPath -Raw | ConvertFrom-Json
$testAssemblyHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
$productionAssemblyHash = (Get-FileHash -LiteralPath $productionAssembly -Algorithm SHA256).Hash
$hostHash = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash
if ($testAssemblyHash -ne $preparation.testAssemblySha256 -or
    $productionAssemblyHash -ne $preparation.productionReferenceSha256 -or
    $hostHash -ne $preparation.hostSha256) {
    throw 'Prepared test snapshot, production DLL, or updater host changed. Prepare and freeze a matching checkpoint before capture.'
}
if (Test-Path -LiteralPath $installRoot) { throw "Audit installation already exists; choose a different ArtifactsRoot: $installRoot" }
New-Item -ItemType Directory -Path $installRoot | Out-Null
$script:markers = Join-Path $installRoot 'runner-markers.jsonl'
$temporary = Join-Path $installRoot 'Temp'
New-Item -ItemType Directory -Path $temporary | Out-Null
$environment = @{
    LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT = $installRoot
    LANMINECRAFT_FUNCTIONAL_AUDIT_SOURCE_ROOT = $repo
    LANMINECRAFT_FUNCTIONAL_AUDIT_HOST = $hostExecutable
    TEMP = $temporary
    TMP = $temporary
}
$filters = [ordered]@{
    steam = 'FullyQualifiedName~SteamBootstrapTests|FullyQualifiedName~SteamRecoveryPolicyTests'
    worlds = 'FullyQualifiedName~WorldTransferHandshakeTests|FullyQualifiedName~WorldTransferLifecycleTests|FullyQualifiedName~WorldTransferArchiveTests'
    reports = 'FullyQualifiedName~BugReportServiceTests|FullyQualifiedName~BugReportCompletenessTests|FullyQualifiedName~FunctionalAuditTests.ReportProtocol_'
    packs = 'FullyQualifiedName~PortablePackSyncServiceTests|FullyQualifiedName~FunctionalAuditTests.PackSyncCancellation_'
    skins = 'FullyQualifiedName~FunctionalAuditTests.SkinProtocol_|FullyQualifiedName~SkinWithoutSteamTests|FullyQualifiedName~SkinEndpointPortTests|FullyQualifiedName~SkinWidthCeilingTests'
    java17 = 'FullyQualifiedName~FunctionalAuditTests.CachedRealJdk17_'
    java21 = 'FullyQualifiedName~FunctionalAuditTests.CachedRealJdk21_'
    java25 = 'FullyQualifiedName~FunctionalAuditTests.CachedRealJdk25_'
    updater = 'FullyQualifiedName~FunctionalAuditTests.Updater_'
    persistence = 'FullyQualifiedName~PersistenceWriteTests'
}
$expectedMinimumTests = @{ steam = 28; worlds = 18; reports = 8; packs = 38; skins = 9; java17 = 1; java21 = 1; java25 = 1; updater = 1; persistence = 5 }
$metadata = [ordered]@{
    phase = $Phase; runnerProcessId = $PID; startedUtc = [DateTimeOffset]::UtcNow
    testAssemblySha256 = $testAssemblyHash
    productionAssemblySha256 = $productionAssemblyHash
    hostSha256 = $hostHash
    preparation = $preparation
    isolation = 'Synthetic worlds/reports/skins, in-memory peer transport and emulated Steam API. Real local cached JDK archives. No account-to-account Valve connection.'
    results = @()
}
Write-Marker 'audit-start' $metadata
foreach ($group in $Groups) {
    if (-not $filters.Contains($group)) { throw "Unknown audit group: $group" }
    Write-Marker 'group-start' @{ group = $group; filter = $filters[$group] }
    if ($CaptureSessionDirectory) {
        & (Join-Path $PSScriptRoot 'AuditSession.ps1') -Action Mark -SessionDirectory $CaptureSessionDirectory -Scenario $group -Status begin -Detail "Isolated production-service scenario group $group" | Out-Null
    }
    $groupEnvironment = $environment.Clone()
    if ($group.StartsWith('java', [StringComparison]::Ordinal)) {
        # The JDK tests already use explicit AppPaths below the audit root.
        # Preserve the caller's real temporary environment so the baseline can
        # reveal whether production Java helpers redirect their own TEMP/TMP.
        $groupEnvironment.Remove('TEMP')
        $groupEnvironment.Remove('TMP')
    }
    if ($TestAssemblyPath) {
        $arguments = @('vstest', $assembly, "/TestCaseFilter:$($filters[$group])",
            "/ResultsDirectory:$(Join-Path $installRoot 'TestResults')", "/Logger:trx;LogFileName=$group.trx")
    } else {
        $arguments = @('test', $testsProject, '-c', 'Release', '--no-build', '--no-restore', '--filter', $filters[$group],
            '--results-directory', (Join-Path $installRoot 'TestResults'), '--logger', "trx;LogFileName=$group.trx", '--nologo')
    }
    $result = Invoke-Dotnet $arguments $group $groupEnvironment
    $metadata.results += $result
}
$metadata['exitedUtc'] = [DateTimeOffset]::UtcNow
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $installRoot 'functional-results.json') -Encoding utf8
Write-Marker 'audit-end' @{ results = $metadata.results }
if ($metadata.results.Where({ $_.exitCode -ne 0 }).Count -gt 0) { throw "Some scenarios failed; see $installRoot" }
Write-Host "Functional audit complete: $installRoot"
