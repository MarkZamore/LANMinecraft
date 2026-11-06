#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ArtifactsDirectory,
    [ValidateRange(10,3600)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$workingDirectory = (Get-Location).ProviderPath
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $artifactsRoot 'portable-host-antivirus' }
$artifactsBase = [IO.Path]::GetFullPath($ArtifactsDirectory, $workingDirectory)
if (-not $artifactsBase.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not $artifactsBase.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Antivirus evidence must stay inside repository artifacts.'
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$evidenceDirectory = Join-Path $artifactsBase $runId
[void][IO.Directory]::CreateDirectory($evidenceDirectory)
$summaryPath = Join-Path $evidenceDirectory 'scan.json'
$evidence = [ordered]@{
    SchemaVersion = 1; Result = 'Unknown'; Reason = ''; StartedAtUtc = [DateTime]::UtcNow.ToString('O')
    DefenderUnavailableAtPreflight = $false
    Executable = [IO.Path]::GetFullPath($ExecutablePath, $workingDirectory); EvidenceDirectory = $evidenceDirectory
    Before = $null; After = $null; DefenderBefore = $null; DefenderAfter = $null
    Tool = $null; Arguments = @(); NativeExitCode = $null; TimedOut = $false; IsAdministrator = $false
    BaselineEventRecordId = 0; ScanStartedAtUtc = $null; ScanEndedAtUtc = $null
    CompletionScanIds = @(); CompletionMethod = 'None'; CompletionEventNote = ''
    RecognizedNativeCompletion = $false; Events = @(); MatchingDetections = @()
    StandardOutput = 'scan.stdout.txt'; StandardError = 'scan.stderr.txt'
    RawStandardOutput = 'scan.stdout.bin'; RawStandardError = 'scan.stderr.bin'
}
$heldExecutable = $null
$scanProcess = $null
$stdoutFile = $null
$stderrFile = $null
$stdoutCopy = $null
$stderrCopy = $null
$eventLogName = 'Microsoft-Windows-Windows Defender/Operational'

function Read-DefenderStatus {
    $status = Get-MpComputerStatus -ErrorAction Stop
    if ($null -eq $status) { throw 'Microsoft Defender did not return its status.' }
    return $status | Select-Object AMServiceEnabled, AntivirusEnabled, AntispywareEnabled,
        RealTimeProtectionEnabled, AMRunningMode, AMProductVersion, AMEngineVersion,
        AntivirusSignatureVersion, AntivirusSignatureLastUpdated, DefenderSignaturesOutOfDate
}

function Assert-DefenderStatus($result) {
    if ($result.AMServiceEnabled -ne $true -or $result.AntivirusEnabled -ne $true -or
        $result.RealTimeProtectionEnabled -ne $true -or $result.AMRunningMode -ne 'Normal' -or
        $result.DefenderSignaturesOutOfDate -ne $false -or
        [string]::IsNullOrWhiteSpace([string]$result.AntivirusSignatureVersion) -or
        $null -eq $result.AntivirusSignatureLastUpdated) {
        throw 'Microsoft Defender must be active in Normal mode with current signatures; no settings were changed.'
    }
}

function Find-DefenderScanner {
    $commonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $platformRoot = Join-Path $commonData 'Microsoft\Windows Defender\Platform'
    if ([IO.Directory]::Exists($platformRoot)) {
        $platforms = @(Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction Stop |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+(?:-\d+)?$' } |
            Sort-Object @{ Expression = { [version]$_.Name.Split('-')[0] }; Descending = $true },
                @{ Expression = { $_.Name }; Descending = $true })
        foreach ($platform in $platforms) {
            $candidate = Join-Path $platform.FullName 'MpCmdRun.exe'
            if ([IO.File]::Exists($candidate)) { return $candidate }
        }
    }
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $candidate = Join-Path $programFiles 'Windows Defender\MpCmdRun.exe'
    if ([IO.File]::Exists($candidate)) { return $candidate }
    throw 'The installed Microsoft Defender command-line scanner is unavailable.'
}

function Read-TargetFingerprint([IO.FileStream]$Stream) {
    $Stream.Position = 0
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = $algorithm.ComputeHash($Stream) } finally { $algorithm.Dispose() }
    return [pscustomobject]@{
        Sha256 = ([BitConverter]::ToString($digest) -replace '-', '').ToLowerInvariant()
        SizeBytes = $Stream.Length
    }
}

function Read-DefenderEvents([DateTime]$SinceUtc, [long]$AfterRecordId) {
    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = $eventLogName; StartTime = $SinceUtc.ToLocalTime(); Id = @(1000,1001,1002,1005,1116,1117)
        } -ErrorAction Stop)
    }
    catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { return @() }
        throw
    }
    return @($events | Where-Object { $_.RecordId -gt $AfterRecordId } | ForEach-Object {
        [xml]$xml = $_.ToXml()
        $fields = @{}
        foreach ($field in @($xml.Event.EventData.Data)) { $fields[$field.GetAttribute('Name')] = $field.InnerText }
        [pscustomobject]@{ Id = $_.Id; RecordId = $_.RecordId; TimeCreatedUtc = $_.TimeCreated.ToUniversalTime(); Fields = $fields }
    })
}

function Test-TargetResource([string]$Resource) {
    if ([string]::IsNullOrWhiteSpace($Resource)) { return $false }
    # Defender resource lists may name an outer container followed by members.
    # Split only explicit resource prefixes, never arbitrary semicolons in a path.
    foreach ($part in [regex]::Split($Resource.Trim(), ';(?=(?i:containerfile|file):_)')) {
        $path = $part.Trim().Trim('"')
        $path = $path -replace '^(?i:containerfile|file):_?', ''
        if ($path.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { $path = '\\' + $path.Substring(8) }
        elseif ($path.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) { $path = $path.Substring(4) }
        try {
            if ([IO.Path]::GetFullPath($path).Equals($evidence.Executable, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        catch { }
    }
    return $false
}

function Read-DetectionSnapshot {
    return @(Get-MpThreatDetection -ErrorAction Stop | ForEach-Object {
        [pscustomobject]@{
            DetectionId = [string]$_.DetectionID; ThreatId = $_.ThreatID
            InitialDetectionTime = $_.InitialDetectionTime; LastThreatStatusChangeTime = $_.LastThreatStatusChangeTime
            Resources = @($_.Resources)
        }
    })
}

function Read-NativeOutput([string]$RawPath) {
    $bytes = [IO.File]::ReadAllBytes($RawPath)
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    try {
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) { $text = [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2) }
        elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) { $text = [Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2) }
        elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { $text = $encoding.GetString($bytes, 3, $bytes.Length - 3) }
        else { $text = $encoding.GetString($bytes) }
    }
    catch {
        $encoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentUICulture.TextInfo.OEMCodePage)
        $text = $encoding.GetString($bytes)
    }
    return $text
}

function Save-NativeOutput([string]$RawPath, [string]$TextPath) {
    if ([IO.File]::Exists($RawPath)) {
        [IO.File]::WriteAllText($TextPath, (Read-NativeOutput $RawPath), [Text.UTF8Encoding]::new($false))
    }
}

function Test-CleanNativeCompletion([string]$Output, [string]$ErrorOutput, [string]$Target) {
    if (-not [string]::IsNullOrWhiteSpace($ErrorOutput)) { return $false }
    $lines = @([regex]::Split($Output, '\r\n|\r|\n') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # Exact calibrated format, including the complete target path. Unknown
    # languages, warnings, extra lines or a clean message for another file fail.
    return $lines.Count -eq 3 -and
        $lines[0].Equals('Scan starting...', [StringComparison]::Ordinal) -and
        $lines[1].Equals('Scan finished.', [StringComparison]::Ordinal) -and
        $lines[2].Equals('Scanning ' + $Target + ' found no threats.', [StringComparison]::OrdinalIgnoreCase)
}

try {
    foreach ($command in @('Get-MpComputerStatus', 'Get-MpThreatDetection', 'Get-WinEvent')) {
        if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) { throw ('Required Defender evidence command unavailable: ' + $command) }
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $evidence.IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }
    if (-not [IO.File]::Exists($evidence.Executable) -or
        -not [IO.Path]::GetExtension($evidence.Executable).Equals('.exe', [StringComparison]::OrdinalIgnoreCase) -or
        ([IO.File]::GetAttributes($evidence.Executable) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The antivirus target must be an existing ordinary EXE file.'
    }
    # Keep the exact candidate readable by Defender while preventing replacement
    # or writes during the scan and the two independent fingerprint reads.
    $heldExecutable = [IO.FileStream]::new($evidence.Executable, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $evidence.Before = Read-TargetFingerprint $heldExecutable
    $evidence.DefenderBefore = Read-DefenderStatus
    try { Assert-DefenderStatus $evidence.DefenderBefore }
    catch {
        # Fingerprint the locked EXE again before reporting a pre-scan skip.
        $evidence.After = Read-TargetFingerprint $heldExecutable
        $evidence.DefenderUnavailableAtPreflight = $true
        throw
    }
    $eventLog = Get-WinEvent -ListLog $eventLogName -ErrorAction Stop
    if (-not $eventLog.IsEnabled) { throw 'The Defender Operational event log is disabled; scan completion cannot be verified.' }
    try { $lastEvent = Get-WinEvent -LogName $eventLogName -MaxEvents 1 -ErrorAction Stop }
    catch {
        if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }
        $lastEvent = $null
    }
    if ($null -ne $lastEvent) { $evidence.BaselineEventRecordId = [long]$lastEvent.RecordId }
    $baselineDetections = @(Read-DetectionSnapshot)
    $baselineDetectionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($detection in $baselineDetections) { [void]$baselineDetectionIds.Add($detection.DetectionId) }

    $tool = Find-DefenderScanner
    $evidence.Tool = [pscustomobject]@{ Path = $tool; Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($tool).FileVersion }
    $arguments = @('-Scan', '-ScanType', '3', '-File', $evidence.Executable, '-DisableRemediation')
    $evidence.Arguments = $arguments
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $tool; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $stdoutFile = [IO.FileStream]::new((Join-Path $evidenceDirectory $evidence.RawStandardOutput), [IO.FileMode]::CreateNew)
    $stderrFile = [IO.FileStream]::new((Join-Path $evidenceDirectory $evidence.RawStandardError), [IO.FileMode]::CreateNew)
    $scanStarted = [DateTime]::UtcNow
    $evidence.ScanStartedAtUtc = $scanStarted.ToString('O')
    Write-Host ('Scanning the exact portable host; evidence: ' + $evidenceDirectory)
    $scanProcess = [Diagnostics.Process]::Start($start)
    if ($null -eq $scanProcess) { throw 'Microsoft Defender scanner did not start.' }
    $stdoutCopy = $scanProcess.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderrCopy = $scanProcess.StandardError.BaseStream.CopyToAsync($stderrFile)
    $deadline = $scanStarted.AddSeconds($TimeoutSeconds)
    while (-not $scanProcess.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            $evidence.TimedOut = $true
            $scanProcess.Kill($true)
            [void]$scanProcess.WaitForExit(10000)
            throw 'The owned Defender scanner process exceeded its time limit; scan result is unknown.'
        }
    }
    $evidence.NativeExitCode = $scanProcess.ExitCode
    if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutCopy, $stderrCopy)).Wait(10000)) {
        throw 'The Defender output streams did not finish; scan result is unknown.'
    }
    $stdoutFile.Dispose(); $stdoutFile = $null; $stderrFile.Dispose(); $stderrFile = $null
    $evidence.ScanEndedAtUtc = [DateTime]::UtcNow.ToString('O')
    $evidence.After = Read-TargetFingerprint $heldExecutable
    $evidence.DefenderAfter = Read-DefenderStatus
    Assert-DefenderStatus $evidence.DefenderAfter
    if ($evidence.Before.Sha256 -ne $evidence.After.Sha256 -or $evidence.Before.SizeBytes -ne $evidence.After.SizeBytes) {
        throw 'The executable changed during antivirus verification.'
    }

    $nativeOutput = Read-NativeOutput (Join-Path $evidenceDirectory $evidence.RawStandardOutput)
    $nativeError = Read-NativeOutput (Join-Path $evidenceDirectory $evidence.RawStandardError)
    $evidence.RecognizedNativeCompletion = Test-CleanNativeCompletion $nativeOutput $nativeError $evidence.Executable
    $completionWaitSeconds = if ($evidence.RecognizedNativeCompletion) { 3 } else { 15 }
    $completionDeadline = [DateTime]::UtcNow.AddSeconds($completionWaitSeconds)
    $matchingStarts = @(); $completed = @(); $scanEvents = @()
    do {
        $scanEvents = @(Read-DefenderEvents $scanStarted.AddSeconds(-1) $evidence.BaselineEventRecordId)
        $matchingStarts = @($scanEvents | Where-Object { $_.Id -eq 1000 -and (Test-TargetResource ([string]$_.Fields['Scan Resources'])) })
        $completed = @($matchingStarts | Where-Object {
            $scanId = [string]$_.Fields['Scan ID']
            $startRecord = $_.RecordId
            $scanId -and @($scanEvents | Where-Object { $_.Id -eq 1001 -and $_.RecordId -gt $startRecord -and $_.Fields['Scan ID'] -eq $scanId }).Count -gt 0 -and
                @($scanEvents | Where-Object { $_.Id -in @(1002,1005) -and $_.Fields['Scan ID'] -eq $scanId }).Count -eq 0
        })
        if ($completed.Count -gt 0 -or [DateTime]::UtcNow -ge $completionDeadline) { break }
        [Threading.Thread]::Sleep(500)
    } while ($true)
    $matchingIds = @($matchingStarts | ForEach-Object { [string]$_.Fields['Scan ID'] })
    $matchingScanErrors = @($scanEvents | Where-Object { $_.Id -in @(1002,1005) -and $_.Fields['Scan ID'] -in $matchingIds })
    $matchingEventDetections = @($scanEvents | Where-Object {
        $_.Id -in @(1116,1117) -and (Test-TargetResource ([string]$_.Fields['Path']))
    })
    $evidence.Events = @($scanEvents | Where-Object { $_.Fields['Scan ID'] -in $matchingIds }) + $matchingEventDetections
    $evidence.CompletionScanIds = @($completed | ForEach-Object { [string]$_.Fields['Scan ID'] } | Select-Object -Unique)
    $evidence.MatchingDetections = @(Read-DetectionSnapshot | Where-Object {
        $detection = $_
        $matchesTarget = @($detection.Resources | Where-Object { Test-TargetResource ([string]$_) }).Count -gt 0
        $changed = -not $baselineDetectionIds.Contains($detection.DetectionId) -or
            ($null -ne $detection.LastThreatStatusChangeTime -and ([DateTime]$detection.LastThreatStatusChangeTime).ToUniversalTime() -ge $scanStarted)
        $matchesTarget -and $changed
    } | ForEach-Object {
        [pscustomobject]@{
            DetectionId = $_.DetectionId; ThreatId = $_.ThreatId
            InitialDetectionTime = $_.InitialDetectionTime; LastThreatStatusChangeTime = $_.LastThreatStatusChangeTime
            Resources = @($_.Resources | Where-Object { Test-TargetResource ([string]$_) })
        }
    })
    if ($matchingEventDetections.Count -gt 0 -or $evidence.MatchingDetections.Count -gt 0) {
        $evidence.Result = 'Detected'
        throw 'Microsoft Defender recorded a detection for this exact executable during verification.'
    }
    # -DisableRemediation suppresses custom-scan detection events/history.
    # Its native zero exit is required; default exit two also covers scan errors.
    if ($evidence.NativeExitCode -ne 0) { throw ('Microsoft Defender rejected the scan or could not complete it (exit ' + $evidence.NativeExitCode + ').') }
    if ($matchingScanErrors.Count -gt 0) { throw 'Defender recorded cancellation or a scan error for this target; result is unknown.' }
    if ($completed.Count -gt 0) {
        $evidence.CompletionMethod = 'DefenderOperationalScanId'
        $evidence.CompletionEventNote = 'Exact-target scan start and successful completion share a Scan ID.'
    }
    elseif ($evidence.RecognizedNativeCompletion) {
        $evidence.CompletionMethod = 'CalibratedEnglishExactTargetOutput'
        $evidence.CompletionEventNote = 'No correlated start/completion events were available. DisableRemediation can suppress custom-scan events; exact native start/finish/target-clean output supplies completion evidence.'
    }
    else { throw 'No correlated completed scan or recognized exact-target clean native output was available; result is unknown.' }
    $evidence.Result = 'Passed'
    $evidence.Reason = 'Custom scan completed for the exact unchanged executable with remediation disabled, exit zero and no matching detections.'
}
catch {
    $evidence.Reason = $_.Exception.Message
}
finally {
    if ($null -ne $scanProcess) {
        try { if (-not $scanProcess.HasExited) { $scanProcess.Kill($true); [void]$scanProcess.WaitForExit(10000) } } catch { }
        $scanProcess.Dispose()
    }
    foreach ($stream in @($stdoutFile, $stderrFile, $heldExecutable)) { if ($null -ne $stream) { $stream.Dispose() } }
    foreach ($channel in @(@($evidence.RawStandardOutput, $evidence.StandardOutput), @($evidence.RawStandardError, $evidence.StandardError))) {
        try { Save-NativeOutput (Join-Path $evidenceDirectory $channel[0]) (Join-Path $evidenceDirectory $channel[1]) }
        catch { $evidence.Result = 'Unknown'; $evidence.Reason = 'Could not preserve the complete native scanner output: ' + $_.Exception.Message }
    }
    $evidence.FinishedAtUtc = [DateTime]::UtcNow.ToString('O')
    [IO.File]::WriteAllText($summaryPath, ($evidence | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

if ($evidence.Result -ne 'Passed') {
    $global:LASTEXITCODE = 1
    throw ('Portable host antivirus verification failed: ' + $evidence.Reason + ' Evidence: ' + $summaryPath)
}
$global:LASTEXITCODE = 0
Write-Host ('Portable host antivirus verification passed. SHA256: ' + $evidence.After.Sha256 + ' Evidence: ' + $summaryPath)
