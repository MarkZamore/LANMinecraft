# Категории требований сборок в выпуске 542

Цвет показывает сравнительную оценку нагрузки для игры с обычными настройками. Она учитывает версию Minecraft, полный загружаемый состав и известные требования автора к памяти или видеокарте. Один цвет не задаёт гарантированные FPS и не меняет выделенную память. На реальную производительность также влияют процессор, видеокарта, шейдеры, дальность прорисовки и состояние мира.

Версии и полное число модов сверены с `pack-manifest.json` и архивами карточек первых поставок каталога 542. Размер и SHA-256 каждого архива совпали с манифестом. Число модов ниже взято из манифеста, а не из видимой части списка. Оно включает зависимости, которые игрок не видит в счётчике карточки. [Исходный индекс](https://github.com/MarkZamore/LANMinecraft/blob/30b5807d6c5f7938608836dd410100979f9f3ec0/catalog.json)

| Раздел | Сборка | Minecraft | Моды | Требования |
|---|---|---:|---:|---|
| Глобальные сборки | All the Mods 10 | 1.21.1 | 644 | Высокие |
| | All the Mods 9 | 1.20.1 | 453 | Высокие |
| | All the Mods 9 Modified | 1.20.1 | 537 | Высокие |
| Приключения и кампании | Awakening Of The Titans | 1.21.1 | 249 | Средние |
| | Caves and Dungeons | 1.20.1 | 120 | Низкие |
| | Fear Nightfall | 1.20.1 | 377 | Высокие |
| Необычные миры | Antimatter Chemistry Reforged | 1.20.1 | 173 | Средние |
| | Clayblock | 1.21.1 | 113 | Низкие |
| | Desolate Planet | 1.20.1 | 186 | Высокие |
| | Escape The End | 1.20.1 | 146 | Низкие |
| | FTB Inferno | 1.18.2 | 274 | Средние |
| | Liminal Industries | 1.20.1 | 152 | Низкие |
| | Reclamation | 1.20.1 | 177 | Средние |
| | UniversIO | 1.19.2 | 180 | Средние |
| Хардкор и технологии | TerraFirma Rebirth 2 | 1.21.1 | 384 | Высокие |
| | Better Than Wolves | 1.6.4 | 11 | Низкие |
| | Nuclear Tech Reborn | 1.12.2 | 139 | Низкие |
| Классика и альтернативные режимы | Better Vanilla | 26.3 | 212 | Средние |
| | Minecraft Java Edition | 26.3 | 3 | Средние |
| | Minecraft Rust Edition | 26.3 | 0 | Средние |
| | Minecraft Rust Mods | 26.3 | 37 | Средние |
| | Hide and Hunt | 26.3 | 47 | Средние |
| Скайблоки | All in One | 1.16.5 | 113 | Низкие |
| | All the Mods 10 To the Sky | 1.21.1 | 355 | Высокие |
| | Beebeeblock | 1.21.1 | 379 | Высокие |

- Команда All The Mods рекомендует 16 ГБ системной памяти для комфортной игры. Автор ATM10 To the Sky рекомендует выделять 6–8 ГБ игре. В Modified дополнительно предусмотрена память для локальной модели. [Документация ATM](https://allthemods.github.io/alltheguides/) · [Страница ATM10 To the Sky](https://www.curseforge.com/minecraft/modpacks/all-the-mods-10-sky)
- Автор Desolate Planet советует выделять 6–8 ГБ и рекомендует 8 ГБ при наличии свободной памяти. Feed The Beast указывает для Inferno 4 ГБ как минимум и 6 ГБ как рекомендацию. [FAQ Desolate Planet](https://www.curseforge.com/minecraft/modpacks/desolate-planet) · [Страница FTB Inferno](https://www.feed-the-beast.com/modpacks/99-ftb-inferno)
- Для текущей Java Edition Mojang указывает 8 ГБ системной памяти с дискретной видеокартой либо 12 ГБ со встроенной как минимум, 16 ГБ как рекомендацию и отдельные условия для GPU. Применённый в Rust сборках Horizon не использует Java. На Windows его движку нужна видеокарта с Vulkan или DirectX 12. [Требования Mojang](https://www.minecraft.net/en-us/article/minecraft-java-edition-system-requirements) · [Закреплённая ревизия Horizon](https://github.com/tr4m0ryp/Minecraft-rust/blob/2ebf00a0281c592f79191e76fce0e7d522c6f425/README.md#download)
