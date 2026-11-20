# Как устроен LANMinecraft

Лаунчер собирается как один проект WPF. Папки C# обозначают владельца поведения. Сборки игры и общие поставляемые компоненты разрабатываются в своих репозиториях.

| Область в Program | Точка входа и ответственность | Первые проверки |
| --- | --- | --- |
| Корень и UI | App и MainWindow показывают интерфейс, передают команды и отображают состояние. UI декодирует фон и значки из файлов, проверенных нативным загрузчиком. | WindowCanvasTests, DesignTokensTests |
| Session | LauncherSession собирает сервисы и завершает операции перед их освобождением. | StartupPreparationPriorityTests, LauncherSessionTests |
| Launch | PlayLaunchCoordinator готовит выбранную ревизию и передаёт её хеш и подготовленные данные игровому процессу. PackPreparationDownloadService объединяет загрузки сборки и её внешних файлов. MinecraftProcessService владеет запуском и наблюдением за процессом. | PackPlayFreshManifestTests, PackPreparationDownloadTests, NativeClientLaunchTests |
| Packs/Catalog | PackCatalogService определяет карточки и готовность к запуску. OfflineCatalogSeed открывает локальные архивы карточек. | PackCatalogStagedStartupTests, CatalogSourceMarkerTests |
| Packs/Sources | PackSourceResolver выбирает источник установки по состоянию каталога, локальной записи и прежним идентификаторам сборки. PackCatalogIndex читает опубликованный индекс и встроенный снимок. | CatalogSourceMarkerTests, PackCatalogAuthorityTests, PortablePackSyncServiceTests |
| Packs/Installation | PortablePackSyncService подготавливает и применяет проверенный снимок. Доступ к установленной папке согласуют синхронизация, подготовка запуска, импорт, удаление, миграция и обслуживание. | PortablePackSyncServiceTests, BuildRemovalServiceTests, PackRenameMigrationTests |
| Runtime | Java, загрузчики, общие библиотеки, компоненты и подготовка локального ИИ. PackRuntimeReceipt читает и записывает квитанцию игрового рантайма для запуска, обслуживания и диагностики. | ManagedComponentServiceTests, PackRuntimeStateTests, SharedRuntimeStoreTests |
| Worlds/Storage и Worlds/Transfer | Хранилище владеет сохранениями и профилями. WorldTransferService согласует передачу, WorldTransferStorage устанавливает принятый мир, TransferPacing оценивает ход передачи. Журнал помогает восстановиться после сбоя. | WorldTransferHandshakeTests, WorldTransferLifecycleTests, WorldTransferArchiveTests, TransferPacingTests |
| Steam | Загрузка проверенной DLL Steam, сессия, идентичность, транспорт и маршрутизация. Потребители передают данные через IPeerTransport и зарегистрированные обработчики протоколов. | SteamBootstrapTests, PeerConnectionRouterTests, SteamIdentityServiceTests |
| Infrastructure, Settings, Maintenance, Updates, Diagnostics | Пути, атомарная запись, настройки, уборка, обновление EXE и поддержка. GuardedFileSystem выполняет общие операции с файлами через проверенные дескрипторы Windows. Нативный Bootstrap работает перед WPF. | PortabilityTests, LauncherStorageCleanupTests, FunctionalAuditTests, NativePortableHostTests |

Для нативной сборки подготовка остаётся активной до появления видимого игрового окна в дереве её процессов. Повторное нажатие кнопки во время подготовки отменяет этот запуск. Проверка и загрузка игровых ресурсов выполняются подготовщиком, входящим в поставку сборки.

Время игры хранится в `Minecraft/Personal/time.json`. В объекте JSON каждому имени репозитория сборки из библиотеки соответствует целое число миллисекунд, например `{"Better-Vanilla": 13440000}`. После подтверждения запуска отдельный нативный наблюдатель сохраняет время до завершения игры, включая работу с закрытым лаунчером. Время личных сборок не записывается. Уборщик удаляет прежний файл `time` из корня `Personal`. Файл `time.json` можно редактировать и переносить при закрытой игре и лаунчере.

Текущий журнал лаунчера, его архивы и транскрипты Steam находятся в `Minecraft/Personal/Logs`. Уборщик удаляет журналы старой структуры из корня `Personal` и ограничивает архивы в `Logs` семью днями и общим объёмом 16 МиБ.

После успешной сетевой проверки каталога лаунчер удаляет из `time.json` записи репозиториев, которых в нём больше нет. Наблюдатель не восстанавливает уже удалённую запись, а при использовании офлайн-кеша история сохраняется.

Для адресного прогона подготовьте зависимости по [инструкции тестов](../../Program.Tests/TESTING.md) и передайте имя класса в фильтр dotnet test. Полный обязательный профиль выпуска запускается через Test-Launcher.ps1.

## Карта исходников

После каждого успешного выпуска лаунчера из `main` GitHub Actions строит карту из коммита этого выпуска. В артефакте `graphify-<SHA коммита>` лежат `graph.json` и `source.json` с перечнем исходных деревьев и контрольной суммой. Для чистого checkout загрузите и проверьте карту в PowerShell из корня проекта.

```powershell
gh run download --name "graphify-$(git rev-parse HEAD)" --dir graphify/release
pwsh -NoProfile -File ./graphify/Update-Graphify.ps1 -ReleaseArtifact -Check
graphify query "PlayLaunchCoordinator" --graph graphify/release/graph.json --budget 800
```

Для карты рабочей копии установите `uv tool install graphifyy==0.9.111` и выполните команды ниже. В карту входят текущее содержимое отслеживаемых файлов из `Program`, `Program.Tests`, `tools`, `.github` и новые неигнорируемые файлы из `Program` и `Program.Tests`. Каталоги обеих карт игнорируются Git.

```powershell
pwsh -NoProfile -File ./graphify/Update-Graphify.ps1 -WorkingTree
pwsh -NoProfile -File ./graphify/Update-Graphify.ps1 -WorkingTree -Check
graphify query "PlayLaunchCoordinator" --graph graphify/local/graph.json --budget 800
```

Команда без дополнительных параметров пересобирает отслеживаемый снимок `graphify/graph.json` из `origin/main`. Во всех режимах перепроверяйте найденные вызовы по коду. Если проверка карты не прошла, используйте `rg` по исходникам.

## Исходники сборок и установка

LocalSources содержит отдельные рабочие репозитории пакетов из актуального каталога GitHub, LANMinecraft Components и e4steam. При продолжении работы сверяйте origin и текущую ветку. Источник списка — ветка pack-catalog, а не встроенный снимок библиотеки. Папки сборок названы по каталогу. Редактируйте публикуемую сборку в её рабочей копии. Для экспериментов используйте отдельную личную сборку. Параметры генераторов вроде --packs-root указывают на исходники явно.

LANMinecraft.exe ищет установку рядом с собой. Следующие пути являются частью формата переносимой установки:

| Путь | Данные и владелец |
| --- | --- |
| Minecraft/Packs | Установленные копии. Синхронизация владеет файлами опубликованного манифеста. Состав личной сборки и добавленные пользователем разрешённые архивы принадлежат игроку. |
| Minecraft/Launcher | Закреплённые компоненты, Java, общие рантаймы, каталог и кеши. Уборщик действует по правилам конкретного кеша. |
| Minecraft/Personal | Настройки, экземпляры, журналируемые операции, конфликты и локальная диагностика. Временные загрузки и длительно хранимое состояние разделены. |
| Minecraft/Worlds | Сохранения. Ссылка saves игрового экземпляра указывает только на папку миров соответствующей сборки. |

В Launcher папка Bootstrap содержит приложение и зависимости нативного старта, а Temp и DotNetBundles обслуживает загрузчик. PackRuntimes хранит состояние игровых рантаймов по сборкам. LauncherRuntime — совместимый кеш старого извлечения лаунчера. JavaRuntimes и Shared переиспользуются разными сборками. В Personal папка Temp содержит временные данные игры и загрузок. PackRenames, ProfileTransactions, Transfers и Backups нужны для восстановления после прерывания и хранятся отдельно от Temp.

`Shared/NativeRuntimes/<SHA-256 архива>` содержит общие проверенные игровые файлы, а `Shared/NativePrepared` хранит подготовленные ресурсы по хешам входов. Дескриптор сборки закрепляет архив и инвентарь рантайма. Моды и оформление остаются в `Packs`, состояние запуска остаётся в `PackRuntimes` конкретной сборки. [Подготовка общей поставки](../../tools/NATIVE-RUNTIME-RELEASE.md).

Версия структуры записывается в файле `Minecraft/Launcher/.storage-layout.json`. Значение `schemaVersion` 1 означает, что прежние `Runtimes` и `Runtime` перенесены безопасно или отсутствовали. Пока перенос отложен, сохраняются старые данные и доступ к проверенным локальным копиям. Новая установка и установка с завершённым переносом используют `PackRuntimes` и `LauncherRuntime`. Подробнее см. [версия структуры Minecraft](../../tools/PORTABILITY.md#версия-структуры-minecraft).

Структура установки определяет расположение файлов. Манифесты и квитанции установки по-прежнему определяют, какие файлы поставки можно проверить или восстановить. Миры, профили игроков, настройки и файлы личных сборок сохраняются при переходе.

Для нового пути долговременных данных добавьте запись к правилам владения LauncherStorageLayout. Тест проверяет, что каталоги, которые выдаёт AppPaths, защищены от общей уборки. Прерванные операции воспроизводите на изолированном стенде через журнал, сохраняя миры, конфликтующие файлы и резерв отправителя при неопределённом результате передачи.
