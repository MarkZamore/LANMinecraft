# Миграция мира Chebupeli: Infinity → LL8

* режим: **ПРИМЕНЕНИЕ (--apply)**
* дата: 2026-08-17 01:29:47 +0500
* мир: `C:\Users\Oskar\Documents\LANMinecraft\Minecraft\Worlds\Chebupeli`
* пак LL8: `C:\Users\Oskar\Documents\LL8`  •  старый пак (теги): `C:\Users\Oskar\Documents\Infinity`
* бэкап: `C:\Users\Oskar\Documents\LANMinecraft\Minecraft\Personal\Backups\Worlds\Chebupeli-pre-ll8-20260817-012857`
* присутствующих неймспейсов: 970

## Сводка по модам (что исчезает из мира)

| мод (namespace) | id | всего | МЭ-диск | рюкзаки | IF | MarkZamore | ASSin | anuvenn |
|---|---|---|---|---|---|---|---|---|
| biomesoplenty | 12 | 620 | 614 | 6 | 0 | 0 | 0 | 0 |
| dimensionalpocketsii | 3 | 343 | 314 | 29 | 0 | 0 | 0 | 0 |
| tfmg | 9 | 322 | 320 | 0 | 0 | 2 | 0 | 0 |
| iceandfire | 39 | 220 | 219 | 0 | 0 | 0 | 0 | 1 |
| ecologics | 10 | 208 | 208 | 0 | 0 | 0 | 0 | 0 |
| minecolonies | 7 | 72 | 72 | 0 | 0 | 0 | 0 | 0 |
| bhc | 3 | 47 | 45 | 1 | 1 | 0 | 0 | 0 |
| simplyswords | 18 | 43 | 41 | 0 | 0 | 1 | 0 | 1 |
| allthecompressed | 5 | 32 | 32 | 0 | 0 | 0 | 0 | 0 |
| wetland_whimsy | 6 | 29 | 29 | 0 | 0 | 0 | 0 | 0 |
| mutantmonsters | 6 | 29 | 29 | 0 | 0 | 0 | 0 | 0 |
| upgrade_aquatic | 3 | 24 | 24 | 0 | 0 | 0 | 0 | 0 |
| mekanism_extras | 2 | 15 | 15 | 0 | 0 | 0 | 0 | 0 |
| exposure | 2 | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| endrem | 3 | 13 | 1 | 12 | 0 | 0 | 0 | 0 |
| mowziesmobs | 9 | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| autumnity | 3 | 9 | 9 | 0 | 0 | 0 | 0 | 0 |
| darkutils | 5 | 8 | 7 | 1 | 0 | 0 | 0 | 0 |
| avaritia | 1 | 6 | 6 | 0 | 0 | 0 | 0 | 0 |
| alltheores | 3 | 5 | 5 | 0 | 0 | 0 | 0 | 0 |
| realmrpg_skeletons | 4 | 4 | 4 | 0 | 0 | 0 | 0 | 0 |
| minformax | 1 | 1 | 0 | 0 | 0 | 1 | 0 | 0 |
| **итого** | 154 | 2076 | 2020 | 49 | 1 | 4 | 0 | 2 |

> Колонки игроков — их `playerdata/<uuid>.dat`. Копия инвентаря хозяина в `level.dat:Data.Player` и файлы `.dat_old` правятся так же, но в сводке не учитываются, чтобы не считать одно и то же дважды.

## Удаляемые предметы по источникам

### AE2 disk_manager — 148 id / 2020 шт.

* **biomesoplenty** (614 шт.): `biomesoplenty:willow_sapling` ×365, `biomesoplenty:lavender` ×128, `biomesoplenty:brimstone` ×32, `biomesoplenty:willow_leaves` ×23, `biomesoplenty:bramble` ×17, `biomesoplenty:rose_quartz_chunk` ×12, `biomesoplenty:cypress_sapling` ×11, `biomesoplenty:burning_blossom` ×11, `biomesoplenty:wildflower` ×9, `biomesoplenty:tall_lavender` ×5, `biomesoplenty:blood_bucket` ×1
* **tfmg** (320 шт.): `tfmg:galena` ×198, `tfmg:fireclay_ball` ×60, `tfmg:sulfur` ×30, `tfmg:raw_lithium` ×13, `tfmg:lignite` ×12, `tfmg:large_aluminum_cogwheel` ×2, `tfmg:steel_cogwheel` ×2, `tfmg:large_steel_cogwheel` ×2, `tfmg:aluminum_cogwheel` ×1
* **dimensionalpocketsii** (314 шт.): `dimensionalpocketsii:dimensional_shard` ×307, `dimensionalpocketsii:block_dimensional_ore` ×5, `dimensionalpocketsii:block_deepslate_dimensional_ore` ×2
* **iceandfire** (219 шт.): `iceandfire:dragonbone` ×44, `iceandfire:sea_serpent_fang` ×33, `iceandfire:manuscript` ×25, `iceandfire:sea_serpent_scales_teal` ×21, `iceandfire:dragonscales_sapphire` ×14, `iceandfire:dragonscales_silver` ×12, `iceandfire:ice_dragon_flesh` ×11, `iceandfire:amphithere_feather` ×6, `iceandfire:ectoplasm` ×5, `iceandfire:sea_serpent_scales_blue` ×5, `iceandfire:frozen_gravel` ×5, `iceandfire:frozen_dirt` ×4, `iceandfire:shiny_scales` ×4, `iceandfire:ice_dragon_heart` ×3, `iceandfire:dragon_skull_ice` ×2, `iceandfire:troll_leather_frost` ×2, `iceandfire:tide_blue_chestplate` ×1, `iceandfire:armor_silver_metal_leggings` ×1, `iceandfire:dragon_ice` ×1, `iceandfire:armor_blue_helmet` ×1, `iceandfire:tide_trident` ×1, `iceandfire:earplugs` ×1, `iceandfire:dragonscales_blue` ×1, `iceandfire:armor_blue_boots` ×1, `iceandfire:seaserpent_skull` ×1, `iceandfire:tide_red_boots` ×1, `iceandfire:tide_red_leggings` ×1, `iceandfire:tide_red_helmet` ×1, `iceandfire:armor_silver_boots` ×1, `iceandfire:dragon_skull_fire` ×1, `iceandfire:armor_blue_chestplate` ×1, `iceandfire:troll_weapon_column_frost` ×1, `iceandfire:armor_silver_metal_chestplate` ×1, `iceandfire:sea_serpent_scales_red` ×1, `iceandfire:armor_silver_chestplate` ×1, `iceandfire:armor_silver_leggings` ×1, `iceandfire:armor_silver_helmet` ×1, `iceandfire:armor_blue_leggings` ×1, `iceandfire:troll_tusk` ×1
* **ecologics** (208 шт.): `ecologics:coconut_slice` ×94, `ecologics:thin_ice` ×63, `ecologics:coconut_seedling` ×25, `ecologics:coconut` ×8, `ecologics:coconut_husk` ×8, `ecologics:crab_claw` ×4, `ecologics:seashell` ×3, `ecologics:music_disc_coconut` ×1, `ecologics:pot` ×1, `ecologics:coconut_boat` ×1
* **minecolonies** (72 шт.): `minecolonies:scroll_buff` ×64, `minecolonies:eggplant` ×2, `minecolonies:onion` ×2, `minecolonies:corn` ×1, `minecolonies:durum` ×1, `minecolonies:supplycampdeployer` ×1, `minecolonies:garlic` ×1
* **bhc** (45 шт.): `bhc:red_heart` ×38, `bhc:yellow_heart` ×6, `bhc:green_heart` ×1
* **simplyswords** (41 шт.): `simplyswords:runic_tablet` ×20, `simplyswords:runefused_gem` ×4, `simplyswords:stormbringer` ×2, `simplyswords:iron_spear` ×2, `simplyswords:toxic_longsword` ×2, `simplyswords:diamond_chakram` ×1, `simplyswords:molten_edge` ×1, `simplyswords:twisted_blade` ×1, `simplyswords:gold_sai` ×1, `simplyswords:diamond_longsword` ×1, `simplyswords:dormant_relic` ×1, `simplyswords:diamond_claymore` ×1, `simplyswords:watcher_claymore` ×1, `simplyswords:empowered_remnant` ×1, `simplyswords:diamond_warglaive` ×1, `simplyswords:soulrender` ×1
* **allthecompressed** (32 шт.): `allthecompressed:redstone_block_3x` ×7, `allthecompressed:blackstone_2x` ×7, `allthecompressed:redstone_block_2x` ×7, `allthecompressed:blackstone_1x` ×7, `allthecompressed:redstone_block_1x` ×4
* **wetland_whimsy** (29 шт.): `wetland_whimsy:fellcap_mushroom` ×18, `wetland_whimsy:lemonstone_brazier` ×6, `wetland_whimsy:music_disc_nuke_the_swamps` ×2, `wetland_whimsy:lemonstone_pillar` ×1, `wetland_whimsy:polished_lemonstone` ×1, `wetland_whimsy:pennywort` ×1
* **mutantmonsters** (29 шт.): `mutantmonsters:mutant_skeleton_limb` ×14, `mutantmonsters:mutant_skeleton_rib` ×7, `mutantmonsters:mutant_skeleton_skull` ×3, `mutantmonsters:mutant_skeleton_pelvis` ×2, `mutantmonsters:mutant_skeleton_shoulder_pad` ×2, `mutantmonsters:hulk_hammer` ×1
* **upgrade_aquatic** (24 шт.): `upgrade_aquatic:pink_searocket` ×21, `upgrade_aquatic:mulberry` ×2, `upgrade_aquatic:pike` ×1
* **mekanism_extras** (15 шт.): `mekanism_extras:ingot_naquadah` ×13, `mekanism_extras:enriched_osmium` ×2
* **exposure** (13 шт.): `exposure:aged_photograph` ×11, `exposure:photograph` ×2
* **mowziesmobs** (13 шт.): `mowziesmobs:sand_rake` ×3, `mowziesmobs:umvuthana_mask_rage` ×2, `mowziesmobs:umvuthana_mask_faith` ×2, `mowziesmobs:ice_crystal` ×1, `mowziesmobs:umvuthana_mask_fear` ×1, `mowziesmobs:geomancer_belt` ×1, `mowziesmobs:umvuthana_mask_fury` ×1, `mowziesmobs:umvuthana_mask_misery` ×1, `mowziesmobs:umvuthana_mask_bliss` ×1
* **autumnity** (9 шт.): `autumnity:maple_log` ×6, `autumnity:sap_bottle` ×2, `autumnity:snail_goo` ×1
* **darkutils** (7 шт.): `darkutils:rune_builder` ×2, `darkutils:rune_galactic` ×2, `darkutils:rune_pigpen` ×1, `darkutils:rune_illager` ×1, `darkutils:rune_nyctography` ×1
* **avaritia** (6 шт.): `avaritia:compressed_crafting_table` ×6
* **alltheores** (5 шт.): `alltheores:cinnabar_ore` ×2, `alltheores:platinum_ore_hammer` ×2, `alltheores:iron_ore_hammer` ×1
* **realmrpg_skeletons** (4 шт.): `realmrpg_skeletons:chorus_skeleton` ×1, `realmrpg_skeletons:chorus_tangled_skeleton` ×1, `realmrpg_skeletons:lucky_skeleton` ×1, `realmrpg_skeletons:corrupted_skeleton` ×1
* **endrem** (1 шт.): `endrem:nether_eye` ×1

### MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) — 3 id / 4 шт.

* **tfmg** (2 шт.): `tfmg:raw_lithium` ×2
* **simplyswords** (1 шт.): `simplyswords:waxweaver` ×1
* **minformax** (1 шт.): `minformax:scanner` ×1

### MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) — 3 id / 4 шт.

* **tfmg** (2 шт.): `tfmg:raw_lithium` ×2
* **simplyswords** (1 шт.): `simplyswords:waxweaver` ×1
* **minformax** (1 шт.): `minformax:scanner` ×1

### anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) — 2 id / 2 шт.

* **iceandfire** (1 шт.): `iceandfire:tide_trident` ×1
* **simplyswords** (1 шт.): `simplyswords:brimstone_claymore` ×1

### anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) — 2 id / 2 шт.

* **iceandfire** (1 шт.): `iceandfire:tide_trident` ×1
* **simplyswords** (1 шт.): `simplyswords:brimstone_claymore` ×1

### data/IFBackpack.dat — 1 id / 1 шт.

* **bhc** (1 шт.): `bhc:red_heart` ×1

### data/sophisticatedbackpacks.dat — 7 id / 49 шт.

* **dimensionalpocketsii** (29 шт.): `dimensionalpocketsii:dimensional_shard` ×29
* **endrem** (12 шт.): `endrem:black_eye` ×6, `endrem:nether_eye` ×4, `endrem:corrupted_eye` ×2
* **biomesoplenty** (6 шт.): `biomesoplenty:empyreal_sapling` ×6
* **darkutils** (1 шт.): `darkutils:rune_nyctography` ×1
* **bhc** (1 шт.): `bhc:red_heart` ×1

### level.dat:Data.Player — 3 id / 4 шт.

* **tfmg** (2 шт.): `tfmg:raw_lithium` ×2
* **simplyswords** (1 шт.): `simplyswords:waxweaver` ×1
* **minformax** (1 шт.): `minformax:scanner` ×1

## Таблица переноса (remap)

| было | стало | шт. | источник соответствия |
|---|---|---|---|
| `alltheores:tin_ingot` | `mekanism:ingot_tin` | 8684 | tag c:ingots/tin |
| `alltheores:osmium_ingot` | `mekanism:ingot_osmium` | 3444 | tag c:ingots/osmium |
| `alltheores:aluminum_ingot` | `modern_industrialization:aluminum_ingot` | 3024 | tag c:ingots/aluminum |
| `alltheores:cinnabar` | `moremekanismprocessing:gem_cinnabar` | 2250 | tag c:gems/cinnabar |
| `alltheores:iridium_ingot` | `modern_industrialization:iridium_ingot` | 2031 | tag c:ingots/iridium |
| `alltheores:zinc_ingot` | `create:zinc_ingot` | 886 | tag c:ingots/zinc |
| `alltheores:lead_ingot` | `mekanism:ingot_lead` | 861 | tag c:ingots/lead |
| `iceandfire:silver_nugget` | `modern_industrialization:silver_nugget` | 712 | tag c:nuggets/silver |
| `ecologics:coconut_log` | `productivetrees:coconut_log` | 660 | override |
| `alltheores:nickel_ingot` | `modern_industrialization:nickel_ingot` | 488 | tag c:ingots/nickel |
| `alltheores:silver_ingot` | `modern_industrialization:silver_ingot` | 467 | tag c:ingots/silver |
| `biomesoplenty:willow_planks` | `regions_unexplored:willow_planks` | 442 | override |
| `alltheores:fluorite` | `mekanism:fluorite_gem` | 431 | tag c:gems/fluorite |
| `alltheores:platinum_dust` | `modern_industrialization:platinum_dust` | 265 | tag c:dusts/platinum |
| `ecologics:coconut_planks` | `productivetrees:coconut_planks` | 227 | override |
| `biomesoplenty:willow_log` | `regions_unexplored:willow_log` | 151 | override |
| `biomesoplenty:willow_fence` | `regions_unexplored:willow_fence` | 129 | override |
| `alltheores:uranium_ingot` | `mekanism:ingot_uranium` | 100 | tag c:ingots/uranium |
| `alltheores:raw_lead` | `mekanism:raw_lead` | 99 | tag c:raw_materials/lead |
| `alltheores:bronze_ingot` | `mekanism:ingot_bronze` | 80 | tag c:ingots/bronze |
| `ecologics:coconut_slab` | `productivetrees:coconut_slab` | 75 | override |
| `ecologics:coconut_stairs` | `productivetrees:coconut_stairs` | 55 | override |
| `alltheores:raw_uranium` | `mekanism:raw_uranium` | 53 | tag c:raw_materials/uranium |
| `piglin:white_glowstone` | `minecraft:glowstone` | 46 | override |
| `biomesoplenty:rose_quartz_block` | `silentgems:rose_quartz_block` | 45 | shape c:storage_blocks/rose_quartz |
| `biomesoplenty:empyreal_log` | `regions_unexplored:blackwood_log` | 45 | override |
| `alltheores:salt` | `mekanism:salt` | 42 | tag c:dusts/salt |
| `alltheores:platinum_ingot` | `modern_industrialization:platinum_ingot` | 41 | tag c:ingots/platinum |
| `alltheores:steel_plate` | `modern_industrialization:steel_plate` | 34 | tag c:plates/steel |
| `alltheores:sapphire` | `irons_jewelry:sapphire` | 33 | tag c:gems/sapphire |
| `alltheores:steel_rod` | `modern_industrialization:steel_rod` | 30 | tag c:rods/steel |
| `ecologics:coconut_trapdoor` | `productivetrees:coconut_trapdoor` | 20 | override |
| `ecologics:stripped_coconut_log` | `productivetrees:coconut_stripped_log` | 19 | override |
| `alltheores:brass_ingot` | `create:brass_ingot` | 16 | tag c:ingots/brass |
| `ecologics:coconut_door` | `productivetrees:coconut_door` | 15 | override |
| `alltheores:bronze_plate` | `modern_industrialization:bronze_plate` | 15 | tag c:plates/bronze |
| `tfmg:raw_lead` | `mekanism:raw_lead` | 15 | tag c:raw_materials/lead |
| `alltheores:sulfur` | `mekanism:dust_sulfur` | 15 | tag c:dusts/sulfur |
| `alltheores:diamond_plate` | `modern_industrialization:diamond_plate` | 12 | tag c:plates/diamond |
| `alltheores:peridot` | `irons_jewelry:peridot` | 11 | tag c:gems/peridot |
| `alltheores:bronze_rod` | `modern_industrialization:bronze_rod` | 10 | tag c:rods/bronze |
| `iceandfire:silver_ingot` | `modern_industrialization:silver_ingot` | 10 | tag c:ingots/silver |
| `alltheores:copper_plate` | `modern_industrialization:copper_plate` | 9 | tag c:plates/copper |
| `alltheores:raw_iridium` | `modern_industrialization:raw_iridium` | 8 | tag c:raw_materials/iridium |
| `iceandfire:sapphire_gem` | `irons_jewelry:sapphire` | 7 | tag c:gems/sapphire |
| `alltheores:ruby` | `irons_jewelry:ruby` | 7 | tag c:gems/ruby |
| `tfmg:lead_ingot` | `mekanism:ingot_lead` | 6 | tag c:ingots/lead |
| `tfmg:raw_nickel` | `modern_industrialization:raw_nickel` | 6 | tag c:raw_materials/nickel |
| `alltheores:electrum_ingot` | `modern_industrialization:electrum_ingot` | 6 | tag c:ingots/electrum |
| `alltheores:bronze_dust` | `mekanism:dust_bronze` | 4 | tag c:dusts/bronze |
| `biomesoplenty:willow_fence_gate` | `regions_unexplored:willow_fence_gate` | 3 | override |
| `biomesoplenty:willow_door` | `regions_unexplored:willow_door` | 3 | override |
| `ecologics:coconut_fence` | `productivetrees:coconut_fence` | 3 | override |
| `ecologics:coconut_sign` | `productivetrees:coconut_sign` | 3 | override |
| `alltheores:raw_zinc` | `create:raw_zinc` | 3 | tag c:raw_materials/zinc |
| `allthecompressed:blaze_rod_block` | `quark:blaze_lantern` | 2 | shape c:storage_blocks/blaze_rod |
| `ecologics:coconut_button` | `productivetrees:coconut_button` | 2 | override |
| `tfmg:nickel_ingot` | `modern_industrialization:nickel_ingot` | 2 | tag c:ingots/nickel |
| `alltheores:nether_fluorite_ore` | `mekanism:deepslate_fluorite_ore` | 1 | tag c:ores/fluorite |
| `alltheores:diamond_dust` | `mekanism:dust_diamond` | 1 | tag c:dusts/diamond |
| `alltheores:raw_platinum` | `modern_industrialization:raw_platinum` | 1 | tag c:raw_materials/platinum |
| `alltheores:copper_gear` | `modern_industrialization:copper_gear` | 1 | tag c:gears/copper |
| **итого** | | 26166 | |

## Компоненты и аттачменты

Срезано компонентов отсутствующих модов: **46** (без этого 1.21.1 не может разобрать весь предмет целиком).

* `gravestonecurioscompat:curio_slot_data` — AE2 disk_manager ×10
* `gravestonecurioscompat:curio_slot_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×9
* `gravestonecurioscompat:curio_slot_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×9
* `gravestonecurioscompat:curio_slot_data` — level.dat:Data.Player ×6
* `gravestonecurioscompat:curio_slot_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×6
* `gravestonecurioscompat:curio_slot_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×6

Удалено `neoforge:attachments`: **88**

* `mowziesmobs:living_data` — level.dat:Data.Player ×1
* `iceandfire:misc_data` — level.dat:Data.Player ×1
* `projecte:knowledge` — level.dat:Data.Player ×1
* `tempad:color` — level.dat:Data.Player ×1
* `projecte:alchemical_bags` — level.dat:Data.Player ×1
* `iceandfire:chain_data` — level.dat:Data.Player ×1
* `mowziesmobs:ability_data` — level.dat:Data.Player ×1
* `mowziesmobs:frozen_data` — level.dat:Data.Player ×1
* `tempad:travel_history` — level.dat:Data.Player ×1
* `astral_dimension:player_variables` — level.dat:Data.Player ×1
* `mowziesmobs:player_data` — level.dat:Data.Player ×1
* `projectexpansion:alchemical_book_locations` — level.dat:Data.Player ×1
* `mowziesmobs:living_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `iceandfire:misc_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `projecte:knowledge` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `tempad:color` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `projecte:alchemical_bags` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `iceandfire:chain_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `mowziesmobs:ability_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `mowziesmobs:frozen_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `tempad:travel_history` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `astral_dimension:player_variables` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `mowziesmobs:player_data` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `projectexpansion:alchemical_book_locations` — MarkZamore (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat) ×1
* `mowziesmobs:living_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `iceandfire:misc_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `projecte:knowledge` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `tempad:color` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `projecte:alchemical_bags` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `iceandfire:chain_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `mowziesmobs:ability_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `mowziesmobs:frozen_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `tempad:travel_history` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `astral_dimension:player_variables` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `mowziesmobs:player_data` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `projectexpansion:alchemical_book_locations` — MarkZamore_old (playerdata/06c83c9e-980b-47d5-b7be-23d2bb649068.dat_old) ×1
* `mowziesmobs:living_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `iceandfire:misc_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `mowziesmobs:ability_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `mowziesmobs:frozen_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `projecte:knowledge` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `tempad:travel_history` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `astral_dimension:player_variables` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `iceandfire:chicken_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `tempad:color` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `mowziesmobs:player_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `iceandfire:portal_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `projectexpansion:alchemical_book_locations` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `projecte:alchemical_bags` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `iceandfire:chain_data` — ASSin (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat) ×1
* `mowziesmobs:living_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `iceandfire:misc_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `mowziesmobs:ability_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `mowziesmobs:frozen_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `projecte:knowledge` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `tempad:travel_history` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `astral_dimension:player_variables` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `iceandfire:chicken_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `tempad:color` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `mowziesmobs:player_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `iceandfire:portal_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `projectexpansion:alchemical_book_locations` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `projecte:alchemical_bags` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `iceandfire:chain_data` — ASSin_old (playerdata/a4c56fa5-a630-42a6-9223-d6abfe63b130.dat_old) ×1
* `mowziesmobs:living_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `iceandfire:misc_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `projecte:knowledge` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `tempad:color` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `projecte:alchemical_bags` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `iceandfire:chain_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `mowziesmobs:ability_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `mowziesmobs:frozen_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `tempad:travel_history` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `astral_dimension:player_variables` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `mowziesmobs:player_data` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `projectexpansion:alchemical_book_locations` — anuvenn (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat) ×1
* `mowziesmobs:living_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `iceandfire:misc_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `projecte:knowledge` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `tempad:color` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `projecte:alchemical_bags` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `iceandfire:chain_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `mowziesmobs:ability_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `mowziesmobs:frozen_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `tempad:travel_history` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `astral_dimension:player_variables` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `mowziesmobs:player_data` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1
* `projectexpansion:alchemical_book_locations` — anuvenn_old (playerdata/f0f5ec1a-14f5-47b6-9e27-b860f62c14e5.dat_old) ×1

Из `recipeBook` удалено 520 рецептов отсутствующих модов.

## level.dat

* измерения: было **39**, стало **3** — minecraft:overworld, minecraft:the_end, minecraft:the_nether
* `Data.DragonFight`: True → False; `bei_ExtraDragonFight`: True → False
* `Data.DataPacks`: Enabled 42 → 34, Disabled 22 → 24
* `level.dat_old` перезаписан копией нового `level.dat`

## Удалённые файлы и каталоги

| путь | тип | размер | причина |
|---|---|---|---|
| `DIM-1` | dir | 237.3 MiB | non-overworld dimension data |
| `DIM1` | dir | 107.6 MiB | non-overworld dimension data |
| `dimensions` | dir | 99.1 MiB | non-overworld dimension data |
| `ftbquests` | dir | 103.8 KiB | LL8 ships its own quests/server config |
| `deaths` | dir | 91.3 KiB | owner mod 'gravestone' is not in the pack |
| `dimpockets` | dir | 16.1 KiB | owner mod 'dimensionalpocketsii' is not in the pack |
| `mfix_stronghold_cache_v2.nbt` | file | 1.1 KiB | stale cache, regenerated on demand |
| `serverconfig` | dir | 572 B | LL8 ships its own quests/server config |
| `data/map_2.dat` | file | 520 B | map of minecraft:the_nether |
| `data/minecolonies_colony_manager.dat` | file | 234 B | owner mod 'minecolonies' is not in the pack |
| `data/map_7.dat` | file | 231 B | map of minecraft:the_end |
| `data/InControlData.dat` | file | 117 B | owner mod 'incontrol' is not in the pack |
| `data/chunkloaders_loaded_chunks.dat` | file | 115 B | owner mod 'chunkloaders' is not in the pack |
| `chunkloaders` | dir | 107 B | owner mod 'chunkloaders' is not in the pack |
| `data/crafttweaker_saved_data.dat` | file | 91 B | owner mod 'crafttweaker' is not in the pack |
| `data/avaritia_accelerated_blocks.dat` | file | 85 B | owner mod 'avaritia' is not in the pack |
| `data/citadel_world_data.dat` | file | 81 B | owner mod 'citadel' is not in the pack |
| `minformax_indices` | dir | 66 B | owner mod 'minformax' is not in the pack |
| `alternate-current.conf` | file | 53 B | owner mod 'alternate_current' is not in the pack |
| `data/dankstorage` | dir | 0 B | owner mod 'dankstorage' is not in the pack |
| **итого** | | 444.3 MiB | |

## Точки телепорта (.minecraft-portable-waypoints)

* anuvenn / ftb-chunks: убрано minecraft_the_end/waypoints.json, minecraft_the_nether/waypoints.json, the_bumblezone_the_bumblezone/waypoints.json; осталось minecraft_overworld/waypoints.json; ревизия 78, sha256 `7185a21e0b332465…`
* anuvenn / xaero-minimap: убрано dim%-1/waypoints.txt; осталось dim%0/waypoints.txt; ревизия 42, sha256 `0c7bf6778e8980eb…`
* MarkZamore / ftb-chunks: убрано minecraft_the_end/waypoints.json, minecraft_the_nether/waypoints.json, the_bumblezone_the_bumblezone/waypoints.json; осталось minecraft_overworld/waypoints.json; ревизия 72, sha256 `76afded691780ce6…`

## Оставлено как есть

* `.minecraft-portable-players.json` (4.7 KiB) — не принадлежит удалённому моду
* `.minecraft-portable-waypoints` (7.4 KiB) — правится этим скриптом
* `.minecraft-portable-world.json` (680 B) — не принадлежит удалённому моду
* `advancements` (258.4 KiB) — не принадлежит удалённому моду
* `armor-hider.json` (55.9 KiB) — не принадлежит удалённому моду
* `compactmachines` (133 B) — не принадлежит удалённому моду
* `data` (15.5 MiB) — правится этим скриптом
* `data/biolith_overworld_state.dat` — 'biolith' is installed
* `entities` (72.1 MiB) — не принадлежит удалённому моду
* `ftbchunks` — teams/claims/homes are kept
* `ftbessentials` — teams/claims/homes are kept
* `ftbteams` — teams/claims/homes are kept
* `icon.png` (563 B) — не принадлежит удалённому моду
* `idas` (0 B) — не принадлежит удалённому моду
* `integratedscripting` (63.2 KiB) — не принадлежит удалённому моду
* `kubejs_persistent_data.nbt` (24 B) — не принадлежит удалённому моду
* `launch-pads.json` (22 B) — не принадлежит удалённому моду
* `level.dat` (26.7 KiB) — правится этим скриптом
* `level.dat.pre-relics012` (158.8 KiB) — не принадлежит удалённому моду
* `level.dat_old` (26.7 KiB) — правится этим скриптом
* `playerdata` (372.8 KiB) — правится этим скриптом
* `pneumaticcraft` (145 B) — не принадлежит удалённому моду
* `poi` (38.2 MiB) — не принадлежит удалённому моду
* `region` (2.7 GiB) — не принадлежит удалённому моду
* `session.lock` (3 B) — не принадлежит удалённому моду
* `stats` (149.5 KiB) — не принадлежит удалённому моду
* `tesseract` (0 B) — не принадлежит удалённому моду
* `tombstone` (198.6 KiB) — не принадлежит удалённому моду
* `xaeromap.txt` (14 B) — не принадлежит удалённому моду

## Примечания

* fresh level.dat: C:\Users\Oskar\Documents\LANMinecraft\Minecraft\Worlds\Новый мир\level.dat
* `data/lootr/**` (44789 файлов) не правится: мод есть в LL8, а неизвестные предметы внутри сундуков Minecraft отбросит сам при первом открытии.
* `region/`, `entities/`, `poi/` не трогаются: блоки и мобы удалённых модов исчезнут при загрузке чанков (с предупреждениями в логе) — это ожидаемо.
* `.minecraft-portable-players.json` и `.minecraft-portable-world.json` не трогаются: лаунчер владеет ими и перезаписывает при запуске.

## Предупреждения

* data/IFBackpack.dat: data.Backpacks.b604760c-9497-4e35-a7b6-fc239d8a0149.Slots.7.Stack replaced with an empty compound

## Ожидания для `--verify`

* стемов измерений: 3 (minecraft:overworld, minecraft:the_end, minecraft:the_nether)
* `item_count` МЭ-дисков: [279212]
* мёртвых стаков после миграции: 0 (сейчас 2086)
* чужих компонентов после миграции: 0 (сейчас 46)
* `ftbteams/`: 5 файл(ов) без изменений
