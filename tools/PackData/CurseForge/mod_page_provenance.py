"""Find CurseForge project-page candidates from the pack's exact JARs and locks."""

from __future__ import annotations

import argparse
from io import BytesIO
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
import zipfile

import prepare
import release_overlay

sys.path.insert(0, str(prepare.ROOT / "tools"))
from pack_library import read_mods


PLATFORM_IDS = {"minecraft", "forge", "neoforge", "mcp", "fml", "java", "fabricloader", "quilt_loader"}


def official_project_urls(document):
    prepare.require(isinstance(document, dict) and isinstance(document.get("data"), list),
                    "Expected an official CurseForge Get Mods response")
    urls = {}
    seen = set()
    for row in document["data"]:
        prepare.require(isinstance(row, dict) and prepare.positive(row.get("id")) and row["id"] not in seen,
                        "Invalid or repeated CurseForge project ID")
        project_id = row["id"]
        seen.add(project_id)
        links = row.get("links")
        url = links.get("websiteUrl") if isinstance(links, dict) else None
        if not isinstance(url, str) or row.get("isAvailable") is False:
            continue
        parsed = urlsplit(url)
        path = re.fullmatch(r"/minecraft/mc-mods/([a-z0-9][a-z0-9_-]*)/?", parsed.path, re.IGNORECASE)
        if (parsed.scheme == "https" and parsed.netloc == prepare.WEBSITE and not parsed.query and
                not parsed.fragment and path and isinstance(row.get("slug"), str) and
                row["slug"].casefold() == path[1].casefold()):
            urls[project_id] = url
    return urls


def locked_mod_jars(author, recipe):
    keys = prepare.overlay_dependency_keys(recipe, author)
    excluded = {row["path"] for row in prepare.excluded_dependency_files(keys, author["files"])}
    paths = {}
    jars = {}
    for row in [*author["files"], *author["overrides"]]:
        path = prepare.safe_path(row["path"])
        if path.is_relative_to("mods") and path.suffix.lower() == ".jar" and row["path"] not in excluded:
            prepare.reserve_path(paths, path)
            jars[row["path"]] = row
    return jars


def inventory_mods(inventory, lock_sha, jars):
    if inventory is None:
        return None
    prepare.require(isinstance(inventory, dict) and inventory.get("schemaVersion") == 1 and
                    isinstance(inventory.get("mods"), list) and
                    (inventory.get("upstreamLockSha256") == lock_sha or
                     inventory.get("sourceJars") == {path: row["sha256"] for path, row in jars.items()}),
                    "Native mod inventory is not bound to these exact author JARs")
    mods = {}
    for row in inventory["mods"]:
        prepare.require(isinstance(row, dict) and isinstance(row.get("id"), str) and row["id"] and
                        row["id"] not in mods, "Invalid or repeated native mod ID")
        mods[row["id"]] = row.get("source")
    return mods


def legacy_declarations(data):
    with zipfile.ZipFile(BytesIO(data)) as archive:
        if "mcmod.info" not in archive.namelist():
            return []
        with archive.open("mcmod.info") as stream:
            metadata = prepare.decode_json(stream.read(prepare.MAX_JSON + 1))
    if isinstance(metadata, dict):
        metadata = metadata.get("modList")
    prepare.require(isinstance(metadata, list), "Invalid legacy mod metadata")
    return [{"id": row["modid"], "_depth": 0, "isLoadedMod": True} for row in metadata
            if isinstance(row, dict) and isinstance(row.get("modid"), str) and row["modid"]]


def find_candidates(game, jars, loader, legacy, observed, urls):
    declarations = {}
    unclassified_jars = []
    for path, row in jars.items():
        target = game.joinpath(*prepare.safe_path(path).parts)
        prepare.no_links(target)
        prepare.require(target.is_file() and target.stat().st_size == row["sizeBytes"],
                        f"Missing or changed pinned mod JAR: {path}")
        data = target.read_bytes()
        prepare.require(prepare.fingerprint(data) == {key: row[key] for key in ("sizeBytes", "sha256")},
                        f"Missing or changed pinned mod JAR: {path}")
        try:
            metadata = legacy_declarations(data) if legacy else read_mods(data, target.name, loader=loader)
        except (KeyError, TypeError, ValueError, zipfile.BadZipFile):
            metadata = []
        if not any(mod["isLoadedMod"] for mod in metadata):
            unclassified_jars.append(path)
        for mod in metadata:
            if mod["isLoadedMod"]:
                declarations.setdefault(mod["id"], []).append((path, mod["_depth"]))

    resolved, unresolved = {}, {}
    for mod_id in sorted(declarations if observed is None else observed):
        if mod_id.casefold() in PLATFORM_IDS:
            continue
        matches = declarations.get(mod_id, [])
        paths = sorted({path for path, _ in matches})
        if len(matches) != 1 or matches[0][1] != 0:
            unresolved[mod_id] = {"reason": "missing, nested or ambiguous declaration", "candidateJars": paths}
            continue
        path = paths[0]
        row = jars[path]
        evidence = {"path": path, "sha256": row["sha256"]}
        if "projectId" in row:
            evidence.update(projectId=row["projectId"], fileId=row["fileId"])
        if legacy and (observed is None or observed[mod_id] != path):
            unresolved[mod_id] = {"reason": "legacy runtime origin required", **evidence}
        elif observed is not None and observed[mod_id] not in (None, path):
            unresolved[mod_id] = {"reason": "runtime origin differs from the declaration", **evidence}
        elif "projectId" not in row:
            unresolved[mod_id] = {"reason": "bundled JAR requires source review", **evidence}
        elif row["projectId"] not in urls:
            unresolved[mod_id] = {"reason": "official project record required", **evidence}
        else:
            resolved[mod_id] = {**evidence, "websiteUrl": urls[row["projectId"]]}
    return resolved, unresolved, sorted(unclassified_jars)


def audit(pack_source, game, project_records, inventory):
    client = release_overlay.read_lock(pack_source / release_overlay.CLIENT_LOCK, "client-only")
    lock_path = pack_source / release_overlay.COMPLETE_LOCK
    author = release_overlay.read_lock(lock_path, "complete")
    release_overlay.same_author(client, author)
    recipe, _, descriptor, _ = release_overlay.source_policy(pack_source, author)
    lock_sha = prepare.fingerprint(prepare.overlay_bytes(lock_path))["sha256"]
    jars = locked_mod_jars(author, recipe)
    observed = inventory_mods(inventory, lock_sha, jars)
    loader = descriptor["loader"]["type"]
    minecraft = descriptor["minecraftVersion"]
    legacy = loader == "forge" and re.fullmatch(r"1\.(?:\d|1[0-2])(?:\.\d+)?", minecraft) is not None
    urls = official_project_urls(project_records) if project_records is not None else {}
    candidates, unresolved, unclassified = find_candidates(game, jars, loader, legacy, observed, urls)
    return {"schemaVersion": 1, "upstreamLockSha256": lock_sha, "minecraftVersion": minecraft,
            "loader": descriptor["loader"], "nativeInventory": observed is not None,
            "candidates": candidates, "unresolved": unresolved, "unclassifiedJars": unclassified}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack-source", type=Path, required=True)
    parser.add_argument("--game", type=Path, required=True, help="Existing game directory with exact author JARs")
    parser.add_argument("--project-records", type=Path, help="Saved official CurseForge Get Mods response")
    parser.add_argument("--inventory", type=Path, help="Actual loaded-mod inventory bound to the author JARs")
    args = parser.parse_args()
    try:
        projects = prepare.read_local_json(args.project_records) if args.project_records else None
        inventory = prepare.read_local_json(args.inventory) if args.inventory else None
        print(json.dumps(audit(args.pack_source, args.game, projects, inventory), ensure_ascii=False, indent=2))
    except (OSError, KeyError, TypeError, ValueError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
