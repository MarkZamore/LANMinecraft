#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot 'Artifacts'),
    [switch]$RefreshAnalysis
)
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath($ArtifactsRoot)
if($RefreshAnalysis -and @(Get-Process -Name Procmon,Procmon64,Procmon64a -ErrorAction SilentlyContinue).Count) {
    throw 'Дождитесь остановки и преобразования всех записей перед сводным анализом.'
}
$utf8=[Text.UTF8Encoding]::new($false)
function Format-Utc($Value) {
    if($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {return 'unknown'}
    return ([DateTimeOffset]$Value).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'",[Globalization.CultureInfo]::InvariantCulture)
}
function Format-Cell($Value) {([string]$Value -replace '\|','\|') -replace '[\r\n]+',' '}
function Metric($Summary,[string]$Name,[switch]$Count) {
    $property=$Summary.PSObject.Properties[$Name]
    if(-not $property -or $null -eq $property.Value) {return 'unknown'}
    if($Count) {return @($property.Value).Count}
    return $property.Value
}
$sessions=New-Object 'System.Collections.Generic.List[object]'
foreach($directory in Get-ChildItem -LiteralPath $artifacts -Directory) {
    $planPath=Join-Path $directory.FullName 'plan.json'
    $finishedPath=Join-Path $directory.FullName 'finished.json'
    if(-not (Test-Path -LiteralPath $planPath) -or -not (Test-Path -LiteralPath $finishedPath)) {continue}
    $plan=[IO.File]::ReadAllText($planPath)|ConvertFrom-Json
    if($plan.Phase -eq 'probe') {continue}
    $finished=[IO.File]::ReadAllText($finishedPath)|ConvertFrom-Json
    if(-not $finished.CsvSha256 -or (Get-FileHash -LiteralPath (Join-Path $directory.FullName 'capture.csv') -Algorithm SHA256).Hash -ne $finished.CsvSha256) {
        throw ('Нарушена сохранность исходного CSV: ' + $directory.Name)
    }
    $summaryPath=Join-Path $directory.FullName 'summary.json'
    if($RefreshAnalysis -or -not (Test-Path -LiteralPath $summaryPath)) {
        & (Join-Path $PSScriptRoot 'AnalyzePortability.ps1') -SessionDirectory $directory.FullName | Out-Null
    }
    $summary=[IO.File]::ReadAllText($summaryPath)|ConvertFrom-Json
    $segments=@(Get-ChildItem -LiteralPath $directory.FullName -Filter 'capture*.pml' -File|Sort-Object Name|ForEach-Object {
        [ordered]@{Name=$_.Name;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    if($finished.PmlSegments) {
        $original=@($finished.PmlSegments)
        if($original.Count -ne $segments.Count) {throw ('Изменился состав исходных PML: ' + $directory.Name)}
        foreach($segment in $segments) {
            $expected=@($original|Where-Object Name -eq $segment.Name)
            if($expected.Count -ne 1 -or $expected[0].Bytes -ne $segment.Bytes -or $expected[0].Sha256 -ne $segment.Sha256) {
                throw ('Нарушена сохранность исходного PML: ' + $directory.Name + '/' + $segment.Name)
            }
        }
    } else {
        # Older captures recorded the base PML hash only. Verify that immutable hash
        # before adding the first complete split-segment inventory.
        $baseSegment=@($segments|Where-Object Name -eq 'capture.pml')
        if($baseSegment.Count -ne 1 -or $baseSegment[0].Sha256 -ne $finished.PmlSha256) {
            throw ('Не совпал исходный хеш базового PML: ' + $directory.Name)
        }
        $finished|Add-Member -NotePropertyName PmlSegments -NotePropertyValue $segments
    }
    $finished|Add-Member -NotePropertyName SegmentInventoryVerifiedUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
    [IO.File]::WriteAllText($finishedPath,($finished|ConvertTo-Json -Depth 8),$utf8)
    $registryPath=Join-Path $directory.FullName 'registry-mutations.csv'
    $registry=@(if(Test-Path -LiteralPath $registryPath) {Import-Csv -LiteralPath $registryPath})
    $filePath=Join-Path $directory.FullName 'file-mutations.csv'
    $externalFileFacts=@(if($plan.Phase -eq 'recheck' -and (Test-Path -LiteralPath $filePath)) {
        Import-Csv -LiteralPath $filePath|Where-Object {$_.Scope -notin @('Installation','AdditionalInstallation')}|ForEach-Object {
            $row=$_
            $family=if($row.Path -match '\\\.dotnet\\TelemetryStorageService\\' -and $row.ProcessName -match '^(?:dotnet|testhost)(?:\.exe)?$') {'%USERPROFILE%/.dotnet/TelemetryStorageService/*'}
                elseif($row.Path -match '\\Windows\\PowerShell\\StartupProfileData-NonInteractive$') {'%LOCALAPPDATA%/Microsoft/Windows/PowerShell/StartupProfileData-NonInteractive'}
                elseif($row.Path -match '\\\.net\\LANMinecraft\\') {'%TEMP%/.net/LANMinecraft/<bundle>/*'}
                elseif($row.Path -match '\\Steam\\logs\\gameoverlay_renderer\.txt$') {'Steam/logs/gameoverlay_renderer.txt'}
                else {$row.Path}
            $purpose=if($family -like '*TelemetryStorageService*') {'Накладные расходы .NET SDK/testhost стенда; временные .trn, не штатный запуск опубликованного EXE.'}
                elseif($family -like '*StartupProfileData*') {'Кеш запуска Windows PowerShell; срок задаёт Windows, точный предел хранения не подтверждён.'}
                elseif($family -like '*<bundle>*') {'Native DLL автономного EXE; текущая версия сохраняется, устаревшая неиспользуемая очищается.'}
                elseif($family -eq 'Steam/logs/gameoverlay_renderer.txt') {'Журнал Steam overlay в контексте GUI; LoadImage GameOverlayRenderer64.dll перед записью, атрибуция inferred по модулю/пути/времени. Срок/ротация Steam-controlled.'}
                else {'Назначение/срок не подтверждены; требует просмотра исходного события.'}
            [pscustomobject]@{ProcessName=$row.ProcessName;ProcessId=$row.ProcessId;Family=$family;Time=$row.Time;Purpose=$purpose;Path=$row.Path}
        }|Group-Object ProcessName,Family|ForEach-Object {
            $one=$_.Group[0]
            [pscustomobject]@{ProcessName=$one.ProcessName;ProcessIds=(@($_.Group.ProcessId|Sort-Object -Unique)-join ', ');Family=$one.Family
                Count=$_.Count;FirstLocal=$one.Time;LastLocal=$_.Group[-1].Time;Purpose=$one.Purpose;ExamplePath=$one.Path}
        }
    })
    $registryFacts=@($registry|Group-Object ProcessName,ProcessId,Operation,Path|ForEach-Object {
        $row=$_.Group[0];$life=@($summary.ProcessTree|Where-Object {[int]$_.ChildId -eq [int]$row.ProcessId})
        $oneLife=if($life.Count -eq 1) {$life[0]} else {$null}
        $updater=$oneLife -and $oneLife.Detail.Contains('apply-update.ps1')
        $role=if($row.Path -match '\\Services\\bam\\' -and $row.ProcessName -match '^(?:dotnet|testhost|vstest\.console)(?:\.exe)?$') {'Windows BAM от стенда; не запись настроек продукта'}
            elseif($row.Path -match '\\Services\\bam\\') {'Учёт запуска процесса Windows; срок хранения задаёт Windows'}
            elseif($row.Path -match '\\Microsoft\\(?:CTF|TIP)\\') {'Стандартный ввод Windows/WPF; назначение inferred по семейству ключей'}
            elseif($row.Path -match '\\Local Settings\\(?:MrtCache|MuiCache)\\') {'Кеш ресурсов/локализации Windows Shell (inferred по пути и .pri/.dll); срок хранения Windows-controlled, не конфигурация продукта'}
            elseif($row.Path -match '\\Windows\\Shell\\(?:Bags|BagMRU)\\') {'Состояние представления папок native picker/Windows Shell (inferred по ключам ColInfo/Sort/View); срок Windows-controlled'}
            elseif($row.Path -match '\\Explorer\\(?:ComDlg32|CIDOpen|Modules\\NavPane)\\') {'Положение/размер/последняя папка и навигация Windows file dialog; состояние Shell, срок Windows-controlled'}
            elseif($updater -and $row.Path -match '\\Internet Settings\\ZoneMap\\') {'Помощник обновления/Shell restart; инициализация зон Windows inferred, стек не подтверждён; не объявлено необходимым'}
            elseif($updater -and $row.Path -match '\\Notifications\\Data\\') {'Помощник обновления; значение Windows Notifications, точная причина не подтверждена; не объявлено необходимым'}
            elseif($row.ProcessName -eq 'LANMinecraft.exe' -and $row.Path -match '\\Internet Settings\\ZoneMap\\') {'GUI/Shell инициализация зон при native picker (inferred по времени/ключам); точный стек и необходимость не подтверждены; срок Windows-controlled'}
            elseif($row.ProcessName -eq 'LANMinecraft.exe' -and $row.Path -match '\\Notifications\\Data\\') {'GUI/Shell Windows Notifications; точная причина и необходимость не подтверждены; срок Windows-controlled'}
            else {'Не объяснено; требует отдельной проверки, не объявлено необходимым'}
        [pscustomobject]@{ProcessName=$row.ProcessName;ProcessId=$row.ProcessId;Operation=$row.Operation;Path=$row.Path;Count=$_.Count
            ParentId=$(if($oneLife){$oneLife.ParentId}else{$null});StartLocal=$(if($oneLife){$oneLife.StartTime}else{'unknown/multiple lifetimes'})
            ExitLocal=$(if($oneLife){$oneLife.ExitTime}else{$null});FirstEventLocal=$_.Group[0].Time;LastEventLocal=$_.Group[-1].Time;Attribution=$role}
    })
    $totalPmlBytes=[long]0
    foreach($segment in $segments) {$totalPmlBytes += [long]$segment.Bytes}
    $sessions.Add([pscustomobject]@{
        Name=$directory.Name;Directory=$directory.FullName;Phase=$plan.Phase;ScenarioKind=$plan.ScenarioKind
        ApplicationSha256=$plan.ApplicationSha256;Summary=$summary;PmlSegments=$segments
        TotalPmlBytes=$totalPmlBytes
        RegistryCategories=@($registry|ForEach-Object {
            $category=if($_.Path -match '\\Services\\bam\\') {'Windows BAM process history'}
                elseif($_.Path -match '\\Microsoft\\(?:CTF|TIP)\\') {'Windows text input/CTF/TIP'}
                elseif($_.Path -match '\\JavaSoft\\Prefs(?:\\|$)') {'Java preferences: requires investigation'}
                else {'Other: inspect registry-mutations.csv'}
            [pscustomobject]@{Category=$category;ProcessName=$_.ProcessName;Operation=$_.Operation}
        }|Group-Object Category|Select-Object Name,Count)
        RegistryFacts=$registryFacts
        ExternalFileFacts=$externalFileFacts
    })
}
if(-not $sessions.Count) {throw 'Нет законченных сеансов с анализом.'}
$report=New-Object 'System.Collections.Generic.List[string]'
$report.Add('# Локальная проверка записей лаунчера')
$report.Add('')
$report.Add(('Сводка сформирована UTC {0}. Исходные PML/CSV, окружение процессов и полные пути остаются только локально в игнорируемом Artifacts.' -f [DateTimeOffset]::UtcNow.ToString('o')))
$report.Add('')
$statusPath=Join-Path $artifacts 'audit-status.json'
$auditStatus=if(Test-Path -LiteralPath $statusPath) {[IO.File]::ReadAllText($statusPath)|ConvertFrom-Json} else {$null}
if($auditStatus) {
    $report.Add('## Что завершено и что осталось');$report.Add('')
    $report.Add([string]$auditStatus.Completed)
    $report.Add('')
    $report.Add([string]$auditStatus.Pending)
    $report.Add('')
    $report.Add([string]$auditStatus.BuildScope)
    $report.Add('')
}
$report.Add('## Подтверждённые события')
$report.Add('')
$report.Add('Суммы разных сеансов не являются прямым сравнением до/после: отличаются длительность, объём сценариев и CLI/Explorer-контекст. Например, финальный native picker записывал состояние Windows Shell, а baseline picker полностью не записан. Адресные сравнения одной операции приведены отдельно ниже.')
$report.Add('')
$report.Add('| Фаза / сеанс | Фактическая граница UTC | Файловые изменения | Реестр | Корни без Process Start | PML, GiB |')
$report.Add('|---|---|---:|---:|---|---:|')
foreach($session in $sessions) {
    $s=$session.Summary
    $report.Add(('| {0} / [{1}]({2}) | {3} — {4} | {5} | {6} | {7} | {8:N2} |' -f $session.Phase,$session.Name,
        ((Join-Path $session.Directory 'PortabilityReport.md') -replace '\\','/'),(Format-Utc $s.FirstObservedEventUtc),(Format-Utc $s.LastObservedEventUtc),
        $s.SuccessfulFileMutationEvents,$s.SuccessfulRegistryMutationEvents,(@($s.UnobservedRootProcessIds)-join ', '),($session.TotalPmlBytes/1GB)))
}
$report.Add('')
$report.Add('CreateFile с OpenResult: Opened и открытие существующего ключа не считаются созданием. В таблице событий учитываются подтверждённые записи, удаления и метаданные; это не число новых файлов. IPC, системные устройства и метаданные NTFS вынесены отдельно.')
$report.Add('WriteFile с Paging I/O остаётся физической записью, но PID может быть контекстом сброса системного кеша. В JDK-сценариях такие события на только что распакованных DLL наблюдались у java.exe; это не доказательство вызова записи файла кодом Java. Detail исходного CSV сохраняет флаги I/O.')
$report.Add('')
$report.Add('## Повторный запуск без изменений: устранённые записи')
$report.Add('')
$before=@($sessions|Where-Object Name -eq 'baseline-transport-a-capture'|Select-Object -First 1)
$after=@($sessions|Where-Object {$_.Phase -eq 'recheck' -and $_.Name -match 'gui.*warm.*capture'}|Sort-Object {$_.Summary.LastObservedEventUtc} -Descending|Select-Object -First 1)
if($before.Count -and $after.Count) {
    $b=$before[0].Summary;$a=$after[0].Summary
    $report.Add(('Парные GUI-сценарии одного CLI-контекста: {0} → {1}. При повторной проверке агент намеренно не менял настройки и геометрию; состояние секции мог изменить пользователь, поэтому полное отсутствие ввода не заявляется.' -f $before[0].Name,$after[0].Name))
    $report.Add('')
    $report.Add('| Операция GUI и его потомков | До | После | Изменение кода / назначение остатка |');$report.Add('|---|---:|---:|---|')
    $report.Add(('| PowerShell удаления текущего bundle при закрытии, процессы | {0} | {1} | Помощник удалён; текущий native cache сохраняется для следующего запуска. |' -f (Metric $b 'GuiShutdownPowerShellHelpers' -Count),(Metric $a 'GuiShutdownPowerShellHelpers' -Count)))
    $report.Add(('| `__PSScriptPolicyTest_*`, подтверждённые файловые операции | {0} | {1} | Побочные временные файлы удалённого помощника. Это счётчик операций, а не файлов. |' -f (Metric $b 'GuiPolicyTempMutationEvents'),(Metric $a 'GuiPolicyTempMutationEvents')))
    $report.Add(('| `settings.json` и атомарная стадия | {0} | {1} | Неизменённые настройки пропускаются; настоящие изменения сохраняются атомарно. |' -f (Metric $b 'GuiSettingsMutationEvents'),(Metric $a 'GuiSettingsMutationEvents')))
    $report.Add(('| `window-placement.json` и атомарная стадия | {0} | {1} | Неизменённое размещение не перезаписывается. |' -f (Metric $b 'GuiWindowPlacementMutationEvents'),(Metric $a 'GuiWindowPlacementMutationEvents')))
    $report.Add(('| Native bundle `.net/LANMinecraft`, файловые операции | {0} | {1} | Повторный запуск переиспользует текущую распаковку; первый запуск новой версии по-прежнему требует native DLL. |' -f (Metric $b 'GuiDotNetNativeCacheMutationEvents'),(Metric $a 'GuiDotNetNativeCacheMutationEvents')))
} else {$report.Add('Полный повторный GUI-сценарий ещё не проанализирован; нулевые значения не подставляются вместо отсутствующих данных.')}
$report.Add('')
$report.Add('## Что осталось и зачем')
$report.Add('')
$report.Add('| Категория | Назначение / срок хранения |')
$report.Add('|---|---|')
$report.Add('| Текущий `%TEMP%/.net/LANMinecraft/<bundle>` | Нативные библиотеки автономного WPF EXE; кеш сохраняется для повторного запуска. Старые неиспользуемые версии удаляет лаунчер. |')
$report.Add('| `Minecraft/Personal/settings.json`, размещение окна | Пользовательские настройки; сохраняются при изменении, до следующего изменения или удаления установки. |')
$report.Add('| Атомарные `.tmp`, стадия установки, журнал транзакции/отката | Предотвращают потерю настроек, мира или установленной версии при сбое; временные данные очищаются после завершения/восстановления операции. |')
$report.Add('| Java, архивы runtime, адаптеры, библиотеки/ресурсы сборки | Повторно используемые зависимости игры; хранятся внутри переносимой установки. Вспомогательная Java получает переносимые home/temp. |')
$report.Add('| Миры, скины, отчёты, данные профиля | Пользовательские данные, результаты разрешённого обмена и восстановления; сохраняются до удаления пользователем. |')
$report.Add('| Диагностические журналы | Текущий журнал лаунчера ограничен 1 MiB; архивы лаунчера 16 MiB, диагностика отдельной сборки 64 MiB, срок 7 дней. Полученные отчёты: не более 30 дней / 256 MiB. |')
$report.Add('| Подготовленное обновление, `apply-update.ps1`, `update.log` | Установка/перезапуск и восстановление после ошибки; файлы могут оставаться до следующей безопасной очистки при запуске, активные/неразрешённые журналы восстановления сохраняются пока нужны. |')
$report.Add('| `%LOCALAPPDATA%/Microsoft/Windows/PowerShell/StartupProfileData-NonInteractive` | Кеш оптимизации Windows PowerShell; подтверждены записи помощников обновления, срок хранения контролирует Windows. Это не конфигурация лаунчера. [Microsoft](https://learn.microsoft.com/en-us/powershell/scripting/dev-cross-plat/performance/startup-performance). |')
$report.Add('| `__PSScriptPolicyTest_*.ps1/.psm1` помощника обновления | Проверка политик PowerShell/AppLocker; реализация создаёт пробные файлы и удаляет после проверки, занятые могут остаться. [Исходник Microsoft](https://github.com/PowerShell/PowerShell/blob/v7.0.0/src/System.Management.Automation/security/wldpNativeMethods.cs#L160). |')
$report.Add('| BAM, CTF/TIP, системные кеши | Учёт запуска процессов и стандартный ввод Windows; требуют отдельной атрибуции. Это не собственные настройки лаунчера. |')
$report.Add('| Shell Bags/BagMRU, ComDlg32/CIDOpen/NavPane, MrtCache/MuiCache | Состояние представления/навигации и кеши ресурсов native Windows picker; срок задаёт Windows. Последняя папка, размер и позиция диалога сохраняются отдельно от списка недавних документов. [Microsoft IFileDialog](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifiledialog-setclientguid). |')
$report.Add('| Файлы стенда, TRX и PML/CSV | Доказательства проверки, не штатные данные лаунчера; остаются в Artifacts до ручного удаления владельцем. |')
$report.Add('| `Steam/logs/gameoverlay_renderer.txt` | GUI PID22412 загрузил GameOverlayRenderer64.dll в 20:51:22.214 UTC, через 21 мс начались записи журнала overlay; 115 операций до 20:51:46.314 UTC. Атрибуция модулю Steam inferred по этому сочетанию; срок/ротация задаёт Steam, точный предел не подтверждён. |')
$report.Add('')
$report.Add('Эта таблица описывает назначение категорий. Полные фактические пути и неподтверждённые события требуют просмотра отчёта соответствующего сеанса; автоматическая сводка не объявляет любой неизвестный файл необходимым.')
$report.Add('AddToRecent=false соответствует запрету добавления выбранного файла в recent documents; он не обещает отсутствия любой записи Windows Shell. [Microsoft FOS_DONTADDTORECENT](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_fileopendialogoptions). В финальном GUI-наблюдении RecentDocs/OpenSavePidlMRU не изменялись, но ComDlg32 last-folder/geometry, Shell view/resource caches и ZoneMap/Notifications записывались; точная причина последних без стека не доказана.')
$findingsPath=Join-Path $artifacts 'audit-findings.json'
if(Test-Path -LiteralPath $findingsPath) {
    $report.Add('');$report.Add('## Проверенные исправления и адресные повторные проверки');$report.Add('')
    $report.Add('| Изменение | Доказательство до | Доказательство после | Вывод |');$report.Add('|---|---|---|---|')
    foreach($finding in @([IO.File]::ReadAllText($findingsPath)|ConvertFrom-Json)) {
        $report.Add(('| {0} | {1} | {2} | {3} |' -f (Format-Cell $finding.Change),(Format-Cell $finding.Before),(Format-Cell $finding.After),(Format-Cell $finding.Conclusion)))
    }
}
$report.Add('')
$report.Add('## Реестр и артефакты за пределами установки')
$report.Add('')
foreach($session in $sessions) {
    $s=$session.Summary
    $report.Add(('### {0}' -f $session.Name));$report.Add('')
    $report.Add(('SHA-256 исходного EXE: `{0}`. Уже запущенные до записи PID: {1}.' -f $session.ApplicationSha256,(@($s.PreexistingRootProcessIds)-join ', ')))
    if($s.GuiRootMetrics) {
        $report.Add('')
        $report.Add('| GUI-корень PID | Все файловые изменения | Settings | Геометрия | PackHash | Native cache | PowerShell закрытия |')
        $report.Add('|---|---:|---:|---:|---:|---:|---:|')
        foreach($metric in $s.GuiRootMetrics) {
            $report.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} |' -f $metric.ProcessId,$metric.FileMutationEvents,$metric.SettingsMutationEvents,$metric.WindowPlacementMutationEvents,$metric.PackHashMutationEvents,$metric.DotNetNativeCacheMutationEvents,$metric.ShutdownPowerShellHelpers))
        }
    }
    foreach($category in $session.RegistryCategories) {$report.Add(('- {0}: {1}.' -f $category.Name,$category.Count))}
    if($session.RegistryFacts.Count) {
        $report.Add('')
        $report.Add('| Процесс / PID / родитель | Жизнь процесса, местное время PML | Значение реестра | События / число | Атрибуция |')
        $report.Add('|---|---|---|---|---|')
        foreach($entry in $session.RegistryFacts) {
            $report.Add(('| {0} / {1} / {2} | {3} — {4} | `{5}` | {6}, {7} — {8}; {9} | {10} |' -f
                $entry.ProcessName,$entry.ProcessId,$entry.ParentId,(Format-Cell $entry.StartLocal),(Format-Cell $entry.ExitLocal),
                (Format-Cell $entry.Path),$entry.Operation,$entry.FirstEventLocal,$entry.LastEventLocal,$entry.Count,(Format-Cell $entry.Attribution)))
        }
        $report.Add('')
    }
    $external=@($s.FileMutationPaths|Where-Object Scope -notin @('Installation','AdditionalInstallation'))
    $report.Add(('Изменённые пути вне объявленных установок: {0}; первые 12 ниже, полный список в отчёте сеанса.' -f $external.Count))
    if($session.ExternalFileFacts.Count) {
        $report.Add('')
        $report.Add('| Процесс / PID | Семейство пути (точные пути в CSV) | Местное время первого/последнего события | Число операций | Назначение / срок |')
        $report.Add('|---|---|---|---:|---|')
        foreach($fact in $session.ExternalFileFacts) {
            $report.Add(('| {0} / {1} | `{2}` | {3} — {4} | {5} | {6} |' -f $fact.ProcessName,$fact.ProcessIds,(Format-Cell $fact.Family),$fact.FirstLocal,$fact.LastLocal,$fact.Count,(Format-Cell $fact.Purpose)))
        }
        $report.Add('')
    }
    foreach($path in $external|Select-Object -First 12) {$report.Add(('- `{0}`; существует на момент анализа: {1}.' -f $path.Path,$path.ExistsAtAnalysis))}
    $classifiedPath=Join-Path $session.Directory 'classified-remaining-writes.csv'
    if(Test-Path -LiteralPath $classifiedPath) {
        $report.Add('')
        $report.Add(('Полный список по каждому пути: [classified-remaining-writes.csv]({0}). Колонки: процесс/PID/родитель, жизнь процесса, точный путь/rename target, операция/число, назначение, срок и степень подтверждения; командных строк и окружения нет.' -f ($classifiedPath -replace '\\','/')))
        $report.Add('')
        $report.Add('| Категория / процесс / PID | Пример точного пути | Назначение | Срок | Подтверждение |')
        $report.Add('|---|---|---|---|---|')
        foreach($group in @(Import-Csv -LiteralPath $classifiedPath|Group-Object Category,ProcessName)) {
            $one=$group.Group[0];$ids=@($group.Group.ProcessId|Sort-Object -Unique)-join ', '
            $report.Add(('| {0} / {1} / {2} | `{3}` | {4} | {5} | {6} |' -f $one.Category,$one.ProcessName,$ids,(Format-Cell $one.Path),(Format-Cell $one.Purpose),(Format-Cell $one.Retention),(Format-Cell $one.Confidence)))
        }
        $report.Add('')
    }
    if($s.CaptureStatistics) {$report.Add(('Счётчик потерь: {0}, доказательство: {1}.' -f $s.CaptureStatistics.KernelDroppedEvents,$s.CaptureStatistics.Evidence))}
    else {$report.Add('Счётчик потерь ядра не подтверждён; нулевое значение не заявляется. Деструктивные фильтры и кольцевой буфер отключены.')}
    $report.Add('')
}
$report.Add('## Границы вывода')
$report.Add('')
$crashReport=Join-Path $artifacts 'game-26.2-native-crash-diagnosis.md'
if(Test-Path -LiteralPath $crashReport) {
    $report.Add(('Предыдущая авария игры отдельно разобрана в [локальном отчёте minidump/Event1000]({0}): модуль завершения Overwolf OWClient.dll, native0xC0000409. Точный триггер не доказан; это не подтверждает успешное видимое игровое окно и не подменяет непрерывную трассу запуска/выхода. Полные argv/окружение не экспортируются.' -f ($crashReport -replace '\\','/')))
    $report.Add('')
}
$report.Add('- Сравнивать одинаковые сценарии и исходное состояние установки. Первая очистка legacy Shared удаляет старые ресурсы; её нельзя сравнивать с уже очищенной папкой.')
$report.Add('- Steam, запущенный из изолированного контекста автоматизации, может быть недоступен. Проверка нормального пользовательского контекста и реальной игры учитывается только по явно зарегистрированным PID и фактическому окну событий.')
$report.Add('- У корня, уже работавшего до записи, запуск и предшествующая активность не считаются покрытыми. Промежутки между записями и маркеры за фактической границей не подтверждают сценарий.')
$report.Add('- Функциональный стенд использует настоящие сервисы, локальные JDK и тестовый транспорт. Он не подтверждает соединение двух аккаунтов Valve; собственные файлы testhost/MSBuild учитываются как накладные расходы стенда.')
$report.Add('- Для не-Java групп стенд перенаправляет TEMP/TMP в изолированную установку. Это включает updater: расположение его PowerShell policy-проб внутри стенда не доказывает, что продукт сам перенаправляет PowerShell TEMP. В обычном запуске возможен системный TEMP; профиль StartupProfileData остаётся Windows-кешем. Java-группы сохраняют окружающий TEMP/TMP, чтобы проверить именно перенаправление кодом продукта.')
$report.Add('- Успешное преобразование PML и хеши сегментов подтверждают сохранность этих файлов; они не заменяют статистику потерь ядра.')
[IO.File]::WriteAllText((Join-Path $artifacts 'audit-index.json'),($sessions|ConvertTo-Json -Depth 14),$utf8)
$publicSummary=[ordered]@{GeneratedUtc=[DateTimeOffset]::UtcNow.ToString('o');Status=$auditStatus
    Sessions=@($sessions|ForEach-Object {
        [ordered]@{Name=$_.Name;Phase=$_.Phase;ApplicationSha256=$_.ApplicationSha256
            FirstObservedEventUtc=(Format-Utc $_.Summary.FirstObservedEventUtc);LastObservedEventUtc=(Format-Utc $_.Summary.LastObservedEventUtc)
            SuccessfulFileMutationEvents=$_.Summary.SuccessfulFileMutationEvents;SuccessfulRegistryMutationEvents=$_.Summary.SuccessfulRegistryMutationEvents
            MissingRootProcessIds=$_.Summary.UnobservedRootProcessIds;PreexistingRootProcessIds=$_.Summary.PreexistingRootProcessIds
            GuiRootMetrics=$_.Summary.GuiRootMetrics;TotalPmlBytes=$_.TotalPmlBytes
            OriginalCsvSha256Verified=$_.Summary.OriginalCsvSha256Verified;KernelDroppedEventCountVerified=[bool]$_.Summary.CaptureStatistics}
    })}
[IO.File]::WriteAllText((Join-Path $artifacts 'audit-public-summary.json'),($publicSummary|ConvertTo-Json -Depth 9),$utf8)
$reportPath=Join-Path $artifacts 'AUDIT-RESULTS.md'
[IO.File]::WriteAllLines($reportPath,$report,$utf8)
[pscustomobject]@{ReportPath=$reportPath;Sessions=$sessions.Count;Phases=@($sessions.Phase|Sort-Object -Unique)}
