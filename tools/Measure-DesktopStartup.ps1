#requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'Measure')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Publish')][switch]$PublishProbe,
    [Parameter(Mandatory, ParameterSetName = 'Publish')][string]$OutputDirectory,
    [Parameter(ParameterSetName = 'Publish')][switch]$SelfContained,
    [Parameter(Mandatory, ParameterSetName = 'Measure')][string]$BaselineExecutable,
    [Parameter(Mandatory, ParameterSetName = 'Measure')][string]$CandidateExecutable,
    [Parameter(ParameterSetName = 'Measure')][string]$BaselineApplication,
    [Parameter(ParameterSetName = 'Measure')][string]$CandidateApplication,
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(1,100)][int]$WarmRuns = 20,
    [Parameter(ParameterSetName = 'Measure')][ValidateSet('Cold','Ready','Uncontrolled')][string]$FirstLaunchState = 'Uncontrolled',
    [Parameter(ParameterSetName = 'Measure')][switch]$FirstLaunchOnly,
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(30,600)][int]$TimeoutSeconds = 120,
    [Parameter(ParameterSetName = 'Measure')][switch]$FailOnRegression,
    [string]$RunDirectory = '',
    [string]$Owner = 'desktop-startup-perf'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ownedBoundary = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/agent-runs/desktop-startup-perf'))
if (!$RunDirectory) { $RunDirectory = $ownedBoundary }
$runRoot = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\','/')
if ($runRoot -ne $ownedBoundary -and !$runRoot.StartsWith($ownedBoundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Probe results and payloads must stay inside artifacts/agent-runs/desktop-startup-perf.'
}

function Assert-OrdinaryPath([string]$Path) {
    for ($item = [IO.DirectoryInfo]::new($Path); $null -ne $item; $item = $item.Parent) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('A probe path traverses a link: ' + $item.FullName) }
    }
}
function Assert-Budget {
    $storageJson = & python (Join-Path $repository 'tools/workspace_storage.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'Workspace storage check failed.' }
    $storage = ($storageJson -join [Environment]::NewLine) | ConvertFrom-Json
    if (!$storage.complete -or !$storage.within_budget) { throw 'The global workspace storage budget is unavailable or exceeded.' }
    Write-Host ('Workspace temporary data: {0:N3} GiB / {1} GiB.' -f $storage.unique_gib, $storage.budget_gib)
    $bytes = (Get-ChildItem -LiteralPath $ownedBoundary -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($bytes -gt 2GB) { throw 'The desktop startup probe exceeded its coordinated 2 GiB comparison budget.' }
}
Assert-OrdinaryPath $runRoot
Assert-Budget
[void][IO.Directory]::CreateDirectory($runRoot)
$manifest = Join-Path $ownedBoundary 'RUN.json'
if (!(Test-Path -LiteralPath $manifest)) {
    [ordered]@{
        owner = $Owner
        purpose = 'Compare isolated actual WPF shell first frame and UI dispatch through old self-contained and new external-runtime native hosts.'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        expectedBytes = 2GB
        deleteAfterUtc = [DateTimeOffset]::UtcNow.AddDays(2).ToString('o')
        necessaryResults = @('summary.json', 'summary.md', 'individual compact JSON measurements and package provenance')
        exclusions = @('No user process control', 'No Application.Run or launcher service startup', 'No personal data', 'No OS cache flushing')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
}

if ($PublishProbe) {
    $publish = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
    if (!$publish.StartsWith($ownedBoundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published probe payloads must stay inside the owned probe directory.'
    }
    Assert-OrdinaryPath $publish
    $project = Join-Path $PSScriptRoot 'DesktopStartupProbe/DesktopStartupProbe.csproj'
    try {
        & dotnet publish $project -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() `
            --disable-build-servers -m:1 -nr:false -o $publish -p:RuntimeFrameworkVersion=10.0.14 -p:TargetLatestRuntimePatch=false
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the isolated startup probe failed.' }
        $config = Get-Content -LiteralPath (Join-Path $publish 'DesktopStartupProbe.runtimeconfig.json') -Raw | ConvertFrom-Json
        $frameworks = if ($SelfContained) { @($config.runtimeOptions.includedFrameworks) } else { @($config.runtimeOptions.frameworks) }
        foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
            $framework = @($frameworks | Where-Object name -eq $name)
            if ($framework.Count -ne 1 -or $framework[0].version -ne '10.0.14') { throw ('The probe runtime is not pinned to 10.0.14: ' + $name) }
        }
        [ordered]@{
            schemaVersion = 1; runtimeVersion = '10.0.14'; selfContained = $SelfContained.IsPresent
            project = 'tools/DesktopStartupProbe/DesktopStartupProbe.csproj'
            probeSha256 = (Get-FileHash -LiteralPath (Join-Path $publish 'DesktopStartupProbe.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
            command = 'dotnet publish -c Release -r win-x64 --self-contained ' + $SelfContained.IsPresent.ToString().ToLowerInvariant() + ' -p:RuntimeFrameworkVersion=10.0.14 -p:TargetLatestRuntimePatch=false'
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot ('probe-publish-' + $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }) + '.json')) -Encoding utf8
    }
    finally { Assert-Budget }
    return
}

$baseline = [IO.Path]::GetFullPath($BaselineExecutable)
$candidate = [IO.Path]::GetFullPath($CandidateExecutable)
foreach ($path in @($baseline, $candidate)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('A packaged probe executable is missing: ' + $path) }
    if (!$path.StartsWith($ownedBoundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Only explicitly packaged probes inside the owned directory may be launched; never point this tool at the user launcher.'
    }
    Assert-OrdinaryPath $path
    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $proof = @(Get-ChildItem -LiteralPath $ownedBoundary -Filter host-build.json -File -Recurse | ForEach-Object {
        $record = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($record.Executable -eq $path -and $record.ExecutableSha256 -eq $digest -and
            $record.EntryAssembly -eq 'DesktopStartupProbe.dll' -and
            @($record.FileRecords | Where-Object Path -eq 'DesktopStartupProbe.dll').Count -eq 1) { $record }
    })
    if ($proof.Count -ne 1) {
        throw ('The executable has no unique matching native build proof selecting DesktopStartupProbe.dll; refusing to launch it: ' + $path)
    }
}
$results = [Collections.Generic.List[object]]::new()
function Invoke-Probe([string]$Label, [string]$Executable, [string]$Application, [string]$Phase, [int]$Iteration) {
    $resultPath = Join-Path $runRoot ('{0}-{1}-{2:00}.json' -f $Label, $Phase, $Iteration)
    if (Test-Path -LiteralPath $resultPath) { throw ('This run already has a measurement. Use a new result subdirectory: ' + $resultPath) }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardOutput = $true
    foreach ($argument in @('--output', $resultPath, '--label', $Label, '--state-directory', (Join-Path $runRoot ($Label + '-ui-fixture')))) { $start.ArgumentList.Add($argument) }
    if ($Application) {
        if (![IO.Path]::IsPathFullyQualified($Application)) { throw 'An application override must be an absolute path.' }
        $start.ArgumentList.Add('--application'); $start.ArgumentList.Add([IO.Path]::GetFullPath($Application))
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        if (!$process.Start()) { throw ('The isolated ' + $Label + ' process did not start.') }
        $started = $true
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            # This Process object was created above and belongs only to this run.
            $process.Kill($true); [void]$process.WaitForExit(5000)
            throw ('The isolated ' + $Label + ' probe timed out.')
        }
        $stderr = $errorOutput.GetAwaiter().GetResult()
        $stdout = $standardOutput.GetAwaiter().GetResult()
        if ($stderr.Length -gt 16384) { $stderr = $stderr.Substring(0,16384) }
        if ($stdout.Length -gt 16384) { $stdout = $stdout.Substring(0,16384) }
        if (!(Test-Path -LiteralPath $resultPath)) {
            [ordered]@{ processId=$process.Id; exitCode=$process.ExitCode; stderr=$stderr; stdout=$stdout } |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($resultPath + '.stdio.json') -Encoding utf8
            throw ('The probe did not write its JSON result; exit code ' + $process.ExitCode + '. See ' + $resultPath + '.stdio.json')
        }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $result | Add-Member -NotePropertyName phase -NotePropertyValue $Phase
        $result | Add-Member -NotePropertyName iteration -NotePropertyValue $Iteration
        $result | Add-Member -NotePropertyName firstLaunchState -NotePropertyValue $FirstLaunchState
        $result | Add-Member -NotePropertyName outerElapsedMs -NotePropertyValue $clock.Elapsed.TotalMilliseconds
        $result | Add-Member -NotePropertyName executableSha256 -NotePropertyValue (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($result.processId -ne $process.Id) { throw 'The packaged native host did not execute the probe in its own process.' }
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
        if ($process.ExitCode -ne 0 -or $result.status -ne 'passed') { throw ($Label + ' probe failed: ' + $result.error) }
        if ($result.runtimeVersion -ne '10.0.14') { throw ('The probe used an unexpected runtime: ' + $result.runtimeVersion) }
        $results.Add($result)
        Write-Host ('{0} {1} {2}: first frame {3:N1} ms' -f $Label, $Phase, $Iteration, $result.firstFrameMs)
    }
    finally {
        if ($started -and !$process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
function Percentile([double[]]$Values, [double]$Percent) {
    $sorted = @($Values | Sort-Object)
    if (!$sorted.Count) { return $null }
    if ($Percent -eq 50 -and $sorted.Count % 2 -eq 0) { return ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    return $sorted[[Math]::Max(0, [int][Math]::Ceiling($sorted.Count * $Percent / 100) - 1)]
}
function Metric([object]$Row, [string]$Name) {
    if ($Name -eq 'firstFrameMs') { return [double]$Row.firstFrameMs }
    if ($Name -eq 'offlineLibraryPreparationMs') { return [double]$Row.offlineLibraryPreparationMs }
    $parts = $Name.Split('.')
    return Percentile @($Row.uiSamples | ForEach-Object { [double]$_.$($parts[0]).$($parts[1]) }) 50
}

try {
    Invoke-Probe 'baseline' $baseline $BaselineApplication 'first-observed' 0
    Invoke-Probe 'candidate' $candidate $CandidateApplication 'first-observed' 0
    if (!$FirstLaunchOnly) {
        for ($iteration = 1; $iteration -le $WarmRuns; $iteration++) {
            # Reverse each pair's order to balance thermal/file-cache ordering.
            if ($iteration % 2) {
                Invoke-Probe 'baseline' $baseline $BaselineApplication 'warm' $iteration
                Invoke-Probe 'candidate' $candidate $CandidateApplication 'warm' $iteration
            }
            else {
                Invoke-Probe 'candidate' $candidate $CandidateApplication 'warm' $iteration
                Invoke-Probe 'baseline' $baseline $BaselineApplication 'warm' $iteration
            }
        }
    }
    $metrics = [Collections.Generic.List[object]]::new()
    foreach ($metric in @('firstFrameMs', 'offlineLibraryPreparationMs', 'input.dispatchMs', 'input.layoutIdleMs', 'libraryOpen.layoutIdleMs', 'libraryReturn.layoutIdleMs', 'resize.layoutIdleMs')) {
        $before = @($results | Where-Object { $_.label -eq 'baseline' -and $_.phase -eq 'warm' } | ForEach-Object { Metric $_ $metric })
        $after = @($results | Where-Object { $_.label -eq 'candidate' -and $_.phase -eq 'warm' } | ForEach-Object { Metric $_ $metric })
        if (!$before.Count) { continue }
        $p50Before = Percentile $before 50; $p50After = Percentile $after 50
        $p95Before = Percentile $before 95; $p95After = Percentile $after 95
        $medianLimit = [Math]::Max(100, $p50Before * 0.05)
        $p95Limit = [Math]::Max(200, $p95Before * 0.10)
        $metrics.Add([pscustomobject]@{
            metric = $metric; runsPerVariant = $before.Count
            baselineMedianMs = $p50Before; candidateMedianMs = $p50After; medianDeltaMs = $p50After - $p50Before
            baselineP95Ms = $p95Before; candidateP95Ms = $p95After; p95DeltaMs = $p95After - $p95Before
            allowedMedianDeltaMs = $medianLimit; allowedP95DeltaMs = $p95Limit
            passed = ($p50After - $p50Before -le $medianLimit) -and ($p95After - $p95Before -le $p95Limit)
        })
    }
    $sameCards = @($results.offlineLibraryCards | Select-Object -Unique).Count -eq 1
    $passed = $sameCards -and @($metrics | Where-Object { !$_.passed }).Count -eq 0
    $summary = [ordered]@{
        schemaVersion = 1; createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        scope = 'Controlled native host + actual offscreen WPF shell, offline cards and UI commands. Excludes full service/Steam readiness, hardware input, display scanout and OS-cold-cache claims.'
        firstLaunchState = $FirstLaunchState; firstObserved = @($results | Where-Object phase -eq 'first-observed')
        order = 'First baseline/candidate recorded separately; warm pairs alternate AB then BA.'
        warmRunsPerVariant = $(if ($FirstLaunchOnly) { 0 } else { $WarmRuns })
        uiAggregation = 'Eight UI operations of each kind per process; compare medians per process, then median/p95 across processes.'
        percentile = 'Median averages middle pair; p95 uses nearest rank.'
        equivalentLibraryCardCount = $sameCards; metrics = @($metrics.ToArray()); passed = $passed
    }
    $summary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Isolated desktop startup measurement')
    $lines.Add('')
    $lines.Add($summary.scope)
    $lines.Add('')
    $lines.Add(('First observed launch cache condition: **{0}**. Warm runs: **{1} per variant**. Same library card count: **{2}**.' -f $FirstLaunchState, $summary.warmRunsPerVariant, $sameCards))
    $lines.Add('')
    $lines.Add('| Metric | Baseline median ms | Candidate median ms | Delta ms | Baseline p95 ms | Candidate p95 ms | Pass |')
    $lines.Add('|---|---:|---:|---:|---:|---:|---|')
    foreach ($row in $metrics) { $lines.Add(('| {0} | {1:N2} | {2:N2} | {3:N2} | {4:N2} | {5:N2} | {6} |' -f $row.metric,$row.baselineMedianMs,$row.candidateMedianMs,$row.medianDeltaMs,$row.baselineP95Ms,$row.candidateP95Ms,$row.passed)) }
    $lines.Add('')
    $lines.Add('Limits: median regression <= max(100 ms, 5% of baseline); p95 regression <= max(200 ms, 10% of baseline). Full service startup and actual visible-frame/input timing require separate acceptance.')
    $lines | Set-Content -LiteralPath (Join-Path $runRoot 'summary.md') -Encoding utf8
    if ($FailOnRegression -and !$passed) { throw 'The isolated WPF probe exceeded its regression budget or compared different card counts. See summary.json.' }
}
finally { Assert-Budget }
