# Предупреждение об экспериментальных настройках мира

При подготовке сборки отключать подтверждение Minecraft об экспериментальных
настройках при создании и открытии мира. Для Java 1.16.5–26.3 здесь хранится
воспроизводимый pack-owned Java agent. Его исходники, точные зависимости ASM,
лицензия, JVM selector и проверочная квитанция принадлежат репозиторию сборки.

Артефакт поставляется как
`config/lanminecraft/experimental-world-confirmation.jar`. В
`launcher/jvm-args.txt` должна присутствовать одна отдельная строка

```text
-javaagent:config/lanminecraft/experimental-world-confirmation.jar
```

Добавить точный путь JAR в `launcher/pack-owned.txt`, если его уже покрывает
`config/**`, дополнительная строка не требуется. Закрепить все изменённые файлы
в distribution lock и обязательном авторском overlay. В стандартном рецепте
CurseForge JAR входит в `supportFiles`, JVM selector сохраняется в `additions`.
После изменения рецепта обновить его SHA256 и ownership в полном overlay receipt.
Использовать актуальные инструменты `tools/PackData/CurseForge` LANMinecraft.

Сборка агента требует JDK 25 и проверяет SHA256 ASM по `build-inputs.json`.
Пример из каталога этих инструментов

```powershell
python build.py --out <стенд>/experimental-world-confirmation.jar `
  --asm <asm-9.10.1.jar> --tree <asm-tree-9.10.1.jar> --javac <JDK>/bin/javac.exe
```

В сборку перенести `policy.py`, `policy.json`, исходник Java, build script,
`build-inputs.json` и `LICENSE.asm.txt` в `tools/experimental-warnings`.
Квитанция закрепляет точную платформу, JAR, JVM selector и ownership.
Перед упаковкой и публикацией выполнить read-only проверку

```powershell
python tools/experimental-warnings/policy.py --pack .
python tools/experimental-warnings/policy.py --pack . --manifest dist/pack-manifest.json
```

Генератор должен вызывать эту проверку. При обновлении автора повторно применить
слой и проверить его присутствие в итоговом манифесте. Изменение версии Minecraft
или загрузчика требует проверки новых нативных методов до обновления квитанции.
При уже включённом Yeetus Experimentus проверить его точный JAR, mixins и
поставку. Для платформ без этого диалога сохранить подтверждение по исходникам.

## Проверки

Итоговая проверка всех 25 опубликованных сборок, Git-ревизии, хеши и границы
приёмки сохранены в репозитории LANMinecraft. [Отчёт](https://github.com/MarkZamore/LANMinecraft/blob/main/tools/PackData/ExperimentalWarnings/verification.json)

`ExperimentalConfirmationProbe.java` вместе с тестовым `Lifecycle.java` проверяет
создание, импорт настроек, открытие мира, штатные deprecated/customized ветви,
количество вызовов продолжения и отсутствие отмены. Запускать с
`-Xverify:all -javaagent:<JAR>`. Тестовый Lifecycle в игровой JAR не включать.

`NativeBytecodeProbe.java` анализирует точные исходные и преобразованные классы
официальных клиентов через ASM BasicVerifier. Проверены Minecraft 1.16.5, 1.18.2,
1.19.2, 1.20.1, 1.21.1 и 26.3. JVM-проверки Java 8/21/25 выполнены без запуска
полных модифицированных клиентов и визуального управления игрой.

Агент меняет только переходы подтверждения. Экспериментальный Lifecycle остаётся
в данных мира. Штатные предупреждения deprecated/customized, проверки версии,
целостности мира и свободного места выполняются исходным игровым кодом.

`stage_update.py` проверяет SHA256/размер затронутых опубликованных архивов и
каждого их файла, сохраняет остальные игровые хеши и пересобирает библиотеку с
новой source revision. `publish.py` проверяет исходную ревизию и свежий манифест,
сначала публикует и скачивает для проверки новые immutable assets, затем
заменяет манифест. Параметр `--publish` предназначен для явно разрешённого выпуска.
