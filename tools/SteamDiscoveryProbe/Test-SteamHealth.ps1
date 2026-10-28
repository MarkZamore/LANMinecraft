#requires -Version 7.0
[CmdletBinding()]
param(
    # The directory containing Minecraft and LANMinecraft.exe.
    [string]$InstallRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')),
    # The game's -Duser.home. Defaults to the production launcher's portable home.
    [string]$GameJavaHome,
    # Optional exact DLL overrides, still checked against the pinned hashes.
    [string]$E4steamNativePath,
    [string]$LauncherNativePath,
    # Returns exit code 1 if either SDK's native client-process probe is false/unavailable.
    [switch]$RequireHealthy
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This probe requires Windows and the pinned x64 Steam DLLs.' }
if (-not [Environment]::Is64BitProcess) { throw 'Run this probe in 64-bit PowerShell 7.' }
$dataRoot = Join-Path ([IO.Path]::GetFullPath($InstallRoot)) 'Minecraft'
if (-not $GameJavaHome) { $GameJavaHome = Join-Path $dataRoot 'Personal\Home' }
if (-not $LauncherNativePath) {
    $LauncherNativePath = Join-Path $dataRoot 'Launcher\Steam\1add7f151fa6\steam_api64.dll'
}
if (-not $E4steamNativePath) {
    $cache = Join-Path $GameJavaHome '.e4steam-steam-natives'
    if (Test-Path -LiteralPath $cache -PathType Container) {
        $E4steamNativePath = Get-ChildItem -LiteralPath $cache -Recurse -File -Filter 'steam_api64.dll' |
            Where-Object { $_.Length -eq 319584 } | Sort-Object FullName |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

# This loads the pinned DLL and calls only SteamAPI_IsSteamRunning. It never
# calls SteamAPI_Init, InitEx, Shutdown, RunCallbacks or any user/account API.
if (-not ('LanMinecraftDiagnostics.SteamProcessProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LanMinecraftDiagnostics {
    public static class SteamProcessProbe {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool RunningDelegate();
        public static bool Check(string path) {
            IntPtr library = NativeLibrary.Load(path);
            try {
                IntPtr symbol = NativeLibrary.GetExport(library, "SteamAPI_IsSteamRunning");
                return Marshal.GetDelegateForFunctionPointer<RunningDelegate>(symbol)();
            } finally { NativeLibrary.Free(library); }
        }
    }
}
'@ | Out-Null
}

function Read-NativeProbe([string]$Path, [long]$ExpectedSize, [string]$ExpectedSha256) {
    $result = [ordered]@{ available = $false; pinnedBytesMatch = $false; nativeSteamRunning = $null }
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }
    $item = Get-Item -LiteralPath $Path
    $result.available = $true
    $result.pinnedBytesMatch = $item.Length -eq $ExpectedSize -and
        (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ieq $ExpectedSha256
    if ($result.pinnedBytesMatch) {
        $result.nativeSteamRunning = [LanMinecraftDiagnostics.SteamProcessProbe]::Check($item.FullName)
    }
    return $result
}

function Read-RegistryPresence([Microsoft.Win32.RegistryView]$View) {
    $result = [ordered]@{ activeProcessRecorded = $false; activeUserRecorded = $false }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $View)
    try {
        $key = $base.OpenSubKey('Software\Valve\Steam\ActiveProcess', $false)
        if ($null -ne $key) {
            try {
                $result.activeProcessRecorded = [long]$key.GetValue('pid', 0) -ne 0
                $result.activeUserRecorded = [long]$key.GetValue('ActiveUser', 0) -ne 0
            } finally { $key.Dispose() }
        }
    } finally { $base.Dispose() }
    return $result
}

$e4steam = Read-NativeProbe $E4steamNativePath 319584 'e082bf5c9f881c822b1540a76b74f9d15e18019a73ebd206a559595badcb7f65'
$launcher = Read-NativeProbe $LauncherNativePath 300392 '1add7f151fa644870a735ae86e68d1f019f296130d8e7c0a7ed3ecc7482dccbc'
$passed = $e4steam.nativeSteamRunning -eq $true -and $launcher.nativeSteamRunning -eq $true
[ordered]@{
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    steamProcessPresent = [bool](Get-Process -Name steam -ErrorAction SilentlyContinue)
    e4steam = $e4steam
    launcher = $launcher
    registry32 = Read-RegistryPresence ([Microsoft.Win32.RegistryView]::Registry32)
    registry64 = Read-RegistryPresence ([Microsoft.Win32.RegistryView]::Registry64)
    nativeHealthPassed = [bool]$passed
    scope = 'Read-only native client-process probes; no SDK initialization, callbacks, account IDs or logon/P2P result.'
} | ConvertTo-Json -Depth 4
if ($RequireHealthy -and -not $passed) { exit 1 }
