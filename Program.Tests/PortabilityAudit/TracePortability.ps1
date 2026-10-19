[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProcmonPath,
    [string]$ConfigPath,
    [string]$SessionDirectory,
    [string]$ApplicationPath,
    [string]$InstallationDirectory,
    [string[]]$AdditionalInstallationDirectory = @(),
    [ValidateSet('gui','functional','probe')][string]$ScenarioKind = 'gui',
    [ValidateSet('probe','baseline','recheck')][string]$Phase = 'probe',
    [ValidateRange(10,600)][int]$RuntimeSeconds = 90,
    [switch]$Observer,
    [switch]$ConvertOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Artifacts'))
if($ConvertOnly -and -not $SessionDirectory) { throw 'Для преобразования укажите существующий каталог сеанса.' }
if(-not $SessionDirectory) {
    $SessionDirectory = Join-Path $artifactsRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
$SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
if(-not $SessionDirectory.StartsWith($artifactsRoot + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Каталог проверки должен находиться внутри Program.Tests\PortabilityAudit\Artifacts.'
}
$ProcmonPath = [IO.Path]::GetFullPath($ProcmonPath)
if($ConfigPath) { $ConfigPath = [IO.Path]::GetFullPath($ConfigPath) }
elseif(-not $ConvertOnly) { throw 'Для новой записи укажите ConfigPath.' }
$metadataPath = Join-Path $SessionDirectory 'observer.json'
$readyPath = Join-Path $SessionDirectory 'ready.json'
$stopPath = Join-Path $SessionDirectory 'stop.request'
$finishedPath = Join-Path $SessionDirectory 'finished.json'
$errorPath = Join-Path $SessionDirectory $(if($ConvertOnly) {'conversion-error.txt'} else {'observer-error.txt'})
$pmlPath = Join-Path $SessionDirectory 'capture.pml'
$csvPath = Join-Path $SessionDirectory 'capture.csv'
if($ConvertOnly -and (Test-Path -LiteralPath $finishedPath)) {
    throw 'Завершённая запись и её исходные CSV/хеши неизменяемы. Используйте существующий CSV; ConvertOnly предназначен для восстановления незавершённого преобразования.'
}

function Write-JsonFile($Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
function Quote-Argument([string]$Value) {
    if($Value.Contains('"')) { throw 'Недопустимая кавычка в аргументе.' }
    return '"' + $Value + '"'
}
function Get-ProcmonProcesses {
    @(Get-Process -Name Procmon,Procmon64,Procmon64a -ErrorAction SilentlyContinue)
}
function Confirm-Tool {
    if(-not (Test-Path -LiteralPath $ProcmonPath -PathType Leaf)) { throw 'Process Monitor не найден.' }
    if(-not $ConvertOnly -and -not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw 'Конфигурация Process Monitor не найдена.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $ProcmonPath
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
        throw 'Подпись Process Monitor не подтверждена как Microsoft Corporation.'
    }
    return $signature
}
function Read-Configuration {
    $bytes = [IO.File]::ReadAllBytes($ConfigPath)
    $result = @{}
    $position = 0
    while($position -lt $bytes.Length) {
        if($position + 16 -gt $bytes.Length) { throw 'Неполная запись конфигурации Process Monitor.' }
        $length = [BitConverter]::ToInt32($bytes,$position)
        $offset = [BitConverter]::ToInt32($bytes,$position + 8)
        $dataLength = [BitConverter]::ToInt32($bytes,$position + 12)
        if($length -lt 16 -or $offset -lt 16 -or $dataLength -lt 0 -or
            $offset + $dataLength -ne $length -or $position + $length -gt $bytes.Length) { throw 'Некорректный формат PMC.' }
        $name = [Text.Encoding]::Unicode.GetString($bytes,$position + 16,$offset - 16).TrimEnd([char]0)
        if($dataLength -eq 4) { $result[$name] = [BitConverter]::ToInt32($bytes,$position + $offset) }
        $position += $length
    }
    if(-not $result.ContainsKey('DestructiveFilter') -or $result.DestructiveFilter -ne 0) {
        throw 'В PMC должно быть отключено удаление отфильтрованных событий.'
    }
    if(-not $result.ContainsKey('RingBufferSize') -or $result.RingBufferSize -ne 0) {
        throw 'Кольцевой буфер должен быть отключён: нужна полная ограниченная запись.'
    }
    return $result
}
function Wait-Child([Diagnostics.Process]$Child,[int]$TimeoutMilliseconds,[string]$Description,[int[]]$AllowedExitCodes=@(0)) {
    if(-not $Child.WaitForExit($TimeoutMilliseconds)) { throw ($Description + ': превышено время ожидания. Процесс не завершается принудительно.') }
    if($AllowedExitCodes -notcontains $Child.ExitCode) { throw ($Description + ': код завершения ' + $Child.ExitCode) }
}

if(-not $Observer) {
    [void](Confirm-Tool)
    if((Get-ProcmonProcesses).Count -ne 0) { throw 'Уже запущен Process Monitor. Сначала завершите существующую запись вручную.' }
    if($ConvertOnly) {
        if(-not (Test-Path -LiteralPath (Join-Path $SessionDirectory 'plan.json')) -or
            -not (Test-Path -LiteralPath $pmlPath) -or (Get-Item -LiteralPath $pmlPath).Length -le 0) {
            throw 'Для преобразования нужны существующие plan.json и непустой capture.pml.'
        }
    } else {
        $configuration = Read-Configuration
        $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($SessionDirectory))
        # The desktop probe produced about 27 MiB/s of PML. Budget conservatively
        # for PML, CSV and conversion overhead before starting a bounded capture.
        $requiredBytes = [Math]::Max(2GB,([long]$RuntimeSeconds * 64MB))
        if($volume.AvailableFreeSpace -lt $requiredBytes) {
            throw ('Недостаточно места для полной записи: требуется {0:N1} GiB, доступно {1:N1} GiB.' -f ($requiredBytes/1GB),($volume.AvailableFreeSpace/1GB))
        }
        if($Phase -ne 'probe' -and -not $ApplicationPath) { throw 'Для проверки лаунчера укажите ApplicationPath.' }
        if($ApplicationPath) {
            $ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
            if(-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) { throw 'Проверяемый EXE не найден.' }
        }
        if(-not $InstallationDirectory -and $ApplicationPath) { $InstallationDirectory = Split-Path $ApplicationPath -Parent }
        if($InstallationDirectory) {
            $InstallationDirectory = [IO.Path]::GetFullPath($InstallationDirectory).TrimEnd('\')
            if($SessionDirectory.Equals($InstallationDirectory,[StringComparison]::OrdinalIgnoreCase) -or
                $SessionDirectory.StartsWith($InstallationDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) {
                throw 'Журнал Process Monitor нельзя размещать внутри проверяемой установки.'
            }
        }
        if(Test-Path -LiteralPath $SessionDirectory) { throw 'Каталог сеанса уже существует; выберите новый каталог.' }
        [void][IO.Directory]::CreateDirectory($SessionDirectory)
        Write-JsonFile ([ordered]@{
        RepositoryPath=$repository; ExpectedApplicationPath=$ApplicationPath; Phase=$Phase; ScenarioKind=$ScenarioKind
        ExpectedInstallDirectory=$InstallationDirectory
        AdditionalInstallationDirectories=@($AdditionalInstallationDirectory | ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')})
        ApplicationSha256=$(if($ApplicationPath) {(Get-FileHash -LiteralPath $ApplicationPath -Algorithm SHA256).Hash} else {$null})
        PreparedUtc=[DateTime]::UtcNow.ToString('o'); NormalLauncherPid=$PID
        ApplicationMustRemainUnelevated=$true; CaptureIncludesAllProcesses=$true
        DestructiveFilterDisabled=($configuration.DestructiveFilter -eq 0); RingBufferDisabled=($configuration.RingBufferSize -eq 0)
        FreeDiskBytesBeforeCapture=$volume.AvailableFreeSpace; ReservedCaptureBudgetBytes=$requiredBytes
        HostTimeZoneId=[TimeZoneInfo]::Local.Id; HostUtcOffset=[DateTimeOffset]::Now.Offset.ToString()
        Privacy='PML и CSV содержат системные пути и события других процессов. Не публиковать.'
        }) (Join-Path $SessionDirectory 'plan.json')
    }
    $argumentParts = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Quote-Argument $PSCommandPath),'-Observer',
        '-SessionDirectory',(Quote-Argument $SessionDirectory),'-ProcmonPath',(Quote-Argument $ProcmonPath),
        '-RuntimeSeconds',$RuntimeSeconds)
    if($ConfigPath) { $argumentParts += @('-ConfigPath',(Quote-Argument $ConfigPath)) }
    if($ConvertOnly) { $argumentParts += '-ConvertOnly' }
    $arguments = $argumentParts -join ' '
    # Only the observer is elevated. Launch the application separately from the normal user session.
    $helper = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    if($ConvertOnly) {
        [pscustomobject]@{SessionDirectory=$SessionDirectory;ObserverPid=$helper.Id;ConvertOnly=$true;FinishedPath=$finishedPath;ErrorPath=$errorPath}
        return
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while([DateTime]::UtcNow -lt $deadline) {
        if(Test-Path -LiteralPath $readyPath) {
            $ready = [IO.File]::ReadAllText($readyPath) | ConvertFrom-Json
            $metadata = [IO.File]::ReadAllText($metadataPath) | ConvertFrom-Json
            [pscustomobject]@{
                SessionDirectory=$SessionDirectory; ReadyPath=$readyPath; StopPath=$stopPath; ObserverPid=$helper.Id
                ReadyUtc=$ready.ReadyUtc
                CaptureDeadlineUtc=([DateTimeOffset]($metadata.StartedUtc)).AddSeconds($RuntimeSeconds).ToString('o')
            }
            return
        }
        if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
        if($helper.HasExited) { throw 'Наблюдатель завершился до начала записи.' }
        Start-Sleep -Milliseconds 200
    }
    throw ('Наблюдатель ещё не подтвердил готовность. Проверьте ' + $SessionDirectory)
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Наблюдателю Process Monitor нужны права администратора.' }
$captureProcess = $null
$capturePid = $null
$captureExitCode = $null
$terminationRequested = $false
$runtimeExpired = $false
$captureStoppedUtc = $null
$startedUtc = [DateTime]::UtcNow
try {
    $signature = Confirm-Tool
    if(-not $ConvertOnly) { [void](Read-Configuration) }
    if((Get-ProcmonProcesses).Count -ne 0) { throw 'Найден другой Process Monitor; запись не начинается.' }
    if(-not (Test-Path -LiteralPath (Join-Path $SessionDirectory 'plan.json'))) { throw 'Не найден план сеанса.' }
    if($ConvertOnly) {
        if(-not (Test-Path -LiteralPath $pmlPath) -or (Get-Item -LiteralPath $pmlPath).Length -le 0) { throw 'Не найден непустой PML для преобразования.' }
        if(Test-Path -LiteralPath $metadataPath) {
            $originalMetadata = [IO.File]::ReadAllText($metadataPath) | ConvertFrom-Json
            $capturePid = $originalMetadata.ProcmonPid
        }
    } else {
    $arguments = @('/AcceptEula','/Quiet','/Minimized','/NoFilter','/LoadConfig',(Quote-Argument $ConfigPath),
        '/BackingFile',(Quote-Argument $pmlPath),'/Runtime',$RuntimeSeconds) -join ' '
    $captureProcess = Start-Process -FilePath $ProcmonPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $capturePid = $captureProcess.Id
    Write-JsonFile ([ordered]@{
        StartedUtc=$startedUtc.ToString('o'); ObserverPid=$PID; ProcmonPid=$capturePid
        ProcmonPath=$ProcmonPath; ProcmonSha256=(Get-FileHash -LiteralPath $ProcmonPath -Algorithm SHA256).Hash
        ProcmonSignature=$signature.Status.ToString(); ConfigPath=$ConfigPath
        ConfigSha256=(Get-FileHash -LiteralPath $ConfigPath -Algorithm SHA256).Hash
        CaptureArguments=$arguments; RuntimeSeconds=$RuntimeSeconds; PmlPath=$pmlPath; CsvPath=$csvPath
    }) $metadataPath
    $idle = Start-Process -FilePath $ProcmonPath -ArgumentList '/WaitForIdle /Quiet' -WindowStyle Hidden -PassThru
    Wait-Child $idle 30000 'Ожидание готовности Process Monitor'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while(-not (Test-Path -LiteralPath $pmlPath)) {
        if($captureProcess.HasExited) { throw 'Process Monitor завершился до создания журнала.' }
        if([DateTime]::UtcNow -ge $deadline) { throw 'Process Monitor не создал журнал PML.' }
        Start-Sleep -Milliseconds 100
    }
    $others = @(Get-ProcmonProcesses | Where-Object Id -ne $capturePid)
    if($others.Count -ne 0) { throw 'Появился другой Process Monitor; автоматическое завершение запрещено.' }
    Write-JsonFile ([ordered]@{ReadyUtc=[DateTime]::UtcNow.ToString('o');ProcmonPid=$capturePid;PmlPath=$pmlPath}) $readyPath
    $deadline = $startedUtc.AddSeconds($RuntimeSeconds + 10)
    while(-not $captureProcess.HasExited -and -not (Test-Path -LiteralPath $stopPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if(-not $captureProcess.HasExited) {
        # /Terminate has no PID option. Use it only while the sole running instance is the one we started.
        $running = @(Get-ProcmonProcesses)
        if($running.Count -ne 1 -or $running[0].Id -ne $capturePid -or $running[0].Path -ne $ProcmonPath) {
            throw 'Состав процессов Process Monitor изменился; чужие экземпляры не завершаются.'
        }
        $terminate = Start-Process -FilePath $ProcmonPath -ArgumentList '/Terminate /Quiet' -WindowStyle Hidden -PassThru
        $terminationRequested = $true
        Wait-Child $terminate 30000 'Остановка своей записи Process Monitor'
        # Exit 1 after our explicit /Terminate is provisional until the PML converts successfully.
        Wait-Child $captureProcess 30000 'Закрытие своей записи Process Monitor' @(0,1)
    }
    $captureExitCode = $captureProcess.ExitCode
    $captureStoppedUtc = [DateTime]::UtcNow.ToString('o')
    # Procmon 4.11 also returns 1 after its /Runtime limit. Accept it provisionally
    # only after the requested interval; successful full conversion is still required.
    $runtimeExpired = [DateTime]::UtcNow -ge $startedUtc.AddSeconds($RuntimeSeconds)
    if($captureExitCode -ne 0 -and -not ($captureExitCode -eq 1 -and ($terminationRequested -or $runtimeExpired))) {
        throw ('Process Monitor неожиданно завершился: ' + $captureExitCode)
    }
    }
    if((Get-ProcmonProcesses).Count -ne 0) { throw 'Другой Process Monitor мешает безопасному преобразованию журнала.' }
    if(-not (Test-Path -LiteralPath $pmlPath) -or (Get-Item -LiteralPath $pmlPath).Length -le 0) { throw 'Журнал PML пуст или отсутствует.' }
    $conversionArguments = @('/AcceptEula','/Quiet','/Minimized','/NoConnect','/OpenLog',(Quote-Argument $pmlPath),'/SaveAs',(Quote-Argument $csvPath)) -join ' '
    $conversion = Start-Process -FilePath $ProcmonPath -ArgumentList $conversionArguments -WindowStyle Hidden -PassThru
    Wait-Child $conversion 600000 'Преобразование журнала в CSV'
    if(-not (Test-Path -LiteralPath $csvPath) -or (Get-Item -LiteralPath $csvPath).Length -le 0) { throw 'CSV журнала пуст или не создан.' }
    $firstCsvEvent = Import-Csv -LiteralPath $csvPath | Select-Object -First 1
    if(-not $firstCsvEvent) { throw 'В преобразованном CSV нет событий.' }
    foreach($column in @('Time of Day','Process Name','PID','Operation','Path','Result','Detail')) {
        if($firstCsvEvent.PSObject.Properties.Name -notcontains $column) { throw ('Некорректный CSV: нет столбца ' + $column) }
    }
    Write-JsonFile ([ordered]@{
        FinishedUtc=[DateTime]::UtcNow.ToString('o'); ProcmonPid=$capturePid
        StoppedByRequest=(Test-Path -LiteralPath $stopPath); PmlBytes=(Get-Item -LiteralPath $pmlPath).Length
        CsvBytes=(Get-Item -LiteralPath $csvPath).Length; Complete=$true; ConvertedExistingPml=[bool]$ConvertOnly
        CaptureExitCode=$captureExitCode; ExplicitTermination=$terminationRequested
        AcceptedTerminationExitOne=($terminationRequested -and $captureExitCode -eq 1)
        RuntimeExpired=$runtimeExpired; AcceptedRuntimeExitOne=($runtimeExpired -and $captureExitCode -eq 1)
        CaptureStoppedUtc=$captureStoppedUtc
        # CSV conversion cannot prove the kernel did not drop events. Do not silently claim it can.
        KernelDroppedEventCount=$null; DroppedEventCheck='Requires Procmon capture statistics; no destructive filters or ring buffer were used.'
        PmlSha256=(Get-FileHash -LiteralPath $pmlPath -Algorithm SHA256).Hash
        PmlSegments=@(Get-ChildItem -LiteralPath $SessionDirectory -Filter 'capture*.pml' -File | Sort-Object Name | ForEach-Object {
            [ordered]@{Name=$_.Name;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
        CsvSha256=(Get-FileHash -LiteralPath $csvPath -Algorithm SHA256).Hash
    }) $finishedPath
} catch {
    [IO.File]::WriteAllText($errorPath,($_ | Out-String),[Text.UTF8Encoding]::new($false))
    # Never kill an unrelated Process Monitor. /Runtime bounds our capture even if an error occurs.
    if($captureProcess -and -not $captureProcess.HasExited) {
        $running = @(Get-ProcmonProcesses)
        if($running.Count -eq 1 -and $running[0].Id -eq $capturePid -and $running[0].Path -eq $ProcmonPath) {
            $terminate = Start-Process -FilePath $ProcmonPath -ArgumentList '/Terminate /Quiet' -WindowStyle Hidden -PassThru
            [void]$terminate.WaitForExit(30000)
        }
    }
    exit 1
}
