"""Bind a tested client's actual ModList to an exact-hash library inventory.

Use a fresh observations.jsonl from Atm9OriginalQa after the native title menu
has loaded. Both directories must contain exactly the same author JARs, with only
explicit exact-hash shared infrastructure pins permitted in the tested game.
No personal paths or raw game logs are saved in metadata.
"""
import argparse
import hashlib
import json
import pathlib
import re

from pack_library import read_mods


def jar_hashes(pack):
    return {jar.relative_to(pack).as_posix(): hashlib.sha256(jar.read_bytes()).hexdigest()
            for jar in sorted((pack / "mods").rglob("*.jar"))}


def managed_infrastructure(game, document, author_hashes, author_candidates):
    """Verify every explicitly excluded shared component; never ignore extras."""
    if document is None:
        return [], {}
    if document.get("schemaVersion") != 1 or not isinstance(document.get("files"), list):
        raise ValueError("Invalid managed infrastructure pin schema")
    files, ids, seen = [], {}, set()
    for row in document["files"]:
        relative = row.get("path", "")
        if (not isinstance(relative, str) or not re.fullmatch(r"mods/[^/\\:]+\.jar", relative, re.IGNORECASE)
                or any(part in ("", ".", "..") for part in relative.split("/"))):
            raise ValueError("Unsafe managed infrastructure path")
        if relative.casefold() in seen or relative in author_hashes:
            raise ValueError("Managed infrastructure path overlaps author content or another pin: " + relative)
        seen.add(relative.casefold())
        digest = row.get("sha256", "")
        size = row.get("sizeBytes")
        version = row.get("version")
        mod_ids = row.get("modIds")
        if (not isinstance(digest, str) or not re.fullmatch(r"[a-f0-9]{64}", digest)
                or not isinstance(size, int) or isinstance(size, bool) or size <= 0
                or not isinstance(version, str) or not version
                or not isinstance(mod_ids, list) or not mod_ids
                or any(not isinstance(x, str) or not re.fullmatch(r"[a-z][a-z0-9_]+", x) for x in mod_ids)
                or len(set(mod_ids)) != len(mod_ids)):
            raise ValueError("Invalid managed infrastructure size/hash/IDs/version pin")
        if set(mod_ids) & (set(author_candidates) | set(ids)):
            raise ValueError("Managed infrastructure mod IDs overlap author content or another pin")
        jar = game / relative
        if (jar.is_symlink() or not jar.is_file() or jar.stat().st_size != size
                or hashlib.sha256(jar.read_bytes()).hexdigest() != digest):
            raise ValueError("Managed infrastructure size/SHA256 differs from its pin: " + relative)
        declared = read_mods(jar.read_bytes(), jar.name, loader="forge")
        top = [mod for mod in declared if mod["isLoadedMod"] and mod["_depth"] == 0]
        if {mod["id"] for mod in top} != set(mod_ids):
            raise ValueError("Managed infrastructure IDs differ from actual JAR metadata: " + relative)
        if any(mod["version"] != version for mod in top):
            raise ValueError("Managed infrastructure version differs from actual JAR metadata: " + relative)
        files.append({"path": relative, "sizeBytes": size, "sha256": digest,
                      "modIds": list(mod_ids), "version": version})
        ids.update({mod_id: version for mod_id in mod_ids})
    return files, ids


def record_inventory(pack, game, observations, infrastructure=None):
    expected = jar_hashes(pack)
    if not expected:
        raise ValueError("The author pack has no JAR files")
    runtime = json.loads((pack / "portable-pack.json").read_text(encoding="utf-8-sig"))
    candidates = {}
    for jar in sorted((pack / "mods").glob("*.jar")):
        for mod in read_mods(jar.read_bytes(), jar.name, loader=runtime["loader"]["type"].lower()):
            if mod["isLoadedMod"]:
                candidates.setdefault(mod["id"], mod)
    excluded_files, excluded_ids = managed_infrastructure(game, infrastructure, expected, candidates)
    tested_expected = {**expected, **{row["path"]: row["sha256"] for row in excluded_files}}
    if jar_hashes(game) != tested_expected:
        raise ValueError("Tested client JARs differ from author content plus exact managed infrastructure pins")
    payload = observations.read_bytes()
    rows = [json.loads(line) for line in payload.decode("utf-8-sig").splitlines() if line.strip()]
    snapshots = [row for row in rows if row.get("kind") == "loaded_mod_ids"]
    if not snapshots or any(row.get("ids") != snapshots[-1].get("ids") for row in snapshots):
        raise ValueError("Missing or inconsistent actual ModList observations")
    observed = snapshots[-1]
    loaded = observed.get("ids", {})
    if observed.get("count") != len(loaded):
        raise ValueError("Actual ModList count does not match its IDs")
    platform = runtime["loader"]["type"].lower()
    if platform in ("forge", "neoforge") and loaded.get(platform) != runtime["loader"]["version"]:
        raise ValueError("Actual loaded platform version differs from the author pack")
    if loaded.get("minecraft") != runtime["minecraftVersion"]:
        raise ValueError("Actual loaded Minecraft version differs from the author pack")
    for mod_id, version in excluded_ids.items():
        if loaded.get(mod_id) != version:
            raise ValueError("Actual managed infrastructure ID/version differs from its pin: " + mod_id)
    actual = []
    for mod_id, version in sorted(loaded.items()):
        if mod_id in ("minecraft", "forge", "neoforge"):
            continue
        if mod_id in excluded_ids:
            continue
        if mod_id not in candidates:
            raise ValueError("Actual loaded ID missing from shipped metadata: " + mod_id)
        actual.append({"id": mod_id, "name": candidates[mod_id]["name"], "version": str(version)})
    if not actual:
        raise ValueError("Actual ModList has no pack mods")
    if infrastructure is not None and set(loaded) != {mod["id"] for mod in actual} | set(excluded_ids) | {"minecraft", platform}:
        raise ValueError("Actual all-ID set is not exactly pack IDs plus pinned infrastructure and Minecraft/loader")
    inventory = {"schemaVersion": 1, "minecraftVersion": runtime["minecraftVersion"], "loader": runtime["loader"],
                 "evidenceUtc": observed.get("utc"),
                 "observationSha256": hashlib.sha256(payload).hexdigest(),
                 "sourceJars": expected, "mods": actual}
    if infrastructure is not None:
        inventory["managedInfrastructure"] = {
            "excludedFromPackCount": True,
            "pinSha256": hashlib.sha256(json.dumps(infrastructure, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
            "files": excluded_files,
            "actualLoadedIds": excluded_ids,
            "actualAllLoadedIdCount": len(loaded),
            "actualPackModIdCount": len(actual),
            "platformIds": {key: loaded[key] for key in ("minecraft", "forge", "neoforge") if key in loaded},
            "unknownExtraJarsIgnored": False,
        }
    target = pack / "library/loaded-mods.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return len(actual)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack", required=True, type=pathlib.Path)
    parser.add_argument("--game-directory", required=True, type=pathlib.Path)
    parser.add_argument("--observations", required=True, type=pathlib.Path)
    parser.add_argument("--managed-infrastructure", type=pathlib.Path,
                        help="Exact path/size/SHA256/modIds/version pins for shared launcher components")
    args = parser.parse_args()
    infrastructure = (json.loads(args.managed_infrastructure.read_text(encoding="utf-8-sig"))
                      if args.managed_infrastructure else None)
    count = record_inventory(args.pack.resolve(), args.game_directory.resolve(), args.observations, infrastructure)
    print(json.dumps({"loadedModCount": count, "output": "library/loaded-mods.json"}))


if __name__ == "__main__":
    main()
