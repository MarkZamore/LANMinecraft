# Replays the launch-time bytecode preflight against a chosen JDK without starting the game.
#
# A failed preflight has not stopped a launch since the adapter learned to step aside: the pack
# starts without the hooks and says so in the log. So this is how the failure is found before a
# player finds it - run it after touching the adapter, or before moving the game to another Java.
#
# What it replays are the aliases the launcher derived the LAST time this pack was started. A
# change to IdentityAdapterMappingService is therefore invisible here until the pack has been
# started once more.
param(
    # Defaults to JAVA_HOME, then to the Java the launcher runs 1.21.1 on: the shared JDK under
    # Minecraft\Launcher\JavaRuntimes, one install per feature release rather than one per pack.
    [string]$JavaHome,

    [string]$AdapterJar,

    # adapter-state.json holds the mapping aliases the launcher derived for this pack.
    [string]$StateJson,

    # The launcher's data folder - the one holding Launcher and Personal. Searched for when it is
    # not given, because it lives wherever the player put the launcher and not beside this repo.
    [string]$InstallRoot
)

$ErrorActionPreference = "Stop"
$commonRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath))
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $commonRoot "..\..\.."))

if (-not $AdapterJar) {
    $AdapterJar = Join-Path $projectRoot "Program\Build\IdentityAdapters\Minecraft-1.21.1-NeoForge\portable-identity-adapter.jar"
}
if (-not (Test-Path -LiteralPath $AdapterJar -PathType Leaf)) {
    throw "Adapter jar was not found: $AdapterJar. Build Program\Minecraft.csproj first."
}

# Where the launcher keeps its data. It used to be assumed to sit inside this repository, which
# it does on nobody's machine: the folder goes wherever the player unpacked the launcher.
$installRoots = @(
    $InstallRoot,
    $env:LANMINECRAFT_ROOT,
    (Join-Path $projectRoot "Minecraft"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "Minecraft")
) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ "Launcher") -PathType Container) }

if (-not $StateJson) {
    $StateJson = $installRoots |
        ForEach-Object {
            Get-ChildItem -LiteralPath (Join-Path $_ "Launcher\IdentityAdapters") `
                -Recurse -File -Filter "adapter-state.json" -ErrorAction SilentlyContinue
        } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $StateJson -or -not (Test-Path -LiteralPath $StateJson -PathType Leaf)) {
    throw ("adapter-state.json was not found under: " + ($installRoots -join "; ") +
        ". Launch the game once so the launcher derives the mappings, or pass -InstallRoot or -StateJson.")
}

# The install that wrote this state file, whichever of the candidates it was: the state names
# absolute paths into it, so the Java that ran the game is found beside them rather than guessed.
$installRoot = (Get-Item -LiteralPath $StateJson).Directory.Parent.Parent.Parent.FullName
if (-not $JavaHome) {
    $JavaHome = if ($env:JAVA_HOME) { $env:JAVA_HOME }
        else { Join-Path $installRoot "Launcher\JavaRuntimes\runtime\windows-x64\java-21" }
}
$java = Join-Path $JavaHome "bin\java.exe"
if (-not (Test-Path -LiteralPath $java -PathType Leaf)) {
    throw "java.exe was not found under: $JavaHome. Pass -JavaHome, or set JAVA_HOME to any JDK 21 or newer."
}

$state = Get-Content -LiteralPath $StateJson -Raw | ConvertFrom-Json
$properties = [ordered]@{}
foreach ($property in $state.properties.PSObject.Properties) {
    $properties[$property.Name] = [string]$property.Value
}

$targets = [System.Collections.Generic.List[object]]::new()
foreach ($target in $state.targets) {
    $targets.Add([pscustomobject]@{ JarPath = [string]$target.jarPath; ClassName = [string]$target.className })
}

$propertyArguments = foreach ($key in $properties.Keys) { "-Dminecraft.portable.identity.$key=$($properties[$key])" }

& $java -version
Write-Host ""
Write-Host "Adapter: $AdapterJar"
Write-Host "State:   $StateJson"
Write-Host "Targets: $($targets.Count)"
Write-Host ""

$failed = 0
foreach ($target in $targets) {
    & $java @propertyArguments -cp $AdapterJar `
        minecraft.portable.identity.PortableIdentityPreflight $target.JarPath $target.ClassName
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL $($target.ClassName)"
        $failed++
    }
}

# The skin path is the only transformer with runtime semantics worth replaying.
$skinTarget = $targets | Where-Object { $_.ClassName -eq "com/mojang/authlib/yggdrasil/TextureUrlChecker" } | Select-Object -First 1
if ($skinTarget) {
    $uuid = "00000000-0000-4000-8000-000000000001"
    $skinHash = "1111111111111111111111111111111111111111111111111111111111111111"
    $otherHash = "2222222222222222222222222222222222222222222222222222222222222222"
    $registry = Join-Path ([System.IO.Path]::GetTempPath()) ("skin-preflight-" + [Guid]::NewGuid().ToString("N") + ".properties")
    # PortableSkinProfiles parses the first field as a UUID, so the file must not
    # carry a BOM: Set-Content -Encoding utf8 would add one on Windows PowerShell.
    [System.IO.File]::WriteAllText(
        $registry,
        "$uuid|$skinHash|classic|http://127.0.0.1:35658/skin/$uuid/$skinHash",
        (New-Object System.Text.UTF8Encoding($false)))
    try {
        & $java @propertyArguments `
            "-Dminecraft.portable.identity.enabled=true" `
            "-Dminecraft.portable.skin.registry=$registry" `
            "-javaagent:$AdapterJar" `
            -cp "$AdapterJar$([System.IO.Path]::PathSeparator)$($skinTarget.JarPath)" `
            minecraft.portable.identity.PortableSkinPreflight `
            "http://127.0.0.1:35658/skin/$uuid/$skinHash" `
            "http://127.0.0.1:35658/skin/$uuid/$otherHash" `
            "https://textures.minecraft.net/texture/portable-preflight"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAIL skin semantic preflight"
            $failed++
        }
    } finally {
        Remove-Item -LiteralPath $registry -Force -ErrorAction SilentlyContinue
    }
}

# Modern immutable profiles need more than the domain-rule test. Replay the
# actual profile constructor and authlib reader/unpacker with their own
# manifest's dependency versions, rather than whichever Guava happens to be
# first in the shared library cache.
$modernAuthlibTargets = @($targets | Where-Object {
    $_.ClassName -eq 'com/mojang/authlib/GameProfile' -and
    [IO.Path]::GetFileName($_.JarPath) -match '^authlib-(\d+)\.' -and
    [int]$Matches[1] -ge 6
} | Group-Object JarPath | ForEach-Object { $_.Group[0] })
foreach ($profileTarget in $modernAuthlibTargets) {
    $matchingVersion = $null
    $versionsRoot = Join-Path $installRoot 'Launcher\Shared\versions'
    foreach ($versionDirectory in Get-ChildItem -LiteralPath $versionsRoot -Directory | Sort-Object Name) {
        $manifestPath = Join-Path $versionDirectory.FullName ($versionDirectory.Name + '.json')
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($library in $manifest.libraries) {
            if ($library.name -notlike 'com.mojang:authlib:*') { continue }
            $runtimeJar = [IO.Path]::GetFullPath((Join-Path $installRoot ('Launcher\Shared\libraries\' + $library.downloads.artifact.path)))
            if ($runtimeJar -eq [IO.Path]::GetFullPath($profileTarget.JarPath)) {
                $matchingVersion = $versionDirectory.Name
                break
            }
        }
        if ($matchingVersion) { break }
    }
    if (-not $matchingVersion) { throw 'No installed runtime manifest matches the authlib selected by this adapter state.' }
    & (Join-Path $projectRoot 'tools\Test-PortableSkinProfiles.ps1') `
        -InstallRoot $installRoot -AdapterJar $AdapterJar -JavaHome $JavaHome -MinecraftVersions @($matchingVersion)
}

Write-Host ""
if ($failed -gt 0) {
    throw "$failed preflight check(s) failed."
}
Write-Host "ALL PREFLIGHTS PASSED"
