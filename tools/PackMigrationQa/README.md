# Проверка обновления пересобранной ATM9

Стенд использует производственные `PortablePackSyncService` и `PackInstanceService`. Старые canonical/instance читаются только по путям ownership-снимков; личные миры и настройки не копируются. Файлы мира, playerdata, options и screenshot в стенде являются синтетическими метками, а не резервной копией.

Нужны Windows, .NET 10, прежняя локальная ATM9 и новая поставка после `generate_manifest.py stage`. Сборки .NET и Java не запускаются одновременно с проверками лаунчера.

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
dotnet run --project tools/PackMigrationQa -p:SkipPortableIdentityAdapter=true -- . artifacts/atm9-original/dist
# Вторая независимая копия имитирует старую установку без ownership-файлов.
dotnet run --project tools/PackMigrationQa -p:SkipPortableIdentityAdapter=true -- . artifacts/atm9-original/dist --legacy
```

HTTP-обработчик подставляет локальные release assets по штатным GitHub URL без сети. Проверяются обрыв после первого полученного байта, сохранность старой ревизии до apply, persistent pending после прерванной записи, отказ offline fallback и подготовки instance, повторная попытка, SHA256/размер всех активных файлов, удаление подтверждённых исторических файлов, исключение неизвестных модов/скриптов из активного состава, сохранность меток и идемпотентность. Повторный sync запрашивает только manifest. Исходный managed-снимок проверяется повторно в конце.

Результаты, независимая установка и snapshot сохраняются в новом каталоге `artifacts/atm9-original/qa-migration`. В существующий каталог стенд не пишет. Успешная миграция файлов сама по себе не подтверждает запуск Minecraft или подключения через Steam: эти проверки выполняются отдельно на той же ревизии.

Для дополнительной проверки прежних custom-helper JAR используйте `history-proof <migration-installation> <dist> <historical-manifest> <historical-payload-root>` через собранную DLL стенда. Команда принимает только независимую установку с прежним PASS-отчётом внутри `artifacts`. Она сверяет три реальных старых helper JAR с опубликованным историческим manifest, добавляет их исключительно в QA canonical/instance и подтверждает удаление штатным sync без загрузки payload. `history-proof-report.json` содержит происхождение, SHA256, размеры, результат очистки и повторного запуска. Архив источника не изменяется.
