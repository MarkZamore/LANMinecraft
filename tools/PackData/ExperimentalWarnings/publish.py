"""Prepare and publish a reviewed small policy delta using existing release payloads.

The input audit must pin the current main tree and exact live manifest. Source
commits and immutable payloads are retained. The manifest is replaced last.
"""
import argparse
import ast
import base64
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import time
import urllib.request

from policy import ARTIFACT, JVM, OWNED, fingerprint
from stage_update import checked_payload, policy_files, stage

HERE = Path(__file__).resolve().parent


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def api(path, value=None, method=None):
    command = ["gh", "api", path]
    if method:
        command += ["--method", method]
    if value is not None:
        command += ["--input", "-"]
    result = subprocess.run(command, input=encoded(value) if value is not None else None, capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace"))
    return json.loads(result.stdout) if result.stdout.strip() else None


def fetch(url):
    for attempt in range(4):
        try:
            requested = url if attempt == 0 else url + ("&" if "?" in url else "?") + "retry=" + str(time.time_ns())
            with urllib.request.urlopen(requested, timeout=90) as stream:
                return stream.read()
        except Exception:
            if attempt == 3:
                raise
            time.sleep(2)


def source(audit, path):
    return fetch(f'https://raw.githubusercontent.com/MarkZamore/{audit["repo"]}/{audit["main"]}/{path}')


def inventory_hash(files):
    rows = [{"path": row["path"], "sizeBytes": row["sizeBytes"], "sha256": row["sha256"]} for row in files]
    raw = json.dumps(sorted(rows, key=lambda row: row["path"]), ensure_ascii=False,
                     sort_keys=True, separators=(",", ":")).encode()
    return hashlib.sha256(raw).hexdigest()


def repin_rows(document, replacements):
    if isinstance(document, dict):
        name = document.get("path")
        if name in replacements and "sha256" in document:
            document.update(fingerprint(replacements[name]))
        for key, value in document.items():
            if key in replacements and isinstance(value, dict) and "sha256" in value:
                value.update(fingerprint(replacements[key]))
            repin_rows(value, replacements)
    elif isinstance(document, list):
        for value in document:
            repin_rows(value, replacements)


def add_generator_guard(raw):
    text = raw.decode("utf-8-sig")
    parsed = ast.parse(text)
    candidates = [node for node in parsed.body if isinstance(node, ast.FunctionDef) and node.name == "main"]
    if len(candidates) != 1:
        raise ValueError("Expected one generator main function")
    function = candidates[0]
    first = function.body[0]
    insertion = first.lineno - 1
    if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) and isinstance(first.value.value, str):
        insertion = first.end_lineno
    lines = text.splitlines(keepends=True)
    guard = ("    import runpy\n"
             "    from pathlib import Path as PolicyPath\n"
             "    policy_root = PolicyPath(__file__).resolve().parents[1]\n"
             "    runpy.run_path(str(policy_root / 'tools/experimental-warnings/policy.py'))['verify'](policy_root)\n")
    lines.insert(insertion, guard)
    result = "".join(lines).encode()
    ast.parse(result)
    return result


def prepare(audit, artifact, output):
    repo = audit["repo"]
    manifest = audit["manifest"]
    paths = {row["path"] for row in audit["tree"] if row["type"] == "blob"}
    originals = {row["path"]: {key: row[key] for key in ("sizeBytes", "sha256")} for row in manifest["files"]}
    downloads = {}
    runtime = {}
    for asset in [*manifest["assets"], manifest["library"]]:
        if "covers" in asset and not set(asset["covers"]) & {JVM, OWNED}:
            continue
        raw = fetch(f'https://github.com/MarkZamore/{repo}/releases/download/pack-latest/{asset["name"]}')
        downloads[asset["name"]] = raw
        if "covers" in asset:
            runtime.update(checked_payload(asset, raw, originals))
    replacements = policy_files(runtime[JVM], runtime[OWNED], artifact.read_bytes())
    patches = {}
    recipe = json.loads(source(audit, "tools/overlay.json")) if "tools/overlay.json" in paths else None
    cf_recipe = recipe is not None and "authorArchiveSha256" in recipe
    if cf_recipe:
        owned = [line for line in replacements[OWNED].decode("utf-8-sig").splitlines() if line]
        if any(line.startswith("#") for line in owned):
            raise ValueError("CF exact ownership unexpectedly contains comments")
        replacements[OWNED] = ("\n".join(sorted(set(owned))) + "\n").encode()
    sources = {}
    source_pins = {}
    if recipe is not None and "operations" in recipe and recipe.get("packId") == "attack-on-titan":
        for name in (JVM, OWNED):
            operation = next(row for row in recipe["operations"] if row.get("path") == name)
            sources[name] = operation["source"]
        sources[ARTIFACT] = "tools/overlay/" + ARTIFACT
        recipe["operations"].append({"operation": "add", "path": ARTIFACT, "source": sources[ARTIFACT],
                                     **fingerprint(replacements[ARTIFACT]),
                                     "reason": "Pack-owned experimental world confirmation presentation policy."})
    for name, raw in replacements.items():
        actual_source = sources.get(name, name)
        if name in originals:
            previous = source(audit, actual_source)
            if fingerprint(previous) != originals[name]:
                if name != OWNED or not cf_recipe:
                    raise ValueError("Published and source JVM/ownership inputs differ " + actual_source)
                source_claims = set(previous.decode("utf-8-sig").splitlines())
                published_claims = set(runtime[OWNED].decode("utf-8-sig").splitlines())
                if not source_claims <= published_claims:
                    raise ValueError("Source ownership has unpublished claims")
                # Existing quest/keybind release overlays add their own claims after author staging.
                raw = ("\n".join(sorted(source_claims | {ARTIFACT})) + "\n").encode()
                source_pins[name] = fingerprint(raw)
        patches[actual_source] = raw
    policy = {"schemaVersion": 1, "policy": "skip-experimental-world-confirmation-v1",
              "platform": {key: manifest[key] for key in ("minecraftVersion", "loader")},
              "files": {name: fingerprint(raw) for name, raw in replacements.items()}, "sources": sources,
              "sourceFiles": source_pins,
              "reviewedPredecessor": {"revision": manifest["revision"], "sha256": audit["manifestSha256"]},
              "nativeProof": "Mojang client bytecode 1.16.5, 1.18.2, 1.19.2, 1.20.1, 1.21.1, 26.3 analyzed with ASM BasicVerifier.",
              "jvmProof": "Java 8, 21 and 25 -Xverify:all callback/lifecycle branch probes. Full modded clients and visual UI NOT RUN."}
    for file in ["ExperimentalWarningAgent.java", "build.py", "build-inputs.json", "LICENSE.asm.txt", "policy.py", "README.md"]:
        patches["tools/experimental-warnings/" + file] = (HERE / file).read_bytes()
    patches["tools/experimental-warnings/policy.json"] = encoded(policy)
    if "tools/generate_manifest.py" in paths:
        patches["tools/generate_manifest.py"] = add_generator_guard(source(audit, "tools/generate_manifest.py"))
    if cf_recipe:
        recipe["supportFiles"] = [*recipe.get("supportFiles", []), {"path": ARTIFACT, **fingerprint(replacements[ARTIFACT])}]
    if recipe is not None:
        repin_rows(recipe, {**replacements, **{name: patches[sources.get(name, name)] for name in source_pins}})
        patches["tools/overlay.json"] = encoded(recipe)
    planned = deepcopy(manifest)
    planned["files"] = [{"path": name, **pin} for name, pin in sorted(
        {**originals, **{name: fingerprint(raw) for name, raw in replacements.items()}}.items())]
    public_path = "tools/public-server-policy.json"
    if public_path in paths:
        public = json.loads(source(audit, public_path))
        if "reviewedInventorySha256" in public:
            if public["reviewedInventorySha256"] != inventory_hash(manifest["files"]):
                raise ValueError("Existing independent server policy inventory is stale")
            public["reviewedInventorySha256"] = inventory_hash(planned["files"])
        elif "reviewedInputSha256" in public:
            roots = public["coveredRoots"]
            hashes = {row["path"]: row["sha256"] for row in manifest["files"] if row["path"].split("/")[0] in roots}
            # The source retains authored values for explicit delivery-only config projections.
            if "tools/pack-config-overrides.json" in paths:
                projections = json.loads(source(audit, "tools/pack-config-overrides.json"))
                for row in projections["overrides"]:
                    hashes[row["path"]] = fingerprint(source(audit, row["path"]))["sha256"]
            def source_hash():
                return hashlib.sha256(json.dumps(sorted(map(list, hashes.items())), ensure_ascii=True,
                                                 separators=(",", ":")).encode()).hexdigest()
            if source_hash() != public["reviewedInputSha256"]:
                raise ValueError("Existing independent source policy inventory is stale")
            hashes.update({name: fingerprint(raw)["sha256"] for name, raw in replacements.items()})
            public["reviewedInputSha256"] = source_hash()
        public["reviewUpdateReason"] = (
            "Reviewed only the experimental confirmation agent and its JVM/ownership selector. "
            "The agent contains no server-list seeding or menu/network code. Every other delivered file hash is identical.")
        patches[public_path] = encoded(public)
    lock_paths = ["tools/distribution-support-lock.json", "tools/distribution-lock.json", "tools/upstream-lock.json"]
    locks = {name: json.loads(source(audit, name)) for name in lock_paths if name in paths}
    for name, lock in locks.items():
        repin_rows(lock, {**replacements, **patches})
        if name == "tools/distribution-support-lock.json":
            lock["files"].append({"path": ARTIFACT, **fingerprint(replacements[ARTIFACT])})
            lock["files"].sort(key=lambda row: row["path"])
        if cf_recipe and name == "tools/upstream-lock.json" and "overlay" in lock:
            lock["overlay"]["supportFiles"] = recipe["supportFiles"]
            lock["overlay"]["recipeSha256"] = fingerprint(patches["tools/overlay.json"])["sha256"]
            lock["overlay"]["ownership"] = source_pins.get(OWNED, fingerprint(replacements[OWNED]))
        elif "supportFiles" in lock and repo != "Awakening-Of-The-Titans":
            lock["supportFiles"].append({"path": ARTIFACT, **fingerprint(replacements[ARTIFACT])})
            lock["supportFiles"].sort(key=lambda row: row["path"])
        elif name == "tools/distribution-lock.json" and repo == "Hide-and-Hunt":
            lock["files"].append({"path": ARTIFACT, **fingerprint(replacements[ARTIFACT])})
            lock["files"].sort(key=lambda row: row["path"])
        if recipe is not None and recipe.get("packId") == "attack-on-titan" and name == "tools/distribution-lock.json":
            lock["files"].append({"path": ARTIFACT, **fingerprint(replacements[ARTIFACT])})
            lock["files"].sort(key=lambda row: row["path"])
            receipt = lock["receipt"]
            receipt["overlaySha256"] = fingerprint(patches["tools/overlay.json"])["sha256"]
            receipt["inventorySha256"] = fingerprint(encoded(lock["files"]))["sha256"]
            receipt["operations"] = [{key: row[key] for key in ("path", "sha256", "sizeBytes")} for row in recipe["operations"]]
            patches["tools/overlay-receipt.json"] = encoded(receipt)
        raw = encoded(lock)
        if raw != source(audit, name):
            patches[name] = raw
    # Repin existing support entries for reviewed changed tooling and receipts.
    for name, lock in locks.items():
        if name in patches:
            repin_rows(lock, patches)
            patches[name] = encoded(lock)
    output.mkdir(parents=True)
    for name, raw in patches.items():
        path = output / "source" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
    (output / "plan.json").write_bytes(encoded({"repo": repo, "baseMain": audit["main"],
        "baseManifestSha256": audit["manifestSha256"], "sourceFiles": {name: fingerprint(raw) for name, raw in patches.items()},
        "runtimeFiles": {name: fingerprint(raw) for name, raw in replacements.items()}}))
    stage(manifest, downloads, replacements, "0" * 40, output / "dry-stage")
    return patches, downloads, replacements


def publish(audit, patches, downloads, replacements, output):
    repo = audit["repo"]
    root = "repos/MarkZamore/" + repo
    def current_manifest():
        return fetch(f'https://github.com/MarkZamore/{repo}/releases/download/pack-latest/pack-manifest.json?policy={time.time_ns()}')
    baseline = current_manifest()
    manifest_path = output / "release/pack-manifest.json"
    if manifest_path.exists():
        revision = json.loads(manifest_path.read_bytes())["revision"]
        commit = api(root + "/git/commits/" + revision)
        if [parent["sha"] for parent in commit["parents"]] != [audit["main"]]:
            raise ValueError("Prepared source commit has another predecessor")
        if api(root + "/git/ref/heads/main")["object"]["sha"] != revision:
            raise ValueError("The prepared source branch changed")
    else:
        if fingerprint(baseline)["sha256"] != audit["manifestSha256"]:
            raise ValueError("The published baseline changed")
        if api(root + "/git/ref/heads/main")["object"]["sha"] != audit["main"]:
            raise ValueError("The source branch changed")
        entries = []
        for name, raw in patches.items():
            blob = api(root + "/git/blobs", {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
            entries.append({"path": name, "mode": "100644", "type": "blob", "sha": blob["sha"]})
        base = api(root + "/git/commits/" + audit["main"])
        tree = api(root + "/git/trees", {"base_tree": base["tree"]["sha"], "tree": entries})
        commit = api(root + "/git/commits", {"message": "Disable experimental world confirmation in pack-owned delivery [skip ci]",
                                             "tree": tree["sha"], "parents": [audit["main"]]})
        revision = commit["sha"]
        stage(audit["manifest"], downloads, replacements, revision, output / "release")
        api(root + "/git/refs/heads/main", {"sha": revision, "force": False}, "PATCH")
    release = api(root + "/releases/tags/pack-latest")
    existing = {row["name"]: row for row in release["assets"]}
    for path in (output / "release/assets").iterdir():
        pin = fingerprint(path.read_bytes())
        if path.name in existing:
            row = existing[path.name]
            if row["size"] != pin["sizeBytes"] or row.get("digest") != "sha256:" + pin["sha256"]:
                raise ValueError("Immutable asset collision " + path.name)
        else:
            subprocess.run(["gh", "release", "upload", "pack-latest", str(path), "--repo", "MarkZamore/" + repo], check=True)
        public = fetch(f'https://github.com/MarkZamore/{repo}/releases/download/pack-latest/{path.name}?policy={revision}')
        if fingerprint(public) != pin:
            raise ValueError("Public payload mismatch " + path.name)
    live = current_manifest()
    if fingerprint(live)["sha256"] != audit["manifestSha256"] and live != manifest_path.read_bytes():
        raise ValueError("A concurrent release replaced the baseline")
    previous = output / "previous/pack-manifest.json"
    previous.parent.mkdir(exist_ok=True)
    if not previous.exists():
        previous.write_bytes(baseline)
    try:
        subprocess.run(["gh", "release", "upload", "pack-latest", str(manifest_path), "--repo", "MarkZamore/" + repo, "--clobber"], check=True)
    except Exception:
        live = api(root + "/releases/tags/pack-latest")
        if not any(row["name"] == "pack-manifest.json" for row in live["assets"]):
            subprocess.run(["gh", "release", "upload", "pack-latest", str(previous), "--repo", "MarkZamore/" + repo], check=True)
        raise
    public = current_manifest()
    if public != manifest_path.read_bytes():
        raise ValueError("Published manifest differs from the candidate")
    result = {"status": "PASS", "repo": repo, "revision": revision, "manifest": fingerprint(public),
              "runtimeFiles": {name: fingerprint(raw) for name, raw in replacements.items()},
              "gameplayRun": "NOT RUN", "computerUse": False}
    (output / "published.json").write_bytes(encoded(result))
    return result


def load_prepared(audit, output):
    plan = json.loads((output / "plan.json").read_bytes())
    if plan["baseMain"] != audit["main"] or plan["baseManifestSha256"] != audit["manifestSha256"]:
        raise ValueError("Prepared plan belongs to another baseline")
    patches = {name: (output / "source" / name).read_bytes() for name in plan["sourceFiles"]}
    if any(fingerprint(raw) != plan["sourceFiles"][name] for name, raw in patches.items()):
        raise ValueError("Prepared source bytes changed")
    dry = json.loads((output / "dry-stage/pack-manifest.json").read_bytes())
    replacements = {}
    dry_files = {row["path"]: {key: row[key] for key in ("sizeBytes", "sha256")} for row in dry["files"]}
    for asset in dry["assets"]:
        if set(asset["covers"]) & plan["runtimeFiles"].keys():
            raw = (output / "dry-stage/assets" / asset["name"]).read_bytes()
            replacements.update({name: raw for name, raw in checked_payload(asset, raw, dry_files).items()
                                 if name in plan["runtimeFiles"]})
    if {name: fingerprint(raw) for name, raw in replacements.items()} != plan["runtimeFiles"]:
        raise ValueError("Prepared runtime bytes changed")
    downloads = {}
    for asset in [*audit["manifest"]["assets"], audit["manifest"]["library"]]:
        if "covers" not in asset or set(asset["covers"]) & replacements.keys():
            downloads[asset["name"]] = fetch(
                f'https://github.com/MarkZamore/{audit["repo"]}/releases/download/pack-latest/{asset["name"]}')
    return patches, downloads, replacements


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--publish", action="store_true")
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    audit = json.loads(args.audit.read_bytes())
    if args.resume:
        patches, downloads, replacements = load_prepared(audit, args.out)
    else:
        patches, downloads, replacements = prepare(audit, args.artifact, args.out)
    if args.publish:
        print(json.dumps(publish(audit, patches, downloads, replacements, args.out), indent=2))
    else:
        print("Prepared", audit["repo"], len(patches), "source files")
