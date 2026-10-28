#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Full', 'Adapters')][string]$Profile = 'Release',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$SkipPortableIdentityAdapter,
    [string]$HostPath,
    [string]$ResultsDirectory,
    [string]$ValidateTrx
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$settingsPath = Join-Path $PSScriptRoot ($Profile + '.runsettings')
$testsProject = Join-Path $PSScriptRoot 'Program.Tests.csproj'

function Invoke-Dotnet([string[]]$DotnetArguments) {
    & dotnet @DotnetArguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed with exit code ' + $LASTEXITCODE) }
}

function Read-ReleaseSelectors {
    [xml]$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Release.runsettings') -Raw
    $filter = [string]$settings.RunSettings.RunConfiguration.TestCaseFilter
    if ([string]::IsNullOrWhiteSpace($filter)) { throw 'The release filter is empty.' }
    $selectors = @($filter.Split('|') | ForEach-Object {
        $part = $_.Trim()
        if ($part -notmatch '^FullyQualifiedName=(Minecraft\.Tests\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+)$') {
            throw ('Unsupported release selector: ' + $part)
        }
        $matches[1]
    })
    if ($selectors.Count -eq 0 -or @($selectors | Select-Object -Unique).Count -ne $selectors.Count) {
        throw 'The release profile has no selectors or contains duplicates.'
    }
    return $selectors
}

function Assert-ReleaseResults([string]$TrxPath, [string[]]$Selectors) {
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) { throw 'The release run produced no TRX.' }
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($null -eq $counters -or [int]$counters.total -le 0 -or $results.Count -eq 0) {
        throw 'The release profile executed zero tests.'
    }
    if ([int]$counters.total -ne $results.Count -or [int]$counters.passed -ne $results.Count -or
        @($results | Where-Object outcome -ne 'Passed').Count -ne 0) {
        throw 'Every selected release case must pass; failures and skipped cases reject the release.'
    }
    # Allow small intentional theory additions without pinning a cosmetic
    # exact count. A lost filter must never expand this into the full suite.
    if ($results.Count -gt 150) { throw 'The release profile exceeded its 150-case budget; review its selectors.' }
    $expected = [Collections.Generic.HashSet[string]]::new($Selectors, [StringComparer]::Ordinal)
    $definitions = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($definition in $trx.SelectNodes("//*[local-name()='UnitTest']")) {
        $identifier = $definition.GetAttribute('id')
        $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
        if ([string]::IsNullOrWhiteSpace($identifier) -or $null -eq $method -or $definitions.ContainsKey($identifier)) {
            throw 'The release TRX contains an invalid or duplicate test definition.'
        }
        $definitions.Add($identifier, ([string]$method.className).Split(',')[0] + '.' + [string]$method.name)
    }
    $resolved = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $executedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        $identifier = $result.GetAttribute('testId')
        if (-not $definitions.ContainsKey($identifier) -or -not $executedIds.Add($identifier)) {
            throw 'A passed release result has no matching definition or repeats a testId.'
        }
        $name = $definitions[$identifier]
        if (-not $expected.Contains($name)) { throw ('An unexpected release method was executed: ' + $name) }
        [void]$resolved.Add($name)
    }
    $missing = @($Selectors | Where-Object { -not $resolved.Contains($_) })
    if ($missing.Count -ne 0) { throw ('Unresolved release selectors: ' + ($missing -join ', ')) }
    return [pscustomobject]@{ Passed = $results.Count; Selectors = $Selectors.Count; Profile = 'Release' }
}

if ($ValidateTrx) {
    # Exercises the real guard against saved/synthetic results without a build,
    # process launch, environment override or test run.
    Assert-ReleaseResults ([IO.Path]::GetFullPath($ValidateTrx)) (Read-ReleaseSelectors)
    return
}

Push-Location -LiteralPath $repository
$previousAuditRoot = [Environment]::GetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT', 'Process')
$previousAuditHost = [Environment]::GetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_HOST', 'Process')
$previousAuditSource = [Environment]::GetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_SOURCE_ROOT', 'Process')
try {
    if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $PSScriptRoot 'TestResults' }
    $resultsBase = [IO.Path]::GetFullPath($ResultsDirectory)
    $session = Join-Path $resultsBase ($Profile + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($session)
    $restoreArguments = if ($NoRestore) { @('--no-restore') } else { @() }
    if (-not $NoBuild) {
        # Build the real adapter once. Tests and the small updater host reference
        # that assembly without rebuilding it with SkipPortableIdentityAdapter.
        $launcherArguments = @('build', (Join-Path $repository 'Program/Minecraft.csproj'), '-c', $Configuration) + $restoreArguments
        if ($SkipPortableIdentityAdapter) { $launcherArguments += '-p:SkipPortableIdentityAdapter=true' }
        Invoke-Dotnet $launcherArguments
        Invoke-Dotnet (@('build', $testsProject, '-c', $Configuration, '-p:BuildProjectReferences=false') + $restoreArguments)
    }

    if ($Profile -eq 'Release') {
        $selectors = Read-ReleaseSelectors
        if (-not $HostPath) {
            if ($NoBuild) { throw '-NoBuild requires -HostPath pointing at a current published PortabilityAuditHost.exe.' }
            $hostOutput = Join-Path $session 'host'
            Invoke-Dotnet (@('publish', (Join-Path $repository 'tools/PortabilityAuditHost/PortabilityAuditHost.csproj'),
                '-c', $Configuration, '-o', $hostOutput, '-p:BuildProjectReferences=false') + $restoreArguments)
            $HostPath = Join-Path $hostOutput 'PortabilityAuditHost.exe'
        }
        $HostPath = [IO.Path]::GetFullPath($HostPath)
        if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) { throw 'The published updater host is missing.' }
        [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT', (Join-Path $session 'fixtures'), 'Process')
        [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_HOST', $HostPath, 'Process')
        [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_SOURCE_ROOT', $repository, 'Process')
    }

    Invoke-Dotnet @('test', $testsProject, '-c', $Configuration, '--no-build', '--no-restore',
        '--settings', $settingsPath, '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $session)
    if ($Profile -eq 'Release') {
        $summary = Assert-ReleaseResults (Join-Path $session 'tests.trx') $selectors
        $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session 'summary.json') -Encoding utf8
        Write-Host ("Release gate: {0} passed cases across {1} selected methods." -f $summary.Passed, $summary.Selectors)
        foreach ($name in @('fixtures', 'host')) {
            $owned = [IO.Path]::GetFullPath((Join-Path $session $name))
            if (-not $owned.StartsWith($session.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing cleanup outside the generated release-test session.'
            }
            # A just-exited updater can still hold an image for a moment. Keep
            # evidence if it cannot be removed; test results remain untouched.
            for ($attempt = 0; $attempt -lt 3 -and (Test-Path -LiteralPath $owned); $attempt++) {
                try { Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop }
                catch { Start-Sleep -Milliseconds 200 }
            }
        }
    }
    Write-Host ('Test results: ' + $session)
}
finally {
    [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT', $previousAuditRoot, 'Process')
    [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_HOST', $previousAuditHost, 'Process')
    [Environment]::SetEnvironmentVariable('LANMINECRAFT_FUNCTIONAL_AUDIT_SOURCE_ROOT', $previousAuditSource, 'Process')
    Pop-Location
}
