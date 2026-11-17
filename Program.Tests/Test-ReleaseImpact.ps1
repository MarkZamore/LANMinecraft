#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$planner = Join-Path $PSScriptRoot 'Get-ReleaseImpact.ps1'
$runsRoot = Join-Path $repositoryRoot 'artifacts/agent-runs'

foreach ($directory in @($repositoryRoot, (Split-Path -Parent $runsRoot), $runsRoot)) {
    if (!(Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    $item = Get-Item -LiteralPath $directory
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to create fixture through a reparse point: $directory"
    }
}
$runRoot = Join-Path $runsRoot ('release-impact-' + [Guid]::NewGuid().ToString('N'))
$gitRoot = Join-Path $runRoot 'history'
$worktree = Join-Path $runRoot 'files'
$rulesPath = Join-Path $runRoot 'rules.psd1'
$assertions = 0

function Invoke-FixtureGit([string[]]$Arguments) {
    $output = & git -C $gitRoot --work-tree $worktree @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('Fixture git failed: ' + ($Arguments -join ' ')) }
    $output
}

function Set-FixtureFile([string]$RelativePath, [string]$Content) {
    $path = Join-Path $worktree $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value $Content -Encoding utf8
}

function Save-FixtureCommit([string]$Message) {
    Invoke-FixtureGit @('add', '--all') | Out-Null
    Invoke-FixtureGit @('commit', '--quiet', '-m', $Message) | Out-Null
    Invoke-FixtureGit @('rev-parse', 'HEAD')
}

function Assert-Plan(
    [string]$Name, [string]$Base, [string]$Head, [string]$Scope,
    [string[]]$Methods = @(), [string[]]$Paths = @()
) {
    $result = @(& $planner -RepositoryRoot $gitRoot -RulesPath $rulesPath -BaseCommit $Base -HeadCommit $Head)
    if ($result.Count -ne 1 -or $result[0].SchemaVersion -ne 1 -or
        $result[0].Scope -cne $Scope -or [string]::IsNullOrWhiteSpace($result[0].Reason)) {
        throw "Unexpected plan for ${Name}: $($result | ConvertTo-Json -Depth 5 -Compress)"
    }
    $plannedMethods = @($result[0].Methods | Sort-Object -CaseSensitive)
    $expectedMethods = @($Methods | Sort-Object -CaseSensitive)
    if (($plannedMethods -join '|') -cne ($expectedMethods -join '|')) {
        throw "Unexpected methods for ${Name}: $($plannedMethods -join ', ')"
    }
    if ($Paths.Count -gt 0) {
        $plannedPaths = @($result[0].Paths | Sort-Object -CaseSensitive)
        if (($plannedPaths -join '|') -cne (($Paths | Sort-Object -CaseSensitive) -join '|')) {
            throw "Unexpected changed paths for ${Name}: $($plannedPaths -join ', ')"
        }
    }
    $script:assertions++
    Write-Host "PASS $Name"
}

$rules = @'
@{
    Always = @('ChangelogTests.EveryVersion_IsTwoShortSentences')
    Suites = @{
        Ui = @{
            DependsOn = @('Layout')
            Methods = @('WindowCanvasTests.TheLayout_KeepsTwoColumnsAndEveryExpandedControlReachable')
        }
        Layout = @{
            DependsOn = @('Tokens')
            Methods = @('WindowCanvasTests.TheExternalScrollbar_SpansPlayAndBodyAndScrollsOnlyTheBody')
        }
        Tokens = @{
            DependsOn = @()
            Methods = @('DesignTokensTests.AppXaml_DefinesEveryTokenDesignMdNames_AndNoOthers')
        }
        Status = @{
            DependsOn = @('Tokens')
            Methods = @('WindowCanvasTests.SteamStatus_RemainsReadableAndAlignedWithTheChangelog')
        }
        History = @{
            DependsOn = @()
            Methods = @('ChangelogTests.EveryVersion_IsTwoShortSentences')
        }
    }
    Rules = @(
        @{ Paths = @('Program/Dialog.xaml'); Suites = @('Ui') }
        @{ Paths = @('Program/Layout.xaml'); Suites = @('Layout') }
        @{ Paths = @('Program/NewStatus.xaml'); Suites = @('Status') }
        @{ Paths = @('Program/Tokens.xaml'); Suites = @('Tokens') }
        @{ Paths = @('Program/Legacy.xaml', 'Program/Obsolete.xaml', 'Program/CaseOnly.xaml'); Suites = @('Layout') }
        @{ Paths = @('Program/Replacement.xaml'); Suites = @('Ui') }
        @{ Paths = @('Program/Changelog.md'); Suites = @('History') }
    )
}
'@
$always = 'ChangelogTests.EveryVersion_IsTwoShortSentences'
$tokens = 'DesignTokensTests.AppXaml_DefinesEveryTokenDesignMdNames_AndNoOthers'
$layout = 'WindowCanvasTests.TheExternalScrollbar_SpansPlayAndBodyAndScrollsOnlyTheBody'
$ui = 'WindowCanvasTests.TheLayout_KeepsTwoColumnsAndEveryExpandedControlReachable'
$status = 'WindowCanvasTests.SteamStatus_RemainsReadableAndAlignedWithTheChangelog'
$uiMethods = @($always, $tokens, $layout, $ui)

New-Item -ItemType Directory -Path $runRoot | Out-Null
try {
    $created = [DateTime]::UtcNow
    @{
        owner = 'Program.Tests/Test-ReleaseImpact.ps1'
        task = 'Synthetic commit history for release impact selection'
        createdAtUtc = $created.ToString('O')
        expectedMaximumBytes = 1MB
        deleteByUtc = $created.AddHours(1).ToString('O')
        necessaryResults = @('Console proof of test cases')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'RUN.json') -Encoding utf8
    Set-Content -LiteralPath $rulesPath -Value $rules -Encoding utf8
    New-Item -ItemType Directory -Path $worktree | Out-Null
    $emptyHooks = Join-Path $runRoot 'hooks'
    New-Item -ItemType Directory -Path $emptyHooks | Out-Null
    # Only disposable test objects go into the bare repository. No project
    # source, real worktree or .git directory is copied into the fixture.
    & git init --bare --quiet --initial-branch main "--template=$emptyHooks" $gitRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture history.' }
    Invoke-FixtureGit @('config', 'user.name', 'Release impact fixture')
    Invoke-FixtureGit @('config', 'user.email', 'release-impact@example.invalid')
    Invoke-FixtureGit @('config', 'commit.gpgSign', 'false')
    Invoke-FixtureGit @('config', 'core.hooksPath', $emptyHooks)
    Invoke-FixtureGit @('config', 'core.autocrlf', 'false')

    Set-FixtureFile 'Program/Dialog.xaml' '<Dialog />'
    Set-FixtureFile 'Program/Layout.xaml' '<Layout />'
    Set-FixtureFile 'Program/Tokens.xaml' '<Tokens />'
    Set-FixtureFile 'Program/Legacy.xaml' '<Legacy />'
    Set-FixtureFile 'Program/Obsolete.xaml' '<Obsolete />'
    Set-FixtureFile 'Program/CaseOnly.xaml' '<CaseOnly />'
    Set-FixtureFile 'Program/Changelog.md' '# Published'
    $base = Save-FixtureCommit 'Published baseline'
    Set-FixtureFile 'Program/Dialog.xaml' '<Dialog Changed="True" />'
    Set-FixtureFile 'Program/Changelog.md' '# Subsequent version'
    $known = Save-FixtureCommit 'Change dialog and changelog'
    Set-FixtureFile 'Program/Layout.xaml' '<Layout Changed="True" />'
    $dependency = Save-FixtureCommit 'Change a shared layout'
    Set-FixtureFile 'Program/NewStatus.xaml' '<Status />'
    $added = Save-FixtureCommit 'Add a registered status file'
    Set-FixtureFile 'Program/Tokens.xaml' '<Tokens Changed="True" />'
    $tokenChanged = Save-FixtureCommit 'Change tokens used by the layout and status'
    Invoke-FixtureGit @('mv', '--', 'Program/Legacy.xaml', 'Program/Replacement.xaml')
    $renamed = Save-FixtureCommit 'Rename a registered file'
    Invoke-FixtureGit @('rm', '--quiet', '--', 'Program/Obsolete.xaml')
    $deleted = Save-FixtureCommit 'Remove a registered file'
    Invoke-FixtureGit @('mv', '--', 'Program/CaseOnly.xaml', 'Program/temporary.xaml')
    Invoke-FixtureGit @('mv', '--', 'Program/temporary.xaml', 'Program/caseonly.xaml')
    $caseRename = Save-FixtureCommit 'Rename with changed capitalisation'
    Set-FixtureFile 'Program/Common.cs' 'namespace Common;'
    $common = Save-FixtureCommit 'Change common infrastructure'
    Invoke-FixtureGit @('mv', '--', 'Program/Replacement.xaml', 'Program/Unregistered.xaml')
    $unknown = Save-FixtureCommit 'Rename onto an unregistered path'
    Set-FixtureFile 'Program/Unexpected path.xaml' '<Unregistered />'
    $quoted = Save-FixtureCommit 'Add an unusual filename'
    $layoutBlob = Invoke-FixtureGit @('rev-parse', ($quoted + ':Program/Layout.xaml'))
    Invoke-FixtureGit @('update-index', '--cacheinfo', "120000,$layoutBlob,Program/Layout.xaml")
    Invoke-FixtureGit @('commit', '--quiet', '-m', 'Change entry type while keeping the blob')
    $typeChanged = Invoke-FixtureGit @('rev-parse', 'HEAD')
    $diverged = Invoke-FixtureGit @('commit-tree', "$base^{tree}", '-m', 'Unrelated history')

    Assert-Plan 'unchanged history' $base $base Standard
    Assert-Plan 'no published base' '' $known Standard
    Assert-Plan 'malformed published SHA' 'invalid' $known Standard
    Assert-Plan 'published SHA absent from history' ('0' * 40) $known Standard
    Assert-Plan 'published SHA outside the head ancestry' $diverged $known Standard
    Assert-Plan 'known files add transitive suites and deduplicate the always checks' $base $known Impact $uiMethods @(
        'Program/Dialog.xaml', 'Program/Changelog.md'
    )
    Assert-Plan 'changed prerequisite also runs its consumer, excluding incidental consumers' $known $dependency Impact $uiMethods @(
        'Program/Layout.xaml'
    )
    Assert-Plan 'registered added file includes only its own prerequisite closure' $dependency $added Impact @(
        $always, $tokens, $status
    ) @('Program/NewStatus.xaml')
    Assert-Plan 'changed root prerequisite runs direct and transitive consumers' $added $tokenChanged Impact @(
        $uiMethods + $status
    ) @('Program/Tokens.xaml')
    Assert-Plan 'known rename widens to the full profile and reports both paths' $tokenChanged $renamed Standard @() @(
        'Program/Legacy.xaml', 'Program/Replacement.xaml'
    )
    Assert-Plan 'deletion widens to the full profile' $renamed $deleted Standard @() @('Program/Obsolete.xaml')
    Assert-Plan 'all unpublished commits affect selection' $base $added Impact @($uiMethods + $status) @(
        'Program/Changelog.md', 'Program/Dialog.xaml', 'Program/Layout.xaml', 'Program/NewStatus.xaml'
    )
    Assert-Plan 'case-only rename to an unregistered spelling' $deleted $caseRename Standard
    Assert-Plan 'common code widens to the full profile' $caseRename $common Standard
    Assert-Plan 'known-to-unknown rename widens to the full profile' $common $unknown Standard
    Assert-Plan 'one unknown among known paths widens to the full profile' $base $unknown Standard
    Assert-Plan 'unusual filename widens to the full profile' $unknown $quoted Standard
    Assert-Plan 'type change widens to the full profile' $quoted $typeChanged Standard @() @('Program/Layout.xaml')

    Set-Content -LiteralPath $rulesPath -Value ($rules.Replace("DependsOn = @('Layout')", "DependsOn = @('Missing')")) -Encoding utf8
    Assert-Plan 'unknown suite dependency' $base $known Standard
    Set-Content -LiteralPath $rulesPath -Value ($rules.Replace("DependsOn = @('Layout')", "DependsOn = @('Ui')")) -Encoding utf8
    Assert-Plan 'cyclic suite dependency' $base $known Standard
    Set-Content -LiteralPath $rulesPath -Value ($rules.Replace($always, 'NonexistentTests.NotInRelease')) -Encoding utf8
    Assert-Plan 'method outside the standard profile' $base $known Standard
    Set-Content -LiteralPath $rulesPath -Value ($rules.Replace('Program/Replacement.xaml', 'Program/Dialog.xaml')) -Encoding utf8
    Assert-Plan 'overlapping path rules' $base $known Standard
    Set-Content -LiteralPath $rulesPath -Value '@{' -Encoding utf8
    Assert-Plan 'unreadable rules' $base $known Standard
    Write-Host "Release impact: $assertions cases passed."
}
finally {
    $owned = Get-Item -LiteralPath $runRoot
    if (!$owned.FullName.StartsWith($runsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ($owned.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $owned.FullName -Recurse -Force -Attributes ReparsePoint)) {
        throw "Refusing to clean an unexpected fixture path: $runRoot"
    }
    Remove-Item -LiteralPath $owned.FullName -Recurse -Force
}
