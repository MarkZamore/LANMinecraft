# Подготовка официальной сборки CurseForge

Инструмент читает конкретный клиентский экспорт по `projectId` и `fileId` и готовит
отдельную папку для локальной приёмки. Перед запуском проверьте общий бюджет
хранилища и выберите новый каталог внутри своего `artifacts/agent-runs/<task-id>`
с `RUN.json`.

```powershell
python -B tools/PackData/CurseForge/prepare.py --project-id 318032 --file-id 5871978 --output $audit --client-only
python -B tools/PackData/CurseForge/prepare.py --project-id 318032 --file-id 5871978 --output $stage --client-archive $authorZip --expect $receipt --java-major 8 --placements LocalSources/Antimatter-Chemistry/tools/curseforge-placements.json
```

`--client-only` сохраняет `intake.json` с SHA256 архива и файлов авторских
переопределений, Minecraft, загрузчиком и списком зависимостей. При указании
`--client-archive` исходный ZIP читается без копирования. Если у официального
источника есть опубликованный хеш, инструмент проверяет его вместе с длиной.
Для последующих прогонов укажите прежний `intake.json` через `--expect`.
Квитанция `client-only` закрепляет входной архив. Квитанция `complete` закрепляет
также байты зависимостей и дескриптор. При восстановлении по полной квитанции
сохраните выбранную версию Java.

Полный режим проверяет и размещает все объявленные файлы в изолированном
`game/`, включая запись статуса `required` в квитанции. Для JAR используется
`mods/`. Для иных файлов укажите подтверждённый каталог каждой пары в
`--placements`. Этот параметр принимает JSON-массив объектов с
`projectId`, `fileId` и `directory`. При восстановлении берутся проверенные пути
из полной квитанции.

Когда та же зависимость уже присутствует по точному пути в авторском
архиве, подготовка сверяет оба источника по размеру и SHA256.
При совпадении обе записи остаются в квитанции, а в игре лежит один
файл. Разные байты останавливают подготовку до пересмотра источников.

Если источник возвращает `{"data":null}` для обязательной зависимости,
сборка может закрепить этот файл в `tools/curseforge-fallbacks.json`.
Подготовка через `--pack-source` сверяет project ID и file ID с авторским
составом и принимает только прямой официальный CDN-адрес с точным именем,
размером и SHA256. Обычные метаданные имеют приоритет. Сетевые ошибки
останавливают подготовку. В исходниках Levitated задокументировано точное
сравнение файла Advanced Rocketry с авторским серверным архивом.

Для сборок с `tools/overlay.json` добавьте `--pack-source LocalSources/<сборка>`
к полной подготовке с явным `--java-major`. Подготовка проверяет SHA256
авторского архива и платформу, переносит проверенный `portable-pack.json` со
свойством `preserveWorldSettingsOnShare` и применяет только указанные в рецепте
замены. Каждая замена требует точные входные и выходные байты. Результат и
владение изменёнными конфигами фиксируются в секции `overlay` квитанции.
Исходный lock авторских файлов сохраняется отдельно.

Для сетевого журнала рецепт закрепляет `additions` с путями, размерами и
SHA256 `config/log4j2.xml` и `launcher/jvm-args.txt`. Эти файлы хранятся в
репозитории сборки, а `launcher/pack-owned.txt` явно включает конфигурацию
журнала. Если тот же путь появится в новом авторском архиве, подготовка
потребует пересмотра вместо автоматической замены.

## Подготовка поставки и обновление автора

Для всех 12 сборок с политикой открытия мира используйте
`release_overlay.py stage`. Он берёт точные project ID, file ID, Java и
расположение дополнительных файлов из исходников сборки. Подготовка
всегда применяет её `tools/overlay.json`. Для воспроизведения выпуска
используйте полный проверенный `tools/upstream-lock.json`.

```powershell
python -B tools/PackData/CurseForge/release_overlay.py stage --pack-source $pack --output $stage --client-archive $authorZip --locked
python -B tools/PackData/CurseForge/release_overlay.py verify --pack-source $pack --stage $stage --manifest $manifest
```

При повторной подготовке внутри одного стенда можно добавить
`--verified-dependencies $testedGame` к `stage --locked --client-archive $authorZip`.
Инструмент переиспользует только зависимости из полного lock после сверки
размера и SHA256. Авторские overrides извлекаются из проверенного ZIP.

Из проверенной подготовки создайте локальные активы выпуска. Используйте
утверждённый `minecraft-N` и полный SHA коммита источника, в котором сохранены
оба lock, рецепт, дескриптор, `library/content.json`, `library/mod-pages.json`,
`library/loaded-mods.json` и `library/preview.jpg`. Для изменённых авторских
файлов в том же коммите закрепите `launcher/pack-owned.txt`.

```powershell
python -B tools/PackData/CurseForge/stage_release.py --pack-source $pack --stage $stage --output $release --revision $revision --id $id
```

Инструмент проверяет байты исходных документов против Git и собирает
игровые активы из точной квитанции. Он записывает архив карточки отдельно
и создаёт манифест последним. Дата манифеста берётся из коммита. Укажите
`--include-optional` с точным путём каждой выбранной необязательной
зависимости. Снимок загруженных модов должен соответствовать выбранному
составу JAR. На Forge 1.12 и старше каждая фактически загруженная запись
закрепляет путь к своему JAR в `source`. Инструмент работает локально.

Чистый подготовленный стенд можно повторно использовать при добавлении
только проверенной политики журналов. Укажите полный Git SHA предыдущего
рецепта, совпадающего с квитанцией, и полного нового рецепта.

```powershell
python -B tools/PackData/CurseForge/bind_stage_logging.py --pack-source $pack --stage $stage --previous-revision $oldRevision --revision $revision
```

Команда заново проверяет все авторские файлы, обе версии исходных lock и
старый слой. Она добавляет два точных файла журналирования, обновляет
ownership и записывает квитанцию последней. Если проверка новой поставки
завершается ошибкой, прежние файлы и квитанция восстанавливаются. Файлы,
созданные игрой после подготовки, приводят к отказу до записи.

После публикации `Verify-PublicPack.ps1` сверяет публичный манифест с
одобренными байтами, скачивает библиотеку и выполняет чистую установку через
`PortablePackSyncService`. Результат содержит ревизию, SHA256 манифеста и
библиотеки, а также число проверенных файлов. Для запуска игры и холодной
проверки публичного EXE нужны отдельные сценарии.

Для первого кандидата снимите `--locked`, чтобы получить полный список
официальных зависимостей и их SHA256 в `intake.json`. После проверки
закрепите эту квитанцию как `tools/upstream-lock.json` в исходниках сборки.
Перед загрузкой активов выполняйте `verify` после обычной проверки полного
манифеста и архивов. Проверка читает из чистой подготовки каждый авторский
файл из overrides и все закреплённые зависимости, сверяет размеры и SHA256.
Для правок из рецепта действуют точные выходные хеши. Исключаемый e4mc должен
отсутствовать. В манифесте обязательны авторские overrides, обязательные
зависимости и управляемые файлы слоя, включая дескриптор и ownership.
Необязательные авторские зависимости допускаются с исходными размерами и
SHA256. Если один файл указан и в overrides, и как зависимость, единый путь
допустим при точном совпадении размеров и SHA256 обоих источников.
Дополнительные файлы из игрового прогона, включая тестовые миры и
личные настройки, отклоняются. Для запуска QA используйте отдельный игровой
экземпляр. Манифест версии 2 объявляет `preserve-steam-world-settings-v1`
в `requiredLauncherFeatures`.

Для авторского обновления сначала получите свежий `intake.json` через
`prepare.py --client-only` с новым официальным file ID в отдельной папке.
Сравните новый авторский состав и подготовьте перенос слоя.

```powershell
python -B tools/PackData/CurseForge/release_overlay.py review-update --pack-source $pack --candidate-lock $newIntake
```

Команда печатает предлагаемый рецепт с новым SHA256 архива. Если Minecraft,
загрузчик, авторский проект и изменяемые слоем входы совпадают, остальные
изменения автора не требуют переписывать слой. Если обновился или исчез
изменяемый файл либо закреплённая исключаемая зависимость, команда
останавливается для пересмотра. Сохраните прежние исходники и locks,
примените проверенные входы и рецепт в отдельном кандидате, затем заново
снимите полный lock и нативные доказательства перед публикацией.

Desolate Planet сохраняет оригинальный e4mc `849519/8225255` в авторском
lock. Подготовка проверяет точный JAR по размеру и SHA256 и исключает его
из игровых файлов. Лаунчер блокирует совместный запуск e4steam и e4mc.
Политика публикации проверяет отсутствие этого JAR в игре и
манифесте. Авторский `config/e4mc/e4mc.toml` остаётся управляемым с
`hostEnabled = false`.

## Официальные страницы модов

`mod_page_provenance.py` сверяет фактические JAR с полным авторским lock
по размеру и SHA256. Для однозначного верхнеуровневого mod ID он связывает
JAR с точными CurseForge project ID и file ID из lock. Если передана
сохранённая официальная запись проекта, инструмент берёт адрес из
`links.websiteUrl` и проверяет его вместе с ID и slug проекта.
Вызов работает офлайн. [Документация CurseForge Get Mods](https://docs.curseforge.com/rest-api/#get-mods)

```powershell
python -B tools/PackData/CurseForge/mod_page_provenance.py --pack-source $pack --game (Join-Path $stage game) --project-records $projectRecords --inventory $loadedMods
```

Параметры `--project-records` и `--inventory` можно опустить для списка
кандидатов на проверку. Отчёт оставляет вложенные, неоднозначные и
встроенные JAR для проверки источника. Для Forge 1.12 и старше
`mcmod.info` даёт только объявленные ID. Кандидат для карточки требует
совпадающей строки нативного списка с полем `source`, привязанного к
этому lock или точным SHA256 JAR. Инструмент печатает отчёт в stdout.
Проверенные страницы закрепляются в `library/mod-pages.json` исходников.

Для FTB Inferno публичный API FTB позволяет связать проверенный авторский
состав с официальными страницами CurseForge. Утилита сверяет пары project ID
и file ID, размеры и SHA256 с выпуском FTB, затем получает сайт каждого
нужного проекта по его ID. Отчёт подходит для `--project-records` и может
использоваться через `--cache` при проверке другой сборки. Ссылки назначаются
только после сверки фактически загруженных модов.

```powershell
python -B tools/PackData/CurseForge/ftb_project_pages.py --pack-source LocalSources/FTB-Inferno --ftb-release 99 100318 --output $projectRecords
```

[Публичный API Modpacks.ch](https://modpacks.ch/api) · [Выпуск FTB Inferno 1.12.0](https://api.feed-the-beast.com/v1/modpacks/public/modpack/99/100318)

Уже установленный экземпляр остаётся отдельным. Авторские карты и конфиги
сохраняются в новой папке для приёмки. Права на публикацию, итоговый комплект
файлов и работа игры проверяются отдельно.

Для первого запуска авторского состава используйте PowerShell 7 и DLL выбранной
версии лаунчера. `$installation` указывает на отдельную папку внутри `$run`.
Сценарий использует общую подготовку runtime и команду запуска лаунчера, запускает
игру с локальным тестовым профилем и сохраняет ограниченный вывод в `evidence`.
Для работы с меню и миром используйте `-Interactive`.
Этот прогон проверяет авторский клиент. Приёмка Steam и публичной установки
требует отдельных прогонов.

```powershell
./tools/PackData/CurseForge/Start-LocalQa.ps1 -RunDirectory $run `
    -InstallationRoot $installation -GameDirectory (Join-Path $stage game) `
    -LauncherAssembly $launcherDll -PackName Antimatter-Chemistry -Label author-client
```

### Нативная проверка Steam для Forge 1.18.2, 1.19.2 и 1.20.1

На точных клиентах Forge 40.2.34, 43.2.10 и 47.4.13 сценарий использует методы
самой игры. Он читает фактические ID модов и имена их JAR, создаёт тестовый
мир Survival с отключёнными командами и вызывает обработчик видимой кнопки
«Открыть для сети» в штатном меню. Затем проверяет меню, действия в чате,
права хоста и тестового профиля гостя. Адрес Steam в отчёт не записывается.
Подключение второго аккаунта остаётся `NOT RUN`. Ввод с клавиатуры и мыши
и снимки окна для сценария не требуются.

Используйте тот же принадлежащий задаче стенд. `$game` должен быть новым
независимым экземпляром внутри `$installation/Minecraft/Personal/Instances`,
без зарезервированного мира `LanMcSteamQa118`, `LanMcSteamQa119` или
`LanMcSteamQa120` соответственно. `$installation` равен `$run/runtime-qa`.

```powershell
$agentDir = Join-Path $run 'forge118-native-agent'
$classes = New-Item -ItemType Directory -Path (Join-Path $agentDir 'classes') -Force
& "$env:JAVA_HOME/bin/javac.exe" --release 17 -encoding UTF-8 -d $classes.FullName tools/PackData/CurseForge/Forge118SteamQaAgent.java
if ($LASTEXITCODE -ne 0) { throw 'Native QA agent compilation failed.' }
$manifest = Join-Path $agentDir 'MANIFEST.MF'
"Manifest-Version: 1.0`nPremain-Class: lanminecraft.qa.Forge118SteamQaAgent`n" | Set-Content -LiteralPath $manifest -Encoding utf8
$agent = Join-Path $agentDir 'forge-modern-steam-qa-agent.jar'
& "$env:JAVA_HOME/bin/jar.exe" --create --file $agent --manifest $manifest --date 1980-01-01T00:00:02Z -C $classes.FullName lanminecraft
if ($LASTEXITCODE -ne 0) { throw 'Native QA agent packaging failed.' }
./tools/PackData/CurseForge/Start-LocalQa.ps1 -RunDirectory $run `
    -InstallationRoot $installation -GameDirectory $game -LauncherAssembly $launcherDll `
    -PackName FTB-Inferno-Policy -Label ftb-native-steam-policy -WithSteam -NativeQaAgent $agent -MaximumSeconds 720
```

Сценарий записывает `native-steam-qa.jsonl` рядом с `launch.json` и завершает
свой клиент через штатный игровой метод. Статус публикации и статус получения
адреса проверяются отдельно. Для повторного прогона задайте новую метку и
`-NativeQaWorldSuffix Retry1`, чтобы предыдущий тестовый мир остался доступен
для проверки результата. Чтобы проверить повторное открытие в новом процессе,
передайте суффикс ранее созданного тестового мира и SHA256 файла `level.dat`,
вычисленный после полного выхода предыдущего клиента, через
`-NativeQaReopenLevelSha256`. Сценарий сверяет эти байты
перед загрузкой и сохраняет мир методом сервера перед штатным завершением.

### Нативная проверка Steam для Forge 1.12.2

`Forge112SteamQaAgent.java` проверяет штатное событие кнопки меню, создание
и сохранение тестового мира, права хоста и тестового профиля гостя.
Нужны точный JAR e4steam для Java 8, соответствующий индекс и экземпляр
внутри `$installation/Minecraft/Personal/Instances`. Поместите JAR в
`$game/mods` для локальной проверки до публикации. Лаунчер сам сверит байты.
Главный класс проверочного агента должен оставаться в именованном пакете,
чтобы не пересекаться с подписанными классами Minecraft.
Проверочный мод передаёт вызов создания мира через штатный клиентский тик
Forge, вне блокировки очереди задач Minecraft. В поставку сборки он не входит.

```powershell
$agentDir = Join-Path $run 'forge112-native-agent'
$classes = New-Item -ItemType Directory -Path (Join-Path $agentDir 'classes') -Force
& "$java8/bin/javac.exe" -encoding UTF-8 -source 8 -target 8 -d $classes.FullName tools/PackData/CurseForge/Forge112SteamQaAgent.java
if ($LASTEXITCODE -ne 0) { throw 'Native Java 8 QA agent compilation failed.' }
$manifest = Join-Path $agentDir 'MANIFEST.MF'
"Manifest-Version: 1.0`nPremain-Class: lanminecraft.qa.Forge112SteamQaAgent`n" | Set-Content -LiteralPath $manifest -Encoding ascii
$agent = Join-Path $agentDir 'forge112-steam-qa-agent.jar'
& "$java8/bin/jar.exe" cfm $agent $manifest -C $classes.FullName lanminecraft
if ($LASTEXITCODE -ne 0) { throw 'Native Java 8 QA agent packaging failed.' }
$bridgeClasses = New-Item -ItemType Directory -Path (Join-Path $agentDir 'bridge-classes') -Force
& "$java8/bin/javac.exe" -encoding UTF-8 -source 8 -target 8 -cp $forge112 -d $bridgeClasses.FullName tools/PackData/CurseForge/Forge112SteamQaTickBridge.java
if ($LASTEXITCODE -ne 0) { throw 'Native Java 8 QA bridge compilation failed.' }
$bridge = Join-Path $game 'mods/lanminecraft-qa-tick-bridge-1.12.2.jar'
& "$java8/bin/jar.exe" cf $bridge -C $bridgeClasses.FullName lanminecraft
if ($LASTEXITCODE -ne 0) { throw 'Native Java 8 QA bridge packaging failed.' }
./tools/PackData/CurseForge/Start-LocalQa.ps1 -RunDirectory $run `
    -InstallationRoot $installation -GameDirectory $game -LauncherAssembly $launcherDll `
    -PackName Antimatter-Chemistry-Policy -Label antimatter-java8-steam-native -WithSteam `
    -SteamComponentIndex $steamComponentIndex -NativeQaAgent $agent -MaximumSeconds 360
```

Результат записывается в `native-steam-qa-112.jsonl`. Для повторной проверки
используйте новую метку и `-NativeQaWorldSuffix`. Реальное подключение
второго аккаунта остаётся `NOT RUN`.

Для All in One на Forge 36.2.34 используйте `Forge165SteamQaAgent.java` и
`Premain-Class: lanminecraft.qa.Forge165SteamQaAgent` с отдельными именами
каталога, JAR и метки. Сценарий вызывает штатные кнопки игры через Java,
проверяет авторский генератор Skyblock Builder и сохраняет результаты
в `native-steam-qa-165.jsonl`. Проверка гостя использует отдельный тестовый
профиль на действующем сервере мира.

```powershell
python -B -m unittest discover -s tools/PackData/CurseForge
```
