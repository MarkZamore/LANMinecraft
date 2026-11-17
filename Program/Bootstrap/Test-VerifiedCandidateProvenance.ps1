#requires -Version 7.2
$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'Publish-VerifiedCandidate.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'The candidate verifier cannot be parsed.' }
$gate = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-CandidateCiJobs'
}, $true))
if ($gate.Count -ne 1) { throw 'The CI job validator is missing or ambiguous.' }

# Check the production validator without invoking GitHub, Defender or the publisher.
. ([scriptblock]::Create($gate[0].Extent.Text))

function New-CiJob([string]$Name, [string]$Conclusion, [string[]]$SuccessfulSteps) {
    [pscustomobject]@{
        name = $Name; status = 'completed'; conclusion = $Conclusion
        steps = @($SuccessfulSteps | ForEach-Object { [pscustomobject]@{ name = $_; conclusion = 'success' } })
    }
}

function New-ParallelJobs {
    New-CiJob verify success @('Run Release tests')
    New-CiJob build success @('Build portable EXE', 'Create update manifest', 'Verify release contents', 'Upload release assets')
    $publish = New-CiJob publish failure @('Scan portable host', 'Prepare exact candidate', 'Upload exact candidate')
    $publish.steps += [pscustomobject]@{ name = 'Publish latest release'; conclusion = 'failure' }
    $publish
}

$legacy = New-CiJob release failure @('Test and publish', 'Create update manifest', 'Verify release contents', 'Prepare exact candidate', 'Upload exact candidate')
Assert-CandidateCiJobs @(New-ParallelJobs)
$failedScan = @(New-ParallelJobs)
$failedScan[2].steps[0].conclusion = 'failure'
Assert-CandidateCiJobs $failedScan
$cases = @(
    @{ Name = 'failed test job'; Change = { param($jobs) $jobs[0].conclusion = 'failure'; $jobs } }
    @{ Name = 'skipped tests'; Change = { param($jobs) $jobs[0].steps[0].conclusion = 'skipped'; $jobs } }
    @{ Name = 'failed artifact upload'; Change = { param($jobs) $jobs[1].steps[-1].conclusion = 'failure'; $jobs } }
    @{ Name = 'duplicate build job'; Change = { param($jobs) $jobs + $jobs[1] } }
    @{ Name = 'missing build job'; Change = { param($jobs) $jobs[0], $jobs[2] } }
    @{ Name = 'missing manifest step'; Change = { param($jobs) $jobs[1].steps[1].name = 'Another step'; $jobs } }
    @{ Name = 'failed manifest creation'; Change = { param($jobs) $jobs[1].steps[1].conclusion = 'failure'; $jobs } }
    @{ Name = 'duplicate manifest step'; Change = { param($jobs) $jobs[1].steps += $jobs[1].steps[1]; $jobs } }
    @{ Name = 'publication did not fail'; Change = { param($jobs) $jobs[2].steps[-1].conclusion = 'success'; $jobs } }
    @{ Name = 'fallback was not produced'; Change = { param($jobs) $jobs[2].steps[2].conclusion = 'skipped'; $jobs } }
    @{ Name = 'legacy workflow layout'; Change = { param($jobs) $legacy } }
    @{ Name = 'mixed workflow layouts'; Change = { param($jobs) $jobs + $legacy } }
)
foreach ($case in $cases) {
    $jobs = @(New-ParallelJobs)
    $invalid = @(& $case.Change $jobs)
    try { Assert-CandidateCiJobs $invalid }
    catch { continue }
    throw ('Candidate provenance accepted ' + $case.Name + '.')
}
Write-Host ('Candidate provenance: ' + (2 + $cases.Count) + ' cases passed.')
