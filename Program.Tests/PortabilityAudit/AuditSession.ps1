[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Register','Mark','Stop','Statistics')][string]$Action,
    [Parameter(Mandatory=$true)][string]$SessionDirectory,
    [int]$ProcessId,
    [string]$Scenario,
    [ValidateSet('begin','pass','fail','skip','note')][string]$Status = 'note',
    [string]$Detail,
    [ValidateRange(0,2147483647)][int]$DroppedEvents = 0
)
$ErrorActionPreference = 'Stop'
$SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
if(-not (Test-Path -LiteralPath (Join-Path $SessionDirectory 'plan.json'))) { throw 'Не найден план сеанса.' }
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Json($Value,[string]$Name) {
    [IO.File]::WriteAllText((Join-Path $SessionDirectory $Name),($Value | ConvertTo-Json -Depth 8),$utf8)
}
switch($Action) {
    'Register' {
        if($ProcessId -le 0) { throw 'Укажите PID запущенного обычного процесса приложения или тестового хоста.' }
        if(-not (Test-Path -LiteralPath (Join-Path $SessionDirectory 'ready.json'))) { throw 'Наблюдатель ещё не готов.' }
        if(Test-Path -LiteralPath (Join-Path $SessionDirectory 'finished.json')) { throw 'Запись уже закончена.' }
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $path = $process.Path
        $command = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $ProcessId)
        $entry = [ordered]@{
            ProcessId=$ProcessId; ProcessName=$process.ProcessName; Path=$path
            StartUtc=$process.StartTime.ToUniversalTime().ToString('o'); RegisteredUtc=[DateTime]::UtcNow.ToString('o')
            Sha256=$(if($path -and (Test-Path -LiteralPath $path)) {(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash} else {$null})
            ParentProcessId=$command.ParentProcessId; CommandLine=$command.CommandLine
        }
        $manifestPath = Join-Path $SessionDirectory 'application.json'
        $entries = @()
        if(Test-Path -LiteralPath $manifestPath) { $entries = @(([IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json).Processes) }
        $entries += $entry
        Write-Json ([ordered]@{Processes=$entries;ProcessIds=@($entries | ForEach-Object {$_.ProcessId} | Select-Object -Unique)}) 'application.json'
        [pscustomobject]$entry
    }
    'Mark' {
        if(-not $Scenario) { throw 'Укажите имя сценария.' }
        $entry = [ordered]@{Utc=[DateTime]::UtcNow.ToString('o');Scenario=$Scenario;Status=$Status;Detail=$Detail;MarkerProcessId=$PID}
        [IO.File]::AppendAllText((Join-Path $SessionDirectory 'scenarios.jsonl'),(($entry | ConvertTo-Json -Compress) + [Environment]::NewLine),$utf8)
        [pscustomobject]$entry
    }
    'Stop' {
        [IO.File]::WriteAllText((Join-Path $SessionDirectory 'stop.request'),[DateTime]::UtcNow.ToString('o'),$utf8)
    }
    'Statistics' {
        if(-not $PSBoundParameters.ContainsKey('DroppedEvents')) { throw 'Укажите фактически наблюдавшийся счётчик -DroppedEvents явно; неизвестный счётчик нельзя заменять нулём.' }
        if(-not $Detail) { throw 'Укажите источник фактического счётчика потерянных событий (снимок/экспорт статистики Procmon).' }
        Write-Json ([ordered]@{ObservedUtc=[DateTime]::UtcNow.ToString('o');KernelDroppedEvents=$DroppedEvents;Evidence=$Detail}) 'capture-statistics.json'
    }
}
