[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$session=Join-Path $PSScriptRoot ('Artifacts\analyzer-selftest-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($session)
$utf8=[Text.UTF8Encoding]::new($false)
$plan=@{RepositoryPath=(Join-Path $PSScriptRoot '..\..');ExpectedInstallDirectory='C:\audit-install';ExpectedApplicationPath='C:\audit-install\LANMinecraft.exe';Phase='probe';HostTimeZoneId='UTC'}
[IO.File]::WriteAllText((Join-Path $session 'plan.json'),($plan|ConvertTo-Json),$utf8)
$events=New-Object 'System.Collections.Generic.List[object]'
function Add-Event([int]$EventPid,[string]$Operation,[string]$Path,[string]$Detail,[string]$Result='SUCCESS',[string]$ProcessName='audit.exe') {
    $events.Add([pscustomobject]@{'Time of Day'=('12:00:00.{0:0000000}' -f $events.Count);'Process Name'=$ProcessName;PID=$EventPid;Operation=$Operation;Path=$Path;Result=$Result;Detail=$Detail})
}
Add-Event 100 'Process Start' 'C:\audit-install\LANMinecraft.exe' 'Parent PID: 99, Command line: audit.exe'
Add-Event 100 'CreateFile' 'C:\audit-install\opened.json' 'Desired Access: Generic Write, Disposition: OpenIf, OpenResult: Opened'
Add-Event 100 'CreateFile' 'C:\audit-install\created.json' 'Desired Access: Generic Write, Disposition: OpenIf, OpenResult: Created'
Add-Event 100 'WriteFile' 'C:\audit-install\comma,"quoted".txt' "Length: 2`r`nCSV continuation, quoted `"detail`""
Add-Event 100 'CreateFile' 'C:\audit-install\ambiguous.json' 'Desired Access: Generic Write, Disposition: OpenIf'
Add-Event 100 'RegCreateKey' 'HKCU\Software\Existing' 'Disposition: REG_OPENED_EXISTING_KEY'
Add-Event 100 'RegCreateKey' 'HKCU\Software\Created' 'Disposition: REG_CREATED_NEW_KEY'
Add-Event 100 'RegSetInfoKey' 'HKCU\Software\Existing' 'KeySetHandleTagsInformation, HandleTags: 0x0'
Add-Event 100 'WriteFile' '\Device\NamedPipe\audit' 'Length: 10'
Add-Event 100 'WriteFile' '\Device\ConDrv' 'Length: 10'
Add-Event 100 'WriteFile' 'C:\audit-install\denied.txt' 'Length: 1' 'ACCESS DENIED'
Add-Event 100 'Process Create' 'C:\audit-install\java.exe' 'PID: 200, Command line: java.exe'
Add-Event 200 'Process Start' 'C:\audit-install\java.exe' 'Parent PID: 100, Command line: java.exe'
Add-Event 200 'WriteFile' 'C:\audit-install\child.txt' 'Length: 2'
Add-Event 200 'Process Exit' '' 'Exit Status: 0'
Add-Event 200 'Process Start' 'C:\unrelated.exe' 'Parent PID: 999, Command line: unrelated.exe'
Add-Event 200 'WriteFile' 'C:\unrelated-must-not-be-counted.txt' 'Length: 3'
Add-Event 100 'Process Exit' '' 'Exit Status: 0'
Add-Event 100 'Process Start' 'C:\unrelated.exe' 'Parent PID: 999, Command line: unrelated.exe'
Add-Event 100 'RegSetValue' 'HKCU\UnrelatedMustNotBeCounted' 'Length: 3'
$events|Export-Csv -LiteralPath (Join-Path $session 'capture.csv') -NoTypeInformation -Encoding UTF8
$summary=& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $session -ApplicationProcessId 100
if($summary.SuccessfulFileMutationEvents -ne 3 -or $summary.SuccessfulRegistryMutationEvents -ne 1 -or
    $summary.NamedPipeEvents -ne 1 -or $summary.RegistryHandleTagEvents -ne 1 -or
    $summary.ExistingRegistryKeyOpenEvents -ne 1 -or $summary.AmbiguousSuccessfulEvents -ne 1 -or
    $summary.DeviceIoEvents -ne 1 -or $summary.ApplicationRows -ne 16 -or $summary.UnobservedRootProcessIds.Count -ne 0) {
    throw ('Analyzer classification regression: ' + ($summary|ConvertTo-Json -Depth 8))
}
$quoted=@(Import-Csv -LiteralPath (Join-Path $session 'file-mutations.csv')|Where-Object Path -eq 'C:\audit-install\comma,"quoted".txt')
if($quoted.Count -ne 1 -or $quoted[0].Detail -ne "Length: 2`r`nCSV continuation, quoted `"detail`"") {throw 'CSV quoted comma/escaped quote/multiline regression.'}
[pscustomobject]@{Passed=$true;SessionDirectory=$session;Verified='Confirmed creation vs open, denied writes, registry key disposition, IPC, handle metadata, descendants and PID reuse.'}
$preexistingSession=Join-Path $session 'preexisting'
[void][IO.Directory]::CreateDirectory($preexistingSession)
[IO.File]::WriteAllText((Join-Path $preexistingSession 'plan.json'),($plan|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $preexistingSession 'observer.json'),(@{StartedUtc='2026-10-19T11:59:00Z';RuntimeSeconds=180}|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $preexistingSession 'application.json'),(@{ProcessIds=@(100);Processes=@(@{ProcessId=100;ParentProcessId=99;Path='C:\audit-install\LANMinecraft.exe';StartUtc='2026-10-19T11:00:00Z'})}|ConvertTo-Json -Depth 5),$utf8)
$events.Clear()
Add-Event 100 'WriteFile' 'C:\audit-install\existing-root.txt' 'Length: 1'
Add-Event 100 'Process Create' 'C:\audit-install\java.exe' 'PID: 200, Command line: java.exe'
Add-Event 200 'Process Start' 'C:\audit-install\java.exe' 'Parent PID: 100, Command line: java.exe'
Add-Event 200 'WriteFile' 'C:\audit-install\child.txt' 'Length: 2'
Add-Event 200 'Process Exit' '' 'Exit Status: 0'
Add-Event 100 'Process Exit' '' 'Exit Status: 0'
Add-Event 100 'Process Start' 'C:\unrelated.exe' 'Parent PID: 999, Command line: unrelated.exe'
Add-Event 100 'WriteFile' 'C:\unrelated.txt' 'Length: 1'
$events|Export-Csv -LiteralPath (Join-Path $preexistingSession 'capture.csv') -NoTypeInformation -Encoding UTF8
$preexisting=& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $preexistingSession
if($preexisting.ApplicationRows -ne 6 -or $preexisting.SuccessfulFileMutationEvents -ne 2 -or
    @($preexisting.PreexistingRootProcessIds).Count -ne 1 -or @($preexisting.UnobservedRootProcessIds).Count -ne 0) {
    throw 'Pre-existing root attribution regression.'
}
[pscustomobject]@{Passed=$true;SessionDirectory=$preexistingSession;Verified='Pre-existing root with observed Java child; startup explicitly outside capture; subsequent PID reuse excluded.'}
$guiSession=Join-Path $session 'gui-metrics'
[void][IO.Directory]::CreateDirectory($guiSession)
[IO.File]::WriteAllText((Join-Path $guiSession 'plan.json'),($plan|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $guiSession 'observer.json'),(@{StartedUtc='2026-10-19T11:59:00Z';RuntimeSeconds=180}|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $guiSession 'application.json'),(@{ProcessIds=@(100);Processes=@(@{ProcessId=100;ProcessName='LANMinecraft';ParentProcessId=99;Path='C:\audit-install\LANMinecraft.exe';StartUtc='2026-10-19T12:00:00Z'})}|ConvertTo-Json -Depth 5),$utf8)
$events.Clear()
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('function Test-ExtractionInUse {}; $extractionDir="cache"'))
Add-Event 100 'Process Start' 'C:\audit-install\LANMinecraft.exe' 'Parent PID: 99' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'WriteFile' 'C:\audit-install\.settings.json.x.tmp' 'Length: 1' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'SetRenameInformationFile' 'C:\audit-install\.settings.json.x.tmp' 'FileName: C:\audit-install\settings.json' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'WriteFile' 'C:\audit-install\.pack-hashes.json.x.tmp' 'Length: 1' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'Process Create' 'C:\Windows\powershell.exe' 'PID: 200' -ProcessName 'LANMinecraft.exe'
Add-Event 200 'Process Start' '' ('Parent PID: 100, Command line: powershell.exe -EncodedCommand ' + $encoded) -ProcessName 'powershell.exe'
Add-Event 200 'WriteFile' 'C:\temp\__PSScriptPolicyTest_x.ps1' 'Length: 1' -ProcessName 'powershell.exe'
Add-Event 200 'Process Exit' '' 'Exit Status: 0' -ProcessName 'powershell.exe'
Add-Event 100 'Process Exit' '' 'Exit Status: 0' -ProcessName 'LANMinecraft.exe'
$events|Export-Csv -LiteralPath (Join-Path $guiSession 'capture.csv') -NoTypeInformation -Encoding UTF8
$gui=& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $guiSession
if(@($gui.GuiShutdownPowerShellHelpers).Count -ne 1 -or $gui.GuiPolicyTempMutationEvents -ne 1 -or
    $gui.GuiSettingsMutationEvents -ne 2 -or $gui.GuiPackHashMutationEvents -ne 1 -or
    @($gui.GuiRootMetrics).Count -ne 1 -or $gui.GuiRootMetrics[0].PackHashMutationEvents -ne 1) {throw 'GUI cleanup/policy/atomic-settings metric regression.'}
[pscustomobject]@{Passed=$true;SessionDirectory=$guiSession;Verified='GUI-only cleanup helper recognition, policy temporary file and atomic settings metrics.'}
$reuseSession=Join-Path $session 'root-pid-used-before-registration'
[void][IO.Directory]::CreateDirectory($reuseSession)
[IO.File]::WriteAllText((Join-Path $reuseSession 'plan.json'),($plan|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $reuseSession 'observer.json'),(@{StartedUtc='2026-10-19T11:59:00Z';RuntimeSeconds=180}|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $reuseSession 'application.json'),(@{ProcessIds=@(100);Processes=@(@{ProcessId=100;ProcessName='LANMinecraft';ParentProcessId=99;Path='C:\audit-install\LANMinecraft.exe';StartUtc='2026-10-19T12:00:30Z'})}|ConvertTo-Json -Depth 5),$utf8)
$events.Clear()
Add-Event 100 'Process Start' '' 'Parent PID: 99' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'WriteFile' 'C:\wrong-lifetime.txt' 'Length: 1' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'Process Exit' '' 'Exit Status: 0' -ProcessName 'LANMinecraft.exe'
Add-Event 100 'Process Start' '' 'Parent PID: 99' -ProcessName 'LANMinecraft.exe'
$events[$events.Count-1].'Time of Day'='12:00:30.0000000'
Add-Event 100 'WriteFile' 'C:\audit-install\right-lifetime.txt' 'Length: 1' -ProcessName 'LANMinecraft.exe'
$events[$events.Count-1].'Time of Day'='12:00:31.0000000'
Add-Event 100 'Process Exit' '' 'Exit Status: 0' -ProcessName 'LANMinecraft.exe'
$events[$events.Count-1].'Time of Day'='12:00:32.0000000'
$events|Export-Csv -LiteralPath (Join-Path $reuseSession 'capture.csv') -NoTypeInformation -Encoding UTF8
$reuse=& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $reuseSession
if($reuse.ApplicationRows -ne 3 -or $reuse.SuccessfulFileMutationEvents -ne 1 -or @($reuse.UnobservedRootProcessIds).Count) {
    throw 'Root PID lifetime identity regression.'
}
[pscustomobject]@{Passed=$true;SessionDirectory=$reuseSession;Verified='Unrelated earlier lifetime with same PID/name/parent excluded using verified start time.'}
$lateChildSession=Join-Path $session 'child-start-after-parent-exit'
[void][IO.Directory]::CreateDirectory($lateChildSession)
[IO.File]::WriteAllText((Join-Path $lateChildSession 'plan.json'),($plan|ConvertTo-Json),$utf8)
$events.Clear()
Add-Event 100 'Process Start' '' 'Parent PID: 99'
Add-Event 100 'Process Create' 'C:\audit-install\java.exe' 'PID: 200, Command line: java.exe'
Add-Event 100 'Process Exit' '' 'Exit Status: 0'
Add-Event 200 'Process Start' 'C:\audit-install\java.exe' 'Parent PID: 100, Command line: java.exe'
Add-Event 200 'WriteFile' 'C:\audit-install\late-child.txt' 'Length: 2'
Add-Event 200 'Process Exit' '' 'Exit Status: 0'
$events|Export-Csv -LiteralPath (Join-Path $lateChildSession 'capture.csv') -NoTypeInformation -Encoding UTF8
$lateChild=& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $lateChildSession -ApplicationProcessId 100
if($lateChild.ApplicationRows -ne 6 -or $lateChild.SuccessfulFileMutationEvents -ne 1 -or
    @($lateChild.ProcessTree|Where-Object {$_.ChildId -eq 200 -and $_.RootProcessId -eq 100}).Count -ne 1) {
    throw 'Child ownership after parent exit regression.'
}
[pscustomobject]@{Passed=$true;SessionDirectory=$lateChildSession;Verified='Observed Process Create preserves child ownership when Process Start follows parent exit.'}
$tamperSession=Join-Path $session 'csv-integrity'
[void][IO.Directory]::CreateDirectory($tamperSession)
[IO.File]::WriteAllText((Join-Path $tamperSession 'plan.json'),($plan|ConvertTo-Json),$utf8)
$tamperCsv=Join-Path $tamperSession 'capture.csv'
$events|Export-Csv -LiteralPath $tamperCsv -NoTypeInformation -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $tamperSession 'finished.json'),(@{Complete=$true;CsvSha256=(Get-FileHash -LiteralPath $tamperCsv -Algorithm SHA256).Hash}|ConvertTo-Json),$utf8)
[IO.File]::AppendAllText($tamperCsv,"`r`n",$utf8)
$integrityRejected=$false
try {& (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $tamperSession -ApplicationProcessId 100 | Out-Null}
catch {if($_.Exception.Message -match '(?i)SHA|хеш|hash|целост') {$integrityRejected=$true} else {throw}}
if(-not $integrityRejected) {throw 'Changed original CSV was accepted.'}
[pscustomobject]@{Passed=$true;SessionDirectory=$tamperSession;Verified='CSV changed after the immutable completed hash is rejected before attribution.'}
$unknownDropsRejected=$false
try {& (Join-Path $PSScriptRoot 'AuditSession.ps1') -Action Statistics -SessionDirectory $session -Detail 'No actual dropped-event counter was observed.' | Out-Null}
catch {if($_.Exception.Message -like '*-DroppedEvents*') {$unknownDropsRejected=$true} else {throw}}
if(-not $unknownDropsRejected -or (Test-Path -LiteralPath (Join-Path $session 'capture-statistics.json'))) {throw 'Unknown kernel drop count was silently recorded as zero.'}
[pscustomobject]@{Passed=$true;SessionDirectory=$session;Verified='Omitted kernel dropped-event counter cannot silently become a recorded zero.'}
