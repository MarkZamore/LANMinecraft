#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SessionDirectory,
    [string]$CsvPath,
    [int[]]$ApplicationProcessId,
    [string]$SaveDirectory,
    [string]$Destination,
    [switch]$ExcludeDescendants
)
$ErrorActionPreference = 'Stop'
$SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
if(-not $CsvPath) { $CsvPath = Join-Path $SessionDirectory 'capture.csv' }
$csvPath = [IO.Path]::GetFullPath($CsvPath)
$planPath = Join-Path $SessionDirectory 'plan.json'
if(-not (Test-Path -LiteralPath $csvPath)) { throw 'Не найден capture.csv.' }
if(-not (Test-Path -LiteralPath $planPath)) { throw 'Не найден plan.json.' }
$plan = [IO.File]::ReadAllText($planPath) | ConvertFrom-Json
$observerPath = Join-Path $SessionDirectory 'observer.json'
$finishedPath = Join-Path $SessionDirectory 'finished.json'
$observer = if(Test-Path -LiteralPath $observerPath) { [IO.File]::ReadAllText($observerPath) | ConvertFrom-Json } else {$null}
$finished = if(Test-Path -LiteralPath $finishedPath) { [IO.File]::ReadAllText($finishedPath) | ConvertFrom-Json } else {$null}
$csvHashVerified=$false
if($finished -and $finished.Complete) {
    if(-not $finished.CsvSha256 -or (Get-FileHash -LiteralPath $csvPath -Algorithm SHA256).Hash -ne $finished.CsvSha256) {
        throw 'Хеш CSV не совпадает с неизменяемой записью finished.json; анализ изменённого файла запрещён.'
    }
    $csvHashVerified=$true
}
$analyzerSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$scopeSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ProcmonCsvScope.cs') -Algorithm SHA256).Hash
$captureStart = if($observer) { [DateTimeOffset]($observer.StartedUtc) } else {$null}
$captureEnd = if($finished -and $finished.CaptureStoppedUtc) { [DateTimeOffset]($finished.CaptureStoppedUtc) }
    elseif($observer -and $finished -and $finished.RuntimeExpired) { $captureStart.AddSeconds($observer.RuntimeSeconds) } else {$null}
$applicationPath = Join-Path $SessionDirectory 'application.json'
$application = if(Test-Path -LiteralPath $applicationPath) { [IO.File]::ReadAllText($applicationPath) | ConvertFrom-Json } else { $null }
foreach($group in @($application.Processes|Group-Object ProcessId)) {
    $starts=@($group.Group|Where-Object StartUtc|ForEach-Object {([DateTimeOffset]($_.StartUtc)).UtcTicks}|Select-Object -Unique)
    if($starts.Count -gt 1) {throw ('Для PID ' + $group.Name + ' зарегистрированы разные жизни. Используйте отдельный сеанс для каждой жизни PID.')}
}
if(-not $ApplicationProcessId -and $application) {
    if($application.ProcessIds) { $ApplicationProcessId = @($application.ProcessIds) }
    elseif($application.ProcessId) { $ApplicationProcessId = @($application.ProcessId) }
}
if(-not $ApplicationProcessId -or @($ApplicationProcessId | Where-Object { $_ -le 0 }).Count -gt 0) {
    throw 'Укажите PID проверенного обычного процесса приложения через -ApplicationProcessId или application.json.'
}
if(-not $SaveDirectory) { $SaveDirectory = $plan.ExpectedSaveDirectory }
if(-not $Destination) { $Destination = $plan.ExpectedDestination }
if(-not $SaveDirectory -and $Destination) { $SaveDirectory = Split-Path $Destination -Parent }
$repository = [IO.Path]::GetFullPath($plan.RepositoryPath).TrimEnd('\')
$installation = if($plan.ExpectedInstallDirectory) { [IO.Path]::GetFullPath($plan.ExpectedInstallDirectory).TrimEnd('\') } else {$null}
$additionalInstallations = @($plan.AdditionalInstallationDirectories | Where-Object {$_} | ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')})
if($SaveDirectory) { $SaveDirectory = [IO.Path]::GetFullPath($SaveDirectory).TrimEnd('\') }
$owned = New-Object 'System.Collections.Generic.HashSet[int]'
$active = New-Object 'System.Collections.Generic.HashSet[int]'
$seeds = New-Object 'System.Collections.Generic.HashSet[int]'
foreach($processIdentifier in $ApplicationProcessId) { [void]$seeds.Add($processIdentifier) }
$lifetimes = @{}
$preexistingRoots = @()
if($application -and $captureStart) {
    $preexistingRoots = @($application.Processes | Where-Object {
        [int]$_.ProcessId -in $ApplicationProcessId -and $_.StartUtc -and ([DateTimeOffset]($_.StartUtc)) -lt $captureStart
    } | ForEach-Object {[int]$_.ProcessId})
    foreach($processIdentifier in $preexistingRoots) {
        [void]$owned.Add($processIdentifier);[void]$active.Add($processIdentifier);[void]$seeds.Remove($processIdentifier)
    }
}
$events = New-Object 'System.Collections.Generic.List[object]'
$mutations = New-Object 'System.Collections.Generic.List[object]'
$attempts = New-Object 'System.Collections.Generic.List[object]'
$ambiguous = New-Object 'System.Collections.Generic.List[object]'
$processTree = New-Object 'System.Collections.Generic.List[object]'
foreach($processIdentifier in $preexistingRoots) {
    $root = @($application.Processes | Where-Object ProcessId -eq $processIdentifier)[0]
    $lifetime = [pscustomobject]@{ParentId=$root.ParentProcessId;ChildId=$processIdentifier;ProcessName=$root.ProcessName;StartTime='Before capture; verified from live registration';ExitTime=$null;Path=$root.Path;Detail='Root already running before observer readiness; startup and earlier Steam activity are outside capture.'}
    $processTree.Add($lifetime);$lifetimes[$processIdentifier]=$lifetime
}
$ipcEvents = New-Object 'System.Collections.Generic.List[object]'
$deviceIoEvents = New-Object 'System.Collections.Generic.List[object]'
$registryHandleMetadata = New-Object 'System.Collections.Generic.List[object]'
$ntfsMetadataMutations = New-Object 'System.Collections.Generic.List[object]'
$firstCommonDialogModuleLoadTime = $null
$commonDialogLoaded = $false
$fileMutationOperations = @('WriteFile','SetEndOfFileInformationFile','SetAllocationInformationFile',
    'SetBasicInformationFile','SetRenameInformationFile','SetRenameInformationEx',
    'SetDispositionInformationFile','SetDispositionInformationEx','SetLinkInformationFile',
    'SetShortNameInformationFile','SetSecurityFile','SetEAFile','SetCompressionInformationFile',
    'SetValidDataLengthInformationFile','SetCaseSensitiveInformationFile')
$registryMutationOperations = @('RegSetValue','RegDeleteValue','RegDeleteKey','RegRenameKey','RegSetKeySecurity',
    'RegLoadKey','RegUnloadKey','RegRestoreKey','RegReplaceKey','RegSetInfoKey')
function Test-NamedPipePath([string]$Path) {
    return $Path -match '^(?:\\Device\\NamedPipe|\\?NamedPipe|\\\\[^\\]+\\pipe)(?:\\|$)'
}
function Test-NtfsMetadataPath([string]$Path) {
    return $Path -match '^[A-Za-z]:\\\$(?:LogFile|Mft|MftMirr|Bitmap|Volume|AttrDef|Boot|BadClus|Secure|UpCase|Extend)(?:[:\\]|$)'
}
function Test-DeviceIoPath([string]$Path) {
    return $Path -match '^\\Device\\(?:ConDrv|Afd|Tcp|Tcp6|Udp|Udp6|Nsi|Null|KsecDD|CNG|Mailslot)(?:\\|$)'
}
function Get-Scope([string]$Path) {
    if(Test-NamedPipePath $Path) { return 'NamedPipeIPC' }
    if(Test-DeviceIoPath $Path) { return 'DeviceIO' }
    if(Test-NtfsMetadataPath $Path) { return 'NtfsFilesystemMetadata' }
    if($Path -match '^(?:HK(LM|CU|CR|U|CC)\\|\\REGISTRY\\)') { return 'Registry' }
    if($installation -and ($Path.Equals($installation,[StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($installation + '\',[StringComparison]::OrdinalIgnoreCase))) { return 'Installation' }
    foreach($additional in $additionalInstallations) {
        if($Path.Equals($additional,[StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith($additional + '\',[StringComparison]::OrdinalIgnoreCase)) { return 'AdditionalInstallation' }
    }
    if($SaveDirectory -and ($Path.Equals($SaveDirectory,[StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($SaveDirectory + '\',[StringComparison]::OrdinalIgnoreCase))) { return 'SelectedSaveDirectory' }
    if($Path.Equals($repository,[StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($repository + '\',[StringComparison]::OrdinalIgnoreCase)) { return 'Repository' }
    return 'OutsideRepositoryAndSaveDirectory'
}
function New-Finding($Event,[string]$Kind,[string]$Reason) {
    $target = $null
    if($Event.Operation -like 'SetRenameInformation*' -and $Event.Detail -match '(?:^|, )FileName: (.+)$') { $target=$matches[1] }
    [pscustomobject]@{
        Time=$Event.'Time of Day'; ProcessName=$Event.'Process Name'; ProcessId=[int]$Event.PID
        RootProcessId=$Event.RootProcessId
        Operation=$Event.Operation; Path=$Event.Path; Result=$Event.Result; Detail=$Event.Detail
        Kind=$Kind; Reason=$Reason; Scope=(Get-Scope $Event.Path)
        BeforeFirstCommonDialogModuleLoad=(-not $commonDialogLoaded)
        RenameTarget=$target; RenameTargetScope=$(if($target) {Get-Scope $target} else {$null})
    }
}
$totalRows = 0
$firstRow = $true
$scopeType = 'ProcmonCsvScope' -as [type]
if(-not $scopeType) { Add-Type -Path (Join-Path $PSScriptRoot 'ProcmonCsvScope.cs') }
$rootIdentities=[Collections.Generic.List[ProcmonCsvScope+RootIdentity]]::new()
$identityTimeZone=if($plan.HostTimeZoneId) {[TimeZoneInfo]::FindSystemTimeZoneById($plan.HostTimeZoneId)} else {[TimeZoneInfo]::Local}
foreach($registration in $application.Processes) {
    if([int]$registration.ProcessId -notin $ApplicationProcessId -or -not $registration.StartUtc) {continue}
    $identity=[ProcmonCsvScope+RootIdentity]::new()
    $identity.ProcessId=[int]$registration.ProcessId;$identity.ParentProcessId=[int]$registration.ParentProcessId
    $identity.ProcessName=[string]$registration.ProcessName
    $identity.LocalStartTimeTicks=([TimeZoneInfo]::ConvertTime([DateTimeOffset]($registration.StartUtc),$identityTimeZone)).TimeOfDay.Ticks
    $rootIdentities.Add($identity)
}
$scopedCsv = [ProcmonCsvScope]::Read($csvPath,[int[]]$ApplicationProcessId,[bool]$ExcludeDescendants,[int[]]$preexistingRoots,$rootIdentities.ToArray())
$scopedCsv.Rows | ForEach-Object {
    [pscustomobject]@{'Time of Day'=$_.Time;'Process Name'=$_.ProcessName;PID=$_.Pid;Operation=$_.Operation;Path=$_.Path;Result=$_.Result;Detail=$_.Detail;RootProcessId=$_.RootProcessId}
} | ForEach-Object {
    $event = $_
    if($firstRow) {
        foreach($column in @('Time of Day','Process Name','PID','Operation','Path','Result','Detail')) {
            if(-not ($event.PSObject.Properties.Name -contains $column)) { throw ('Отсутствует столбец CSV Process Monitor: ' + $column) }
        }
        $firstRow = $false
    }
    $totalRows++
    $eventProcessId = 0
    if(-not [int]::TryParse($event.PID,[ref]$eventProcessId)) { return }
    if($event.Operation -eq 'Process Start') {
        $parentProcessId = 0
        if($event.Detail -match '(?:^|, )Parent PID: ([0-9]+)') { $parentProcessId = [int]$matches[1] }
        $lifetime = [pscustomobject]@{ParentId=$parentProcessId;ChildId=$eventProcessId;ProcessName=$event.'Process Name';RootProcessId=$event.RootProcessId;StartTime=$event.'Time of Day';ExitTime=$null;Path=$event.Path;Detail=$event.Detail}
        $processTree.Add($lifetime); $lifetimes[$eventProcessId]=$lifetime
    }
    # Ownership and registration matching were established by the CSV prefilter.
    [void]$owned.Add($eventProcessId)
    $events.Add($event)
    if($event.Operation -eq 'Process Exit') {
        if($lifetimes.ContainsKey($eventProcessId)) { $lifetimes[$eventProcessId].ExitTime=$event.'Time of Day' }
        [void]$active.Remove($eventProcessId)
    }
    if(-not $commonDialogLoaded -and $event.Operation -eq 'Load Image' -and $event.Path -match '(?i)\\comdlg32\.dll$') {
        $commonDialogLoaded = $true
        $firstCommonDialogModuleLoadTime = $event.'Time of Day'
    }
    $success = $event.Result -eq 'SUCCESS'
    if(Test-NamedPipePath $event.Path) {
        $ipcEvents.Add((New-Finding $event 'NamedPipeIPC' 'Именованный канал межпроцессного обмена; это не дисковый файл.'))
        return
    }
    if(Test-DeviceIoPath $event.Path) {
        $deviceIoEvents.Add((New-Finding $event 'DeviceIO' 'Обращение к именованному системному устройству; не создание обычного дискового файла.'))
        return
    }
    if($event.Operation -eq 'RegSetInfoKey' -and $event.Detail -match 'KeySetHandleTagsInformation') {
        $registryHandleMetadata.Add((New-Finding $event 'RegistryHandleTags' 'Теги открытого дескриптора ключа; не запись значения или создание постоянного ключа реестра.'))
        return
    }
    $knownMutation = $fileMutationOperations -contains $event.Operation -or $registryMutationOperations -contains $event.Operation
    if($knownMutation) {
        $isNtfsMetadata = Test-NtfsMetadataPath $event.Path
        $finding = New-Finding $event $(if($event.Operation -like 'Reg*') {'RegistryMutation'} elseif($isNtfsMetadata) {'NtfsFilesystemMetadata'} else {'FileMutation'}) 'Операция изменяет данные или метаданные.'
        if($success -and $isNtfsMetadata) { $ntfsMetadataMutations.Add($finding) }
        elseif($success) { $mutations.Add($finding) }
        else { $attempts.Add($finding) }
        return
    }
    if($event.Operation -eq 'CreateFile') {
        if($success -and $event.Detail -match 'OpenResult: (Created|Overwritten|Superseded)(?:,|$)') {
            $finding=New-Finding $event 'FileCreationOrReplacement' ('Подтверждено OpenResult: ' + $matches[1])
            if(Test-NtfsMetadataPath $event.Path) { $ntfsMetadataMutations.Add($finding) }
            else { $mutations.Add($finding) }
        } elseif($event.Detail -match 'Desired Access: [^,]*(Write|Append|Delete|Generic All|All Access)' -or
                 $event.Detail -match 'Disposition: (Create|OpenIf|Overwrite|OverwriteIf|Supersede)(?:,|$)') {
            $attempts.Add((New-Finding $event 'FileWriteIntent' 'Запрошен доступ для записи/создания; само открытие не доказывает изменение.'))
            if($success -and $event.Detail -notmatch 'OpenResult: (Opened|Created|Overwritten|Superseded)(?:,|$)') {
                $ambiguous.Add((New-Finding $event 'UnknownCreateDisposition' 'В CSV не указан результат открытия; требуется проверка исходного события.'))
            }
        }
        return
    }
    if($event.Operation -eq 'RegCreateKey') {
        if($success -and $event.Detail -match 'REG_CREATED_NEW_KEY') {
            $mutations.Add((New-Finding $event 'RegistryKeyCreation' 'Disposition: REG_CREATED_NEW_KEY.'))
        } elseif($success -and $event.Detail -match 'REG_OPENED_EXISTING_KEY') {
            $attempts.Add((New-Finding $event 'ExistingRegistryKeyOpened' 'Открыт существующий ключ; RegCreateKey здесь не доказывает создание.'))
        } else {
            $finding=New-Finding $event 'RegistryCreateAttempt' 'Попытка RegCreateKey; результат создания ключа не подтверждён.'
            $attempts.Add($finding)
            if($success) { $ambiguous.Add($finding) }
        }
        return
    }
    if($event.Operation -eq 'RegOpenKey' -and $event.Detail -match 'Desired Access: [^,]*(Write|All Access|Set Value|Create Sub Key)') {
        $attempts.Add((New-Finding $event 'RegistryWriteIntent' 'Запрошено открытие с правом записи; запись не подтверждена.'))
    }
}
$totalRows = $scopedCsv.TotalRows
$seeds=[Collections.Generic.HashSet[int]]::new([int[]]$scopedCsv.MissingRootProcessIds)
if($events.Count -eq 0) { throw 'Для указанных PID нет событий; такой журнал не подтверждает проверку приложения.' }
$events | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'owned-events.csv') -NoTypeInformation -Encoding UTF8
$fileMutations = @($mutations | Where-Object Kind -like 'File*')
$registryMutations = @($mutations | Where-Object Kind -like 'Registry*')
$outsideMutations = @($fileMutations | Where-Object {
    $_.Scope -notin @('Installation','AdditionalInstallation','NamedPipeIPC','NtfsFilesystemMetadata') -or
        ($_.RenameTarget -and $_.RenameTargetScope -notin @('Installation','AdditionalInstallation'))
})
$temporaryMutations = @($fileMutations | Where-Object { $_.Path -match '(?i)\\\.ap-[^\\]+\.tmp$' })
$resourceTemporaryMutations = @($fileMutations | Where-Object { $_.Path -match '(?i)\\RCX[^\\]+\.tmp$' })
$fileMutations | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'file-mutations.csv') -NoTypeInformation -Encoding UTF8
$registryMutations | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'registry-mutations.csv') -NoTypeInformation -Encoding UTF8
$attempts | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'write-intents-and-attempts.csv') -NoTypeInformation -Encoding UTF8
$ambiguous | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'ambiguous-events.csv') -NoTypeInformation -Encoding UTF8
$ipcEvents | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'named-pipe-ipc.csv') -NoTypeInformation -Encoding UTF8
$deviceIoEvents | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'device-io.csv') -NoTypeInformation -Encoding UTF8
$registryHandleMetadata | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'registry-handle-tags.csv') -NoTypeInformation -Encoding UTF8
$ntfsMetadataMutations | Export-Csv -LiteralPath (Join-Path $SessionDirectory 'ntfs-metadata-mutations.csv') -NoTypeInformation -Encoding UTF8
$paths = @($fileMutations | Select-Object -ExpandProperty Path -Unique | Sort-Object | ForEach-Object {
    [pscustomobject]@{Path=$_;Scope=(Get-Scope $_);ExistsAtAnalysis=(Test-Path -LiteralPath $_)
        IsExporterTemporary=($_ -match '(?i)\\\.ap-[^\\]+\.tmp$');IsResourceUpdateTemporary=($_ -match '(?i)\\RCX[^\\]+\.tmp$')}
})
$remainingTemporary = @($paths | Where-Object { $_.IsExporterTemporary -and $_.ExistsAtAnalysis })
$remainingResourceTemporary = @($paths | Where-Object { $_.IsResourceUpdateTemporary -and $_.ExistsAtAnalysis })
$guiRoots = @($application.Processes | Where-Object { [int]$_.ProcessId -in $ApplicationProcessId -and $_.ProcessName -match '^LANMinecraft(?:\.exe)?$' } | ForEach-Object {[int]$_.ProcessId})
$guiOwned = [Collections.Generic.HashSet[int]]::new()
foreach($event in $events) {if([int]$event.RootProcessId -in $guiRoots) {[void]$guiOwned.Add([int]$event.PID)}}
$guiFileMutations=@($fileMutations|Where-Object {[int]$_.RootProcessId -in $guiRoots})
$cleanupHelpers=@($processTree|Where-Object {
    if($_.ProcessName -notmatch '^powershell(?:\.exe)?$' -or [int]$_.RootProcessId -notin $guiRoots -or [int]$_.ParentId -notin $guiRoots) {return $false}
    if($_.Detail -notmatch '(?i)-EncodedCommand\s+([A-Za-z0-9+/=]+)') {return $false}
    try {
        $scriptText=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($matches[1]))
        return $scriptText.Contains('Test-ExtractionInUse') -and $scriptText.Contains('$extractionDir')
    } catch {return $false}
})
function Get-EventTimeSpan([string]$Value) {
    if($Value -notmatch '^(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})(?:[.,](?<f>\d{1,7}))?$') { return $null }
    $fraction = $matches.f
    $ticks = [long]([TimeSpan]::new([int]$matches.h,[int]$matches.m,[int]$matches.s)).Ticks
    if($fraction) { $ticks += [long]$fraction.PadRight(7,'0') }
    return [TimeSpan]::FromTicks($ticks)
}
$observedStart = $null; $observedEnd = $null
if($captureStart) {
    $timeZone = if($plan.HostTimeZoneId) { [TimeZoneInfo]::FindSystemTimeZoneById($plan.HostTimeZoneId) } else {[TimeZoneInfo]::Local}
    $localStart = [TimeZoneInfo]::ConvertTime($captureStart,$timeZone)
    $firstTime = Get-EventTimeSpan $scopedCsv.FirstEventTime
    $lastTime = Get-EventTimeSpan $scopedCsv.LastEventTime
    if($null -ne $firstTime -and $null -ne $lastTime) {
        $observedStart = [DateTimeOffset]::new($localStart.Date.AddTicks($firstTime.Ticks),$localStart.Offset)
        if(($observedStart - $captureStart).TotalHours -gt 12) { $observedStart=$observedStart.AddDays(-1) }
        elseif(($observedStart - $captureStart).TotalHours -lt -12) { $observedStart=$observedStart.AddDays(1) }
        $observedEnd = [DateTimeOffset]::new($observedStart.Date.AddTicks($lastTime.Ticks),$observedStart.Offset)
        if($lastTime -lt $firstTime) { $observedEnd=$observedEnd.AddDays(1) }
    }
}
$summary = [ordered]@{
    AnalyzedUtc=[DateTime]::UtcNow.ToString('o'); CsvRows=$totalRows; ApplicationRows=$events.Count
    AnalyzerSha256=$analyzerSha256;CsvScopeSourceSha256=$scopeSourceSha256;OriginalCsvSha256Verified=$csvHashVerified
    FirstObservedEventLocalTime=$scopedCsv.FirstEventTime;LastObservedEventLocalTime=$scopedCsv.LastEventTime
    FirstObservedEventUtc=$(if($observedStart) {$observedStart.ToUniversalTime().ToString('o')} else {$null})
    LastObservedEventUtc=$(if($observedEnd) {$observedEnd.ToUniversalTime().ToString('o')} else {$null})
    CsvPath=$csvPath
    InitialProcessIds=@($ApplicationProcessId); IncludedProcessIds=@($owned | Sort-Object); ProcessTree=$processTree.ToArray()
    ExpectedApplicationPath=$plan.ExpectedApplicationPath; RepositoryPath=$repository
    InstallationDirectory=$installation; Phase=$plan.Phase; ApplicationSha256=$plan.ApplicationSha256
    AdditionalInstallationDirectories=$additionalInstallations
    ScenarioKind=$plan.ScenarioKind
    CaptureStartUtc=$(if($captureStart) {$captureStart.ToString('o')} else {$null})
    CaptureEndUtc=$(if($captureEnd) {$captureEnd.ToString('o')} else {$null})
    CaptureEndIsRuntimeEstimate=($finished -and -not $finished.CaptureStoppedUtc -and $finished.RuntimeExpired)
    SelectedSaveDirectory=$SaveDirectory; ExpectedDestination=$Destination
    SuccessfulFileMutationEvents=$fileMutations.Count; SuccessfulRegistryMutationEvents=$registryMutations.Count
    GuiRootProcessIds=$guiRoots;GuiOwnedProcessIds=@($guiOwned)
    GuiShutdownPowerShellHelpers=@($cleanupHelpers|Select-Object ChildId,ParentId,ProcessName,StartTime,ExitTime)
    GuiFileMutationEvents=$guiFileMutations.Count
    GuiSettingsMutationEvents=@($guiFileMutations|Where-Object {$_.Path -match '(?i)\\\.?settings\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\settings\.json$'}).Count
    GuiWindowPlacementMutationEvents=@($guiFileMutations|Where-Object {$_.Path -match '(?i)\\\.?window-placement\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\window-placement\.json$'}).Count
    GuiPackHashMutationEvents=@($guiFileMutations|Where-Object {$_.Path -match '(?i)\\\.?pack-hashes\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\pack-hashes\.json$'}).Count
    GuiRootMetrics=@(foreach($rootId in $guiRoots) {
        $rootFiles=@($guiFileMutations|Where-Object {[int]$_.RootProcessId -eq $rootId})
        [pscustomobject]@{ProcessId=$rootId;FileMutationEvents=$rootFiles.Count
            SettingsMutationEvents=@($rootFiles|Where-Object {$_.Path -match '(?i)\\\.?settings\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\settings\.json$'}).Count
            WindowPlacementMutationEvents=@($rootFiles|Where-Object {$_.Path -match '(?i)\\\.?window-placement\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\window-placement\.json$'}).Count
            PackHashMutationEvents=@($rootFiles|Where-Object {$_.Path -match '(?i)\\\.?pack-hashes\.json(?:\.[^\\]+\.tmp)?$' -or $_.RenameTarget -match '(?i)\\pack-hashes\.json$'}).Count
            DotNetNativeCacheMutationEvents=@($rootFiles|Where-Object Path -match '(?i)\\\.net\\LANMinecraft(?:\\|$)').Count
            ShutdownPowerShellHelpers=@($cleanupHelpers|Where-Object {[int]$_.RootProcessId -eq $rootId}).Count}
    })
    GuiPolicyTempMutationEvents=@($guiFileMutations|Where-Object Path -match '(?i)\\__PSScriptPolicyTest_[^\\]+$').Count
    GuiDotNetNativeCacheMutationEvents=@($guiFileMutations|Where-Object Path -match '(?i)\\\.net\\LANMinecraft(?:\\|$)').Count
    SuccessfulFileWriteEvents=@($fileMutations | Where-Object Operation -eq 'WriteFile').Count
    SuccessfulRegistrySetValueEvents=@($registryMutations | Where-Object Operation -eq 'RegSetValue').Count
    SuccessfulRegistryDeleteValueEvents=@($registryMutations | Where-Object Operation -eq 'RegDeleteValue').Count
    SuccessfulRegistryKeyCreationEvents=@($registryMutations | Where-Object Kind -eq 'RegistryKeyCreation').Count
    ExistingRegistryKeyOpenEvents=@($attempts | Where-Object Kind -eq 'ExistingRegistryKeyOpened').Count
    NamedPipeEvents=$ipcEvents.Count
    DeviceIoEvents=$deviceIoEvents.Count
    SuccessfulNamedPipeWriteEvents=@($ipcEvents | Where-Object { $_.Operation -eq 'WriteFile' -and $_.Result -eq 'SUCCESS' }).Count
    RegistryHandleTagEvents=$registryHandleMetadata.Count
    SuccessfulNtfsMetadataMutationEvents=$ntfsMetadataMutations.Count
    NtfsMetadataPaths=@($ntfsMetadataMutations | Select-Object -ExpandProperty Path -Unique | Sort-Object)
    FirstCommonDialogModuleLoadTime=$firstCommonDialogModuleLoadTime
    SuccessfulFileMutationsBeforeFirstCommonDialogLoad=@($fileMutations | Where-Object BeforeFirstCommonDialogModuleLoad).Count
    SuccessfulRegistryMutationsBeforeFirstCommonDialogLoad=@($registryMutations | Where-Object BeforeFirstCommonDialogModuleLoad).Count
    FileMutationEventsOutsideInstallation=$outsideMutations.Count
    ExporterTemporaryMutationEvents=$temporaryMutations.Count; RemainingExporterTemporaryFiles=$remainingTemporary.Count
    ExporterTemporaryWriteEvents=@($temporaryMutations | Where-Object Operation -eq 'WriteFile').Count
    ResourceUpdateTemporaryMutationEvents=$resourceTemporaryMutations.Count
    ResourceUpdateTemporaryWriteEvents=@($resourceTemporaryMutations | Where-Object Operation -eq 'WriteFile').Count
    RemainingResourceUpdateTemporaryFiles=$remainingResourceTemporary.Count
    WriteIntentOrFailedAttemptEvents=$attempts.Count; AmbiguousSuccessfulEvents=$ambiguous.Count
    CaptureFinished=(Test-Path -LiteralPath (Join-Path $SessionDirectory 'finished.json'))
    CaptureStatistics=$(if(Test-Path -LiteralPath (Join-Path $SessionDirectory 'capture-statistics.json')) {
        [IO.File]::ReadAllText((Join-Path $SessionDirectory 'capture-statistics.json')) | ConvertFrom-Json
    } else {$null})
    UnobservedRootProcessIds=@($seeds)
    PreexistingRootProcessIds=$preexistingRoots
    Scenarios=$(if(Test-Path -LiteralPath (Join-Path $SessionDirectory 'scenarios.jsonl')) {
        @(Get-Content -LiteralPath (Join-Path $SessionDirectory 'scenarios.jsonl') | ForEach-Object {
            $marker = $_ | ConvertFrom-Json
            $inCapture = if($observedStart -and $observedEnd) {
                $markerTime=[DateTimeOffset]($marker.Utc); $markerTime -ge $observedStart -and $markerTime -le $observedEnd
            } else {$null}
            $marker | Add-Member -NotePropertyName MarkerRecordedDuringCapture -NotePropertyValue $inCapture -PassThru
        })
    } else {@()})
    FileMutationPaths=$paths; RegistryMutationPaths=@($registryMutations | Select-Object -ExpandProperty Path -Unique | Sort-Object)
    Limitations=@('Проверен только записанный сценарий и указанные PID с найденными дочерними процессами.',
        'Действия общих системных служб и других процессов не приписываются приложению без дополнительного доказательства.',
        'Успешная операция записи не равнозначна пользовательской настройке приложения: ОС и стандартный диалог тоже могут писать от его PID.',
        'ReadFile и открытие существующего файла/ключа не считаются записью. Неоднозначные события требуют ручной проверки.')
}
[IO.File]::WriteAllText((Join-Path $SessionDirectory 'summary.json'),($summary | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
function Format-Cell([string]$Value) { ($Value -replace '\|','\|') -replace '[\r\n]+',' ' }
$report = New-Object 'System.Collections.Generic.List[string]'
$report.Add('# Фактические события Process Monitor')
$report.Add('')
$report.Add(('Приложение: `{0}`. PID: {1}. Проанализировано событий: {2}.' -f $plan.ExpectedApplicationPath,(@($owned | Sort-Object) -join ', '),$events.Count))
$report.Add(('Репозиторий: `{0}`. Выбранная папка: `{1}`.' -f $repository,$SaveDirectory))
$report.Add(('Фаза: {0}. Установка: `{1}`. SHA-256 EXE: `{2}`.' -f $plan.Phase,$installation,$plan.ApplicationSha256))
$report.Add(('Тип сценария: {0}. Для функционального стенда корневые процессы тестов и хеши сборок перечислены отдельно; это не запуск игры из опубликованного EXE.' -f $plan.ScenarioKind))
$report.Add(('Окно записи UTC: {0} — {1}. Конец по таймеру: {2}.' -f $summary.CaptureStartUtc,$summary.CaptureEndUtc,$summary.CaptureEndIsRuntimeEstimate))
$report.Add(('Фактически первый/последний экспортированный event (локальное время): {0} — {1}. Время выхода наблюдателя включает сброс/закрытие журнала и не заменяет последнюю отметку события.' -f $summary.FirstObservedEventLocalTime,$summary.LastObservedEventLocalTime))
$report.Add(('Фактически наблюдавшиеся границы UTC: {0} — {1}. Проверка времени маркеров использует именно эти границы.' -f $summary.FirstObservedEventUtc,$summary.LastObservedEventUtc))
$report.Add(('Корневые PID, уже работавшие до записи: {0}. Их запуск и предшествующая активность в эту трассу не входят.' -f ($preexistingRoots -join ', ')))
$report.Add('')
$report.Add(('- Успешные операции изменения обычных дисковых файлов/метаданных: **{0}**.' -f $fileMutations.Count))
$report.Add(('- Из них вне проверяемой установки: **{0}**.' -f $outsideMutations.Count))
$report.Add(('- Успешные изменения реестра: **{0}**.' -f $registryMutations.Count))
$report.Add(('- Операции с метаданными NTFS (отдельно от пользовательских файлов): **{0}**.' -f $ntfsMetadataMutations.Count))
$report.Add(('- Успешные WriteFile в именованные каналы IPC, не на диск: **{0}**.' -f $summary.SuccessfulNamedPipeWriteEvents))
$report.Add(('- Отдельно учтённые обращения к системным устройствам, не обычным файлам: **{0}**.' -f $deviceIoEvents.Count))
$report.Add(('- RegSetInfoKey/KeySetHandleTagsInformation, не изменение постоянных значений реестра: **{0}**.' -f $registryHandleMetadata.Count))
$report.Add(('- Операции с временными `.ap-*.tmp`: **{0}**, оставшиеся файлы: **{1}**.' -f $temporaryMutations.Count,$remainingTemporary.Count))
$report.Add(('- Операции с временными `RCX*.tmp`: **{0}**, оставшиеся файлы: **{1}**.' -f $resourceTemporaryMutations.Count,$remainingResourceTemporary.Count))
$report.Add(('- Неоднозначные успешные события, требующие проверки: **{0}**.' -f $ambiguous.Count))
$report.Add(('- Отдельно по GUI и его потомкам: PowerShell-помощников закрытия {0}; изменений settings {1}; window-placement {2}; __PSScriptPolicyTest {3}; кеша .NET {4}.' -f $cleanupHelpers.Count,$summary.GuiSettingsMutationEvents,$summary.GuiWindowPlacementMutationEvents,$summary.GuiPolicyTempMutationEvents,$summary.GuiDotNetNativeCacheMutationEvents))
$report.Add(('- Не найден Process Start зарегистрированных корневых PID: **{0}**.' -f (@($seeds) -join ', ')))
if($summary.CaptureStatistics) {
    $report.Add(('- Счётчик потерянных событий: **{0}**. Источник: {1}.' -f $summary.CaptureStatistics.KernelDroppedEvents,$summary.CaptureStatistics.Evidence))
} else {
    $report.Add('- Счётчик потерянных событий ядра не подтверждён статистикой Procmon; нулевое значение не заявляется.')
}
$report.Add('')
$report.Add('## Выполненные сценарии')
$report.Add('')
foreach($marker in $summary.Scenarios) { $report.Add(('- {0}: **{1}**, {2}. {3} Маркер записан внутри окна трассы: {4}.' -f $marker.Utc,$marker.Status,$marker.Scenario,$marker.Detail,$marker.MarkerRecordedDuringCapture)) }
$report.Add('')
$report.Add('## Дерево процессов и время жизни')
$report.Add('')
$report.Add('| PID | Родитель | Начало | Выход | Образ |');$report.Add('|---|---|---|---|---|')
foreach($entry in $processTree) { $report.Add(('| {0} | {1} | {2} | {3} | {4} |' -f $entry.ChildId,$entry.ParentId,$entry.StartTime,$entry.ExitTime,(Format-Cell $entry.Path))) }
$report.Add('')
$report.Add('## Файлы с подтверждёнными изменениями')
$report.Add('')
$report.Add('| Путь | Область | Существует после проверки |')
$report.Add('|---|---|---|')
foreach($entry in $paths) { $report.Add(('| {0} | {1} | {2} |' -f (Format-Cell $entry.Path),$entry.Scope,$entry.ExistsAtAnalysis)) }
$report.Add('')
$report.Add('## Отдельно: метаданные NTFS')
$report.Add('')
if($ntfsMetadataMutations.Count -eq 0) { $report.Add('В записанном сценарии не обнаружены.') }
else {
    foreach($entry in $ntfsMetadataMutations) { $report.Add(('- `{0}`: {1}, {2}.' -f $entry.Path,$entry.Operation,$entry.Result)) }
}
$report.Add('')
$report.Add('## Подтверждённые изменения реестра')
$report.Add('')
if($registryMutations.Count -eq 0) { $report.Add('В записанном сценарии не обнаружены.') }
else {
    $report.Add('| Операция | Путь | Детали |'); $report.Add('|---|---|---|')
    foreach($entry in $registryMutations) { $report.Add(('| {0} | {1} | {2} |' -f $entry.Operation,(Format-Cell $entry.Path),(Format-Cell $entry.Detail))) }
}
$report.Add('')
$report.Add('## Границы вывода')
$report.Add('')
foreach($limitation in $summary.Limitations) { $report.Add(('- ' + $limitation)) }
$report.Add('- Журналы содержат локальные пути и системные события. Не публикуйте PML/CSV и application.json в GitHub.')
[IO.File]::WriteAllLines((Join-Path $SessionDirectory 'PortabilityReport.md'),$report,[Text.UTF8Encoding]::new($false))
[pscustomobject]$summary
