#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SessionDirectory)
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath($SessionDirectory)
$summary=Get-Content -Raw -LiteralPath (Join-Path $directory 'summary.json')|ConvertFrom-Json
if(-not $summary.OriginalCsvSha256Verified) {throw 'Сначала нужен завершённый анализ с проверкой оригинального CSV.'}
$guiIds=@($summary.GuiRootProcessIds|ForEach-Object {[int]$_})
$filePath=Join-Path $directory 'file-mutations.csv'
$regPath=Join-Path $directory 'registry-mutations.csv'
$events=@(Import-Csv -LiteralPath $filePath)+@(Import-Csv -LiteralPath $regPath)
function Describe($Row) {
    $path=[string]$Row.Path
    if($Row.Operation -like 'Reg*') {
        if($path -match '\\Services\\bam\\') {return @('WindowsProcessAccounting','Учёт запуска дочернего процесса Windows BAM.','Windows-controlled; точный срок не установлен.','Family/path known; no native stack attribution.')}
        if($path -match '\\Microsoft\\(?:CTF|TIP)\\') {return @('WindowsTextInput','Состояние текстового ввода Windows/WPF.','Windows-controlled.','Inferred by key family.')}
        if($path -match '\\Local Settings\\(?:MrtCache|MuiCache)\\') {return @('WindowsResourceCache','Кеш ресурсов/локализации Shell: .pri/.dll и файловые ассоциации.','Windows-controlled; точный срок не установлен.','Inferred by key and value paths.')}
        if($path -match '\\Windows\\Shell\\(?:Bags|BagMRU)\\') {return @('WindowsFolderViewState','Состояние вида папок native Windows picker/Shell: сортировка, столбцы и режим просмотра.','Windows-controlled; сохраняется между диалогами.','Inferred by key/value families; not a product config key.')}
        if($path -match '\\Explorer\\(?:ComDlg32|CIDOpen|Modules\\NavPane)\\') {return @('WindowsFileDialogState','Последняя папка, геометрия и навигация native file dialog.','Windows-controlled; сохраняется между диалогами.','Purpose supported by IFileDialog state API; exact registry mapping inferred.')}
        if($path -match '\\Internet Settings\\ZoneMap\\') {return @('WindowsShellInitializationUnconfirmed','Настройки зон Windows в контексте native picker/Shell; точная причина и необходимость не доказаны.','Windows-controlled; точный срок не установлен.','Unconfirmed without native stacks; not declared necessary.')}
        if($path -match '\\Notifications\\Data\\') {return @('WindowsNotificationUnconfirmed','Состояние уведомлений Windows; точная причина и необходимость не доказаны.','Windows-controlled; точный срок не установлен.','Unconfirmed without native stacks; not declared necessary.')}
        return @('UnconfirmedRegistry','Назначение записи не установлено.','Не установлен.','Unconfirmed; do not classify as necessary.')
    }
    if($path -match '\\\.dotnet\\TelemetryStorageService\\') {return @('HarnessDotNetTelemetry','Накладные расходы .NET SDK/testhost стенда; не штатный запуск автономного EXE.','Файлы .trn обслуживает SDK; наблюдаемый остаток указан отдельно.','Known process/path and functional harness context.')}
    if($path -match '\\Windows\\PowerShell\\StartupProfileData-NonInteractive$') {return @('WindowsPowerShellOptimization','Кеш оптимизации запуска Windows PowerShell.','Windows-controlled; кеш пересоздаётся при запуске.','Microsoft startup-performance documentation and observed helper PID.')}
    if($path -match '\\__PSScriptPolicyTest_[^\\]+$') {return @('WindowsPowerShellPolicyProbe','Пробный script/module для определения политик PowerShell/AppLocker.','Обычно удаляется после проверки; занятый файл может остаться. TEMP здесь перенаправлен стендом, не продуктом.','Microsoft PowerShell policy implementation; observed helper PID.')}
    if($path -match '\\Steam\\logs\\gameoverlay_renderer(?:\.previous)?\.txt$') {return @('SteamOverlayDiagnostic','Журнал overlay Steam и его предыдущая ротация; GUI загружает GameOverlayRenderer64.dll.','Steam-controlled rotation; точный предел не установлен.','Inferred strongly from module/path/timing; no native stack.')}
    if($path -match '\\\.net\\LANMinecraft\\') {return @('CurrentNativeBundle','Распаковка native DLL автономного WPF EXE и обслуживание неиспользуемых старых версий.','Текущая версия сохраняется для reuse; старые неиспользуемые версии очищаются.','Known deployment format/source and cold/warm trace.')}
    if($path -match '\\Launcher\\Steam(?:\\|$)') {return @('PortableSteamNative','Hash-verified распаковка встроенной steam_api64.dll для Steamworks.NET.','Постоянный SDK cache установки; .tmp удаляется после публикации. Срок старых SDK-версий не установлен.','SteamNativeLibraryService source; warm launch reuses the verified DLL.')}
    if($path -match '\\Launcher\\Shared\\runtime\\' -and $Row.Operation -eq 'WriteFile' -and $Row.Detail -match 'Paging I/O') {return @('FilesystemPagingIoDuringSweep','Физический сброс кеша файловой системы во время sweep runtime; наблюдались directory paths bin/legal с Paging I/O.','Состояние файловой системы до удаления runtime-каталога, не отдельный временный файл приложения.','Paging I/O flags observed; directory/cache-flush attribution inferred without native stack.')}
    if($path -match '\\Launcher\\Shared\\(?:assets|runtime)(?:\\|$)' -and $Row.Operation -match '^Set(?:Disposition|Basic)Information') {return @('PortableDependencySweep','Удаление неиспользуемых shared assets/runtime по объединению файлов .portable-runtime.json всех сборок.','Ненужные зависимости удаляются при sweep; нужные остаются.','SharedRuntimeStore.Sweep source, constrained to assets/runtime.')}
    if($path -match '\\Personal\\\.?pack-(?:hashes|weights)\.json(?:\.[^\\]+\.tmp)?$') {return @('PortablePackMetadataCache','Кеш хешей/метаданных пакетов и веса вложенных модов для оценки памяти.','До изменения/удаления пакета или cache; атомарная .tmp исчезает после rename.','PackHashService/PackWeightCache source; unchanged warm cache is not rewritten.')}
    if($path -match '\\Minecraft\\(?:Worlds|Personal\\(?:BugReports|Instances|ProfileTransactions))$') {return @('PortableServiceDirectory','Каталог данных миров, отчётов, экземпляров или транзакций профиля, подготовленный AppPaths.Ensure.','Постоянный каталог установки, не временный файл.','Known startup directory initialization source.')}
    if($path -match '\\audit-(?:restarted|host)[^\\]*' -or $path -match '\\(?:TestResults|results)\\' -or $path -match '\.trx$') {return @('HarnessEvidence','Маркеры дочерних процессов и результаты тестов.','Artifacts до ручной очистки владельцем проверки.','Known audit-only path.')}
    if($path -match '\\Personal\\logs(?:-[^\\]+)?\.log$') {return @('PortableLauncherDiagnostic','Текущий журнал лаунчера/его архив.','Текущий1MiB, архивы16MiB;7дней.','Known logger policy/source.')}
    if($path -match '\\Personal\\\.(?:settings\.json|window-placement\.json|skin-profiles\.properties)\.[^\\]+\.tmp$') {return @('AtomicUserConfiguration','Атомарная стадия реального изменения настроек/размещения/профиля скина.','Исчезает при rename/завершении; восстановление при следующем запуске.','Known AtomicFile source; RenameTarget identifies committed data.')}
    if($path -match '\\Personal\\(?:settings\.json|window-placement\.json|skin-profiles\.properties)$') {return @('PortableUserConfiguration','Настройки пользователя, геометрия окна или соответствие скинов профилю.','До следующего изменения/удаления пользователем.','Known product schema and source.')}
    if($path -match '\\Personal\\Updates(?:\\|$)') {return @('PortableUpdateTransaction','Готовое обновление, атомарные стадии, installer script/log и журнал восстановления/отката.','До завершения/безопасной startup cleanup; неразрешённые active/failed recovery journals пока нужны.','Known updater source and journal scenarios; individual existence is recorded.')}
    if($path -match '\\(?:update-install|update-restart|update-replacement-failure|update-rollback-failure)\\LANMinecraft\.exe$') {return @('UpdateExecutableFixture','Исполняемый тестовый target настоящего алгоритма замены/отката.','Artifacts до ручной очистки; штатный EXE сохраняется как установленная версия.','Known production updater exercised against disposable host target.')}
    if([int]$Row.RootProcessId -notin $guiIds) {return @('FunctionalScenarioData','Данные/fixture функционального стенда и вызываемого production сервиса. Индивидуальная необходимость штатного файла из этого пути не выводится.','Artifacts до ручной очистки владельцем; штатная политика для fixture не предполагается.','Harness context known; per-file production necessity unconfirmed.')}
    return @('UnconfirmedFile','Назначение файла в дереве GUI не установлено.','Не установлен.','Unconfirmed; do not classify as necessary.')
}
$classified=@($events|Group-Object ProcessName,ProcessId,RootProcessId,Operation,Path,RenameTarget|ForEach-Object {
    $first=$_.Group[0];$description=Describe $first
    $lifetimes=@($summary.ProcessTree|Where-Object {[int]$_.ChildId -eq [int]$first.ProcessId})
    $life=if($lifetimes.Count -eq 1){$lifetimes[0]}else{$null}
    [pscustomobject]@{SourceSession=[IO.Path]::GetFileName($directory);Phase=$summary.Phase
        ScenarioContext=$(if([int]$first.RootProcessId -in $guiIds){'GUI; see root PID/scenario markers'}else{'Functional harness and production services'})
        ProcessName=$first.ProcessName;ProcessId=$first.ProcessId;RootProcessId=$first.RootProcessId
        ParentProcessId=$(if($life){$life.ParentId}else{$null});ProcessStartLocal=$(if($life){$life.StartTime}else{'unknown/multiple'})
        ProcessExitLocal=$(if($life){$life.ExitTime}else{$null});FirstEventLocal=$first.Time;LastEventLocal=$_.Group[-1].Time
        Operation=$first.Operation;Path=$first.Path;RenameTarget=$first.RenameTarget;EventCount=$_.Count;Scope=$first.Scope
        Category=$description[0];Purpose=$description[1];Retention=$description[2];Confidence=$description[3]
        ExistsAtClassification=$(if($first.Operation -notlike 'Reg*'){Test-Path -LiteralPath $first.Path}else{$null})}
}|Sort-Object ProcessName,{[int]$_.ProcessId},Category,Path,Operation)
$output=Join-Path $directory 'classified-remaining-writes.csv'
$classified|Export-Csv -LiteralPath $output -NoTypeInformation -Encoding UTF8
$manifest=[ordered]@{GeneratedUtc=[DateTimeOffset]::UtcNow.ToString('o');SourceSession=[IO.Path]::GetFileName($directory)
    SourceOriginalCsvVerified=[bool]$summary.OriginalCsvSha256Verified;SourceFileMutationsSha256=(Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
    SourceRegistryMutationsSha256=(Get-FileHash -LiteralPath $regPath -Algorithm SHA256).Hash
    OutputSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;Rows=$classified.Count;EventCount=$events.Count
    Categories=@($classified|Group-Object Category|ForEach-Object {[pscustomobject]@{Category=$_.Name;Rows=$_.Count;Events=($_.Group.EventCount|Measure-Object -Sum).Sum}})
    Privacy='Contains local paths and process IDs, no environment/command lines/ProcessTree.Detail.'}
[IO.File]::WriteAllText((Join-Path $directory 'classified-remaining-writes-summary.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{OutputPath=$output;Rows=$classified.Count;EventCount=$events.Count
    UncategorizedGuiFileRows=@($classified|Where-Object Category -eq 'UnconfirmedFile').Count
    UncategorizedRegistryRows=@($classified|Where-Object Category -eq 'UnconfirmedRegistry').Count
    UnconfirmedWindowsCauseEvents=(@($classified|Where-Object Category -match 'Windows.*Unconfirmed').EventCount|Measure-Object -Sum).Sum
    FunctionalRowsWithUnconfirmedProductionNecessity=@($classified|Where-Object Category -eq 'FunctionalScenarioData').Count}
