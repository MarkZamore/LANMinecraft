"""Rebuild the embedded library catalogue from current pack jars and metadata."""
import argparse
import json
import pathlib
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
from pack_library import stage_library, verify_library


def main():
    repository = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packs-root", required=True, type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=repository / "Program/LibrarySeed")
    parser.add_argument("--staging", type=pathlib.Path, default=repository / "artifacts/library-seeds")
    args = parser.parse_args()
    index = json.loads((repository / "tools/pack-catalog.json").read_text(encoding="utf-8-sig"))
    # Generate and verify every pack before replacing any embedded seed.
    verified = []
    for entry in index["packs"]:
        pack = args.packs_root / entry.get("sourcePath", entry["relativePath"])
        revision = subprocess.check_output(["git", "-C", str(pack), "rev-parse", "HEAD"], text=True).strip()
        stage = args.staging / entry["id"]
        descriptor = stage_library(pack, stage, revision)
        verify_library(stage, descriptor)
        payload = stage / "assets" / descriptor["name"]
        with zipfile.ZipFile(payload) as archive:
            metadata = json.loads(archive.read("library.json"))
        if metadata["id"] != entry["id"] or metadata["relativePath"] != entry["relativePath"]:
            raise ValueError("Pack identity does not match catalogue: " + entry["id"])
        (stage / "descriptor.json").write_text(json.dumps(descriptor, indent=2) + "\n", encoding="utf-8")
        verified.append((entry["id"], payload, revision, len(metadata["mods"])))
    args.output.mkdir(parents=True, exist_ok=True)
    for pack_id, payload, revision, mods in verified:
        (args.output / (pack_id + ".zip")).write_bytes(payload.read_bytes())
        print(json.dumps({"id": pack_id, "revision": revision, "mods": mods}))


if __name__ == "__main__":
    main()
