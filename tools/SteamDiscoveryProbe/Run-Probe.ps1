param(
    [string]$JavaHome = $env:JAVA_HOME,
    [ValidateSet('0.3.2', '0.3.4')][string]$TransportVersion = '0.3.4'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $JavaHome) { $JavaHome = 'C:\Tools\jdk-25.0.2' }
$resultRoot = Join-Path $repository 'artifacts\atm9-original-steam\discovery-probe'
$classes = Join-Path $resultRoot 'classes'
New-Item -ItemType Directory -Force -Path $classes | Out-Null
$common = Join-Path $repository 'Program\IdentityAdapters\Common'
$asm = @((Join-Path $common 'lib\asm-9.8.jar'), (Join-Path $common 'lib\asm-tree-9.8.jar')) -join ';'
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Filter '*.java' | ForEach-Object FullName)
$sources += Join-Path $common 'PortableSteamDiscoveryHooks.java'
$sources += Join-Path $common 'PortableSteamDiscoveryTransformer.java'
& (Join-Path $JavaHome 'bin\javac.exe') --release 17 -cp $asm -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw 'Discovery probe javac failed.' }
[IO.File]::WriteAllText((Join-Path $classes 'e4steam-version.properties'), "version=$TransportVersion`n", [Text.UTF8Encoding]::new($false))
& (Join-Path $JavaHome 'bin\java.exe') -Xverify:all -cp "$classes;$asm" minecraft.portable.identity.SteamDiscoveryProbe $TransportVersion |
    Tee-Object -FilePath (Join-Path $resultRoot 'stdout.log')
if ($LASTEXITCODE -ne 0) { throw 'Discovery semantic probe failed.' }
