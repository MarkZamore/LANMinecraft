"""Bind a tested client's actual ModList to an exact-hash library inventory.

Use a fresh observations.jsonl from Atm9OriginalQa after the native title menu
has loaded. Both the author pack and tested game directory must contain exactly
the same author JARs. No personal paths or raw game logs are saved in metadata.
"""
import argparse
import hashlib
import json
import pathlib

from pack_library import read_mods


def jar_hashes(pack):
    return {jar.relative_to(pack).as_posix(): hashlib.sha256(jar.read_bytes()).hexdigest()
            for jar in sorted((pack / "mods").glob("*.jar"))}


def record_inventory(pack, game, observations):
    expected = jar_hashes(pack)
    if not expected or jar_hashes(game) != expected:
        raise ValueError("Tested client JARs differ from the author pack")
    payload = observations.read_bytes()
    rows = [json.loads(line) for line in payload.decode("utf-8-sig").splitlines() if line.strip()]
    snapshots = [row for row in rows if row.get("kind") == "loaded_mod_ids"]
    if not snapshots or any(row.get("ids") != snapshots[-1].get("ids") for row in snapshots):
        raise ValueError("Missing or inconsistent actual ModList observations")
    observed = snapshots[-1]
    loaded = observed.get("ids", {})
    if observed.get("count") != len(loaded):
        raise ValueError("Actual ModList count does not match its IDs")
    runtime = json.loads((pack / "portable-pack.json").read_text(encoding="utf-8-sig"))
    platform = runtime["loader"]["type"].lower()
    if platform in ("forge", "neoforge") and loaded.get(platform) != runtime["loader"]["version"]:
        raise ValueError("Actual loaded platform version differs from the author pack")
    if loaded.get("minecraft") != runtime["minecraftVersion"]:
        raise ValueError("Actual loaded Minecraft version differs from the author pack")
    candidates = {}
    for jar in sorted((pack / "mods").glob("*.jar")):
        for mod in read_mods(jar.read_bytes(), jar.name, loader=runtime["loader"]["type"].lower()):
            if mod["isLoadedMod"]:
                candidates.setdefault(mod["id"], mod)
    actual = []
    for mod_id, version in sorted(loaded.items()):
        if mod_id in ("minecraft", "forge", "neoforge"):
            continue
        if mod_id not in candidates:
            raise ValueError("Actual loaded ID missing from shipped metadata: " + mod_id)
        actual.append({"id": mod_id, "name": candidates[mod_id]["name"], "version": str(version)})
    if not actual:
        raise ValueError("Actual ModList has no pack mods")
    inventory = {"schemaVersion": 1, "minecraftVersion": runtime["minecraftVersion"], "loader": runtime["loader"],
                 "evidenceUtc": observed.get("utc"),
                 "observationSha256": hashlib.sha256(payload).hexdigest(),
                 "sourceJars": expected, "mods": actual}
    target = pack / "library/loaded-mods.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return len(actual)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack", required=True, type=pathlib.Path)
    parser.add_argument("--game-directory", required=True, type=pathlib.Path)
    parser.add_argument("--observations", required=True, type=pathlib.Path)
    args = parser.parse_args()
    count = record_inventory(args.pack.resolve(), args.game_directory.resolve(), args.observations)
    print(json.dumps({"loadedModCount": count, "output": "library/loaded-mods.json"}))


if __name__ == "__main__":
    main()
