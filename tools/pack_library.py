"""Build a deterministic presentation bundle, separately from installed game files.

Python 3.12 standard library only. The pack owns library/content.json and preview.jpg.
Mod names come from shipped JARs or a verified native inventory.
"""
import hashlib
import io
import json
import pathlib
import re
import tomllib
import zipfile
from urllib.parse import urlsplit
from library_mod_groups import group_presentation


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


def read_mods(data, jar_name, depth=0, loader=None):
    """Read declared mod IDs, including dependencies registered with the loader.

    Language providers and other non-mod components remain visible library
    entries, but never invent a loaded mod ID for the pack's mod counter.
    """
    result = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = set(archive.namelist())
        manifest = {}
        if "META-INF/MANIFEST.MF" in names:
            text = archive.read("META-INF/MANIFEST.MF").decode("utf-8-sig").replace("\r\n", "\n")
            text = text.replace("\n ", "")
            manifest = dict(line.split(": ", 1) for line in text.splitlines() if ": " in line)
        fabric_loader = loader in ("fabric", "quilt", "legacyfabric")
        metadata_paths = () if fabric_loader else (("META-INF/mods.toml",) if loader == "forge" else (
            "META-INF/neoforge.mods.toml", "META-INF/mods.toml"))
        for path in metadata_paths:
            if path in names:
                doc = tomllib.loads(archive.read(path).decode("utf-8-sig"))
                for mod in doc.get("mods", []):
                    mod_id = mod.get("modId")
                    if not mod_id:
                        continue
                    version = str(mod.get("version", ""))
                    substitutions = {"jarVersion": manifest.get("Implementation-Version", ""),
                                     **doc.get("properties", {})}
                    for key, value in substitutions.items():
                        version = version.replace("${file." + key + "}", str(value))
                    if "${" in version:
                        version = ""
                    result.append({"id": mod_id, "name": mod.get("displayName", mod_id),
                                   "version": version, "isLoadedMod": True, "_depth": depth})
                break
        fabric = None
        if not result and loader not in ("forge", "neoforge") and "fabric.mod.json" in names:
            doc = json.loads(archive.read("fabric.mod.json"))
            fabric = doc
            result.append({"id": doc["id"], "name": doc.get("name", doc["id"]),
                           "version": str(doc.get("version", "")), "isLoadedMod": True, "_depth": depth})
        nested_paths = []
        if "META-INF/jarjar/metadata.json" in names and not fabric_loader:
            nested_paths.extend(nested.get("path", "") for nested in
                                json.loads(archive.read("META-INF/jarjar/metadata.json")).get("jars", []))
        if fabric:
            nested_paths.extend(nested.get("file", "") for nested in fabric.get("jars", []))
        for path in nested_paths:
            if not path or path not in names:
                raise ValueError("Missing declared nested mod in " + jar_name + ": " + path)
            if depth >= 8:
                raise ValueError("Nested mod depth exceeds limit in " + jar_name)
            result.extend(read_mods(archive.read(path), path, depth + 1, loader))
    if not result and depth == 0:
        # Explicit nonstandard jars, never silently disappear from the catalogue.
        if jar_name.lower().startswith("scalablecatsforce"):
            result = [{"id": "scalablecatsforce", "name": "Scalable Cat's Force", "version": manifest.get("Implementation-Version", ""),
                       "isLoadedMod": False, "_depth": depth}]
        else:
            result = [{"id": re.sub(r"[^a-z0-9_]", "_", pathlib.Path(jar_name).stem.lower()),
                       "name": pathlib.Path(jar_name).stem, "version": manifest.get("Implementation-Version", ""),
                       "isLoadedMod": False, "_depth": depth}]
    return result


def runtime_sources(pack):
    """Reviewed tooling-only JAR inputs, never installed as pack payload files."""
    pack = pathlib.Path(pack).resolve()
    manifest = pack / "library/runtime-sources.json"
    if not manifest.exists():
        return []
    document = json.loads(manifest.read_text(encoding="utf-8-sig"))
    if document.get("schemaVersion") != 1 or not isinstance(document.get("sources"), list):
        raise ValueError("Invalid library runtime source manifest")
    result, paths, ids = [], set(), set()
    for row in document["sources"]:
        rel, selected = row.get("cachePath"), row.get("modIds")
        address = urlsplit(row.get("sourceUrl", ""))
        if (not isinstance(rel, str) or not rel.startswith("artifacts/") or "\\" in rel or ":" in rel or
                any(part in ("", ".", "..") for part in rel.split("/")) or not rel.endswith(".jar") or rel.casefold() in paths or
                type(row.get("sizeBytes")) is not int or row["sizeBytes"] <= 0 or not re.fullmatch("[a-f0-9]{64}", row.get("sha256", "")) or
                address.scheme != "https" or not address.hostname or address.username or address.password or
                not isinstance(selected, list) or not selected or any(not isinstance(mid, str) or not mid for mid in selected) or
                len(set(selected)) != len(selected) or ids.intersection(selected)):
            raise ValueError("Invalid/duplicate runtime source or selected mod ID")
        path = pack / rel
        if not path.resolve().is_relative_to(pack) or any(part.is_symlink() or (hasattr(part, "is_junction") and part.is_junction()) for part in [path, *path.parents] if part != pack.parent):
            raise ValueError("Linked or escaping runtime source")
        paths.add(rel.casefold()); ids.update(selected); result.append(row)
    return result


def read_runtime_mods(pack, loader):
    pack = pathlib.Path(pack)
    mods, hashes = {}, {}
    for row in runtime_sources(pack):
        path = pack / row["cachePath"]
        if not path.is_file() or path.stat().st_size != row["sizeBytes"] or hashlib.sha256(path.read_bytes()).hexdigest() != row["sha256"]:
            raise ValueError("Missing/changed reviewed runtime source JAR: " + row["cachePath"])
        records = {mod["id"]: mod for mod in read_mods(path.read_bytes(), path.name, loader=loader)}
        for mod_id in row["modIds"]:
            if mod_id not in records or not records[mod_id]["isLoadedMod"]:
                raise ValueError("Selected runtime-source mod ID is absent: " + mod_id)
            mods[mod_id] = records[mod_id]
        hashes[row["cachePath"]] = row["sha256"]
    return mods, hashes


def read_native_mods(pack, runtime, shipped):
    descriptor = runtime["nativeRuntime"]
    if runtime.get("schemaVersion") != 2 or "loader" in runtime or not isinstance(descriptor, dict):
        raise ValueError("Invalid native runtime descriptor")

    native_dir = pack / "native"
    if not native_dir.is_dir() or native_dir.is_symlink() or native_dir.is_junction():
        raise ValueError("Missing or linked native runtime")
    files = {}
    paths = set()
    for root, dirs, names in native_dir.walk():
        for name in dirs + names:
            path = root / name
            if path.is_symlink() or path.is_junction():
                raise ValueError("Linked native runtime entry: " + str(path.relative_to(pack)))
        for name in names:
            path = root / name
            rel = path.relative_to(pack).as_posix()
            if not path.is_file() or rel.casefold() in paths:
                raise ValueError("Invalid or duplicate native runtime file: " + rel)
            paths.add(rel.casefold())
            with path.open("rb") as stream:
                files[rel] = hashlib.file_digest(stream, "sha256").hexdigest()
    if not isinstance(descriptor.get("executable"), str) or descriptor["executable"] not in files:
        raise ValueError("Native executable is absent from the shipped runtime")
    if shipped is not None and set(files) != {rel for rel in shipped if rel.startswith("native/")}:
        raise ValueError("Native runtime files differ from the shipped payload")

    inventory_path = pack / "library/loaded-mods.json"
    if not inventory_path.is_file():
        raise ValueError("Native runtime requires an actual loaded-mod inventory")
    inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))
    if (not isinstance(inventory, dict) or inventory.get("schemaVersion") != 1 or
            inventory.get("minecraftVersion") != runtime["minecraftVersion"] or
            inventory.get("nativeRuntime") != descriptor or inventory.get("nativeFiles") != files or
            not isinstance(inventory.get("mods"), list)):
        raise ValueError("Actual native mod inventory does not match the shipped runtime")

    mods = {}
    for row in inventory["mods"]:
        if not isinstance(row, dict):
            raise ValueError("Invalid native mod inventory entry")
        mod_id, name, version, source = (row.get(key) for key in ("id", "name", "version", "source"))
        if (not isinstance(mod_id, str) or not re.fullmatch(r"[a-z0-9][a-z0-9_.-]{0,127}", mod_id) or
                mod_id in mods or not isinstance(name, str) or not name.strip() or len(name) > 256 or
                not isinstance(version, str) or not version.strip() or len(version) > 128 or
                not isinstance(source, str) or source not in files):
            raise ValueError("Invalid native mod ID, metadata or verified source: " + str(mod_id))
        mods[mod_id] = {"id": mod_id, "name": name, "version": version, "isLoadedMod": True}
    return mods


def read_pack_mods(pack, shipped=None):
    """Read metadata, optionally pinned to an exact-hash actual loader inventory.

    JarJar can select different versions of the same dependency, including a
    version that is a Java library rather than a mod. A recorded ModList from
    the tested client resolves that ambiguity without reimplementing the loader.
    """
    pack = pathlib.Path(pack)
    runtime = json.loads((pack / "portable-pack.json").read_text(encoding="utf-8-sig"))
    if "nativeRuntime" in runtime:
        return read_native_mods(pack, runtime, shipped)
    jars = (sorted((pack / "mods").glob("*.jar")) if shipped is None else
            sorted(pack / rel for rel in shipped if rel.startswith("mods/") and rel.lower().endswith(".jar")))
    mods = {}
    for jar in jars:
        for mod in read_mods(jar.read_bytes(), jar.name, loader=runtime["loader"]["type"].lower()):
            existing = mods.get(mod["id"])
            if existing is None or mod["_depth"] < existing["_depth"]:
                mods[mod["id"]] = mod
    provided, runtime_hashes = read_runtime_mods(pack, runtime["loader"]["type"].lower())
    for mod_id, mod in provided.items():
        if mod_id not in mods:
            mods[mod_id] = mod
    for mod in mods.values():
        mod.pop("_depth")
    inventory_path = pack / "library/loaded-mods.json"
    if inventory_path.exists():
        inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))
        actual_hashes = {jar.relative_to(pack).as_posix(): hashlib.sha256(jar.read_bytes()).hexdigest()
                         for jar in jars}
        if (inventory.get("schemaVersion") != 1 or inventory.get("sourceJars") != actual_hashes or
                inventory.get("runtimeSources", {}) != runtime_hashes or
                inventory.get("minecraftVersion") != runtime["minecraftVersion"] or
                inventory.get("loader") != runtime["loader"]):
            raise ValueError("Actual loader inventory does not match the shipped JAR hashes")
        actual_mods = {}
        for loaded in inventory.get("mods", []):
            mod_id = loaded.get("id")
            if mod_id in ("minecraft", "forge", "neoforge") or (
                    runtime["loader"]["type"].lower() in ("fabric", "quilt", "legacyfabric") and mod_id in ("fabricloader", "quilt_loader", "java")):
                continue
            if mod_id not in mods or not mods[mod_id]["isLoadedMod"] or mod_id in actual_mods:
                raise ValueError("Unexpected/duplicate actual loaded mod ID: " + str(mod_id))
            actual_mods[mod_id] = {**mods[mod_id], "name": loaded["name"], "version": loaded["version"]}
        if not actual_mods and any(mod["isLoadedMod"] for mod in mods.values()):
            raise ValueError("Actual loader inventory is empty")
        mods = {**{key: mod for key, mod in mods.items() if not mod["isLoadedMod"]}, **actual_mods}
    return mods


def is_https_project_page(value):
    parsed = urlsplit(value) if isinstance(value, str) else None
    return (parsed and parsed.scheme == "https" and parsed.hostname and
            not parsed.username and not parsed.password and len(value) <= 2048)


def native_project_metadata(authored):
    project = authored.get("nativeProject")
    if not isinstance(project, dict):
        raise ValueError("Native library requires official project metadata")
    name, version, revision, website = (project.get(field) for field in ("name", "version", "revision", "websiteUrl"))
    if (not all(isinstance(label, str) and label.strip() and len(label) <= 80 and
                not any(ord(c) < 32 or 127 <= ord(c) < 160 for c in label)
                for label in (name, version)) or
            not isinstance(revision, str) or not re.fullmatch(r"[a-f0-9]{40}", revision) or
            (website is not None and not is_https_project_page(website))):
        raise ValueError("Invalid native project name, version, revision or official HTTPS page")
    metadata = {"name": name, "version": version, "revision": revision}
    if website is not None:
        metadata["websiteUrl"] = website
    return metadata


def build_library(pack, revision):
    pack = pathlib.Path(pack)
    authored = json.loads((pack / "library/content.json").read_text(encoding="utf-8-sig"))
    runtime = json.loads((pack / "portable-pack.json").read_text(encoding="utf-8-sig"))
    pages_path = pack / "library/mod-pages.json"
    pages = json.loads(pages_path.read_text(encoding="utf-8-sig")) if pages_path.exists() else {}
    mods = read_pack_mods(pack)
    presentation = {}
    if "nativeRuntime" in runtime:
        loader = "native"
        presentation["nativeProject"] = native_project_metadata(authored)
        if "showNativeProject" in authored:
            if not isinstance(authored["showNativeProject"], bool):
                raise ValueError("showNativeProject must be a boolean")
            presentation["showNativeProject"] = authored["showNativeProject"]
    else:
        loader = runtime["loader"]["type"]
    if "modsTitle" in authored:
        title = authored["modsTitle"]
        if not isinstance(title, str) or not title.strip() or len(title) > 120:
            raise ValueError("Invalid mod list title")
        presentation["modsTitle"] = title
    for field in ("baseModIds", "visibleModIds"):
        if field in authored:
            ids = authored[field]
            if (not isinstance(ids, list) or len(ids) > 10000 or
                    any(not isinstance(mid, str) or not mid.strip() for mid in ids) or len(set(ids)) != len(ids)):
                raise ValueError("Invalid/duplicate " + field)
            presentation[field] = sorted(ids)
    visible = presentation.get("visibleModIds")
    if visible is not None:
        # A manually selected list is presentation only. Its overall counter
        # must still describe the actual loader, including selected JarJar mods.
        if not (pack / "library/loaded-mods.json").exists():
            raise ValueError("A filtered mod list requires an exact-hash actual loader inventory")
        base_ids = set(presentation.get("baseModIds", []))
        for mod_id in visible:
            if mod_id in base_ids:
                raise ValueError("Base mod cannot be presented as an addition: " + mod_id)
            if mod_id not in mods or not mods[mod_id]["isLoadedMod"]:
                raise ValueError("Visible mod is not actually loaded: " + mod_id)
    libraries = LIBRARIES | set(authored.get("libraryIds", []))
    parents = PARENTS | authored.get("parents", {})
    for mod in mods.values():
        entry = pages.get(mod["id"])
        page = entry.get("url") if isinstance(entry, dict) else entry
        available = entry.get("available", True) if isinstance(entry, dict) else True
        if not is_https_project_page(page):
            raise ValueError("Missing/invalid official project page for mod: " + mod["id"])
        if not isinstance(available, bool):
            raise ValueError("Invalid project page availability for mod: " + mod["id"])
        mod["websiteUrl"] = page
        mod["websiteAvailable"] = available
        parent = parents.get(mod["id"])
        mod["parentId"] = parent if parent in mods and parent != mod["id"] and parent not in libraries else None
        mod["isLibrary"] = mod["id"] in libraries or not mod["isLoadedMod"]
        if visible is not None and mod["id"] in visible and mod["isLibrary"]:
            raise ValueError("Auxiliary library cannot be presented as an addition: " + mod["id"])
    # Fail publication rather than handing the UI a cyclic tree.
    for mod in mods.values():
        seen = {mod["id"]}
        parent = mod["parentId"]
        while parent:
            if parent in seen:
                raise ValueError("Cyclic addon relation: " + mod["id"])
            seen.add(parent)
            parent = mods[parent]["parentId"]
    presentation.update(group_presentation(authored, mods))
    doc = {"schemaVersion": 1, "id": authored["id"], "relativePath": authored.get("relativePath", pack.name),
           "name": authored["name"], "summary": authored["summary"], "description": authored["description"],
           "minecraftVersion": runtime["minecraftVersion"], "loader": loader,
           "revision": revision, **presentation,
           "mods": sorted(mods.values(), key=lambda m: (m["name"].casefold(), m["id"]))}
    data = json.dumps(doc, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    preview = (pack / "library/preview.jpg").read_bytes()
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, payload in (("library.json", data), ("preview.jpg", preview)):
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.create_system = 0
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
