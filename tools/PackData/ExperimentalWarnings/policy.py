"""Read-only verification of a pack-owned experimental confirmation policy."""
import argparse
import hashlib
import json
from pathlib import Path

ARTIFACT = "config/lanminecraft/experimental-world-confirmation.jar"
JVM = "launcher/jvm-args.txt"
OWNED = "launcher/pack-owned.txt"
SELECTOR = "-javaagent:" + ARTIFACT


def fingerprint(data):
    return {"sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def verify(pack, manifest=None):
    pack = Path(pack).resolve()
    receipt = json.loads((pack / "tools/experimental-warnings/policy.json").read_bytes())
    runtime = json.loads((pack / "portable-pack.json").read_bytes())
    if {key: runtime.get(key) for key in ("minecraftVersion", "loader")} != receipt["platform"]:
        raise ValueError("Review the experimental prompt on the new Minecraft/loader before publication")
    for relative, pin in receipt["files"].items():
        source = pack / receipt.get("sources", {}).get(relative, relative)
        if not source.resolve().is_relative_to(pack) or source.is_symlink() or source.is_junction():
            raise ValueError("Policy source must be an ordinary pack-owned file")
        if fingerprint(source.read_bytes()) != receipt.get("sourceFiles", {}).get(relative, pin):
            raise ValueError("Experimental confirmation policy input changed " + relative)
    jvm = (pack / receipt.get("sources", {}).get(JVM, JVM)).read_text(encoding="utf-8-sig")
    if jvm.splitlines().count(SELECTOR) != 1:
        raise ValueError("Expected exactly one experimental confirmation JVM selector")
    owned = (pack / receipt.get("sources", {}).get(OWNED, OWNED)).read_text(encoding="utf-8-sig").splitlines()
    if ARTIFACT not in owned and "config/**" not in owned and "config/lanminecraft/**" not in owned:
        raise ValueError("The pack must own its experimental confirmation artifact")
    if manifest is not None:
        files = {row["path"]: {key: row[key] for key in ("sizeBytes", "sha256")} for row in manifest["files"]}
        if any(files.get(path) != pin for path, pin in receipt["files"].items()):
            raise ValueError("The release lost the pack-owned experimental confirmation policy")
    return {"status": "PASS", "platform": receipt["platform"], "files": receipt["files"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_bytes()) if args.manifest else None
    print(json.dumps(verify(args.pack, manifest), indent=2))
