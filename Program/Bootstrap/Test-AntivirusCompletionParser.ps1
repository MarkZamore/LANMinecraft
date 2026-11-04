#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scannerScript = Join-Path $PSScriptRoot 'Test-PortableHostAntivirus.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scannerScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ('Antivirus gate has invalid PowerShell syntax: ' + ($parseErrors.Message -join '; ')) }
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-CleanNativeCompletion'
}, $true))
if ($functions.Count -ne 1) { throw 'The production native-output completion parser is missing or ambiguous.' }

# Load only this pure function definition, never the scanner script's top-level
# statements. These checks neither launch Defender nor read/change its settings.
Invoke-Expression $functions[0].Extent.Text
$target = 'C:\fixture\Лаунчер с пробелом.exe'
$clean = "Scan starting...`r`nScan finished.`r`nScanning $target found no threats.`r`n"
$cases = @(
    @{ Name = 'Exact target and complete calibrated output'; Text = $clean; ErrorText = ''; Expected = $true }
    @{ Name = 'LF line endings'; Text = $clean.Replace("`r`n", "`n"); ErrorText = ''; Expected = $true }
    @{ Name = 'Another target with the same filename prefix'; Text = $clean.Replace($target, $target + '.other'); ErrorText = ''; Expected = $false }
    @{ Name = 'Warning on stderr'; Text = $clean; ErrorText = 'warning'; Expected = $false }
    @{ Name = 'Unexpected extra stdout'; Text = $clean + "Unexpected warning`r`n"; ErrorText = ''; Expected = $false }
    @{ Name = 'Missing start marker'; Text = $clean.Replace("Scan starting...`r`n", ''); ErrorText = ''; Expected = $false }
    @{ Name = 'Missing finish marker'; Text = $clean.Replace("Scan finished.`r`n", ''); ErrorText = ''; Expected = $false }
    @{ Name = 'A positive threat count'; Text = $clean.Replace('no threats', '1 threat'); ErrorText = ''; Expected = $false }
    @{ Name = 'Uncalibrated localized format'; Text = $clean.Replace('Scan starting...', 'Проверка начата'); ErrorText = ''; Expected = $false }
    @{ Name = 'Unattributed clean substring'; Text = 'found no threats.'; ErrorText = ''; Expected = $false }
)
foreach ($case in $cases) {
    $actual = Test-CleanNativeCompletion $case.Text $case.ErrorText $target
    if ($actual -ne $case.Expected) {
        $global:LASTEXITCODE = 1
        throw ('Unexpected antivirus output verdict: ' + $case.Name)
    }
}
$global:LASTEXITCODE = 0
Write-Host ('Antivirus completion parser: ' + $cases.Count + ' cases passed; no scanner was started.')
