#requires -Version 7.0
[CmdletBinding()]
param(
    [AllowEmptyString()][string]$BaseCommit,
    [string]$HeadCommit = 'HEAD',
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$RulesPath = (Join-Path $PSScriptRoot 'ReleaseImpactRules.psd1')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseProfile.ps1')
$baseSha = ''
$headSha = ''
$changedPaths = @()
$shaPattern = '^[0-9a-fA-F]{40}$'
$pathPattern = '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+$'

function New-ImpactPlan([string]$Scope, [string]$Reason, [string[]]$Methods = @()) {
    [pscustomobject]@{
        SchemaVersion = 1
        Scope = $Scope
        Reason = $Reason
        BaseCommit = $baseSha
        HeadCommit = $headSha
        Paths = [string[]]$changedPaths
        Methods = $Methods
    }
}

try {
    if ($HeadCommit -cne 'HEAD' -and $HeadCommit -notmatch $shaPattern) {
        return New-ImpactPlan 'Standard' 'The candidate commit is invalid.'
    }
    $headSha = [string](git -C $RepositoryRoot rev-parse --verify "$HeadCommit^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $headSha -notmatch $shaPattern) {
        return New-ImpactPlan 'Standard' 'The candidate commit is unavailable.'
    }
    if ($BaseCommit -notmatch $shaPattern) {
        return New-ImpactPlan 'Standard' 'The last published commit is missing or invalid.'
    }
    $baseSha = [string](git -C $RepositoryRoot rev-parse --verify "$BaseCommit^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $baseSha -notmatch $shaPattern) {
        return New-ImpactPlan 'Standard' 'The last published commit is unavailable in this checkout.'
    }
    git -C $RepositoryRoot merge-base --is-ancestor $baseSha $headSha 2>$null
    if ($LASTEXITCODE -ne 0) {
        return New-ImpactPlan 'Standard' 'The last published commit is not a verified ancestor of the candidate.'
    }

    # Quote unusual names and fall back if any record cannot be interpreted.
    # Disabling rename detection exposes both ends as a deletion and addition.
    $changes = @(git -C $RepositoryRoot -c core.quotePath=true diff --name-status --no-renames --no-ext-diff --no-textconv $baseSha $headSha -- 2>$null)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -eq 0) {
        return New-ImpactPlan 'Standard' 'There is no verified nonempty diff from the published release.'
    }
    $entries = @(foreach ($change in $changes) {
        if ($change -cnotmatch '^([A-Z])\t(.+)$') { throw 'A Git change record is ambiguous.' }
        [pscustomobject]@{ Status = $Matches[1]; Path = $Matches[2] }
    })
    $changedPaths = @($entries.Path)
    if (@($entries | Where-Object { $_.Status -cnotin @('A', 'M') -or $_.Path -cnotmatch $pathPattern }).Count -gt 0) {
        return New-ImpactPlan 'Standard' 'The diff includes a deletion, rename, type change or unusual path.'
    }

    $rules = Import-PowerShellDataFile -LiteralPath $RulesPath
    if ($rules.Suites -isnot [hashtable] -or $rules.Suites.Count -eq 0 -or !$rules.Rules -or !$rules.Always) {
        throw 'Release impact rules do not define suites, paths and mandatory methods.'
    }
    $standard = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($selector in Read-ReleaseSelectors) {
        [void]$standard.Add($selector.Substring('Minecraft.Tests.'.Length))
    }
    $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($method in $rules.Always) {
        if ($method -isnot [string] -or !$standard.Contains($method) -or !$selected.Add($method)) {
            throw "Invalid or duplicate mandatory Release method: $method"
        }
    }

    $closures = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    $visiting = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $suiteNames = @($rules.Suites.Keys)
    function Resolve-Suite([string]$Name) {
        if ($closures.ContainsKey($Name)) { return $closures[$Name] }
        if ($Name -cnotin $suiteNames -or !$visiting.Add($Name)) {
            throw "An impact suite is unknown or cyclic: $Name"
        }
        $suite = $rules.Suites[$Name]
        if ($suite -isnot [hashtable] -or !$suite.Methods -or !$suite.ContainsKey('DependsOn')) {
            throw "Incomplete impact suite: $Name"
        }
        $methods = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($method in $suite.Methods) {
            if ($method -isnot [string] -or !$standard.Contains($method) -or !$methods.Add($method)) {
                throw "Invalid or duplicate Release method in suite ${Name}: $method"
            }
        }
        $requirements = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [void]$requirements.Add($Name)
        foreach ($dependency in $suite.DependsOn) {
            if ($dependency -isnot [string]) { throw "Invalid dependency in suite $Name" }
            $requirements.UnionWith([string[]]@(Resolve-Suite $dependency))
        }
        [void]$visiting.Remove($Name)
        $closures.Add($Name, [string[]]@($requirements))
        return $closures[$Name]
    }
    foreach ($suiteName in $suiteNames) { [void](Resolve-Suite $suiteName) }

    $pathSuites = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    foreach ($rule in $rules.Rules) {
        if ($rule -isnot [hashtable] -or !$rule.Paths -or !$rule.Suites) { throw 'Incomplete impact path rule.' }
        foreach ($suiteName in $rule.Suites) {
            if ($suiteName -isnot [string] -or !$closures.ContainsKey($suiteName)) { throw "Unknown path suite: $suiteName" }
        }
        foreach ($path in $rule.Paths) {
            if ($path -isnot [string] -or $path -cnotmatch $pathPattern -or $pathSuites.ContainsKey($path)) {
                throw "Invalid or overlapping impact path rule: $path"
            }
            $pathSuites.Add($path, [string[]]@($rule.Suites))
        }
    }

    $directSuites = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unknown = @(foreach ($path in $changedPaths) {
        if ($pathSuites.ContainsKey($path)) { $directSuites.UnionWith($pathSuites[$path]) }
        else { $path }
    })
    if ($unknown.Count -gt 0) {
        return New-ImpactPlan 'Standard' ('Shared or unmapped paths: ' + ($unknown -join ', '))
    }

    # A consumer is impacted if any transitive prerequisite changed. Include
    # the consumer's prerequisites, without treating those files as changed.
    $requiredSuites = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($suiteName in $suiteNames) {
        if ($directSuites.Overlaps($closures[$suiteName])) { $requiredSuites.UnionWith($closures[$suiteName]) }
    }
    foreach ($suiteName in $requiredSuites) { $selected.UnionWith([string[]]@($rules.Suites[$suiteName].Methods)) }
    if ($selected.Count -ge $standard.Count) { return New-ImpactPlan 'Standard' 'The impacted suites cover the full Release profile.' }

    $methods = [string[]]@($selected)
    [Array]::Sort($methods, [StringComparer]::Ordinal)
    $matched = [string[]]@($requiredSuites)
    [Array]::Sort($matched, [StringComparer]::Ordinal)
    New-ImpactPlan 'Impact' ('Matched suites: ' + ($matched -join ', ')) $methods
}
catch {
    New-ImpactPlan 'Standard' ('Impact could not be determined: ' + $_.Exception.Message)
}
