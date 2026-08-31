# Правила работы над лаунчером

## Интерфейс

- Дизайн-система — `DESIGN.md`; токены — `Program/App.xaml`, `Application.Resources`, одним плоским списком (без merged dictionaries: тесты грузят разметку loosely и вклеивают ресурсы по имени).
- В разметке нет hex-цветов, `SystemColors` и своих чисел для размеров, отступов и шрифтов — только токены (`Brush.*`, `Font.*`, `Space.*`/`Gap.*`/`Pad.*`, `Size.*`, `Radius.*`). Нужно новое значение — сначала токен и строка в `DESIGN.md`, потом использование.
- Отступы только из шкалы 4 / 8 / 12 / 16 / 24. Один акцент на экране. Прозрачность для выключенных не используем.
- Тесты `DesignTokensTests` держат `App.xaml` и `DESIGN.md` в одном составе и не пускают цвета в разметку.
- Полотно окна фиксированное (`RootGrid` в `Viewbox`); после любого изменения размеров, отступов или шрифтов в левом столбце — прогнать `WindowCanvasTests` и перемерить `RootGrid.Height` и `Window Width/Height`. Точный замер и рендер — только настоящими контролами (net10-консоль `MeasureLayout` в scratchpad), офлайн-рендер PowerShell 5.1 подменяет `CenteredDropDown` и ошибается по высоте.
- Шрифт вшит как WPF-ресурс; `pack://application:` в тестах не обслуживается (нет `Application`), поэтому тесты, которым нужны реальные метрики текста (`WindowCanvasTests`), подменяют `Font.Family` на файловый URI папки `Program/Fonts` — те же файлы, те же размеры.
- Обработчики в `MainWindow.xaml` — только из списка, который вырезает `WindowCanvasTests.LoadWindow` (`Click|Loaded|Closing|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject.Pasting`); новый тип обработчика — сначала в этот список.
- Имена элементов, `Content` кнопок и позиции строк/колонок пинят тесты `PeerDiagnosticUiAndDiscoveryTests` — менять вместе с ними.
- Системные `MessageBox` вне темы; свои диалоги (`WorldTransferConfirmationDialog`) наследуют стили из `App.xaml`.

## Релизы

- Номер релиза = число коммитов в `main`; перед коммитом добавить `## <N+1>` в `Program/Changelog.md` — одна версия, один абзац, без длинного тире.
- Описание версии — ровно два коротких предложения, не длиннее 240 символов вместе. Это окно «Что нового», а не отчёт: берутся два самых заметных для игрока изменения, остальное остаётся в коммите. Держит `ChangelogTests`.
- `dotnet test Program.Tests -c Debug` перед коммитом. Для сборки java-адаптера задать `JAVA_HOME=C:\Program Files\Java\jdk-25.0.3`: в `PATH` только стубы Oracle (`java`, `javac`), `jar.exe` там нет, и без `JAVA_HOME` сборка падает на `jar.exe was not found`. Без JDK — `-p:SkipPortableIdentityAdapter=true`, состав тестов тот же.
- Префлайт по настоящим jar'ам гонится локально: `Program/IdentityAdapters/Common/Verify-IdentityAdapter.ps1`. Он повторяет то, что лаунчер вывел в прошлый запуск сборки, поэтому после правки `IdentityAdapterMappingService` сборку надо один раз запустить, иначе проверятся старые алиасы.
