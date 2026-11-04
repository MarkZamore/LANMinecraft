# Состояние аудита QA сборок на 5 ноября 2026 года

Этот документ сохраняет итог для продолжения работы с другого ПК, без локальных
артефактов и истории чата. См. [инструкцию продолжения](CONTINUING-ON-ANOTHER-PC.md)
и [правила подготовки новых сборок](ADDING-A-LIBRARY-PACK.md).
Таблица — исторический охват аудита, не постоянный список доступных сборок:
текущий состав берётся из `catalog.json` ветки `pack-catalog`.

## Что опубликовано

Все восемь QA-only коммитов отправлены в исходные репозитории с `[skip ci]`.
Для этих коммитов зарегистрировано **0 запусков CI**. Теги и игровые релизы
`pack-latest` сохранены; **1 064 записи release-assets** остались без изменений.
Новый HEAD исходников QA поэтому может быть новее коммита игровой поставки.

Числа ниже получены статическим разбором `test_*` методов Python, без раскрытия
`subTest`; это не число успешно выполненных случаев. Состав сокращён
**850 → 836 методов**. Python-синтаксис и diff проверены без импорта модулей.
Тесты, сборки, production validators, игра и новые runtime-проверки не запускались
по явному решению пользователя. Публикация исходников не означает новый PASS.

| Репозиторий / README | Исходный коммит | QA-коммит | Методы | QA-документация |
| --- | --- | --- | ---: | --- |
| [All-The-Mods-10](https://github.com/MarkZamore/All-The-Mods-10/blob/d191a3dffb827daa3fbdda232cd7bf36f446586f/README.md) | [70c6cf0](https://github.com/MarkZamore/All-The-Mods-10/commit/70c6cf051fb52dc0aa7703e3bb861e99928be950) | [d191a3d](https://github.com/MarkZamore/All-The-Mods-10/commit/d191a3dffb827daa3fbdda232cd7bf36f446586f) | 59 → 58 | [Client policy](https://github.com/MarkZamore/All-The-Mods-10/blob/d191a3dffb827daa3fbdda232cd7bf36f446586f/tools/client-policy/README.md) |
| [All-The-Mods-9](https://github.com/MarkZamore/All-The-Mods-9/blob/32460210572235c4c1eb6f82cf9d791cc96a4a11/README.md) | [48bafee](https://github.com/MarkZamore/All-The-Mods-9/commit/48bafee6ff7fb5fc89c02b174faa4821f914c44c) | [3246021](https://github.com/MarkZamore/All-The-Mods-9/commit/32460210572235c4c1eb6f82cf9d791cc96a4a11) | 162 → 162 | [Статус](https://github.com/MarkZamore/All-The-Mods-9/blob/32460210572235c4c1eb6f82cf9d791cc96a4a11/TESTING-STATUS.md) |
| [All-The-Mods-9-Modified](https://github.com/MarkZamore/All-The-Mods-9-Modified/blob/1b241bb945d14d7cc912567cc42c260c8e9ea4c1/README.md) | [bca0f2c](https://github.com/MarkZamore/All-The-Mods-9-Modified/commit/bca0f2cb4c4b664b4b508a500356ef5b6c6bc125) | [1b241bb](https://github.com/MarkZamore/All-The-Mods-9-Modified/commit/1b241bb945d14d7cc912567cc42c260c8e9ea4c1) | 271 → 271 | [Статус](https://github.com/MarkZamore/All-The-Mods-9-Modified/blob/1b241bb945d14d7cc912567cc42c260c8e9ea4c1/TESTING-STATUS.md) |
| [Awakening-Of-The-Titans](https://github.com/MarkZamore/Awakening-Of-The-Titans/blob/eed04a543afe65c32c289a86965dfa75c0d3917f/README.md) | [4c9b4de](https://github.com/MarkZamore/Awakening-Of-The-Titans/commit/4c9b4ded406ca1c49faeabff2191f0b32d18faed) | [eed04a5](https://github.com/MarkZamore/Awakening-Of-The-Titans/commit/eed04a543afe65c32c289a86965dfa75c0d3917f) | 73 → 69 | [Probes](https://github.com/MarkZamore/Awakening-Of-The-Titans/blob/eed04a543afe65c32c289a86965dfa75c0d3917f/tools/runtime-qa/probes/README.md) |
| [Better-Than-Wolves](https://github.com/MarkZamore/Better-Than-Wolves/blob/5413e54434553757f881f13caee3e201b0f0edb8/README.md) | [47fb2d9](https://github.com/MarkZamore/Better-Than-Wolves/commit/47fb2d9dd3c1caaa9390c86c8b03beebc3aad9ee) | [5413e54](https://github.com/MarkZamore/Better-Than-Wolves/commit/5413e54434553757f881f13caee3e201b0f0edb8) | 59 → 54 | [Runtime QA](https://github.com/MarkZamore/Better-Than-Wolves/blob/5413e54434553757f881f13caee3e201b0f0edb8/tools/runtime-qa/README.md) |
| [Caves-and-Dungeons](https://github.com/MarkZamore/Caves-and-Dungeons/blob/ceb19ebbda76d5aa2d1bf6da2aec4778c178858c/README.md) | [614706a](https://github.com/MarkZamore/Caves-and-Dungeons/commit/614706a7fe7b3a5ffc0f14c58b4f99f50015abe7) | [ceb19eb](https://github.com/MarkZamore/Caves-and-Dungeons/commit/ceb19ebbda76d5aa2d1bf6da2aec4778c178858c) | 63 → 63 | [Статус](https://github.com/MarkZamore/Caves-and-Dungeons/blob/ceb19ebbda76d5aa2d1bf6da2aec4778c178858c/TESTING-STATUS.md) |
| [Minecraft-26.3](https://github.com/MarkZamore/Minecraft-26.3/blob/6cc42f4f80e438f0704eeca62aa31395be18de1a/README.md) | [66cea9b](https://github.com/MarkZamore/Minecraft-26.3/commit/66cea9b6413249c66c074529e2a890f12860a382) | [6cc42f4](https://github.com/MarkZamore/Minecraft-26.3/commit/6cc42f4f80e438f0704eeca62aa31395be18de1a) | 56 → 52 | [QA](https://github.com/MarkZamore/Minecraft-26.3/blob/6cc42f4f80e438f0704eeca62aa31395be18de1a/tools/QA.md) |
| [TerraFirma-Rebirth-2](https://github.com/MarkZamore/TerraFirma-Rebirth-2/blob/300b596690b1df5a9224507bf5aca3c475a9c18a/README.md) | [a92d0d4](https://github.com/MarkZamore/TerraFirma-Rebirth-2/commit/a92d0d4cb05be9137d13fdabc66fbdade0e9c83a) | [300b596](https://github.com/MarkZamore/TerraFirma-Rebirth-2/commit/300b596690b1df5a9224507bf5aca3c475a9c18a) | 107 → 107 | [Metadata](https://github.com/MarkZamore/TerraFirma-Rebirth-2/blob/300b596690b1df5a9224507bf5aca3c475a9c18a/tools/native-version-metadata/README.md) |

## Что сокращено и что осталось

- Original ATM9: пять модулей по-прежнему обнаруживаются общим unittest discovery.
  Убран отдельный вызов тех же **99 методов**: 261 посещение → 162 без потери
  уникальных методов. Исторический logging probe сократился с трёх JVM до двух:
  reference/restored INFO использовали один файл, одинаковые аргументы и ожидание.
- TerraFirma Rebirth 2: 15 Node-сценариев уже вызываются из unittest; повторная
  команда README убрана, последовательность 30 → 15 при тех же 15 сценариях.
  Metadata refresh/retirement проверяют текущую платформу с прежними границами
  ownership, чужих файлов, exact pins и rollback.
- Awakening Of The Titans: удалён точный дубль `AotGameplayProbeV3.java`;
  канонический `AotGameplayProbe.java`, native сценарии и их границы сохранены.
- В сборках убраны неиспользуемые копии filtered/group/Forge-only матриц;
  fixtures приведены к реальным Minecraft/loader. Общие матрицы остаются в
  [LANMinecraft, tools/test_pack_library.py, 1b728eb](https://github.com/MarkZamore/LANMinecraft/blob/1b728eb7359e05af1939ac022fbb7f3f2d312be0/tools/test_pack_library.py).
- Minecraft 26.3 сохраняет четыре текущих mod ID, Sodium nested dedup и все три
  точные legacy-миграции. ATM10 сохраняет отдельный guard обязательных API-классов
  и проверку native API при обычной подготовке публикации.

Production-код, JAR, настройки, механики, manifests/locks, состав и игровые
payload не менялись. Проверки Safety, идентичности, авторизации, сохранности
миров, SHA/размеров, происхождения, путей, конфликтов, rollback и обязательных
receipt не удалялись. Сырые логи и личные данные не входят в этот отчёт.

## Как продолжать

Получить актуальные ветки лаунчера, каталога и нужных source-репозиториев по
[инструкции](CONTINUING-ON-ANOTHER-PC.md). Сначала прочитать последние AGENTS и
pack-owned QA: таблица фиксирует этот аудит, а не заменяет будущие изменения.
Отказ от повторных прогонов действует для этой QA-only задачи; автоматически
перезапускать их после `fetch` не нужно. Будущие изменения проверяются по их
собственному риску и согласованной приёмке. Существующие runtime-отчёты остаются
датированными свидетельствами именно своих ревизий, не новыми результатами.
