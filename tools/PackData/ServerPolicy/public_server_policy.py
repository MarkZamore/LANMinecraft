"""Release gate for reviewed Steam-only automatic server lists; never edits a pack."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re

POLICY = "steam-only-automatic-server-list-v1"
SEEDS = {"servers.dat", "servers.dat_old"}
TEXT = {".txt", ".json", ".json5", ".toml", ".snbt", ".js", ".cfg"}


def inventory_sha256(files):
    rows, seen = [], set()
    for item in files:
        name = item["path"]
        path = PurePosixPath(name)
        key = name.casefold()
        if (not name or "\\" in name or ":" in name or path.is_absolute() or
                any(part in {"", ".", ".."} for part in name.split("/")) or key in seen):
            raise ValueError("Unsafe or duplicate delivery path: " + name)
        size, sha = item["sizeBytes"], item["sha256"]
        if type(size) is not int or size < 0 or not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise ValueError("Invalid delivery fingerprint: " + name)
        seen.add(key)
        rows.append({"path": name, "sizeBytes": size, "sha256": sha})
    raw = json.dumps(sorted(rows, key=lambda row: row["path"]), ensure_ascii=False,
                     sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def verify(pack, manifest, policy=None):
    pack = Path(pack).resolve()
    policy_path = pack / "tools/public-server-policy.json" if policy is None else Path(policy)
    data = json.loads(policy_path.read_text(encoding="utf-8-sig"))
    if data.get("schemaVersion") != 1 or data.get("policy") != POLICY:
        raise ValueError("Mandatory automatic server-list policy is missing or unsupported")
    files = manifest["files"]
    actual = inventory_sha256(files)
    if actual != data.get("reviewedInventorySha256"):
        raise ValueError("Unreviewed delivery inputs: re-audit server seeds, menus and native hooks before publication; never auto-repin")
    checked = []
    for item in files:
        relative = PurePosixPath(item["path"])
        if relative.name.casefold() in SEEDS:
            raise ValueError("A server-list seed may not be shipped: " + item["path"])
        if relative.suffix.casefold() not in TEXT or item["sizeBytes"] > 2 * 1024 * 1024:
            continue
        target = pack.joinpath(*relative.parts)
        # Do not follow a link to personal or external data, even for inspection.
        for part in (target, *target.parents):
            if part == pack.parent:
                break
            if part.is_symlink() or part.is_junction():
                raise ValueError("Policy input may not be a link: " + item["path"])
        raw = target.read_bytes()
        if len(raw) != item["sizeBytes"] or hashlib.sha256(raw).hexdigest() != item["sha256"]:
            raise ValueError("Policy input differs from the manifest: " + item["path"])
        text = raw.decode("utf-8-sig", errors="replace")
        if re.search(r"\[action_type\s*:\s*joinserver\s*\]", text, re.IGNORECASE):
            raise ValueError("Public server menu shortcut returned: " + item["path"])
        if re.search(r'AddedServerlist[^\r\n=]*=\s*true\b', text, re.IGNORECASE):
            raise ValueError("Automatic public server seeding enabled: " + item["path"])
        checked.append(item["path"])
    return {"status": "PASS", "policy": POLICY, "reviewedInventorySha256": actual,
            "files": len(files), "textFilesChecked": len(checked), "personalServerListsModified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.pack, json.loads(args.manifest.read_text(encoding="utf-8-sig"))), indent=2))


if __name__ == "__main__":
    main()
