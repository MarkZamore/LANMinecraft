param(
    [Parameter(Mandatory=$true)][string]$OriginalRoot,
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [Parameter(Mandatory=$true)][string]$Candidates,
    [string]$BaselineRoot
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $OriginalRoot).Path
$inputRoot = if ($BaselineRoot) { (Resolve-Path -LiteralPath $BaselineRoot).Path } else { $sourceRoot }
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
$candidateRoot = (Resolve-Path -LiteralPath $Candidates).Path
$boundary = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $fixture.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixture)) {
    throw 'Use a NEW independent fixture directory under the original workspace artifacts; nothing existing is removed.'
}
$profiles = @(
    'All The Mods 10', 'All The Mods 9', 'All the Mods 9 Modified',
    'Caves and Dungeons', 'LL8 Extended', 'Minecraft 26.2', 'TerraFirma Rebirth'
)
$sourceProfiles = @{}
foreach ($profile in $profiles) {
    $sourceProfile = $profile
    if ($profile -eq 'TerraFirma Rebirth' -and -not (Test-Path -LiteralPath (Join-Path $inputRoot "Minecraft\Packs\$profile\portable-pack.json"))) {
        $sourceProfile = 'TerraFirma Rebirth 2'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $inputRoot "Minecraft\Packs\$sourceProfile\portable-pack.json"))) {
        throw "The baseline descriptor is missing: $profile"
    }
    $sourceProfiles[$profile] = $sourceProfile
}
function Put([string]$relative, [string]$text) {
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $text, [Text.UTF8Encoding]::new($false))
}
function CopyExact([string]$source, [string]$relative, [string]$sha) {
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
        throw 'The fixture input is not the exact verified author artifact.'
    }
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
Put '.deployment-rehearsal' 'Generated sentinels only. No personal world was copied; this is not a backup.'
$memory = [ordered]@{}
foreach ($profile in $profiles) {
    $memory[$profile] = 4
    $sourceProfile = $sourceProfiles[$profile]
    $descriptor = Join-Path $inputRoot "Minecraft\Packs\$sourceProfile\portable-pack.json"
    $runtime = Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json
    Put "Minecraft\Packs\$profile\portable-pack.json" ($runtime | ConvertTo-Json -Depth 8)
    $oldName = if ($runtime.loader.type -eq 'forge') { 'e4steam-forge-mc1.18.2-1.20.2-v0.3.2.jar' }
        else { 'e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar' }
    $oldSha = if ($runtime.loader.type -eq 'forge') { '9a0fc4e1a8c170f37e04cf8469091990d5c1f0953b334481dbb5eab9475e3e1a' }
        else { '4f182b2560a6e001f248cd59cc063dd1c99d92a9b1b38ef099bc8ee822fe0418' }
    $oldJar = Join-Path $candidateRoot $oldName
    if (-not (Test-Path -LiteralPath $oldJar)) { $oldJar = Join-Path $inputRoot "Minecraft\Personal\Instances\$sourceProfile\mods\$oldName" }
    CopyExact $oldJar "Minecraft\Personal\Instances\$profile\mods\$oldName" $oldSha
    Put "Minecraft\Personal\Instances\$profile\options.txt" "renderDistance:12`nsimulationDistance:8`nkey_key.forward:key.keyboard.r`n"
    Put "Minecraft\Worlds\$profile\DeploymentFixture\level.dat" 'Generated fixture sentinel, not a playable or personal world.'
    Put "Minecraft\Worlds\$profile\DeploymentFixture\playerdata\00000000-0000-4000-8000-000000000001.dat" 'Fixture inventory sentinel: must remain byte-identical.'
    Put "Minecraft\Worlds\$profile\DeploymentFixture\session.lock" ''
    Put "Minecraft\Worlds\$profile\DeploymentFixture\.minecraft-portable-world.json" (@{
        schemaVersion=1; buildRelativePath=$profile; buildName=$profile; worldId='fixture-world-identity'
    } | ConvertTo-Json)
    $link = Join-Path $fixture "Minecraft\Personal\Instances\$profile\saves"
    $target = Join-Path $fixture "Minecraft\Worlds\$profile"
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
}
Put 'Minecraft\Packs\Minecraft 26.2\portable-pack-source.json' '{"schemaVersion":1,"owner":"MarkZamore","repo":"Minecraft-26.2","tag":"pack-latest"}'
$oldEmi = Join-Path $candidateRoot 'emi-1.1.24-b60b8f8+26.2+neoforge.jar'
$oldEmiSha = '72e4e63f3ba39c149d15681a864282c116d3cc064e978f0848958cd1bf71058e'
CopyExact $oldEmi 'Minecraft\Packs\Minecraft 26.2\mods\emi-1.1.24-b60b8f8+26.2+neoforge.jar' $oldEmiSha
CopyExact $oldEmi 'Minecraft\Personal\Instances\Minecraft 26.2\mods\emi-1.1.24-b60b8f8+26.2+neoforge.jar' $oldEmiSha
Put 'Minecraft\Packs\Minecraft 26.2\mods\fixture-unknown.jar' 'Unknown canonical fixture bytes must stay preserved.'
Put 'Minecraft\Personal\Instances\Minecraft 26.2\mods\fixture-unknown.jar' 'Different unknown instance fixture bytes must be preserved in PackConflicts.'
Put 'Minecraft\Personal\identity-profiles.properties' '00000000-0000-4000-8000-000000000001|DeploymentFixture'
Put 'Minecraft\Personal\skin-profiles.properties' '00000000-0000-4000-8000-000000000001|fixture-only'
$skin = Join-Path $fixture 'Minecraft\Personal\fixture-skin.png'
[IO.File]::WriteAllBytes($skin, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAl0lEQVR4nO3QMREAIBDAsJ+RiDDsgowMdMje66x97s9GB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoDVAB2gN0AFaA3SA1gAdoD3TbJHSso7RFAAAAABJRU5ErkJggg=='))
Put 'Minecraft\Personal\settings.json' (@{
    schemaVersion=2; playerName='DeploymentFixture'; previousPlayerName=''; maxMemoryGb=4
    memorySettingIsTheHeap=$true; memoryByPack=$memory; clientRelativePath='Minecraft 26.2'
    addedPackRelativePaths=$profiles; hiddenPackRelativePaths=@(); ll8LocalVisibilityMigrated=$true
    skinPath=$skin; selectedWorldRelativePath='Minecraft 26.2/DeploymentFixture'
} | ConvertTo-Json -Depth 8)
Write-Host "Generated independent fixture: $fixture"
Write-Host 'Copied only baseline descriptors and exact author transport/EMI JARs; world/player/skin data are generated sentinels.'
