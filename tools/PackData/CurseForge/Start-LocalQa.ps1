[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RunDirectory,
    [Parameter(Mandatory)][string] $InstallationRoot,
    [Parameter(Mandatory)][string] $GameDirectory,
    [Parameter(Mandatory)][string] $LauncherAssembly,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string] $PackName,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')][string] $Label,
    [ValidateRange(512, 32768)][int] $HeapMiB = 4096,
    [ValidateRange(15, 1800)][int] $MaximumSeconds = 600,
    [switch] $Interactive,
    [switch] $WithSteam,
    [string] $SteamComponentIndex,
    [string] $NativeQaAgent,
    [string] $NativeQaLibraryArchive,
    [ValidatePattern('^[0-9a-f]{64}$')][string] $NativeQaLibrarySha256,
    [switch] $UseProductionJavaArgumentFile,
    [ValidatePattern('^[a-z0-9_]+:[a-z0-9_./-]+$')][string] $NativeQaWorldPreset,
    [switch] $NativeQaRequireDefaultWorldPreset,
    [ValidatePattern('^[a-z0-9_]+:[a-z0-9_./-]+$')][string] $NativeQaExpectedInitialItem,
    [ValidatePattern('^[A-Za-z0-9_-]{1,16}$')][string] $NativeQaWorldSuffix,
    [ValidatePattern('^[0-9a-f]{64}$')][string] $NativeQaReopenLevelSha256,
    [string] $RunOwner = 'Codex'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$runs = [IO.Path]::Combine($workspace, 'artifacts', 'agent-runs')
$runRoot = [IO.Path]::GetFullPath($RunDirectory)
$pathComparison = [StringComparison]::OrdinalIgnoreCase

function Assert-NoReparsePath([string] $Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "QA paths cannot pass through reparse points: $current"
        }
    }
}

function Get-RunPath([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw "QA paths must stay below the task run: $full"
    }
    Assert-NoReparsePath $full
    return $full
}

function Write-Evidence([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

if (![string]::Equals([IO.Path]::GetDirectoryName($runRoot), $runs, $pathComparison)) {
    throw "RunDirectory must be an immediate child of $runs."
}
$ownerPath = Get-RunPath (Join-Path $runRoot 'RUN.json')
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.owner -cne $RunOwner) { throw 'The task run owner does not match.' }
$install = Get-RunPath $InstallationRoot
$game = Get-RunPath $GameDirectory
$sourceDescriptor = Get-RunPath (Join-Path $game 'portable-pack.json')
if (!(Test-Path -LiteralPath $install -PathType Container) -or
    !(Test-Path -LiteralPath $sourceDescriptor -PathType Leaf)) {
    throw 'Use the existing QA installation and staged game directory.'
}
foreach ($tree in @($install, $game)) {
    if (Get-ChildItem -LiteralPath $tree -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) {
        throw "QA installation or game input contains a reparse point: $tree"
    }
}
$mods = Join-Path $game 'mods'
if ($WithSteam -and !$game.StartsWith($install + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
    throw 'The Steam test game must be inside the QA installation.'
}
if ($NativeQaAgent) {
    if (!$WithSteam) { throw 'A native Steam QA agent requires -WithSteam.' }
    $NativeQaAgent = Get-RunPath $NativeQaAgent
    if (![IO.File]::Exists($NativeQaAgent) -or [IO.Path]::GetExtension($NativeQaAgent) -ne '.jar') {
        throw 'Supply a built native QA agent JAR inside the owned run.'
    }
}
if (($NativeQaWorldSuffix -or $NativeQaReopenLevelSha256) -and !$NativeQaAgent) {
    throw 'Native QA world settings require -NativeQaAgent.'
}
if (($NativeQaWorldPreset -and !$NativeQaAgent) -or ($NativeQaRequireDefaultWorldPreset -and !$NativeQaWorldPreset)) {
    throw 'Native QA preset verification requires an agent and an explicit preset.'
}
if ($NativeQaExpectedInitialItem -and !$NativeQaAgent) {
    throw 'Native QA item verification requires an agent.'
}
if ([bool]$NativeQaLibraryArchive -ne [bool]$NativeQaLibrarySha256 -or
    ($NativeQaLibraryArchive -and !$NativeQaAgent)) {
    throw 'Native title QA requires the native agent and both the archive and its SHA256.'
}
$card = $null
if ($NativeQaLibraryArchive) {
    $NativeQaLibraryArchive = Get-RunPath $NativeQaLibraryArchive
    if (!(Test-Path -LiteralPath $NativeQaLibraryArchive -PathType Leaf) -or
        (Get-Item -LiteralPath $NativeQaLibraryArchive).Length -gt 32MB) {
        throw 'Supply a bounded library archive inside the owned run.'
    }
    $cardBytes = [IO.File]::ReadAllBytes($NativeQaLibraryArchive)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cardBytes)) -ine $NativeQaLibrarySha256) {
        throw 'The QA library archive did not match the supplied SHA256.'
    }
}
if (!$WithSteam -and (Test-Path -LiteralPath $mods) -and
    (Get-ChildItem -LiteralPath $mods -Recurse -File -Filter '*e4steam*.jar' | Select-Object -First 1)) {
    throw 'This author baseline does not run with an installed Steam transport.'
}

$assemblyPath = [IO.Path]::GetFullPath($LauncherAssembly)
Assert-NoReparsePath $assemblyPath
if ([IO.Path]::GetFileName($assemblyPath) -cne 'LANMinecraft.dll' -or
    !(Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw 'Supply the existing LANMinecraft.dll from the selected launcher.'
}
$assemblyDirectory = [IO.Path]::GetDirectoryName($assemblyPath)
foreach ($name in @('CmlLib.Core.Commons.dll', 'CmlLib.Core.dll', 'LANMinecraft.dll')) {
    $dependency = Join-Path $assemblyDirectory $name
    Assert-NoReparsePath $dependency
    $assembly = [Reflection.Assembly]::LoadFrom($dependency)
}
$buildFlags = [Reflection.BindingFlags]'NonPublic, Static'
$instanceFlags = [Reflection.BindingFlags]'NonPublic, Instance'
$buildProcess = $assembly.GetType('Minecraft.MinecraftLaunchArguments', $true).GetMethod('BuildProcess', $buildFlags)
if (!$buildProcess) { throw 'This launcher assembly has no production BuildProcess entrypoint.' }
$componentIndexSha256 = $null
if ($SteamComponentIndex) {
    if (!$WithSteam) { throw 'A local Steam component index requires -WithSteam.' }
    $indexPath = Get-RunPath $SteamComponentIndex
    $catalogType = $assembly.GetType('Minecraft.SteamComponentCatalog', $true)
    $transportType = $assembly.GetType('Minecraft.SteamTransportCatalog', $true)
    $index = $catalogType.GetMethod('Parse', $buildFlags).Invoke($null, [object[]]@([IO.File]::ReadAllText($indexPath)))
    [void]$transportType.GetMethod('Activate', $buildFlags).Invoke($null, [object[]]@($index))
    $componentIndexSha256 = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

$lockPath = Get-RunPath (Join-Path $install '.local-qa.lock')
$lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
$runtimeService = $null
$identityAdapter = $null
$displayNames = $null
$notice = $null
$productionArgumentFile = $null
$productionArgumentPath = $null
$process = $null
$timeout = $null
$captures = @()
$started = $false
$stopReason = $null
try {
    $paths = [Minecraft.AppPaths]::new($install)
    $paths.Ensure()
    if ($NativeQaLibraryArchive) {
        $zip = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($cardBytes, $false))
        try {
            $entry = $zip.GetEntry('library.json')
            if (!$entry -or $entry.Length -gt 4MB) { throw 'Invalid library JSON entry.' }
            $stream = [IO.StreamReader]::new($entry.Open())
            try { $metadata = $stream.ReadToEnd() | ConvertFrom-Json }
            finally { $stream.Dispose() }
        }
        finally { $zip.Dispose() }
        $source = [Minecraft.PackSyncSource]::new('local', $PackName, 'qa')
        $entry = [Minecraft.CatalogSource]::new($metadata.id, $metadata.relativePath, $source)
        $entry.LegacyRelativePaths.Add($PackName)
        $decode = $assembly.GetType('Minecraft.PackCatalogService', $true).GetMethod('DecodeBundle', $buildFlags)
        $card = $decode.Invoke($null, [object[]]@($entry, $cardBytes, $metadata.revision))
        $displayNames = [Minecraft.PackDisplayNames]::new($paths)
        [void]$displayNames.Publish([Minecraft.CatalogPack[]]@($card))
    }
    $canonical = Get-RunPath ($paths.CombineUnderPacks($PackName))
    $canonicalDescriptor = Get-RunPath (Join-Path $canonical 'portable-pack.json')
    $descriptorSha256 = (Get-FileHash -LiteralPath $sourceDescriptor -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([IO.File]::Exists($canonicalDescriptor)) {
        if ((Get-FileHash -LiteralPath $canonicalDescriptor -Algorithm SHA256).Hash -ine $descriptorSha256) {
            throw 'The canonical descriptor differs from the staged game descriptor.'
        }
    } else {
        [void][IO.Directory]::CreateDirectory($canonical)
        [IO.File]::Copy($sourceDescriptor, $canonicalDescriptor, $false)
    }

    $evidence = Get-RunPath (Join-Path $install "evidence/$Label")
    if (Test-Path -LiteralPath $evidence) { throw "Use a new QA evidence label: $Label" }
    [void][IO.Directory]::CreateDirectory($evidence)
    $qaHome = Join-Path $evidence 'java-home'
    $qaTemp = Join-Path $evidence 'java-temp'
    $qaRoaming = Join-Path $qaHome 'AppData/Roaming'
    $qaLocal = Join-Path $qaHome 'AppData/Local'
    foreach ($directory in @($qaTemp, $qaRoaming, $qaLocal)) {
        [void][IO.Directory]::CreateDirectory($directory)
    }

    $logger = [Minecraft.Logger]::new($paths.LogFile)
    $runtimeService = [Minecraft.PackRuntimeService]::new($paths, $logger, $null, $null)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(45))
    $runtime = $runtimeService.PrepareAsync($PackName, $null, $timeout.Token).GetAwaiter().GetResult()
    $player = 'LanMcPackQa'
    $adapterArguments = @()
    $steamBuild = $null
    if ($WithSteam) {
        if (!$runtime.Descriptor.PreserveWorldSettingsOnShare) { throw 'The QA descriptor must opt into the world-sharing policy.' }
        $steamBuild = [Minecraft.SteamTransportCatalog]::Find($runtime.Descriptor)
        if (!$steamBuild) { throw 'The QA launcher has no verified Steam transport for this descriptor.' }
        $managed = [Minecraft.ManagedComponentService]::new($paths, $logger, $null)
        $instance = [Minecraft.PackInstanceContext]::new($canonical, $game, $runtime.ClientJarPath)
        [void]$managed.EnsureSteamTransportModAsync($instance, $runtime.Descriptor, $timeout.Token).GetAwaiter().GetResult()
        $identityAdapter = [Minecraft.PortableIdentityAdapterService]::new($paths, $logger)
        $adapterArguments = @($identityAdapter.PrepareJvmArgumentsAsync($runtime, $game, $timeout.Token).GetAwaiter().GetResult())
        if ($steamBuild.PreservesWorldSettingsOnShare) {
            $adapterArguments += "-Dlink.e4steam.launcherPlayerName=$player"
        }
    }
    $launcher = $runtimeService.CreateLocalLauncher($runtime)
    $profile = $launcher.GetVersionAsync($runtime.ProfileId, $timeout.Token).GetAwaiter().GetResult()
    $presentationArguments = @()
    if ($card) {
        $notices = [Minecraft.GameNoticeOverlayService]::new($paths, $logger)
        $notice = $notices.GetType().GetMethod('PrepareSession', $instanceFlags).Invoke($notices, [object[]]@($false))
        $presentation = $assembly.GetType('Minecraft.GameWindowPresentation', $true).GetMethod('Prepare', $buildFlags)
        $presentationArguments = @($notice.GetType().GetProperty('JvmArguments', $instanceFlags).GetValue($notice)) +
            @($presentation.Invoke($null, [object[]]@($paths, $runtime, $profile, $canonical, $game)))
    }
    $launchPath = [CmlLib.Core.MinecraftPath]::new($game)
    foreach ($property in @('Library', 'Versions', 'Assets', 'Resource', 'Runtime')) {
        $launchPath.$property = $launcher.MinecraftPath.$property
    }
    $launchPath.CreateDirs()

    $session = [CmlLib.Core.Auth.MSession]::CreateOfflineSession($player)
    $uuid = [Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes("OfflinePlayer:$player"))
    $uuid[6] = ($uuid[6] -band 15) -bor 48
    $uuid[8] = ($uuid[8] -band 63) -bor 128
    $session.UUID = [Convert]::ToHexString($uuid).ToLowerInvariant()
    $options = [CmlLib.Core.ProcessBuilder.MLaunchOption]::new()
    $options.Path = $launchPath
    $options.JavaPath = $runtime.JavaPath
    $options.Session = $session
    $options.MaximumRamMb = $HeapMiB
    $options.MinimumRamMb = [Math]::Min(1024, $HeapMiB)
    $options.GameLauncherName = 'LANMinecraft'
    $options.GameLauncherVersion = '1'
    $options.VersionType = "$($runtime.Descriptor.Loader.Type) $($runtime.Descriptor.Loader.Version)"
    $options.FullScreen = $false
    $options.ScreenWidth = 960
    $options.ScreenHeight = 540
    $arguments = [Collections.Generic.List[CmlLib.Core.ProcessBuilder.MArgument]]::new()
    foreach ($value in @('-Dfile.encoding=UTF-8', "-Duser.home=$qaHome", "-Djava.io.tmpdir=$qaTemp",
                         ('-XX:ErrorFile=' + (Join-Path $evidence 'hs_err_pid%p.log')))) {
        $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new($value))
    }
    $packArguments = [Minecraft.PackJvmArgumentsService]::new($logger).Load($game)
    foreach ($value in $packArguments) { $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new($value)) }
    foreach ($value in $adapterArguments) { $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new($value)) }
    foreach ($value in $presentationArguments) { $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new($value)) }
    if ($NativeQaAgent) {
        foreach ($value in @("-Dlanminecraft.steamqa.game=$game", "-Dlanminecraft.steamqa.evidence=$evidence",
            "-Dlanminecraft.steamqa.run=$runRoot", "-Dlanminecraft.steamqa.minecraftVersion=$($runtime.Descriptor.MinecraftVersion)",
            "-javaagent:$NativeQaAgent")) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new($value))
        }
        if ($NativeQaWorldSuffix) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new("-Dlanminecraft.steamqa.worldSuffix=$NativeQaWorldSuffix"))
        }
        if ($NativeQaReopenLevelSha256) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new("-Dlanminecraft.steamqa.reopenLevelSha256=$NativeQaReopenLevelSha256"))
        }
        if ($NativeQaWorldPreset) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new("-Dlanminecraft.steamqa.worldPreset=$NativeQaWorldPreset"))
            if ($NativeQaRequireDefaultWorldPreset) {
                $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new('-Dlanminecraft.steamqa.requireDefaultWorldPreset=true'))
            }
        }
        if ($NativeQaExpectedInitialItem) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new("-Dlanminecraft.steamqa.expectedInitialItem=$NativeQaExpectedInitialItem"))
        }
        if ($card) {
            $arguments.Add([CmlLib.Core.ProcessBuilder.MArgument]::new("-Dlanminecraft.steamqa.expectedWindowTitle=$($card.Content.Name)"))
        }
    }
    $options.ExtraJvmArguments = $arguments

    $process = $buildProcess.Invoke($null, [object[]]@($launcher, $profile, $options))
    $start = $process.StartInfo
    # Keep the production Java command line unless verifying its long-command path.
    $start.WorkingDirectory = $game
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = if ($Interactive) { 'Normal' } else { 'Hidden' }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($name in @('SteamAppId', 'SteamGameId', 'SteamOverlayGameId', 'SteamNoOverlayUIDrawing',
                        'JAVA_TOOL_OPTIONS', '_JAVA_OPTIONS', 'JDK_JAVA_OPTIONS')) {
        [void]$start.Environment.Remove($name)
    }
    if ($WithSteam) {
        $appId = [Minecraft.SteamworksApiFacade]::SharedAppId.ToString([Globalization.CultureInfo]::InvariantCulture)
        $start.Environment['SteamAppId'] = $appId
        $start.Environment['SteamGameId'] = $appId
    }
    $start.Environment['TEMP'] = $qaTemp
    $start.Environment['TMP'] = $qaTemp
    $start.Environment['APPDATA'] = $qaRoaming
    $start.Environment['LOCALAPPDATA'] = $qaLocal
    $start.Environment['USERPROFILE'] = $qaHome

    $javaArgumentFileUsed = $false
    $windowsCommandLengthBefore = $null
    $windowsCommandLengthAfter = $null
    if ($UseProductionJavaArgumentFile) {
        $processType = $assembly.GetType('Minecraft.MinecraftProcessService', $true)
        $complete = $processType.GetMethod('CompleteJvmArguments', $buildFlags)
        $completeArguments = $complete.Invoke($null, [object[]]@($start, $profile, $game, [Minecraft.PackJvmArgumentsService]::new(), $null))
        $mainClass = $profile.MainClass
        for ($parent = $profile.ParentVersion; !$mainClass -and $parent; $parent = $parent.ParentVersion) {
            $mainClass = $parent.MainClass
        }
        $mainIndex = [Array]::IndexOf([string[]]$completeArguments, [string]$mainClass)
        if ($mainIndex -lt 0) { throw 'The QA profile has no main class for production argument-file verification.' }
        $runtimeCatalog = $assembly.GetType('Minecraft.JavaRuntimeCatalog', $true)
        $runtimePin = $runtimeCatalog.GetMethod('RequiredFor').Invoke($null, [object[]]@($runtime.Descriptor))
        $argFileType = $assembly.GetType('Minecraft.JavaLaunchArgumentFile', $true)
        $windowsLength = $argFileType.GetMethod('WindowsLength', $buildFlags)
        $windowsCommandLengthBefore = $windowsLength.Invoke($null, [object[]]@($start))
        $productionArgumentFile = $argFileType.GetMethod('Prepare', $buildFlags).Invoke($null,
            [object[]]@($paths, $start, $completeArguments, $runtimePin.MajorVersion, $mainIndex, $logger))
        $windowsCommandLengthAfter = $windowsLength.Invoke($null, [object[]]@($start))
        if ($productionArgumentFile) {
            if (!$start.ArgumentList[0].StartsWith('@', [StringComparison]::Ordinal)) { throw 'Expected a production Java argument file.' }
            $productionArgumentPath = Get-RunPath ($start.ArgumentList[0].Substring(1))
            $javaArgumentFileUsed = $true
        }
    }

    Write-Evidence (Join-Path $evidence 'launch.json') ([ordered]@{
        mode = if ($WithSteam) { 'launcher-steam' } else { 'author-baseline' }
        launcherAssemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
        descriptorSha256 = $descriptorSha256
        profile = $runtime.ProfileId
        javaPath = $runtime.JavaPath
        clientJar = $runtime.ClientJarPath
        gameDirectory = $game
        minecraftVersion = $runtime.Descriptor.MinecraftVersion
        loader = $runtime.Descriptor.Loader
        heapMiB = $HeapMiB
        player = $player
        steam = if ($WithSteam) { $steamBuild.Sha256 } else { 'NOT RUN' }
        steamComponentIndexSha256 = $componentIndexSha256
        nativeQaAgentSha256 = if ($NativeQaAgent) { (Get-FileHash -LiteralPath $NativeQaAgent -Algorithm SHA256).Hash.ToLowerInvariant() }
        nativeQaLibrarySha256 = if ($card) { $NativeQaLibrarySha256 }
        javaArgumentFileUsed = $javaArgumentFileUsed
        productionJavaArgumentFileMode = [bool]$UseProductionJavaArgumentFile
        windowsCommandLengthBefore = $windowsCommandLengthBefore
        windowsCommandLengthAfter = $windowsCommandLengthAfter
    })
    $started = $process.Start()
    if (!$started) { throw 'Production BuildProcess did not start a Java child.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Evidence (Join-Path $evidence 'run.json') ([ordered]@{
        processId = $process.Id
        startedUtc = [DateTimeOffset]::UtcNow
        maximumSeconds = $MaximumSeconds
        gameDirectory = $game
    })
    Write-Host "CLIENT_STARTED pid=$($process.Id) label=$Label evidence=$evidence"

    $logLimit = 16MB
    foreach ($stream in @(
        @{ Name = 'stdout'; Reader = $process.StandardOutput }
        @{ Name = 'stderr'; Reader = $process.StandardError }
    )) {
        $buffer = [char[]]::new(4096)
        $capture = [pscustomobject]@{
            Name = $stream.Name
            Reader = $stream.Reader
            Buffer = $buffer
            Task = $stream.Reader.ReadAsync($buffer, 0, $buffer.Length)
            Output = [IO.StreamWriter]::new((Join-Path $evidence "$($stream.Name).log"), $false, [Text.Encoding]::UTF8)
            Written = 0
            Truncated = $false
        }
        $capture.Output.AutoFlush = $true
        $captures += $capture
    }
    $nextLogCheck = 1
    $gameLogs = Join-Path $game 'logs'
    do {
        foreach ($capture in $captures) {
            if (!$capture.Task -or !$capture.Task.IsCompleted) { continue }
            $read = $capture.Task.GetAwaiter().GetResult()
            if ($read -eq 0) {
                $capture.Task = $null
                continue
            }
            $bytes = [Text.Encoding]::UTF8.GetByteCount($capture.Buffer, 0, $read)
            if ($capture.Truncated -or $capture.Written + $bytes -gt $logLimit) {
                $capture.Truncated = $true
            } else {
                $capture.Output.Write($capture.Buffer, 0, $read)
                $capture.Written += $bytes
            }
            $capture.Task = $capture.Reader.ReadAsync($capture.Buffer, 0, $capture.Buffer.Length)
        }
        if (!$process.HasExited -and $clock.Elapsed.TotalSeconds -ge $MaximumSeconds) {
            $stopReason = 'lifetime-limit'
            $process.Kill($true)
        }
        if (!$process.HasExited -and $clock.Elapsed.TotalSeconds -ge $nextLogCheck) {
            $nextLogCheck = $clock.Elapsed.TotalSeconds + 1
            if (Test-Path -LiteralPath $gameLogs -PathType Container) {
                $logSize = (Get-ChildItem -LiteralPath $gameLogs -Recurse -File | Measure-Object Length -Sum).Sum
                if ($logSize -gt 128MB) {
                    $stopReason = 'game-log-limit'
                    $process.Kill($true)
                }
            }
        }
        if ($clock.Elapsed.TotalSeconds -gt $MaximumSeconds + 5) { break }
        Start-Sleep -Milliseconds 100
    } while (!$process.HasExited -or ($captures | Where-Object Task))
}
finally {
    if ($started -and !$process.HasExited) {
        if (!$stopReason) { $stopReason = 'runner-stopped' }
        $process.Kill($true)
    }
    foreach ($capture in $captures) { $capture.Output.Dispose() }
    $childExited = $started -and $process.WaitForExit(5000)
    if ($productionArgumentFile) { $productionArgumentFile.Dispose() }
    if ($childExited) {
        Write-Evidence (Join-Path $evidence 'exit.json') ([ordered]@{
            processId = $process.Id
            exitCode = $process.ExitCode
            stopReason = $stopReason
            productionJavaArgumentFileRemoved = if ($productionArgumentPath) { ![IO.File]::Exists($productionArgumentPath) }
            exitedUtc = [DateTimeOffset]::UtcNow
            console = @($captures | ForEach-Object { @{ name = $_.Name; writtenBytes = $_.Written; truncated = $_.Truncated } })
        })
        Write-Host "CLIENT_EXIT code=$($process.ExitCode) stop=$stopReason"
    }
    if ($null -ne $process) { $process.Dispose() }
    if ($null -ne $timeout) { $timeout.Dispose() }
    if ($null -ne $identityAdapter) { $identityAdapter.Dispose() }
    if ($null -ne $notice) { $notice.Dispose() }
    if ($null -ne $displayNames) { $displayNames.Dispose() }
    if ($null -ne $runtimeService) { $runtimeService.Dispose() }
    $lock.Dispose()
}
