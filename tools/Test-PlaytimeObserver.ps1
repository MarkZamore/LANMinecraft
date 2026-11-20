#requires -Version 7.2
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$boundary = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\agent-runs'
if (!$exe.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a packaged launcher in an isolated agent-runs installation.'
}
$root = Split-Path -Parent $exe
$file = Join-Path $root 'Minecraft/Personal/time.json'
$key = 'Playtime-QA-' + [guid]::NewGuid().ToString('N')
$observerIdFile = Join-Path $root 'qa-observer-pid.txt'
if (Test-Path -LiteralPath $observerIdFile) { throw 'Remove the previous owned QA run first.' }
Add-Type -Namespace PlaytimeQa -Name Clock -MemberDefinition '
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool QueryUnbiasedInterruptTime(out ulong time);'

function Read-Checkpoint {
    if (Test-Path -LiteralPath $file) {
        $values = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
        if ($values.Contains($key)) { return [long]$values[$key] }
    }
    return -1L
}

$parent = $null
$observer = $null
$game = Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 480' -WindowStyle Hidden -PassThru
try {
    [ulong]$start = 0
    if (![PlaytimeQa.Clock]::QueryUnbiasedInterruptTime([ref]$start)) { throw 'Clock unavailable.' }
    $arguments = "--track-playtime $($game.Id) $($game.StartTime.ToFileTimeUtc()) $start $key"
    $parentTemplate = @'
$ErrorActionPreference = 'Stop'
$watcher = Start-Process -FilePath '{0}' -ArgumentList '{1}' -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText('{2}', [string]$watcher.Id)
'@
    $parentScript = $parentTemplate -f $exe.Replace("'", "''"), $arguments, $observerIdFile.Replace("'", "''")
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($parentScript))
    $parent = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -WindowStyle Hidden -PassThru
    if (!$parent.WaitForExit(15000) -or $parent.ExitCode -ne 0) { throw 'Observer parent failed.' }
    $observer = Get-Process -Id ([int](Get-Content -LiteralPath $observerIdFile -Raw))
    $null = $observer.Handle
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ((Read-Checkpoint) -lt 0 -and !$observer.HasExited -and $elapsed.Elapsed.TotalSeconds -lt 15) {
        Start-Sleep -Milliseconds 100
    }
    if ((Read-Checkpoint) -ne 0 -or $observer.HasExited) { throw 'First checkpoint was not written.' }
    $baselineCpu = $observer.TotalProcessorTime.TotalMilliseconds
    $peakPrivate = $observer.PrivateMemorySize64
    do {
        if ($observer.HasExited) { throw 'Observer ended before the game.' }
        Start-Sleep -Milliseconds 500
        $checkpoint = Read-Checkpoint
        $observer.Refresh()
        $peakPrivate = [Math]::Max($peakPrivate, $observer.PrivateMemorySize64)
    } until ($checkpoint -ge 299000 -or $elapsed.Elapsed.TotalSeconds -gt 330)
    if ($checkpoint -lt 299000) { throw "Missing periodic checkpoint: $checkpoint" }
    $idleCpu = $observer.TotalProcessorTime.TotalMilliseconds - $baselineCpu
    Start-Sleep -Seconds 25
    $signal = [Diagnostics.Stopwatch]::StartNew()
    $game.Kill()
    if (!$observer.WaitForExit(5000) -or $observer.ExitCode -ne 0) { throw 'Observer did not complete on game exit.' }
    $final = Read-Checkpoint
    if ($final - $checkpoint -lt 24500 -or $final - $checkpoint -gt 28500) { throw "Incorrect final checkpoint: $checkpoint -> $final" }
    if ($idleCpu -gt 300 -or $peakPrivate -gt 10MB) { throw "CPU $idleCpu ms, peak private bytes $peakPrivate" }
    [ordered]@{
        parentExited = $parent.HasExited; checkpointMs = $checkpoint; finalMs = $final
        finalIntervalMs = $final - $checkpoint; exitSignalToSavedMs = $signal.ElapsedMilliseconds
        cpuMsOverFiveMinutes = $idleCpu; peakPrivateBytes = $peakPrivate
    } | ConvertTo-Json
}
finally {
    foreach ($process in @($observer, $game, $parent)) {
        if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }
    if (Test-Path -LiteralPath $observerIdFile) { Remove-Item -LiteralPath $observerIdFile }
}
