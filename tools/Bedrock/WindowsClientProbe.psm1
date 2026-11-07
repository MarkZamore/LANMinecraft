Set-StrictMode -Version Latest

# A registered, Store-signed production identity is the trust boundary. This is
# deliberately not a search for executable names, Preview, Education or launchers.
$script:ProductionName = 'Microsoft.MinecraftUWP'
$script:ProductionFamily = 'Microsoft.MinecraftUWP_8wekyb3d8bbwe'
$script:MicrosoftPublisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
$script:StoreUri = 'ms-windows-store://pdp/?ProductId=9NBLGGH2JHXJ'

function Get-BedrockErrorCode {
    param([Parameter(Mandatory)] [System.Exception] $Exception)
    # Exception messages can contain user paths or identifiers.
    return '{0}:0x{1:X8}' -f $Exception.GetType().Name, ($Exception.HResult -band 0xffffffffL)
}

function Test-BedrockProductionPackage {
    param([Parameter(Mandatory)] [object] $Package)
    $expected = @{
        Name = $script:ProductionName
        PackageFamilyName = $script:ProductionFamily
        PublisherId = '8wekyb3d8bbwe'
        Publisher = $script:MicrosoftPublisher
        SignatureKind = 'Store'
        Status = 'Ok'
    }
    foreach ($key in $expected.Keys) {
        if (-not $Package.PSObject.Properties[$key] -or
            -not [string]::Equals([string]$Package.$key, $expected[$key], [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    if (-not $Package.PSObject.Properties['IsDevelopmentMode'] -or
        $Package.IsDevelopmentMode -isnot [bool] -or $Package.IsDevelopmentMode -ne $false) {
        return $false
    }
    return $true
}

function Get-BedrockActivationId {
    param(
        [Parameter(Mandatory)] [object] $Package,
        [Parameter(Mandatory)] [xml] $Manifest
    )
    if (-not (Test-BedrockProductionPackage $Package)) {
        throw [InvalidOperationException]::new('Untrusted production package.')
    }
    # Use only the registered manifest, and never its Executable or arguments.
    $identities = @($Manifest.SelectNodes('/*[local-name()="Package"]/*[local-name()="Identity"]'))
    if ($identities.Count -ne 1 -or
        -not [string]::Equals($identities[0].GetAttribute('Name'), $script:ProductionName, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($identities[0].GetAttribute('Publisher'), $script:MicrosoftPublisher, [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('Registered manifest identity mismatch.')
    }
    $applications = @($Manifest.SelectNodes('/*[local-name()="Package"]/*[local-name()="Applications"]/*[local-name()="Application"]'))
    if ($applications.Count -ne 1) {
        throw [InvalidOperationException]::new('Ambiguous registered applications.')
    }
    $applicationId = $applications[0].GetAttribute('Id')
    if ($applicationId -cnotmatch '^[A-Za-z][A-Za-z0-9.]{0,63}$') {
        throw [InvalidOperationException]::new('Invalid registered application ID.')
    }
    return $script:ProductionFamily + '!' + $applicationId
}

function Get-BedrockPlainPathState {
    param([Parameter(Mandatory)] [string] $LiteralPath)
    try {
        $cursor = [IO.Path]::GetFullPath($LiteralPath)
        $missing = $false
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor -ErrorAction Stop) {
                $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return 'blocked-reparse' }
            } else {
                $missing = $true
            }
            $parent = [IO.Directory]::GetParent($cursor)
            if ($null -eq $parent) { break }
            $cursor = $parent.FullName
        }
        if ($missing) { return 'missing' }
        return 'ok'
    } catch {
        return 'error'
    }
}

function Get-BedrockWorldLocation {
    param(
        [Parameter(Mandatory)] [string] $LiteralPath,
        [Parameter(Mandatory)] [string] $Layout,
        [switch] $IncludePersonalPaths
    )
    $result = [ordered]@{ layout = $Layout; status = (Get-BedrockPlainPathState $LiteralPath); candidateCount = 0; skippedReparseCount = 0 }
    if ($IncludePersonalPaths) { $result.path = $LiteralPath; $result.candidatePaths = @() }
    if ($result.status -ne 'ok') { return [pscustomobject]$result }
    try {
        foreach ($candidate in @(Get-ChildItem -LiteralPath $LiteralPath -Directory -Force -ErrorAction Stop)) {
            $candidateState = Get-BedrockPlainPathState $candidate.FullName
            if ($candidateState -eq 'blocked-reparse') { $result.skippedReparseCount++; continue }
            if ($candidateState -ne 'ok') { $result.status = 'partial'; continue }
            $level = Join-Path $candidate.FullName 'level.dat'
            $database = Join-Path $candidate.FullName 'db'
            $levelState = Get-BedrockPlainPathState $level
            $databaseState = Get-BedrockPlainPathState $database
            if ($levelState -eq 'blocked-reparse' -or $databaseState -eq 'blocked-reparse') {
                $result.skippedReparseCount++; continue
            }
            if ($levelState -eq 'error' -or $databaseState -eq 'error') { $result.status = 'partial'; continue }
            if ($levelState -eq 'ok' -and $databaseState -eq 'ok' -and
                (Test-Path -LiteralPath $level -PathType Leaf -ErrorAction Stop) -and
                (Test-Path -LiteralPath $database -PathType Container -ErrorAction Stop)) {
                $result.candidateCount++
                if ($IncludePersonalPaths) { $result.candidatePaths += $candidate.FullName }
            }
        }
    } catch {
        $result.status = 'error'
        $result.errorCode = Get-BedrockErrorCode $_.Exception
    }
    return [pscustomobject]$result
}

function Get-BedrockWorldInventory {
    param(
        [Parameter(Mandatory)] [string] $RoamingAppData,
        [Parameter(Mandatory)] [string] $LocalAppData,
        [switch] $IncludePersonalPaths
    )
    $locations = [Collections.Generic.List[object]]::new()
    $usersRoot = Join-Path $RoamingAppData 'Minecraft Bedrock/users'
    $usersState = Get-BedrockPlainPathState $usersRoot
    if ($usersState -eq 'ok') {
        try {
            foreach ($profile in @(Get-ChildItem -LiteralPath $usersRoot -Directory -Force -ErrorAction Stop)) {
                # The literal shared directory is not an individual player profile.
                if ($profile.Name -eq 'shared') { continue }
                $worlds = Join-Path $profile.FullName 'games/com.mojang/minecraftWorlds'
                $locations.Add((Get-BedrockWorldLocation -LiteralPath $worlds -Layout 'gdk' -IncludePersonalPaths:$IncludePersonalPaths))
            }
        } catch {
            $locations.Add([pscustomobject]@{ layout = 'gdk-users'; status = 'error'; candidateCount = 0; skippedReparseCount = 0; errorCode = (Get-BedrockErrorCode $_.Exception) })
        }
    } else {
        $entry = [ordered]@{ layout = 'gdk-users'; status = $usersState; candidateCount = 0; skippedReparseCount = 0 }
        if ($IncludePersonalPaths) { $entry.path = $usersRoot }
        $locations.Add([pscustomobject]$entry)
    }
    $legacyRoot = Join-Path $LocalAppData ('Packages/' + $script:ProductionFamily + '/LocalState/games/com.mojang/minecraftWorlds')
    $locations.Add((Get-BedrockWorldLocation -LiteralPath $legacyRoot -Layout 'uwp' -IncludePersonalPaths:$IncludePersonalPaths))
    $candidateCount = 0
    foreach ($location in $locations) { $candidateCount += $location.candidateCount }
    return [pscustomobject]@{
        status = 'read-only'
        candidateCount = $candidateCount
        validation = 'directory-shape-only; databases and player identities are not opened'
        locations = @($locations.ToArray())
    }
}

function Invoke-BedrockApplicationActivation {
    param([Parameter(Mandatory)] [string] $ApplicationUserModelId)
    if ($ApplicationUserModelId -cnotmatch '^Microsoft\.MinecraftUWP_8wekyb3d8bbwe![A-Za-z][A-Za-z0-9.]{0,63}$') {
        throw [InvalidOperationException]::new('Activation identity is outside the production package.')
    }
    if (-not ('LanMinecraft.BedrockProbe.NativeActivation' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LanMinecraft.BedrockProbe {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appId, IntPtr items, IntPtr verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appId, IntPtr items, out uint processId);
    }
    public static class NativeActivation {
        public static uint Activate(string appId) {
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
            try {
                uint processId;
                int hr = ((IApplicationActivationManager)instance).ActivateApplication(appId, null, 0, out processId);
                Marshal.ThrowExceptionForHR(hr);
                return processId;
            } finally {
                if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
            }
        }
    }
}
'@
    }
    return [LanMinecraft.BedrockProbe.NativeActivation]::Activate($ApplicationUserModelId)
}

function Invoke-BedrockWindowsProbe {
    param([switch] $IncludePersonalPaths, [switch] $Launch, [switch] $OpenStore)
    if ($Launch -and $OpenStore) { throw 'Choose either -Launch or -OpenStore.' }
    $result = [ordered]@{
        schemaVersion = 1
        discovery = [ordered]@{ status = 'not-installed'; verifiedPackageCount = 0; rejectedCandidateCount = 0 }
        entitlement = 'unknown; package registration and activation do not establish a license'
        activation = [ordered]@{ status = 'not-run' }
        installation = [ordered]@{ status = 'not-run'; officialStoreProductId = '9NBLGGH2JHXJ' }
        microsoftSignIn = 'not-tested'
        nativeLan = 'not-tested'
        steamGameConnection = 'not-implemented; not-tested'
    }
    $package = $null
    $activationId = $null
    try {
        $candidates = @(Get-AppxPackage -Name $script:ProductionName -ErrorAction Stop)
        $verified = @($candidates | Where-Object { Test-BedrockProductionPackage $_ })
        $result.discovery.verifiedPackageCount = $verified.Count
        $result.discovery.rejectedCandidateCount = $candidates.Count - $verified.Count
        if ($verified.Count -eq 1) {
            $package = $verified[0]
            $manifest = Get-AppxPackageManifest -Package $package -ErrorAction Stop
            $activationId = Get-BedrockActivationId -Package $package -Manifest $manifest
            $result.discovery.status = 'registered-production-client'
            $result.discovery.version = [string]$package.Version
            $result.discovery.applicationUserModelId = $activationId
            if ($IncludePersonalPaths) { $result.discovery.installLocation = [string]$package.InstallLocation }
        } elseif ($verified.Count -gt 1) {
            $result.discovery.status = 'ambiguous'
        } elseif ($candidates.Count -gt 0) {
            $result.discovery.status = 'untrusted-candidates'
        }
    } catch {
        $result.discovery.status = 'error'
        $result.discovery.errorCode = Get-BedrockErrorCode $_.Exception
    }
    $result.worlds = Get-BedrockWorldInventory -RoamingAppData ([Environment]::GetFolderPath('ApplicationData')) -LocalAppData ([Environment]::GetFolderPath('LocalApplicationData')) -IncludePersonalPaths:$IncludePersonalPaths
    if ($OpenStore) {
        try {
            Start-Process -FilePath $script:StoreUri -ErrorAction Stop | Out-Null
            $result.installation.status = 'store-page-requested; installation-not-verified'
        } catch {
            $result.installation.status = 'error'
            $result.installation.errorCode = Get-BedrockErrorCode $_.Exception
        }
    }
    if ($Launch) {
        if ($null -eq $activationId) {
            $result.activation.status = 'blocked-no-verified-client'
        } else {
            try {
                $processId = Invoke-BedrockApplicationActivation $activationId
                $result.activation.status = 'activation-request-accepted; game-entry-not-verified'
                $result.activation.processId = $processId
            } catch {
                $result.activation.status = 'error'
                $result.activation.errorCode = Get-BedrockErrorCode $_.Exception
            }
        }
    }
    return [pscustomobject]$result
}

Export-ModuleMember -Function Invoke-BedrockWindowsProbe, Test-BedrockProductionPackage, Get-BedrockActivationId, Get-BedrockWorldInventory, Get-BedrockPlainPathState
