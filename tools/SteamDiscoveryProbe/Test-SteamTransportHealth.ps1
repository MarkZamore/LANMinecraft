#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$TransportJar,
    [string]$JavaHome = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $JavaHome) { $JavaHome = 'C:\Tools\jdk-25.0.2' }
$hash = (Get-FileHash -LiteralPath $TransportJar -Algorithm SHA256).Hash.ToLowerInvariant()
$fixed = switch ($hash) {
    '9a0fc4e1a8c170f37e04cf8469091990d5c1f0953b334481dbb5eab9475e3e1a' { $false }
    '33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc' { $true }
    default { throw 'The health proof accepts only the reviewed Forge 0.3.2/0.3.4 release bytes.' }
}
$classes = Join-Path $repository 'artifacts\atm9-original-steam\e4steam-health-proof\classes'
New-Item -ItemType Directory -Path $classes -Force | Out-Null
& (Join-Path $JavaHome 'bin\javac.exe') --release 17 -d $classes (Join-Path $PSScriptRoot 'SteamTransportHealthProof.java')
if ($LASTEXITCODE -ne 0) { throw 'Health proof compilation failed.' }
$libraries = Join-Path $repository 'Minecraft\Launcher\Shared\libraries\org\apache\logging\log4j'
$api = Join-Path $libraries 'log4j-api\2.19.0\log4j-api-2.19.0.jar'
$core = Join-Path $libraries 'log4j-core\2.19.0\log4j-core-2.19.0.jar'
$cp = @($classes, (Resolve-Path -LiteralPath $TransportJar).Path, $api, $core) -join ';'
& (Join-Path $JavaHome 'bin\java.exe') -Xverify:all -cp $cp diagnostics.SteamTransportHealthProof $fixed.ToString().ToLowerInvariant()
if ($LASTEXITCODE -ne 0) { throw 'Actual release health regression failed.' }
