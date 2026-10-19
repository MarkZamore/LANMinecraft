# Правила работы над лаунчером

## Интерфейс

- Дизайн-система — `DESIGN.md`; токены — `Program/App.xaml`, `Application.Resources`, одним плоским списком (без merged dictionaries: тесты грузят разметку loosely и вклеивают ресурсы по имени).
- В разметке нет hex-цветов, `SystemColors` и своих чисел для размеров, отступов и шрифтов — только токены (`Brush.*`, `Font.*`, `Space.*`/`Gap.*`/`Pad.*`, `Size.*`, `Radius.*`). Нужно новое значение — сначала токен и строка в `DESIGN.md`, потом использование.
- Отступы только из шкалы 4 / 8 / 12 / 16 / 24. Один акцент на экране. Прозрачность для выключенных не используем.
- Тесты `DesignTokensTests` держат `App.xaml` и `DESIGN.md` в одном составе и не пускают цвета в разметку.
- Окно свободно меняет размеры, сохраняет системную рамку и Windows Snap. `RootGrid` без `Viewbox`, основные столбцы всегда 2:1 с промежутком 24 DIP; настройки остаются сеткой 2×2. После изменения размеров, отступов или шрифтов прогнать `WindowCanvasTests` и перемерить окно настоящими контролами (net10-консоль `MeasureLayout` в scratchpad); офлайн-рендер PowerShell 5.1 подменяет `CenteredDropDown` и ошибается по высоте.
- Шрифт системный `Segoe UI Variable Text, Segoe UI`; тесты используют те же установленные шрифты без подмены на Montserrat или файловый URI.
- Обработчики в `MainWindow.xaml` — только из списка, который вырезает `WindowCanvasTests.LoadWindow` (`Click|Loaded|Closing|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject.Pasting`); новый тип обработчика — сначала в этот список.
- Имена элементов, `Content` кнопок и позиции строк/колонок пинят тесты `PeerDiagnosticUiAndDiscoveryTests` — менять вместе с ними.
- Системные `MessageBox` вне темы; свои диалоги (`WorldTransferConfirmationDialog`) наследуют стили из `App.xaml`.

## Релизы

- Номер релиза = число коммитов в `main`; перед коммитом добавить `## <N+1>` в `Program/Changelog.md` — одна версия, один абзац, без длинного тире.
- Описание версии — ровно два коротких предложения, не длиннее 240 символов вместе. Это окно «Что нового», а не отчёт: берутся два самых заметных для игрока изменения, остальное остаётся в коммите. Держит `ChangelogTests`.
- Перед коммитом прогнать `.\Program.Tests\Test-Launcher.ps1 -Profile Release` (компактный обязательный профиль с проверкой TRX, без пропущенных случаев). Полный набор необязателен для каждого выпуска: `-Profile Full`; матрица адаптеров: `-Profile Adapters`. Состав профилей и правила запуска — `Program.Tests/TESTING.md`. Для сборки java-адаптера задать `JAVA_HOME=C:\Tools\jdk-25.0.2`: в `PATH` только стубы Oracle (`java`, `javac`), `jar.exe` там нет, и без `JAVA_HOME` сборка падает на `jar.exe was not found`. Без JDK — `-SkipPortableIdentityAdapter`, состав тестов тот же.
- Префлайт по настоящим jar'ам гонится локально: `Program/IdentityAdapters/Common/Verify-IdentityAdapter.ps1`. Он повторяет то, что лаунчер вывел в прошлый запуск сборки, поэтому после правки `IdentityAdapterMappingService` сборку надо один раз запустить, иначе проверятся старые алиасы.
