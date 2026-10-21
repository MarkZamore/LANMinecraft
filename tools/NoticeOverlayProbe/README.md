# Проверка уведомлений в настоящем OpenGL

Этот стенд запускает поставляемые с Minecraft LWJGL и DLL в отдельном Java-процессе с тем же агентом уведомлений, который подключает лаунчер. Он не устанавливает мод и не запускает Minecraft.

Нужны Windows с работающим драйвером OpenGL, PowerShell 7, JDK 17 или новее и кешированные библиотеки игры. Поэтому стенд запускается локально, а не входит в обязательный CI-профиль. Все классы, DLL, снимки, логи, временные файлы Java/JNA и перенаправленный профиль располагаются в `.claude/NoticeOverlayProbe` внутри репозитория. Скрипт восстанавливает переменные своего процесса при выходе. Перед запуском агент копируется в файл с уникальным именем внутри сеанса, чтобы JVM не блокировала пересборку исходного JAR; SHA-256 источника и копии записываются в `agent-source.json`.

Из корня репозитория:

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
.\Program\NoticeOverlay\Build-NoticeOverlay.ps1 -OutputPath .\Program\Build\NoticeOverlay\launcher-notice-agent.jar
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 3.3.3
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 3.4.1
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 3.2.2
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 2.9.3 -DownloadLegacy
```

Для проверки настоящего полноэкранного окна:

```powershell
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 3.3.3 -Fullscreen
.\tools\NoticeOverlayProbe\Run-Probe.ps1 -Version 3.4.1 -Fullscreen
```

Полноэкранная проверка на несколько секунд создаёт окно на основном мониторе с его текущим разрешением. Не запускайте её одновременно с игрой, другой такой проверкой или при активной работе с окнами: изменение фокуса пользователем испортит измерение. Исходный foreground HWND записывается отдельно от HWND после создания окна; проверка уведомлений сравнивает HWND после инициализации с HWND после отрисовки. После закрытия стенд восстанавливает исходное окно.

Стенд требует ненулевой монитор GLFW в полноэкранном режиме, проверяет пиксели текста/карточки в настоящем переднем буфере, свежий heartbeat с `renderedCount=1`, отсутствие ошибок OpenGL, неизменность foreground HWND и восстановление состояния GL. Перед каждой презентацией намеренно задаются нестандартные framebuffer, VAO/VBO, PBO загрузки, blending, viewport/scissor, маски цвета/глубины, depth/cull/stencil, rasterizer discard и sRGB. `rendered.png` и `probe.log` сохраняются в папке соответствующего сеанса.

Параметры `-Repository`, `-JavaHome` и `-Agent` позволяют проверить другой checkout/JDK/собранный агент. `-CompileOnly` проверяет компиляцию стенда; `-WithoutAgent` является отрицательным контролем и должен завершиться ошибкой из-за отсутствующего изображения уведомления/heartbeat. Старые LWJGL 2.9.3 загружаются только с `-DownloadLegacy` из Maven Central и проверяются по закреплённым SHA-256. Нативные библиотеки и JAR не добавляются в Git.

Это проверка механизма презентации, а не доказательство совместимости с любым будущим графическим движком. В частности, она не запускает модифицированные рендереры Minecraft и не проверяет Vulkan.
