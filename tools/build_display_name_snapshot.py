"""Extract the tiny offline name snapshot from embedded or hash-verified libraries.

This is a derived copy of library.json:name, never another editable name table.
It contains no images/mod trees and does not determine catalog membership.
"""
import argparse
import base64
import hashlib
import io
import json
import pathlib
import zipfile


def read_metadata(payload):
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        if set(archive.namelist()) != {"library.json", "preview.jpg"}:
            raise ValueError("Unexpected library bundle contents")
        metadata = json.loads(archive.read("library.json"))
    for field in ("id", "relativePath", "name", "revision"):
        if not isinstance(metadata.get(field), str) or not metadata[field].strip():
            raise ValueError("Missing library " + field)
    return {field: metadata[field] for field in ("id", "relativePath", "name", "revision")} | {
        "bundleSha256": hashlib.sha256(payload).hexdigest()
    }


def read_verified_cache(path):
    cache = json.loads(path.read_text(encoding="utf-8-sig"))
    payload = base64.b64decode(cache["bundle"], validate=True)
    manifest = json.loads(cache["manifest"])
    descriptor = manifest["library"]
    if len(payload) != descriptor["sizeBytes"] or hashlib.sha256(payload).hexdigest() != descriptor["sha256"].lower():
        raise ValueError("Library bundle does not match the verified manifest")
    metadata = read_metadata(payload)
    if metadata["revision"] != descriptor["revision"]:
        raise ValueError("Library revision does not match the verified manifest")
    if any(metadata[field] != cache["entry"][field] for field in ("id", "relativePath")):
        raise ValueError("Library identity does not match its catalog entry")
    return metadata


def main():
    repository = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seed-directory", type=pathlib.Path, default=repository / "Program/LibrarySeed")
    parser.add_argument("--verified-cache", type=pathlib.Path, action="append", default=[])
    parser.add_argument("--output", type=pathlib.Path, default=repository / "Program/Resources/pack-display-names.json")
    args = parser.parse_args()
    names = {}
    for bundle in sorted(args.seed_directory.glob("*.zip")):
        metadata = read_metadata(bundle.read_bytes())
        names[metadata["id"]] = metadata
    for cache in args.verified_cache:
        metadata = read_verified_cache(cache)
        names[metadata["id"]] = metadata
    spellings = {}
    for metadata in names.values():
        spelling = metadata["name"]
        previous = spellings.setdefault(spelling.casefold(), spelling)
        if previous != spelling:
            raise ValueError("Ambiguous display-name casing")
    snapshot = {"schemaVersion": 1, "packs": sorted(names.values(), key=lambda entry: entry["id"])}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Wrote " + str(len(names)) + " verified display-name fallbacks")


if __name__ == "__main__":
    main()
