# Replays the selected-skin profile path through the real installed authlib.
# This is an offline semantic bench, not a visual or two-client game check.
param(
    [string]$InstallRoot = (Join-Path $PSScriptRoot '..\Minecraft'),
    [string]$AdapterJar = (Join-Path $PSScriptRoot '..\Program\Build\IdentityAdapters\Minecraft-1.21.1-NeoForge\portable-identity-adapter.jar'),
    [string]$JavaHome = $env:JAVA_HOME,
    [string[]]$MinecraftVersions = @('26.2', '1.21.1')
)

$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$AdapterJar = [IO.Path]::GetFullPath($AdapterJar)
if (-not $JavaHome) {
    $JavaHome = Join-Path $InstallRoot 'Launcher\JavaRuntimes\runtime\windows-x64\java-25'
}
$java = Join-Path $JavaHome 'bin\java.exe'
if (-not (Test-Path -LiteralPath $java -PathType Leaf)) { throw "Java not found: $java" }
if (-not (Test-Path -LiteralPath $AdapterJar -PathType Leaf)) { throw "Adapter not found: $AdapterJar" }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\PortableSkinProfiles'))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($version in $MinecraftVersions) {
    if ($version -notmatch '^[0-9A-Za-z._-]+$') { throw 'Invalid version identifier.' }
    $manifestPath = Join-Path $InstallRoot "Launcher\Shared\versions\$version\$version.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $dependencies = @($manifest.libraries | Where-Object {
        $_.name -match '^(com\.mojang:authlib|com\.google\.guava:(guava|failureaccess)|com\.google\.code\.gson:gson|org\.slf4j:slf4j-api|org\.apache\.commons:commons-lang3):'
    })
    $authlib = @($dependencies | Where-Object { $_.name -like 'com.mojang:authlib:*' })
    if ($authlib.Count -ne 1) { throw "Expected exactly one authlib in Minecraft $version." }
    $classpath = @($AdapterJar)
    foreach ($dependency in $dependencies) {
        $path = Join-Path $InstallRoot ('Launcher\Shared\libraries\' + $dependency.downloads.artifact.path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Runtime dependency not installed: $($dependency.name)" }
        $classpath += $path
    }
    $uuid = '00000000-0000-4000-8000-000000000001'
    $hash = '1111111111111111111111111111111111111111111111111111111111111111'
    $otherHash = '2222222222222222222222222222222222222222222222222222222222222222'
    $url = "http://127.0.0.1:35658/skin/$uuid/$hash"
    $registry = Join-Path $outputRoot "$version.properties"
    [IO.File]::WriteAllText($registry, "$uuid|$hash|slim|$url", $utf8)
    Write-Host "Minecraft $version, $($authlib[0].name)"
    $javaArguments = @(
        '-Dminecraft.portable.identity.enabled=true',
        '-Dminecraft.portable.identity.identityHooksEnabled=false',
        '-Dminecraft.portable.identity.lanPublishEnabled=false',
        "-Dminecraft.portable.skin.registry=$registry",
        "-javaagent:$AdapterJar",
        '-cp', ($classpath -join [IO.Path]::PathSeparator),
        'minecraft.portable.identity.PortableSkinPreflight',
        $url,
        "http://127.0.0.1:35658/skin/$uuid/$otherHash",
        'https://textures.minecraft.net/texture/portable-preflight',
        '--profiles'
    )
    & $java @javaArguments
    if ($LASTEXITCODE -ne 0) { throw "Minecraft $version skin semantic preflight failed ($LASTEXITCODE)." }
}
Write-Host 'ALL SKIN PROFILE PREFLIGHTS PASSED'
