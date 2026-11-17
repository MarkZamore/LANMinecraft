"""Stage a minimal policy update against verified immutable published assets."""
from copy import deepcopy
import io
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

from policy import ARTIFACT, JVM, OWNED, SELECTOR, fingerprint


def checked_payload(asset, raw, files=None):
    if fingerprint(raw) != {key: asset[key] for key in ("sizeBytes", "sha256")}:
        raise ValueError("Published asset hash changed " + asset["name"])
    if asset.get("kind") == "jar":
        if len(asset.get("covers", [])) != 1:
            raise ValueError("A raw asset must cover one file")
        payload = {asset["covers"][0]: raw}
    else:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)):
                raise ValueError("Duplicate archive member")
            payload = {}
            for entry in archive.infolist():
                name = entry.filename
                if (entry.is_dir() or PurePosixPath(name).is_absolute() or "\\" in name or ":" in name or
                        any(part in {"", ".", ".."} for part in name.split("/")) or
                        (entry.external_attr >> 16) & 0o170000 == 0o120000):
                    raise ValueError("Unsafe published archive entry " + name)
                payload[name] = archive.read(entry)
    if "covers" in asset and set(asset["covers"]) != payload.keys():
        raise ValueError("Archive coverage differs from its manifest")
    if files is not None:
        if any(files.get(name) != fingerprint(raw) for name, raw in payload.items()):
            raise ValueError("Archive contents differ from the managed inventory")
    return payload


def zip_bytes(payload):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, raw in sorted(payload.items()):
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, raw)
    return output.getvalue()


def policy_files(jvm, ownership, artifact):
    if SELECTOR.encode() in jvm:
        raise ValueError("Policy is already installed")
    newline = b"\r\n" if b"\r\n" in jvm else b"\n"
    jvm = jvm + (b"" if jvm.endswith(b"\n") else newline) + SELECTOR.encode() + newline
    owned = ownership.decode("utf-8-sig").splitlines()
    if not any(path in owned for path in (ARTIFACT, "config/**", "config/lanminecraft/**")):
        # Keep comments and existing bytes intact. CF's sorted exact ownership is handled by its source recipe.
        ownership = ownership + (b"" if ownership.endswith(b"\n") else b"\n") + ARTIFACT.encode() + b"\n"
    return {ARTIFACT: artifact, JVM: jvm, OWNED: ownership}


def stage(manifest, downloaded, replacements, revision, output):
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("A full source commit is required")
    if output.exists():
        raise ValueError("Use a new stage directory")
    original = {row["path"]: {key: row[key] for key in ("sizeBytes", "sha256")} for row in manifest["files"]}
    if len(original) != len(manifest["files"]) or ARTIFACT in original or set(replacements) != {ARTIFACT, JVM, OWNED}:
        raise ValueError("Unexpected policy update inputs")
    updated = deepcopy(manifest)
    assets = []
    new_assets = {}
    for asset in manifest["assets"]:
        touched = set(asset["covers"]) & replacements.keys()
        if not touched:
            assets.append(asset)
            continue
        payload = checked_payload(asset, downloaded[asset["name"]], original)
        payload.update({path: replacements[path] for path in touched})
        raw = zip_bytes(payload)
        pin = fingerprint(raw)
        name = "r-" + pin["sha256"][:12] + "-launcher.zip"
        new_assets[name] = raw
        assets.append({"name": name, "kind": "zipRoot", **pin, "covers": sorted(payload)})
    agent_pin = fingerprint(replacements[ARTIFACT])
    agent_name = "r-" + agent_pin["sha256"][:12] + "-experimental-world-confirmation.jar"
    new_assets[agent_name] = replacements[ARTIFACT]
    assets.append({"name": agent_name, "kind": "jar", **agent_pin, "covers": [ARTIFACT]})
    files = {**original, **{path: fingerprint(raw) for path, raw in replacements.items()}}
    updated["files"] = [{"path": path, **pin} for path, pin in sorted(files.items())]
    updated["assets"] = sorted(assets, key=lambda row: row["name"])
    updated["revision"] = revision
    library = manifest["library"]
    content = checked_payload(library, downloaded[library["name"]])
    if set(content) != {"library.json", "preview.jpg"}:
        raise ValueError("Unexpected library payload")
    card = json.loads(content["library.json"])
    if card["revision"] != manifest["revision"] or library["revision"] != manifest["revision"]:
        raise ValueError("Published library revisions disagree")
    card["revision"] = revision
    content["library.json"] = (json.dumps(card, ensure_ascii=False, indent=2) + "\n").encode()
    raw = zip_bytes(content)
    pin = fingerprint(raw)
    name = "library-" + pin["sha256"] + ".zip"
    new_assets[name] = raw
    updated["library"] = {**library, "name": name, **pin, "revision": revision}
    # Restrict the transaction to the three reviewed policy files and matching packaging metadata.
    changed = {path for path in files if files[path] != original.get(path)}
    if not {ARTIFACT, JVM} <= changed or changed - {ARTIFACT, JVM, OWNED}:
        raise ValueError("The release modifies unrelated game data")
    covered = [path for asset in assets for path in asset["covers"]]
    if len(covered) != len(set(covered)) or set(covered) != files.keys():
        raise ValueError("Incomplete or duplicate release coverage")
    output.joinpath("assets").mkdir(parents=True)
    for name, raw in new_assets.items():
        (output / "assets" / name).write_bytes(raw)
    (output / "pack-manifest.json").write_text(json.dumps(updated, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return updated
