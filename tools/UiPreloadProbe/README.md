# Проверка первого кадра и предзагрузки

Запускать из checkout на Windows с .NET 10:

```powershell
dotnet run --project tools/UiPreloadProbe -- . artifacts/performance/first-frame.json --first-frame
dotnet run --project tools/UiPreloadProbe -- . artifacts/performance/scroll.json
dotnet run --project tools/UiPreloadProbe -- . artifacts/performance/dialogs-close.json --dialogs-close
```

Первый режим создаёт настоящий MainWindow с собственным изолированным корнем рядом с EXE стенда и закрывает его после первого ContentRendered до инициализации сети. Он измеряет конструктор/загрузку/показ окна, а не холодный старт CLR. Настоящий пользовательский экземпляр и его миры не меняются.

Второй режим использует встроенный каталог и настоящее видимое HWND с программным WPF renderer. DPI 100/150/200% задаётся WPF для layout, не меняет системный масштаб монитора. На каждой итерации фиксируются завершение очереди, ошибки прогрева, задержки Dispatcher Input и интервалы CompositionTarget.Rendering при wheel-событиях. Это диагностические интервалы WPF, а не измерение физического дисплея. Аппаратную плавность после maximize/restore дополнительно проверять на пользовательском мониторе. Все записи результатов находятся в artifacts; в репозиторий они не включаются.

Для сравнения с прежним кодом распаковать нужный git archive в отдельный artifacts-каталог и передать `-p:BaselineLauncherPath=ABSOLUTE_PATH/Program/Minecraft.csproj` при `dotnet run`. В этом режиме стенд использует прежнюю библиотеку без координатора предзагрузки. Перед следующим запуском без этого свойства дождаться завершения процесса: выходной EXE и DLL являются общими. Производительные прогоны и тесты с анимациями выполнять последовательно, без конкурирующей нагрузки.

`--dialogs-close` создаёт все пять собственных диалогов без показа и проверяет, что после очистки кеша в Application.Windows не осталось окон. Это важно для штатного завершения процесса с OnLastWindowClose.
