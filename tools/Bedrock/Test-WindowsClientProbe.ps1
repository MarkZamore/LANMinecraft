#Requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'WindowsClientProbe.psm1') -Force

$checks = [Collections.Generic.List[string]]::new()
function Assert-Probe {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $checks.Add($Name)
}
function Assert-ProbeThrows {
    param([scriptblock] $Action, [string] $Name)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-Probe $threw $Name
}
function New-PackageFixture {
    return [pscustomobject]@{
        Name = 'Microsoft.MinecraftUWP'
        PackageFamilyName = 'Microsoft.MinecraftUWP_8wekyb3d8bbwe'
        PublisherId = '8wekyb3d8bbwe'
        Publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        SignatureKind = 'Store'
        Status = 'Ok'
        IsDevelopmentMode = $false
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/agent-runs/bedrock-support-20261107'))
$runRoot = Join-Path $allowedRoot ('probe-fixture-' + [Guid]::NewGuid().ToString('N'))
$links = [Collections.Generic.List[string]]::new()
try {
    if ((Get-BedrockPlainPathState $allowedRoot) -notin @('ok', 'missing')) { throw 'Unsafe test parent.' }
    New-Item -ItemType Directory -Path $runRoot -ErrorAction Stop | Out-Null
    [ordered]@{
        owner = 'Bedrock WindowsClientProbe tests'
        purpose = 'Small synthetic package/world fixtures; no game data'
        createdUtc = [DateTime]::UtcNow.ToString('o')
        expectedBytes = 65536
        deleteByUtc = [DateTime]::UtcNow.AddHours(1).ToString('o')
        necessaryResults = @('JSON test summary on stdout; fixture removed in finally')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'RUN.json') -Encoding utf8

    $package = New-PackageFixture
    Assert-Probe (Test-BedrockProductionPackage $package) 'accept exact production Store identity'
    foreach ($field in @('Name', 'PackageFamilyName', 'PublisherId', 'Publisher', 'SignatureKind', 'Status')) {
        $bad = New-PackageFixture
        $bad.$field = 'untrusted'
        Assert-Probe (-not (Test-BedrockProductionPackage $bad)) "reject incorrect $field"
    }
    foreach ($variant in @('Microsoft.MinecraftWindowsBeta', 'Microsoft.MinecraftEducationEdition', 'Microsoft.4297127D64EC6')) {
        $bad = New-PackageFixture
        $bad.Name = $variant
        Assert-Probe (-not (Test-BedrockProductionPackage $bad)) "reject nonproduction package $variant"
    }
    $bad = New-PackageFixture
    $bad.IsDevelopmentMode = $true
    Assert-Probe (-not (Test-BedrockProductionPackage $bad)) 'reject development registration'
    $bad.IsDevelopmentMode = 'False'
    Assert-Probe (-not (Test-BedrockProductionPackage $bad)) 'reject untyped development flag'

    [xml]$manifest = @'
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="Microsoft.MinecraftUWP" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
  <Applications><Application Id="App" Executable="must-never-be-executed.exe" /></Applications>
</Package>
'@
    Assert-Probe ((Get-BedrockActivationId -Package $package -Manifest $manifest) -ceq 'Microsoft.MinecraftUWP_8wekyb3d8bbwe!App') 'derive AUMID without using manifest executable'
    $uppercaseManifest = [xml]$manifest.OuterXml.Replace('Name="Microsoft.MinecraftUWP"', 'Name="MICROSOFT.MINECRAFTUWP"')
    Assert-Probe ((Get-BedrockActivationId -Package $package -Manifest $uppercaseManifest) -ceq 'Microsoft.MinecraftUWP_8wekyb3d8bbwe!App') 'accept canonical Windows package identity with different case'
    $badManifest = [xml]$manifest.OuterXml.Replace('Id="App"', 'Id="App &amp; calculator.exe"')
    Assert-ProbeThrows { Get-BedrockActivationId -Package $package -Manifest $badManifest } 'reject command-shaped application ID'
    $badManifest = [xml]$manifest.OuterXml.Replace('Name="Microsoft.MinecraftUWP"', 'Name="Other"')
    Assert-ProbeThrows { Get-BedrockActivationId -Package $package -Manifest $badManifest } 'reject manifest/package identity mismatch'
    $badManifest = [xml]$manifest.OuterXml.Replace('</Applications>', '<Application Id="Other" /></Applications>')
    Assert-ProbeThrows { Get-BedrockActivationId -Package $package -Manifest $badManifest } 'reject ambiguous application list'
    Assert-ProbeThrows { & (Get-Module WindowsClientProbe) { Invoke-BedrockApplicationActivation 'Other!App;calc.exe' } } 'reject nonproduction activation identity before invoking Windows'

    $roaming = Join-Path $runRoot 'roaming-private'
    $local = Join-Path $runRoot 'local-private'
    $gdk = Join-Path $roaming 'Minecraft Bedrock/users/private-profile-id/games/com.mojang/minecraftWorlds'
    $legacy = Join-Path $local 'Packages/Microsoft.MinecraftUWP_8wekyb3d8bbwe/LocalState/games/com.mojang/minecraftWorlds'
    foreach ($world in @((Join-Path $gdk 'private-world-a'), (Join-Path $legacy 'private-world-b'))) {
        New-Item -ItemType Directory -Path (Join-Path $world 'db') -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $world 'level.dat'), [byte[]]@(1,2,3,4))
    }
    $untouchedLevel = Join-Path $gdk 'private-world-a/level.dat'
    $before = (Get-FileHash -LiteralPath $untouchedLevel -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Path (Join-Path $gdk 'incomplete-world') | Out-Null
    $sharedWorld = Join-Path $roaming 'Minecraft Bedrock/users/shared/games/com.mojang/minecraftWorlds/unused-shared-world'
    New-Item -ItemType Directory -Path (Join-Path $sharedWorld 'db') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $sharedWorld 'level.dat'), [byte[]]@(1))

    $inventory = Get-BedrockWorldInventory -RoamingAppData $roaming -LocalAppData $local
    Assert-Probe ($inventory.candidateCount -eq 2) 'discover GDK profiles and legacy candidates; exclude shared and incomplete dirs'
    $redacted = $inventory | ConvertTo-Json -Depth 12
    Assert-Probe ($redacted -notmatch 'private-|[A-Z]:\\') 'default output omits personal paths, profile IDs and world names'
    $detailed = Get-BedrockWorldInventory -RoamingAppData $roaming -LocalAppData $local -IncludePersonalPaths
    Assert-Probe (($detailed | ConvertTo-Json -Depth 12) -match 'private-world-a') 'personal paths require explicit opt-in'
    Assert-Probe (((Get-FileHash -LiteralPath $untouchedLevel -Algorithm SHA256).Hash) -ceq $before) 'inventory leaves world contents unchanged'

    $junction = Join-Path $gdk 'linked-world'
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $legacy 'private-world-b') | Out-Null
    $links.Add($junction)
    $linkedProfile = Join-Path $roaming 'Minecraft Bedrock/users/linked-profile'
    New-Item -ItemType Junction -Path $linkedProfile -Target (Join-Path $roaming 'Minecraft Bedrock/users/private-profile-id') | Out-Null
    $links.Add($linkedProfile)
    $inventory = Get-BedrockWorldInventory -RoamingAppData $roaming -LocalAppData $local
    Assert-Probe ($inventory.candidateCount -eq 2) 'do not follow world or profile junctions'
    Assert-Probe (@($inventory.locations | Where-Object status -eq 'blocked-reparse').Count -eq 1) 'report blocked profile ancestor separately from missing data'
    Assert-Probe (@($inventory.locations | Where-Object skippedReparseCount -gt 0).Count -eq 1) 'report skipped world junction'
    Assert-Probe ((Get-BedrockPlainPathState (Join-Path $linkedProfile 'does-not-exist')) -eq 'blocked-reparse') 'check reparse ancestors even when child path is missing'
    Assert-Probe ((Get-BedrockPlainPathState (Join-Path $runRoot 'missing')) -eq 'missing') 'distinguish absent path from blocked path'

    [ordered]@{ status = 'PASS'; checkCount = $checks.Count; checks = @($checks.ToArray()); nativeClient = 'NOT RUN'; fixtureCleanup = 'finally' } | ConvertTo-Json -Depth 5
} finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-BedrockPlainPathState $resolvedRunRoot) -ne 'ok') { throw 'Refusing cleanup outside the verified test root.' }
        foreach ($link in $links) {
            if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
        }
        # No recursive delete may encounter an unexpected reparse point.
        $remainingLinks = @(Get-ChildItem -LiteralPath $resolvedRunRoot -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($remainingLinks.Count -ne 0) { throw 'Unexpected reparse point in test cleanup; preserving fixture.' }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
