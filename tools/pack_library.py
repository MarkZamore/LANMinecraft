"""Build a deterministic presentation bundle, separately from installed game files.

Python 3.12 standard library only. The pack owns library/content.json and preview.jpg.
All mod names come from shipped jars; curated semantic relationships live in content.
"""
import hashlib
import io
import json
import pathlib
import re
import tomllib
import zipfile


LIBRARIES = set("architectury balm cloth_config clothconfig forgeconfigapiport geckolib geckolib3 citadel curios patchouli kotlinforforge kotlin_forge scalablecatsforce resourcefullib resourcefulconfig puzzleslib moonlight framework collective bookshelflib bookshelf selene placebo iceberg cristellib caelus creativecore cucumber cyclopscore supermartijn642corelib supermartijn642configlib titanium terrablender fabric_api forgified_fabric_api connector connector_extras yet_another_config_lib_v3 yacl_v3 owo libipn libgui jamlib badpackets smartbrainlib azurelib azurelibarmor athena octolib lithostitched rhino kubejs libx ftblibrary ftbteams prometheus libjf cardinal_components_base cardinal_components_entity cardinal_components_world cloth_basic_math unobtainiumlib coroutil unionlib flywheel ponder ferritecore modernfix embeddium sodium neoforge jei_api connector attributeslib apothic_attributes apothic_enchanting apothic_spawners apothic_core structure_gel mru prickle glitchcore searchables playeranimator player_animation_library catalogue configured cerbons_api cerbonsapi fusion fzzy_config codechickenlib cofh_core hammerlib kiwi mafg_lib mafg_lib sophis_backpacks sophisticatedcore sophisticatedstorageinmotion_helper commonnetwork cc_tweaked_api theurgy_kubejs theurgy_api modonomicon guideme ae2wtlib chemlib alchemylib kotlinforforge alltheleaks mixinextras mixinextras_neoforge mixin_squared mixinsquared sparkutil almostunified polylib towntalk cobaltscareers_api granular_config blueflame balm_api cupboard glitchcore entity_texture_features entity_model_features animation_overhaul_api lionfishapi curioscontinuation jamlib ftbfilters ftbxmodcompat librarium valhelsia_core yungsapi yungsmenutweaks zeta mutil mcjtylib xnetgases_lib exact_version_api fairplaylib octo_lib octolib epherolib permafrost_core puzzlesaccessapi simpleclouds_api lodestone selene_lib auxilium reliquary_core porting_lib connector modmenu_api domum_ornamentum blockui structurize dummmmmmy_api kambrik mantle shetiphiancore yacl yetanotherconfiglib cloth_config_api openloader kleeslabs_api catconfig mcwbridges_api expandability trenzalore reborncore ftbchunks_api".split())
# Optimization mods stay visible gameplay/utility entries, rather than masquerading as libraries.
LIBRARIES -= {"ferritecore", "modernfix", "embeddium", "sodium", "catalogue", "configured", "almostunified", "alltheleaks"}
LIBRARIES |= set("atlas_api brandonscore create_dragons_plus konkrete melody moogs_structures tesseract_api xycraft_core zerocore anvilcore databank extraspecialcore integrated_api mechanicals uteamcore".split())

PARENTS = {
    "ars_additions": "ars_nouveau", "ars_elemental": "ars_nouveau", "ars_technica": "ars_nouveau",
    "ars_creo": "ars_nouveau", "ars_ocultas": "ars_nouveau", "ars_instrumentum": "ars_nouveau",
    "starbunclemania": "ars_nouveau", "toomanyglyphs": "ars_nouveau", "arseng": "ars_nouveau",
    "ars_energistique": "ars_nouveau", "arsdelight": "ars_nouveau", "arsomega": "ars_nouveau",
    "mekanismgenerators": "mekanism", "mekanismtools": "mekanism", "mekanismadditions": "mekanism",
    "mekanism_extras": "mekanism", "mekanism_extra": "mekanism", "mekanismcovers": "mekanism",
    "createaddition": "create", "create_enchantment_industry": "create", "create_aeronautics": "create",
    "create_connected": "create", "create_new_age": "create", "create_central_kitchen": "create",
    "create_sa": "create", "createutilities": "create", "railways": "create", "createrailwaysnavigator": "create",
    "extendedgears": "create", "copycats": "create", "sliceanddice": "create", "tfmg": "create",
    "create_bic_bit": "create", "create_framed": "create", "createoreexcavation": "create",
    "create_mechanical_extruder": "create", "create_mobile_packages": "create", "create_power_loader": "create",
    "create_hypertube": "create", "create_pattern_schematics": "create", "create_better_motors": "create",
    "create_factory_logistics": "create", "create_vibrant_vaults": "create", "create_things_and_misc": "create",
    "createmechanicalspawner": "create", "createstuffadditions": "create", "create_dd": "create",
    "createconfectionery": "create", "createfood": "create", "create_haven_qualifier": "create",
    "ae2wtlib": None, "ae2things": "ae2", "ae2additions": "ae2", "ae2extras": "ae2",
    "advanced_ae": "ae2", "extendedae": "ae2", "megacells": "ae2", "appflux": "ae2",
    "appliedenergistics2": None, "appmek": "ae2", "appliedcooking": "ae2", "expatternprovider": "ae2",
    "refinedstorageaddons": "refinedstorage", "extrastorage": "refinedstorage",
    "extradisks": "refinedstorage", "functionalstorage": None,
    "thermal_expansion": "thermal", "thermal_innovation": "thermal", "thermal_cultivation": "thermal",
    "thermal_dynamics": "thermal", "thermal_locomotion": "thermal", "thermal_extra": "thermal",
    "botanypots_tiers": "botanypots", "botanypotstiers": "botanypots", "botanytrees": "botanypots",
    "delightful": "farmersdelight", "farmersrespite": "farmersdelight", "nethersdelight": "farmersdelight",
    "ends_delight": "farmersdelight", "oceansdelight": "farmersdelight", "brewinandchewin": "farmersdelight",
    "enderdelight": "farmersdelight", "culturaldelights": "farmersdelight",
    "mysticalagradditions": "mysticalagriculture", "mysticalcustomization": "mysticalagriculture",
    "industrialforegoing": None, "industrialforegoing_core": "industrialforegoing",
    "irons_spellbooks_compat": "irons_spellbooks", "iss_magicfromtheeast": "irons_spellbooks",
    "sophisticatedbackpacks": None, "sophisticatedstorage": None,
    "tfcambiental": "tfc", "tfcastikorcarts": "tfc", "tfccanes": "tfc", "tfc_canes": "tfc",
    "tfcchannelcasting": "tfc", "tfcgroomingstation": "tfc", "tfc_ie_addon": "tfc",
    "tfcgenviewer": "tfc", "tfcmedicinal": "tfc", "tfcpan": "tfc", "tfcsmoker": "tfc",
    "tfcshaving": "tfc", "tfc_water_flasks": "tfc", "tfcweather": "tfc", "tfcwaterwheel": "tfc",
    "firmalife": "tfc", "afc": "tfc", "firmaciv": "tfc", "firmacivplus": "tfc",
    "coralstfc": "tfc", "tfcorewashing": "tfc", "tfcsablerivers": "tfc", "tfcthings": "tfc",
    "beneath": "tfc", "firmacreate": "tfc", "precisionprospecting": "tfc",
}


def read_mods(data, jar_name, depth=0):
    """Read actual mod metadata; nested libraries belong to their owning jar."""
    result = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = set(archive.namelist())
        for path in ("META-INF/neoforge.mods.toml", "META-INF/mods.toml"):
            if path in names:
                doc = tomllib.loads(archive.read(path).decode("utf-8-sig"))
                for mod in doc.get("mods", []):
                    mod_id = mod.get("modId")
                    if not mod_id:
                        continue
                    version = str(mod.get("version", ""))
                    if "${" in version:
                        version = ""
                    result.append({"id": mod_id, "name": mod.get("displayName", mod_id), "version": version})
                break
        if not result and "fabric.mod.json" in names:
            doc = json.loads(archive.read("fabric.mod.json"))
            result.append({"id": doc["id"], "name": doc.get("name", doc["id"]), "version": str(doc.get("version", ""))})
        if not result and depth < 2 and "META-INF/jarjar/metadata.json" in names:
            for nested in json.loads(archive.read("META-INF/jarjar/metadata.json")).get("jars", []):
                path = nested.get("path", "")
                if path in names:
                    result.extend(read_mods(archive.read(path), jar_name, depth + 1))
    if not result and depth == 0:
        # Explicit nonstandard jars, never silently disappear from the catalogue.
        if jar_name.lower().startswith("scalablecatsforce"):
            result = [{"id": "scalablecatsforce", "name": "Scalable Cat's Force", "version": ""}]
        else:
            result = [{"id": re.sub(r"[^a-z0-9_]", "_", pathlib.Path(jar_name).stem.lower()),
                       "name": pathlib.Path(jar_name).stem, "version": ""}]
    return result


def build_library(pack, revision):
    pack = pathlib.Path(pack)
    authored = json.loads((pack / "library/content.json").read_text(encoding="utf-8-sig"))
    runtime = json.loads((pack / "portable-pack.json").read_text(encoding="utf-8-sig"))
    mods = {}
    for jar in sorted((pack / "mods").glob("*.jar")):
        for mod in read_mods(jar.read_bytes(), jar.name):
            mods.setdefault(mod["id"], mod)
    libraries = LIBRARIES | set(authored.get("libraryIds", []))
    parents = PARENTS | authored.get("parents", {})
    for mod in mods.values():
        parent = parents.get(mod["id"])
        mod["parentId"] = parent if parent in mods and parent != mod["id"] and parent not in libraries else None
        mod["isLibrary"] = mod["id"] in libraries
    # Fail publication rather than handing the UI a cyclic tree.
    for mod in mods.values():
        seen = {mod["id"]}
        parent = mod["parentId"]
        while parent:
            if parent in seen:
                raise ValueError("Cyclic addon relation: " + mod["id"])
            seen.add(parent)
            parent = mods[parent]["parentId"]
    doc = {"schemaVersion": 1, "id": authored["id"], "relativePath": authored.get("relativePath", pack.name),
           "name": authored["name"], "summary": authored["summary"], "description": authored["description"],
           "minecraftVersion": runtime["minecraftVersion"], "loader": runtime["loader"]["type"],
           "revision": revision, "mods": sorted(mods.values(), key=lambda m: (m["name"].casefold(), m["id"]))}
    data = json.dumps(doc, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    preview = (pack / "library/preview.jpg").read_bytes()
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, payload in (("library.json", data), ("preview.jpg", preview)):
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, payload)
    return buffer.getvalue()


def stage_library(pack, out, revision):
    payload = build_library(pack, revision)
    digest = hashlib.sha256(payload).hexdigest()
    name = "library-" + digest + ".zip"
    target = pathlib.Path(out) / "assets" / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(payload)
    return {"name": name, "sizeBytes": len(payload), "sha256": digest, "revision": revision}


def verify_library(out, descriptor):
    payload = (pathlib.Path(out) / "assets" / descriptor["name"]).read_bytes()
    assert len(payload) == descriptor["sizeBytes"]
    assert hashlib.sha256(payload).hexdigest() == descriptor["sha256"]
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        doc = json.loads(archive.read("library.json"))
        assert doc["revision"] == descriptor["revision"]
        assert archive.read("preview.jpg")
