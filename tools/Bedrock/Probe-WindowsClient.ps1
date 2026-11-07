#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch] $IncludePersonalPaths,
    [switch] $Launch,
    [switch] $OpenStore
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsClientProbe.psm1') -Force
$result = Invoke-BedrockWindowsProbe -IncludePersonalPaths:$IncludePersonalPaths -Launch:$Launch -OpenStore:$OpenStore
$result | ConvertTo-Json -Depth 12
if ($result.discovery.status -eq 'error') { exit 2 }
if ($Launch -and $result.activation.status -notlike 'activation-request-accepted*') { exit 3 }
if ($OpenStore -and $result.installation.status -eq 'error') { exit 4 }
