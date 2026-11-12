"""Build immutable release assets from a verified CurseForge stage and source commit."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory
import zipfile

import prepare
import release_overlay

sys.path.insert(0, str(prepare.ROOT / "tools"))
from pack_library import stage_library, verify_library


MOD_BUCKETS = 256
ZIP_EPOCH = (1980, 1, 1, 0, 0, 0)
LIBRARY_LIMIT = 8 * 1024 * 1024
LIBRARY_INPUTS = ("library/content.json", "library/mod-pages.json", "library/loaded-mods.json", "library/preview.jpg")
LAUNCHER_STATE = {"portable-pack-source.json", ".portable-pack-sync.json", ".portable-pack-apply.json",
                  ".portable-instance.json"}


def git(pack_source, *args):
    result = subprocess.run(["git", "-C", str(pack_source), *args], capture_output=True, check=False)
    prepare.require(result.returncode == 0, "Cannot read the requested pack source commit: " + " ".join(args))
    return result.stdout


def check_source(pack_source, revision, pack_id, policy_files):
    prepare.require(isinstance(revision, str) and re.fullmatch("[0-9a-f]{40}", revision), "Use a full Git source revision")
    prepare.require(isinstance(pack_id, str) and re.fullmatch(r"minecraft-[1-9][0-9]*", pack_id), "Use a numbered minecraft-N ID")
    prepare.no_links(pack_source)
    git_root = Path(git(pack_source, "rev-parse", "--show-toplevel").decode("utf-8").strip()).resolve()
    prepare.require(git_root == pack_source.resolve(), "Pack source must be a Git repository root")
    timestamp = git(pack_source, "show", "-s", "--format=%ct", revision + "^{commit}").strip()
    required = [release_overlay.CLIENT_LOCK, release_overlay.COMPLETE_LOCK, release_overlay.DESCRIPTOR,
                "tools/overlay.json", *LIBRARY_INPUTS]
    if release_overlay.OWNERSHIP in policy_files:
        required.append(release_overlay.OWNERSHIP)
    required.extend(sorted(prepare.LOGGING_FILES & policy_files.keys()))
    runtime_sources = "library/runtime-sources.json"
    if (pack_source / runtime_sources).exists():
        required.append(runtime_sources)
    for relative in required:
        path = pack_source / relative
        prepare.no_links(path)
        prepare.require(path.is_file(), f"Missing required pack source file: {relative}")
        blob = f"{revision}:{relative}"
        size = path.stat().st_size
        prepare.require(0 < size <= prepare.MAX_JSON and int(git(pack_source, "cat-file", "-s", blob)) == size and
                        path.read_bytes() == git(pack_source, "cat-file", "blob", blob),
                        f"Pack source file differs from the requested commit: {relative}")
    authored = prepare.read_local_json(pack_source / "library/content.json")
    prepare.require(isinstance(authored, dict) and authored.get("id") == pack_id and
                    all(isinstance(authored.get(field), str) for field in ("name", "summary", "description")) and
                    authored["name"].strip() and authored["description"].strip(),
                    "Library metadata must match the numbered pack ID and contain authored text")
    prepare.safe_path(authored.get("relativePath"))
    preview = (pack_source / "library/preview.jpg").read_bytes()
    prepare.require(0 < len(preview) <= LIBRARY_LIMIT and preview.startswith(b"\xff\xd8\xff") and
                    preview.endswith(b"\xff\xd9"), "Library preview must be a bounded JPEG")
    return datetime.fromtimestamp(int(timestamp), timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def copy_pin(game, relative, pin, output):
    source = game.joinpath(*prepare.safe_path(relative).parts)
    prepare.no_links(source)
    with source.open("rb") as stream:
        prepare.exact_bytes(stream, pin["sizeBytes"], output, {"sha256": pin["sha256"]})


def asset_label(value):
    return re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-")[:80] or "asset"


def build_assets(game, asset_dir, payload):
    assets = []
    groups = {}
    for relative in sorted(payload):
        root, separator, _ = relative.partition("/")
        if not separator:
            pin = payload[relative]
            name = f"c-{pin['sha256'][:12]}-{asset_label(relative)}"
            with (asset_dir / name).open("xb") as output:
                copy_pin(game, relative, pin, output)
            assets.append({"name": name, "kind": "jar", **pin, "covers": [relative]})
            continue
        label, compression = root + ".zip", zipfile.ZIP_DEFLATED
        if root == "mods":
            bucket = int.from_bytes(hashlib.sha256(relative.encode("utf-8")).digest()[:4], "big") % MOD_BUCKETS
            label, compression = f"mods.c{bucket:03}.zip", zipfile.ZIP_STORED
        groups.setdefault((label, compression), []).append(relative)

    for (label, compression), paths in sorted(groups.items()):
        pending = asset_dir / ".asset.zip.tmp"
        with zipfile.ZipFile(pending, "x", compression=compression) as archive:
            for relative in paths:
                info = zipfile.ZipInfo(relative, ZIP_EPOCH)
                info.compress_type = compression
                info.create_system = 3
                with archive.open(info, "w") as output:
                    copy_pin(game, relative, payload[relative], output)
                info.external_attr = 0
        size = pending.stat().st_size
        prepare.require(0 < size <= prepare.MAX_FILE, f"Release asset exceeds the file size limit: {label}")
        with pending.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        name = f"r-{digest[:12]}-{asset_label(label)}"
        prepare.require(not (asset_dir / name).exists(), f"Release asset name collision: {name}")
        pending.rename(asset_dir / name)
        assets.append({"name": name, "kind": "zipRoot", "sizeBytes": size, "sha256": digest, "covers": paths})

    prepare.require(sorted(path for asset in assets for path in asset["covers"]) == sorted(payload),
                    "Release assets do not cover the verified payload exactly once")
    return sorted(assets, key=lambda asset: asset["name"])


def build_release(pack_source, stage, out, revision, pack_id, include_optional=()):
    prepare.no_links(out)
    prepare.require(out.parent.is_dir() and not out.exists(), "Use a new release output directory")
    client = release_overlay.read_lock(pack_source / release_overlay.CLIENT_LOCK, "client-only")
    author = release_overlay.read_lock(pack_source / release_overlay.COMPLETE_LOCK, "complete")
    release_overlay.same_author(client, author)
    policy_files, excluded = release_overlay.verify_stage(stage, pack_source, author)
    generated_at = check_source(pack_source, revision, pack_id, policy_files)
    inventory, required = release_overlay.payload_inventory(author, policy_files, excluded)
    optional = set(include_optional)
    prepare.require(optional <= inventory.keys() - required, "Optional selections must be exact optional author paths")
    payload = {path: inventory[path] for path in sorted(required | optional)}
    for path in payload:
        prepare.require(len(path.encode("utf-16-le")) <= 1024 and path.casefold() not in LAUNCHER_STATE and
                        path.partition("/")[0].casefold() != "launcher-runtime",
                        f"Verified author path is incompatible with the launcher manifest: {path}")
    game = prepare.native_path(stage / "game")
    with TemporaryDirectory(prefix=".curseforge-release-", dir=out.parent) as temporary:
        pending = Path(temporary) / "release"
        library = stage_library(game, pending, revision, metadata_root=pack_source, shipped=payload)
        verify_library(pending, library)
        library_path = pending / "assets" / library["name"]
        with zipfile.ZipFile(library_path) as archive:
            prepare.require(library["sizeBytes"] <= LIBRARY_LIMIT and
                            all(entry.file_size <= LIBRARY_LIMIT for entry in archive.infolist()),
                            "Library exceeds the launcher bundle limit")
            catalog = prepare.decode_json(archive.read("library.json"))
        mod_count = sum(mod["isLoadedMod"] for mod in catalog["mods"])
        prepare.require(mod_count > 0, "Library requires actual loaded author mods")
        manifest = {
            "schemaVersion": 2, "packId": pack_id, "revision": revision, "generatedAtUtc": generated_at,
            "minecraftVersion": author["pack"]["minecraftVersion"], "loader": author["pack"]["loader"],
            "modCount": mod_count, "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE],
            "files": [{"path": path, **pin} for path, pin in payload.items()],
            "assets": build_assets(game, pending / "assets", payload), "library": library,
        }
        release_overlay.verify_manifest(manifest, policy_files, author, excluded)
        prepare.write_json(pending / "pack-manifest.json", manifest)
        pending.rename(out)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack-source", type=Path, required=True)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--id", required=True)
    parser.add_argument("--include-optional", action="append", default=[], metavar="AUTHOR-PATH")
    args = parser.parse_args()
    try:
        out = prepare.check_output(args.output)
        manifest = build_release(args.pack_source, args.stage, out, args.revision, args.id, args.include_optional)
        print(f"Staged {len(manifest['files'])} files, {len(manifest['assets'])} game assets and library: {out}")
    except (AssertionError, OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
