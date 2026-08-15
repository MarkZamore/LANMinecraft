# Replays the launch-time bytecode preflight against a chosen JDK without starting the game.
# The preflight blocks the launch when it fails, so run this after touching the adapter
# or before moving the game to a different Java version.
param(
    # Defaults to the JDK that currently runs the game.
    [string]$JavaHome,

    [string]$AdapterJar,

    # adapter-state.json holds the mapping aliases the launcher derived for this pack.
    [string]$StateJson
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

if (-not $StateJson) {
    $StateJson = Get-ChildItem -LiteralPath (Join-Path $projectRoot "Minecraft\Launcher\IdentityAdapters") `
        -Recurse -File -Filter "adapter-state.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $StateJson -or -not (Test-Path -LiteralPath $StateJson -PathType Leaf)) {
    throw "adapter-state.json was not found. Launch the game once so the launcher derives the mappings."
}

$runtimeRoot = Join-Path $projectRoot "Minecraft\Launcher\Runtimes\Infinity"
if (-not $JavaHome) {
    $JavaHome = Join-Path $runtimeRoot "runtime\windows-x64\java-runtime-delta"
}
$java = Join-Path $JavaHome "bin\java.exe"
if (-not (Test-Path -LiteralPath $java -PathType Leaf)) {
    throw "java.exe was not found under: $JavaHome"
}

$state = Get-Content -LiteralPath $StateJson -Raw | ConvertFrom-Json
$properties = [ordered]@{}
foreach ($property in $state.properties.PSObject.Properties) {
    $properties[$property.Name] = [string]$property.Value
}

# Aliases added after the state file was written stay absent until the next launch,
# so seed the ones this pack resolves to. Existing values always win.
$srgJar = Join-Path $runtimeRoot "libraries\net\minecraft\client\1.21.1-20240808.144430\client-1.21.1-20240808.144430-srg.jar"
$clientJar = Join-Path $runtimeRoot "versions\1.21.1\1.21.1.jar"
$ftbJar = Join-Path $projectRoot "Minecraft\Packs\Infinity\mods\ftb-chunks-neoforge-2101.1.14.jar"
$seed = [ordered]@{
    "lanShareScreenClasses"        = "net/minecraft/client/gui/screens/ShareToLanScreen,foe"
    "lanShareInitMethods"          = "init,aT_"
    "integratedServerClasses"      = "net/minecraft/client/server/IntegratedServer,guo"
    "publishServerMethods"         = "publishServer,a"
    "getDefaultGameTypeMethods"    = "getDefaultGameType,u_"
    "getWorldDataMethods"          = "getWorldData,bb"
    "isPublishedMethods"           = "isPublished,r"
    "worldDataClasses"             = "net/minecraft/world/level/storage/WorldData,erl"
    "isAllowCommandsMethods"       = "isAllowCommands,m"
    "httpUtilClasses"              = "net.minecraft.util.HttpUtil,ayf"
    "getAvailablePortMethods"      = "getAvailablePort,a"
    "gameTypeClasses"              = "net.minecraft.world.level.GameType,dct"
    "publishCommandClasses"        = "net.minecraft.server.commands.PublishCommand,ans"
    "publishSuccessMethods"        = "getSuccessMessage,a"
    "minecraftClasses"             = "net.minecraft.client.Minecraft,fgo"
    "minecraftGetInstanceMethods"  = "getInstance,Q"
    "getSingleplayerServerMethods" = "getSingleplayerServer,V"
    "setScreenMethods"             = "setScreen,a"
    "updateTitleMethods"           = "updateTitle,d"
    "minecraftGuiFields"           = "gui,l"
    "screenClasses"                = "net.minecraft.client.gui.screens.Screen,fod"
    "guiClasses"                   = "net/minecraft/client/gui/Gui,fhy"
    "guiChatMethods"               = "getChat,d"
    "chatComponentClasses"         = "net/minecraft/client/gui/components/ChatComponent,fin"
    "chatAddMessageMethods"        = "addMessage,a"
    "ftbTeleportEnabled"           = "true"
    "ftbTeleportClasses"           = "dev/ftb/mods/ftbchunks/client/gui/WaypointEditorScreen`$RowPanel,dev/ftb/mods/ftbchunks/client/mapicon/WaypointMapIcon,dev/ftb/mods/ftbchunks/net/TeleportFromMapPacket"
    "ftbPermissionMethods"         = "hasPermissions"
}
foreach ($key in $seed.Keys) {
    if (-not $properties.Contains($key)) { $properties[$key] = $seed[$key] }
}

$targets = [System.Collections.Generic.List[object]]::new()
foreach ($target in $state.targets) {
    $targets.Add([pscustomobject]@{ JarPath = [string]$target.jarPath; ClassName = [string]$target.className })
}
foreach ($extra in @(
    @{ Jar = $srgJar;    Class = "net/minecraft/client/gui/screens/ShareToLanScreen" },
    @{ Jar = $clientJar; Class = "foe" },
    @{ Jar = $ftbJar;    Class = 'dev/ftb/mods/ftbchunks/client/gui/WaypointEditorScreen$RowPanel' },
    @{ Jar = $ftbJar;    Class = "dev/ftb/mods/ftbchunks/client/mapicon/WaypointMapIcon" },
    @{ Jar = $ftbJar;    Class = "dev/ftb/mods/ftbchunks/net/TeleportFromMapPacket" }
)) {
    if (-not (Test-Path -LiteralPath $extra.Jar -PathType Leaf)) { continue }
    $known = $targets | Where-Object { $_.ClassName -eq $extra.Class }
    if (-not $known) {
        $targets.Add([pscustomobject]@{ JarPath = $extra.Jar; ClassName = $extra.Class })
    }
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

Write-Host ""
if ($failed -gt 0) {
    throw "$failed preflight check(s) failed."
}
Write-Host "ALL PREFLIGHTS PASSED"
