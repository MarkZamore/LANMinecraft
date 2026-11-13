"""Append a verified public pack to the live launcher catalog."""

from __future__ import annotations

import argparse
import base64
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile

import prepare

sys.path.insert(0, str(prepare.ROOT / "tools"))
from numbered_pack_catalog import project_catalog


CATALOG = "repos/MarkZamore/LANMinecraft/contents/catalog.json"
BRANCH = "pack-catalog"


def github(*args, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    result = subprocess.run(["gh", "api", *args], input=data, stdout=subprocess.PIPE, check=True)
    return prepare.decode_json(result.stdout)


def published_entry(install, repository):
    manifest_bytes = prepare.overlay_bytes(install / "pack-manifest.json")
    manifest = prepare.decode_json(manifest_bytes)
    receipt = prepare.read_local_json(install / "public-acceptance.json")
    revision = manifest["revision"]
    files = manifest["files"]
    assets = manifest["assets"]
    library = manifest["library"]
    manifest_pin = prepare.fingerprint(manifest_bytes)
    prepare.require(receipt["outcome"] == "PASS" and receipt["revision"] == revision and
                    receipt["manifestSha256"] == manifest_pin["sha256"] and
                    receipt["librarySha256"] == library["sha256"] and receipt["installedFiles"] == len(files) and
                    receipt["installedBytes"] == sum(row["sizeBytes"] for row in files) and
                    receipt["gameAssets"] == len(assets) and
                    receipt["gameAssetBytes"] == sum(row["sizeBytes"] for row in assets),
                    "The production clean installation receipt differs from the manifest")
    library_path = prepare.safe_path(library["name"])
    prepare.require(len(library_path.parts) == 1, "Invalid library archive name")
    library_path = install / library_path.name
    library_bytes = prepare.overlay_bytes(library_path)
    prepare.require(prepare.fingerprint(library_bytes) == {key: library[key] for key in ("sizeBytes", "sha256")},
                    "Library archive differs from the verified installation")
    with zipfile.ZipFile(library_path) as archive:
        info = archive.getinfo("library.json")
        prepare.require(info.file_size <= prepare.MAX_JSON, "Oversized library metadata")
        card = prepare.decode_json(archive.read(info))
    prepare.require(card["id"] == manifest["packId"] and card["revision"] == revision and
                    card["minecraftVersion"] == manifest["minecraftVersion"], "Card differs from the verified manifest")
    path = prepare.safe_path(card["relativePath"])
    prepare.require(len(path.parts) == 1, "Expected one library installation folder")
    release = github(f"repos/MarkZamore/{repository}/releases/tags/pack-latest")
    asset_pages = github("--paginate", "--slurp",
                         f"repos/MarkZamore/{repository}/releases/{release['id']}/assets?per_page=100")
    manifest_assets = [asset for page in asset_pages for asset in page if asset["name"] == "pack-manifest.json"]
    prepare.require(len(manifest_assets) == 1, "Missing or ambiguous public manifest asset")
    public_manifest = manifest_assets[0]
    prepare.require(release["draft"] is False and release["tag_name"] == "pack-latest" and
                    public_manifest["name"] == "pack-manifest.json" and public_manifest["state"] == "uploaded" and
                    public_manifest["size"] == manifest_pin["sizeBytes"] and
                    public_manifest["digest"] == f"sha256:{manifest_pin['sha256']}",
                    "Public release changed since clean installation")
    tag = github(f"repos/MarkZamore/{repository}/git/ref/tags/pack-latest")["object"]
    prepare.require(tag["type"] == "commit" and tag["sha"] == revision, "Public tag differs from the verified source revision")
    return {"id": card["id"], "relativePath": path.name,
            "source": {"owner": "MarkZamore", "repo": repository, "tag": "pack-latest"}}


def catalog_document():
    result = github(f"{CATALOG}?ref={BRANCH}")
    prepare.require(result["encoding"] == "base64", "Unexpected GitHub catalog encoding")
    return result["sha"], prepare.decode_json(base64.b64decode(result["content"]))


def append_entry(index, entry, allocations):
    prepare.require(project_catalog(index, allocations) == index, "The existing catalog and number registry differ")
    packs = index["packs"]
    if entry in packs:
        return index
    for existing in packs:
        prepare.require(existing.get("numberedId", existing["id"]).casefold() != entry["id"].casefold() and
                        existing["relativePath"].casefold() != entry["relativePath"].casefold() and
                        existing.get("installationPath", "").casefold() != entry["relativePath"].casefold() and
                        all(path.casefold() != entry["relativePath"].casefold()
                            for path in existing.get("legacyRelativePaths", [])),
                        "The new pack identity or installation path is already in the live catalog")
    updated = {**index, "packs": [*packs, entry]}
    prepare.require(project_catalog(updated, allocations) == updated, "The new pack ID is not allocated")
    return updated


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--installation", type=Path, required=True)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        prepare.require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", args.repo), "Invalid pack repository")
        entry = published_entry(args.installation, args.repo)
        allocations = prepare.read_local_json(prepare.ROOT / "tools/numbered-pack-ids.json")
        sha, before = catalog_document()
        after = append_entry(before, entry, allocations)
        if not args.apply or after == before:
            print(json.dumps({"catalogBlob": sha, "wouldAppend": after != before, "entry": entry}, ensure_ascii=False))
            return
        payload = (json.dumps(after, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        result = github(CATALOG, "--method", "PUT", "--input", "-", body={"branch": BRANCH, "sha": sha,
                        "message": f"Add {entry['relativePath']} to the library [skip ci]",
                        "content": base64.b64encode(payload).decode("ascii")})
        verified = github(f"{CATALOG}?ref={result['commit']['sha']}")
        prepare.require(base64.b64decode(verified["content"]) == payload and
                        verified["sha"] == result["content"]["sha"], "Published catalog differs from the update")
        current_sha, current = catalog_document()
        prepare.require(current["packs"].count(entry) == 1, "The live catalog no longer includes the verified entry")
        print(json.dumps({"commit": result["commit"]["sha"], "previousCatalogBlob": sha,
                          "catalogBlob": current_sha, "entry": entry}, ensure_ascii=False))
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
