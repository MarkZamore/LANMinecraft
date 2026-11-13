# Контрольная точка выпуска 12 сборок

## Продолжение 14 ноября 2026 года

- Реестр `minecraft-12`–`minecraft-23` закреплён коммитом `94ab5aef061e968ed6b66114fcbd88caf648ce89`. Все 12 репозиториев исходников доступны, включая [Fear Nightfall](https://github.com/MarkZamore/Fear-Nightfall-Remains-of-Chaos/tree/8e76573b79e1f29c311a24f1c097c18f1ba8de3b).
- Публичный EXE 494 скачан целиком и сверён по SHA256 из `update.json`. Из него извлечена проверенная сборка `LANMinecraft.dll` с SHA256 `372c5af3f15981aac818ea231b699179b11568f8e82043fc8eb0908a43a28b6c` для проверки публичной установки и нативного запуска.
- FTB Inferno на `be001bbd365d18d2badcf2439cdd9f80e56d9f76` выпущена и добавлена как `minecraft-12` в коммите каталога `17fe8b1398d6a89d5a7bebd2bc111b18f4eb51f9`. Все 171 игровой архив скачан и проверен, сверены 4 642 установленных файла. При запуске, после повторного открытия мира и после публикации через Steam заголовок был ровно `FTB Inferno`. Нативный Steam-хост, меню, запрет команд и чат прошли проверку. [Игровой выпуск](https://github.com/MarkZamore/FTB-Inferno/releases/tag/pack-latest).
- All in One на `b83744c9969b02c27d00e386f36911d0e63af6ed` выпущена и добавлена как `minecraft-14` в `18c20685af34eeb13749589cf5f8c38a09049e75`. Проверены 99 публичных игровых архивов и 692 установленных файла. Заголовок `All in One [Modded One Block]` прошёл проверку в тех же трёх состояниях, повторное открытие мира, нативный Steam, меню и чат подтверждены. [Игровой выпуск](https://github.com/MarkZamore/All-in-One/releases/tag/pack-latest).
- Код каталога из публичного 494 подтвердил обе новые карточки, все прежние записи, точные архивы и ссылки на моды. Холодный запуск отдельного EXE оставлен до освобождения текущего сеанса. Личный лаунчер и игровой процесс не останавливались.
- Выпущен общий лаунчер 497, ревизия `e45c6f5830c8aa4725b4190cf05462097d94b753`. Исправлены ранний конфликт Java 8 с менеджером безопасности Forge и запуск Java 9+ при превышении лимита Windows. Точный Release прошёл 711 случаев по 436 методам. Публичный EXE скачан целиком и сверён с SHA256 `1169b958dca5633ecabc43b32cfd5091ce4f9de111fa767c5c58f4ec765a6b7c`. [Выпуск лаунчера](https://github.com/MarkZamore/LANMinecraft/releases/tag/latest).
- UniversIO `minecraft-18` на `0d1b6b5c3e211f38ed17473eb516dc8c329c37f9` опубликована и добавлена в `a9ddd57bf8ed4d190e59f3022622aa4025374462`. Сверены 124 публичных игровых архива и 7 321 установленный файл. Нативный тест подтвердил заголовок `UniversIO`, повторное открытие мира, Steam и данные хоста. Штатный файл аргументов сократил команду с 35 918 до 1 038 символов и был удалён после выхода. [Игровой выпуск](https://github.com/MarkZamore/UniversIO/releases/tag/pack-latest).
- Antimatter Chemistry `minecraft-17` на `640b9840b1fe1638f46407aba697b48920596b8f` опубликована и добавлена в `9560ccbd718bb0a54130ecee72665ad76abe447b`. Сверены 121 публичный игровой архив и 898 установленных файлов. В нативном Java 8 проверены заголовок, повторное открытие мира, Steam и отказ в командах хосту и тестовому гостю. [Игровой выпуск](https://github.com/MarkZamore/Antimatter-Chemistry/releases/tag/pack-latest).
- Liminal Industries `minecraft-15` на `9a1ed9dffab2458324e6dcec1b9a011986b1040e` опубликована и добавлена в `52de9d8b64b4e2a2aa5569a0797b7ee923e5439e`. Сверены 121 публичный игровой архив и 4 341 установленный файл. Заголовок и Steam проверены при первом запуске и повторном открытии мира. Forge штатно выбрал `musketmod` 1.5.4, оба авторских JAR сохранены. [Игровой выпуск](https://github.com/MarkZamore/Liminal-Industries/releases/tag/pack-latest).
- Desolate Planet `minecraft-19` на `87b3a5f64e83de69d3a2ae1199365015269a78d6` опубликована и добавлена в `98620fb8a1442448b82978f5da038e8cbb78614e`. Сверены 143 публичных игровых архива, 2 912 установленных файлов и карточка. В обоих нативных запусках сохранены Hardcore, запрет команд и заголовок. Точные 32 ошибки таблиц добычи и ошибка корейского перевода HQM присутствуют в неизменённых авторских файлах и задокументированы в выпуске. [Игровой выпуск](https://github.com/MarkZamore/Desolate-Planet/releases/tag/pack-latest).
- Clayblock `minecraft-22` на `1df15b178afdf78eba954870d6ffc785dc6e47b6` опубликована и добавлена в `12171aa410236496d65ddab7b8f4c1ce76df2ea8`. Сверены 88 публичных игровых архивов и 6 956 установленных файлов. При повторном запуске NeoForge получал два заголовка версии из настроек, сгенерированных KubeJS. Рецепт сборки убирает заголовок только из входного шаблона. После того как KubeJS добавляет свой заголовок, результат побайтно совпадает с исходным авторским шаблоном. Два запуска публичного 497 подтвердили сохранность настроек, мира, биомов Clayland и Terracottaland, заголовка и Steam. Публичный каталог сохранил все 17 прежних записей и добавил карточку с 113 подтверждёнными ссылками. [Игровой выпуск](https://github.com/MarkZamore/Clayblock/releases/tag/pack-latest).
- Levitated `minecraft-20` на `59ee118c9800e8d76010805f31c52ec589936ceb` опубликована и добавлена в `104cdba38ac322fb97e841695ad80566b44c514b`. Сверены 122 публичных игровых архива и 947 установленных файлов. Конфликт подписей Mixin воспроизводился при совместной загрузке авторских JAR и e4steam. Сборка загружает подписанный PlusTweaks первым через свой вспомогательный JAR Java 8. Байты и подписи авторских JAR сохранены. Публичный 497 подтвердил штатное начало в Крае, повторное открытие того же мира, заголовок, запрет команд, Steam и штатный выход. [Игровой выпуск](https://github.com/MarkZamore/Levitated/releases/tag/pack-latest).
- Холодный старт точного публичного EXE 497 из чистого переносимого каталога подтвердил семь первых опубликованных карточек и 18 записей каталога. Проверены ссылки, превью, загруженные манифесты и их SHA256. Личный экземпляр не участвовал.
- В Fear Nightfall точный Luna исключён как конкурирующее реле. Авторский lock Luna сохранён. В оформлении FancyMenu изменён только жёстко заданный текст штатной сетевой кнопки. Остальные файлы сверены с исходным lock.
- Reclamation на `bbb53acb6f78746a2fc643012df307cc72999d11` прошла первый нативный запуск. Подтверждены `reclamation:overworld`, Hard, выключенные команды и странствующий торговец, заголовок, Steam и сохранение. Авторский ExtendedAE 1.4.15 содержит таблицу добычи со ссылкой на отсутствующий `expatternprovider:ex_emc_interface`. JAR оставлен без изменений. Повторное открытие и публикация впереди.
- All the Mods 10: To the Sky на `5e01e651b0a0ed4005dcf462dcc48b247e54ee86` прошла первый нативный запуск. Штатный экран уже выбрал `skyblockbuilder:skyblock`. Игра создала мир с авторским генератором и показала тот же заголовок до и после Steam. Проверены 355 авторских mod ID, меню, права, чат и Steam-метаданные. Манифест и архив карточки собраны. Повторное открытие и публикация впереди.

## Сверка названий

У 11 прежних карточек имена прочитаны из опубликованных архивов по SHA256 манифеста. У 12 новых имена сверены в `library/content.json`, пути по каталогу или кандидату. В GitHub пробелы заменены дефисами. Для существующих установок проверены папки `Minecraft/Packs` на этом ПК. Сверка выполнена без переименований.

| Карточка LANMinecraft | Официальное название проекта | Папка установки | Репозиторий GitHub |
| --- | --- | --- | --- |
| All The Mods 10 | [All the Mods 10 - ATM10](https://www.curseforge.com/minecraft/modpacks/all-the-mods-10) | All The Mods 10 | All-The-Mods-10 |
| TerraFirma Rebirth 2 | [TerraFirma Rebirth 2](https://www.curseforge.com/minecraft/modpacks/terrafirma-rebirth) | TerraFirma Rebirth в каталоге, TerraFirma Rebirth 2 на этом ПК | TerraFirma-Rebirth-2 |
| Minecraft Java Edition | [Minecraft: Java Edition](https://www.minecraft.net/en-us/store/minecraft-java-bedrock-edition-pc) | Minecraft Java Edition, прежние пути Minecraft 26.3 и Minecraft 26.2 | Minecraft-Java-Edition |
| Caves and Dungeons | [Caves and Dungeons](https://www.curseforge.com/minecraft/modpacks/caves-and-dungeons) | Caves and Dungeons | Caves-and-Dungeons |
| All The Mods 9 | [All the Mods 9 - ATM9](https://www.curseforge.com/minecraft/modpacks/all-the-mods-9) | All The Mods 9 | All-The-Mods-9 |
| All The Mods 9 Modified | [Вариант LANMinecraft на основе ATM9](https://github.com/MarkZamore/All-The-Mods-9-Modified) | All the Mods 9 Modified | All-The-Mods-9-Modified |
| Awakening Of The Titans | [Attack On Titan - Awakening Of The Titans](https://www.curseforge.com/minecraft/modpacks/attack-on-titan-awakening-of-the-titans) | Awakening Of The Titans | Awakening-Of-The-Titans |
| Better Than Wolves | [Better Than Wolves: CE](https://modrinth.com/mod/btwce), [сайт Better Than Wolves](https://btw-community.github.io/) | Better Than Wolves | Better-Than-Wolves |
| Minecraft Rust Edition | [Вариант LANMinecraft на основе Horizon](https://github.com/MarkZamore/Minecraft-Rust-Edition) | Minecraft Rust Edition | Minecraft-Rust-Edition |
| Minecraft Rust Mods | [Вариант LANMinecraft на основе Horizon](https://github.com/MarkZamore/Minecraft-Rust-Mods) | Minecraft Rust Mods | Minecraft-Rust-Mods |
| Hide and Hunt | [Hide and Hunt](https://modrinth.com/plugin/hide-and-hunt/version/FaWrAGlY) | Hide and Hunt | Hide-and-Hunt |
| FTB Inferno | [FTB Inferno](https://www.curseforge.com/minecraft/modpacks/ftb-inferno) | FTB Inferno | FTB-Inferno |
| All the Mods 10: To the Sky | [All the Mods 10: To the Sky ATM10SKY](https://www.curseforge.com/minecraft/modpacks/all-the-mods-10-sky) | ATM10 To the Sky | ATM10-To-the-Sky |
| All in One [Modded One Block] | [All in One [Modded One Block]](https://www.curseforge.com/minecraft/modpacks/all-in-one-modded-one-block) | All in One | All-in-One |
| Liminal Industries | [Liminal Industries](https://www.curseforge.com/minecraft/modpacks/liminal-industries) | Liminal Industries | Liminal-Industries |
| Beebeeblock - The Skyhive | [Beebeeblock - The Skyhive](https://www.curseforge.com/minecraft/modpacks/beebeeblock-the-skyhive) | Beebeeblock - The Skyhive | Beebeeblock |
| Antimatter Chemistry | [Antimatter Chemistry](https://www.curseforge.com/minecraft/modpacks/antimatter-chemistry) | Antimatter Chemistry | Antimatter-Chemistry |
| UniversIO | [UniversIO](https://www.curseforge.com/minecraft/modpacks/universio) | UniversIO | UniversIO |
| Desolate Planet | [Desolate Planet - A Crash Landing Remake](https://www.curseforge.com/minecraft/modpacks/desolate-planet) | Desolate Planet | Desolate-Planet |
| Levitated | [Levitated](https://www.curseforge.com/minecraft/modpacks/levitated) | Levitated | Levitated |
| Reclamation | [Reclamation - Reclaim the World!](https://www.curseforge.com/minecraft/modpacks/reclamation-reclaim-the-world) | Reclamation | Reclamation |
| Clayblock | [Clayblock](https://www.curseforge.com/minecraft/modpacks/clayblock) | Clayblock | Clayblock |
| Fear Nightfall: Remains of Chaos | [Fear Nightfall: Remains of Chaos](https://www.curseforge.com/minecraft/modpacks/fear-nightfall) | Fear Nightfall - Remains of Chaos | Fear-Nightfall-Remains-of-Chaos |

## Состояние на момент паузы 12 ноября

По запросу пользователя работа поставлена на паузу 12 ноября 2026 года. Игровые релизы и карточки не опубликованы. Номера ниже предварительные. Перед выделением перечитать реестр и ветку `pack-catalog`. Проверки шли без computer-use. Подключение второго Steam-аккаунта везде `NOT RUN`.

## Лаунчер и Steam

- Версия 494 опубликована из `3ec258af1343b25d670d6565e265e6b2c22c8a17`. Локальный `Test-Launcher.ps1 -Profile Release -WithAdapters -Configuration Release -NoRestore` прошёл 708 случаев. Публичный `update.json` и метаданные GitHub совпали по размеру EXE (158 097 408 байт) и SHA256 `bb67baae177477254ca82d7931608fd12127b0d704eee046db8d808913e6dda4`. Полное скачивание и холодный запуск остаются на возобновление. [Выпуск](https://github.com/MarkZamore/LANMinecraft/releases/tag/latest) · [GitHub Actions](https://github.com/MarkZamore/LANMinecraft/actions/runs/43275629874).
- Forge 1.12.2 использует `0.3.2-lanmc.1`, SHA256 `a0e0593ec5bc45ab098b0b30711edc3c4167f9bac4c65cad296d612a6c242f40`. [Компонент](https://github.com/MarkZamore/e4steam/releases/tag/java8-world-sharing-v1).
- Forge 1.16.5 использует `0.3.2-lanmc.3`, SHA256 `0188f8a9d5c22a13efcdae8b8e353b5144cc02407c935b04c3bee7516ce3d185`. Совместимость с обоими таймаутами Connectivity подтверждена в All in One. Create и Moving Elevators сохранены в авторских версиях. Оба активных JAR скачаны с GitHub целиком и сверены. [Компонент](https://github.com/MarkZamore/e4steam/releases/tag/java8-world-sharing-v2).
- Текущий индекс e4steam закреплён коммитом `2406eddb20f57fc44b59cb78a1b3a548a17ae686`. Перед публикацией сборок перечитать `latest/update.json`, сверить версию, ревизию и SHA256 полного публичного EXE.

## Исходники и игровые проверки

У всех 12 сборок закреплены полные авторские locks, слой обновления, журналирование и превью. В лаунчере общая инфраструктура Forge, Java и Steam. Настройки конкретных модов закреплены в репозиториях сборок. При изменении входного файла, от которого зависит правка, подготовка требует пересмотра. Desolate Planet сохраняет точный e4mc в исходном lock и исключает его из Steam-поставки.

| ID | Репозиторий в `LocalSources` | Ревизия на паузе | Состояние |
| --- | --- | --- | --- |
| `minecraft-12` | FTB-Inferno | `be001bbd365d18d2badcf2439cdd9f80e56d9f76` | Нативный Steam и повторное открытие прошли. В черновике 172 проверенных файла. |
| `minecraft-13` | ATM10-To-the-Sky | `e4633bd8d8aecc150ae016181a3aa599795c99f8` | Чистая подготовка сверена. Нативная проверка впереди. |
| `minecraft-14` | All-in-One | `b83744c9969b02c27d00e386f36911d0e63af6ed` | Нативный Steam прошёл. В черновике 100 файлов. Сохранение есть, повторное открытие предстоит. |
| `minecraft-15` | Liminal-Industries | `fe7784d24e825066ff549584b1c6d29c2a72adfd` | Чистая подготовка и тестовый Java-агент готовы. Нативный запуск впереди. |
| `minecraft-16` | Beebeeblock | `2092780b6e3930ee09c1e32a1e21accef7c317d4` | Чистая подготовка сверена. Нативная проверка впереди. |
| `minecraft-17` | Antimatter-Chemistry | `640b9840b1fe1638f46407aba697b48920596b8f` | Нативный Steam прошёл. В черновике 122 проверенных файла. Сохранение есть, повторное открытие предстоит. |
| `minecraft-18` | UniversIO | `0773ff105caafdb6e6788911f5f42a893efb3098` | Нативный Steam прошёл через файл аргументов Java. Сохранение есть, повторное открытие и перенос 183 загруженных ID в карточку предстоят. |
| `minecraft-19` | Desolate-Planet | `84b87c6a364e91b0074da2adc989b0b4ec014c65` | Чистая подготовка сверена. Проверка Steam без e4mc впереди. |
| `minecraft-20` | Levitated | `97f30a39a75749f43b8b62456c4a937b24393ffe` | Изолированный экземпляр Forge 14.23.5.2847 готов. Нативный Steam предстоит. |
| `minecraft-21` | Reclamation | `54c78cce3c26c72f6808c4168f2fe65e3b5fb3ee` | Чистая подготовка сверена. Нативная проверка впереди. |
| `minecraft-22` | Clayblock | `ef48ff1f22dca41a141ca323801fd4f545039a70` | Сверены 100 авторских JAR и официальный источник каждого. Нативный выбор вложенных зависимостей предстоит. |
| `minecraft-23` | Fear-Nightfall-Remains-of-Chaos | `8e76573b79e1f29c311a24f1c097c18f1ba8de3b` | Исходники локально. GitHub дважды вернул ограничение `createRepository`. Чистая подготовка сверена. |

Остальные 11 репозиториев исходников созданы на GitHub. Три черновика `pack-latest` содержат игровые файлы и архив карточки. В каждом отсутствует манифест. [FTB Inferno](https://github.com/MarkZamore/FTB-Inferno/releases/tag/untagged-96e25654f4f747570d55) · [All in One](https://github.com/MarkZamore/All-in-One/releases/tag/untagged-0c856029de4978bab404) · [Antimatter Chemistry](https://github.com/MarkZamore/Antimatter-Chemistry/releases/tag/untagged-13f6d3f62f2c615970fd).

## Возобновление

1. Сверить публичный EXE 494, текущий каталог и черновики. Для всех 12 проверить [заголовок игрового окна](../../ADDING-A-LIBRARY-PACK.md#6-рано-выполнить-native-приёмку) при запуске и до/после открытия мира через Steam. Во всех состояниях требуется только название сборки из карточки. Этот сценарий добавлен к приёмке на паузе и ещё не отмечен `PASS`.
2. Первый выпуск — FTB Inferno. Его локальный манифест имеет SHA256 `88a117a8dfcb4e5b9a4cca78ecc3bf1bfffc8c2615e11a7309eb8b1463c883b3`. После обязательных проверок загрузить манифест последним, открыть выпуск, выполнить `Verify-PublicPack.ps1`, добавить карточку в свежий каталог с проверкой исходного SHA и запустить публичный EXE из чистой папки.
3. Повторно открыть сохранения All in One, Antimatter и UniversIO по SHA256 `level.dat`. Проверить Levitated и Liminal, затем оставшиеся сборки по готовности. Уже проверенные общие платформы используют прежние доказательства. Для каждой сборки проверить поведение на её авторском наборе модов.
4. UniversIO с обычным запуском в длинном пути стенда получила Windows 206. Измерено 35 012 символов аргументов. Путь `C:\LANMinecraft` даёт оценку около 25 537. Для нативной проверки сработал `Start-LocalQaJavaArgFile.ps1` с Java 17 и удалением временного файла при завершении. После паузы воспроизвести порог на обычной установке и решить вопрос общей поддержки длинной командной строки Java 9+. Файл аргументов не используется для Java 8.
5. Перед тяжёлым продолжением выполнить `python tools/workspace_storage.py --check`. Стенд `artifacts/agent-runs/atm10-style-packs-20261112` содержит точные дистрибутивы, квитанции, сохранения и журналы. Для следующего запуска брать существующий проверенный экземпляр и держать один активный игровой процесс.
