#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$NativeLibraryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Minecraft\Launcher\Steam\1add7f151fa6\steam_api64.dll')),
    [switch]$RequireReady,
    [Parameter(DontShow)][switch]$NativeChild
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell 7 on Windows.' }
$marker = 'LANMINECRAFT_STEAM_READINESS_JSON:'

if (-not $NativeChild) {
    # Some Steam DLL versions print an account identifier during InitFlat.
    # Keep ALL native stdout/stderr inside a child pipe and expose only the
    # deliberately bounded boolean result. Never save or echo its raw output.
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath,
            '-NativeChild', '-NativeLibraryPath', [IO.Path]::GetFullPath($NativeLibraryPath))) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['SteamAppId'] = '480'
    $start.Environment['SteamGameId'] = '480'
    $start.Environment['SteamNoOverlayUIDrawing'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill() # Only this diagnostic child; never Steam or Minecraft.
            throw 'The isolated Steam readiness probe timed out.'
        }
        $raw = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'The isolated Steam readiness probe failed.' }
        $line = $raw.Split("`n") | Where-Object { $_.StartsWith($marker, [StringComparison]::Ordinal) } | Select-Object -Last 1
        if (-not $line) { throw 'The isolated Steam readiness probe returned no boolean result.' }
        $result = $line.Substring($marker.Length) | ConvertFrom-Json
        $safe = [ordered]@{
            initSucceeded = [bool]$result.InitSucceeded
            loggedOn = [bool]$result.LoggedOn
            nativeSteamRunning = [bool]$result.NativeSteamRunning
            probeSessionShutdown = [bool]$result.ProbeSessionShutdown
            accessorAvailable = [bool]$result.AccessorAvailable
            ready = [bool]($result.InitSucceeded -and $result.LoggedOn -and $result.ProbeSessionShutdown)
        }
        $safe | ConvertTo-Json
        if ($RequireReady -and -not $safe.ready) { exit 1 }
    } finally { $process.Dispose() }
    exit
}

$item = Get-Item -LiteralPath $NativeLibraryPath
$hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -notin @('1add7f151fa644870a735ae86e68d1f019f296130d8e7c0a7ed3ecc7482dccbc',
        'e082bf5c9f881c822b1540a76b74f9d15e18019a73ebd206a559595badcb7f65')) {
    throw 'The probe accepts only the pinned launcher/e4steam SDK bytes.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class IsolatedSteamReadiness {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitCall(IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool BoolCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr UserAccessor();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool UserBoolCall(IntPtr user);
    public sealed class Result {
        public bool InitSucceeded, LoggedOn, NativeSteamRunning, ProbeSessionShutdown, AccessorAvailable;
    }
    public static Result Check(string path) {
        var result = new Result();
        IntPtr library = NativeLibrary.Load(path);
        try {
            var init = Marshal.GetDelegateForFunctionPointer<InitCall>(NativeLibrary.GetExport(library,"SteamAPI_InitFlat"));
            var shutdown = Marshal.GetDelegateForFunctionPointer<VoidCall>(NativeLibrary.GetExport(library,"SteamAPI_Shutdown"));
            var running = Marshal.GetDelegateForFunctionPointer<BoolCall>(NativeLibrary.GetExport(library,"SteamAPI_IsSteamRunning"));
            var callbacks = Marshal.GetDelegateForFunctionPointer<VoidCall>(NativeLibrary.GetExport(library,"SteamAPI_RunCallbacks"));
            var logged = Marshal.GetDelegateForFunctionPointer<UserBoolCall>(NativeLibrary.GetExport(library,"SteamAPI_ISteamUser_BLoggedOn"));
            IntPtr accessor = IntPtr.Zero;
            foreach (string name in new[]{"SteamAPI_SteamUser_v024","SteamAPI_SteamUser_v023","SteamAPI_SteamUser_v022","SteamAPI_SteamUser_v021"})
                if (NativeLibrary.TryGetExport(library,name,out accessor)) break;
            result.AccessorAvailable = accessor != IntPtr.Zero;
            result.NativeSteamRunning = running();
            if (!result.AccessorAvailable) return result;
            IntPtr error = Marshal.AllocHGlobal(1024);
            try { Marshal.WriteByte(error,0); result.InitSucceeded = init(error) == 0; }
            finally { Marshal.FreeHGlobal(error); }
            if (!result.InitSucceeded) return result;
            try {
                IntPtr user = Marshal.GetDelegateForFunctionPointer<UserAccessor>(accessor)();
                if (user == IntPtr.Zero) return result;
                for (int attempt = 0; attempt < 51; attempt++) {
                    callbacks(); result.LoggedOn = logged(user);
                    if (result.LoggedOn) break;
                    Thread.Sleep(100);
                }
                result.NativeSteamRunning = running();
            } finally { shutdown(); result.ProbeSessionShutdown = true; }
            return result;
        } finally { NativeLibrary.Free(library); }
    }
}
'@ | Out-Null
$result = [IsolatedSteamReadiness]::Check($item.FullName)
[Console]::WriteLine($marker + ($result | ConvertTo-Json -Compress))
