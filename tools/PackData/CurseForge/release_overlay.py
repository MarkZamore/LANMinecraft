"""Stage and check the required Steam world-sharing layer of a CurseForge pack."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import zipfile

import prepare


AUTHOR_FIELDS = ("client", "pack", "declaredDependencies", "overrides")
CLIENT_LOCK = "tools/upstream-client-lock.json"
COMPLETE_LOCK = "tools/upstream-lock.json"
DESCRIPTOR = "portable-pack.json"
OWNERSHIP = "launcher/pack-owned.txt"


def read_lock(path, status):
    lock = prepare.read_local_json(path)
    prepare.require(isinstance(lock, dict) and lock.get("schemaVersion") == 1 and lock.get("status") == status and
                    isinstance(lock.get("client"), dict) and isinstance(lock.get("pack"), dict) and
                    isinstance(lock.get("declaredDependencies"), list) and isinstance(lock.get("overrides"), list) and
                    (status != "complete" or isinstance(lock.get("files"), list)),
                    f"Expected a {status} author lock: {path}")
    return lock


def same_author(left, right):
    prepare.require(all(left[field] == right[field] for field in AUTHOR_FIELDS),
                    "Complete author lock differs from the current client lock")


def patch_inputs(recipe, author):
    overrides = {row["path"]: row for row in author["overrides"]}
    paths = {}
    for patch in recipe["patches"]:
        path = prepare.safe_path(patch["path"])
        prepare.reserve_path(paths, path)
        original = overrides.get(path.as_posix())
        prepare.require(original is not None and patch["before"] ==
                        {key: original[key] for key in ("sizeBytes", "sha256")},
                        f"Review required for changed or removed author input: {path}")


def source_policy(pack_source, author):
    recipe_bytes = prepare.overlay_bytes(pack_source / "tools/overlay.json")
    recipe = prepare.decode_json(recipe_bytes)
    prepare.require(isinstance(recipe, dict) and recipe.get("schemaVersion") == 1 and
                    isinstance(recipe.get("patches"), list) and
                    recipe.get("authorArchiveSha256") == author["client"]["sha256"],
                    "Overlay does not match the current author archive lock")
    descriptor_bytes = prepare.overlay_bytes(pack_source / DESCRIPTOR)
    descriptor = prepare.decode_json(descriptor_bytes)
    descriptor_pin = prepare.fingerprint(descriptor_bytes)
    prepare.require(isinstance(descriptor, dict) and descriptor.get("preserveWorldSettingsOnShare") is True and
                    descriptor_pin["sha256"] == recipe.get("descriptorSha256") and
                    descriptor.get("schemaVersion") == 1 and
                    descriptor.get("minecraftVersion") == author["pack"]["minecraftVersion"] and
                    descriptor.get("loader") == author["pack"]["loader"] and
                    prepare.positive(descriptor.get("javaMajorVersion")),
                    "Checked pack descriptor must retain the platform and Steam world-sharing policy")
    patch_inputs(recipe, author)
    prepare.overlay_dependency_keys(recipe, author)
    additions = prepare.overlay_additions(recipe, pack_source, author)
    prepare.overlay_bootstrap(recipe, pack_source, author, additions)
    prepare.jar_metadata_patches(recipe, author, pack_source)
    return recipe, prepare.fingerprint(recipe_bytes)["sha256"], descriptor, descriptor_pin


def payload_inventory(author, policy_files, excluded, jar_patches=()):
    declared = {}
    for row in author["declaredDependencies"]:
        key = row["projectId"], row["fileId"]
        prepare.require(all(prepare.positive(value) for value in key) and key not in declared and
                        type(row["required"]) is bool, "Invalid or repeated declared author dependency")
        declared[key] = row["required"]

    authored = [(row, True, False) for row in author["overrides"]]
    dependencies = {}
    for row in author["files"]:
        key = row["projectId"], row["fileId"]
        prepare.require(key in declared and key not in dependencies and row["required"] is declared[key],
                        f"Resolved dependency differs from its author declaration: {key}")
        dependencies[key] = row
        authored.append((row, row["required"], True))
    prepare.require(all(not required or key in dependencies for key, required in declared.items()),
                    "Complete author lock is missing a required dependency")

    expected = {}
    required = set()
    paths = {}
    override_paths = set()
    shared_paths = set()
    for row, needed, dependency in authored:
        relative = prepare.safe_path(row["path"])
        name = relative.as_posix()
        size, digest = row["sizeBytes"], row["sha256"]
        prepare.require(type(size) is int and 0 <= size <= prepare.MAX_FILE and
                        isinstance(digest, str) and re.fullmatch("[0-9a-f]{64}", digest),
                        f"Invalid author file pin: {name}")
        pin = {"sizeBytes": size, "sha256": digest}
        if dependency and name in override_paths and name not in shared_paths:
            prepare.require(expected[name] == pin, f"Author override and dependency differ at the same path: {name}")
            shared_paths.add(name)
        else:
            prepare.reserve_path(paths, relative)
            expected[name] = pin
            if not dependency:
                override_paths.add(name)
        if needed:
            required.add(name)

    for row in excluded:
        name = prepare.safe_path(row["path"]).as_posix()
        resolved = dependencies.get((row["projectId"], row["fileId"]))
        prepare.require(resolved is not None and resolved["required"] is True and
                        all(resolved[key] == row[key] for key in ("path", "sizeBytes", "sha256")) and
                        name in expected and name not in shared_paths,
                        f"Exclusion differs from its checked author dependency: {name}")
        del expected[name]
        required.remove(name)

    for name, pin in policy_files.items():
        if name in (DESCRIPTOR, OWNERSHIP) or name in prepare.LOGGING_FILES or prepare.BOOTSTRAP_PATH.fullmatch(name):
            prepare.reserve_path(paths, prepare.safe_path(name))
        else:
            prepare.require(name in override_paths and name not in shared_paths,
                            f"Policy output is not a patched author override: {name}")
        expected[name] = pin
        required.add(name)
    for row in jar_patches:
        name = row["path"]
        prepare.require(name in required and name not in override_paths and expected[name] == row["before"],
                        f"Patched JAR is not the checked author dependency: {name}")
        expected[name] = row["after"]
    return expected, required


def verify_stage(stage, pack_source, author):
    game = prepare.native_path(stage / "game")
    receipt = read_lock(stage / "intake.json", "complete")
    same_author(author, receipt)
    if author["status"] == "complete":
        prepare.require(author["files"] == receipt["files"], "Staged dependencies differ from the complete author lock")
    recipe, recipe_sha, descriptor, descriptor_pin = source_policy(pack_source, receipt)
    base_descriptor = {key: value for key, value in descriptor.items() if key != "preserveWorldSettingsOnShare"}
    prepare.require(receipt.get("descriptor") == base_descriptor, "Stage was prepared for another platform")
    patches = [{key: patch[key] for key in ("path", "before", "after")} for patch in recipe["patches"]]
    expected = {"recipeSha256": recipe_sha, "descriptor": descriptor_pin, "patches": patches,
                "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE]}
    policy_files = {DESCRIPTOR: descriptor_pin, **{patch["path"]: patch["after"] for patch in patches}}
    owned_names = {patch["path"] for patch in patches}
    jar_patches = prepare.jar_metadata_patches(recipe, receipt, pack_source)
    if jar_patches:
        expected["jarMetadataPatches"] = jar_patches
    if "additions" in recipe:
        expected["additions"] = recipe["additions"]
        policy_files.update({row["path"]: {key: row[key] for key in ("sizeBytes", "sha256")}
                             for row in recipe["additions"]})
        owned_names.add(prepare.LOGGING_CONFIG)
    if "bootstrap" in recipe:
        expected["bootstrap"] = recipe["bootstrap"]
        artifact = recipe["bootstrap"]["artifact"]
        policy_files[artifact["path"]] = {key: artifact[key] for key in ("sizeBytes", "sha256")}
        owned_names.add(artifact["path"])
    if owned_names:
        ownership = prepare.overlay_bytes(pack_source / OWNERSHIP)
        prepare.require(ownership == ("\n".join(sorted(owned_names)) + "\n").encode("utf-8"),
                        "Source ownership does not match the overlay edits")
        expected["ownership"] = prepare.fingerprint(ownership)
        policy_files[OWNERSHIP] = expected["ownership"]
    dependency_keys = prepare.overlay_dependency_keys(recipe, author)
    excluded = []
    if dependency_keys:
        excluded = prepare.excluded_dependency_files(dependency_keys, receipt["files"])
        expected["excludedDependencies"] = excluded
        for row in excluded:
            path = row["path"]
            target = game / path
            prepare.no_links(target)
            prepare.require(not target.exists(), f"Excluded author dependency remains in staged game: {path}")
    prepare.require(receipt.get("overlay") == expected, "Missing or changed overlay receipt or required launcher feature")
    inventory, _ = payload_inventory(receipt, policy_files, excluded, jar_patches)
    for path, pin in inventory.items():
        target = game / path
        prepare.no_links(target)
        kind = "policy output" if path in policy_files else "author payload"
        prepare.require(target.is_file(), f"Staged {kind} is missing: {path}")
        try:
            with target.open("rb") as source:
                prepare.exact_bytes(source, pin["sizeBytes"], expected={"sha256": pin["sha256"]})
        except ValueError as error:
            raise ValueError(f"Staged {kind} differs: {path}: {error}") from error
    return policy_files, excluded


def verify_manifest(manifest, policy_files, author, excluded=(), jar_patches=()):
    prepare.require(isinstance(manifest, dict) and manifest.get("schemaVersion") == 2 and
                    manifest.get("minecraftVersion") == author["pack"]["minecraftVersion"] and
                    manifest.get("loader") == author["pack"]["loader"],
                    "Release manifest must declare the verified Minecraft and loader")
    required = manifest.get("requiredLauncherFeatures")
    prepare.require(isinstance(required, list) and required.count(prepare.REQUIRED_SHARE_FEATURE) == 1,
                    f"Release manifest must require {prepare.REQUIRED_SHARE_FEATURE}")
    files = manifest.get("files")
    prepare.require(isinstance(files, list) and
                    all(isinstance(row, dict) and isinstance(row.get("path"), str) for row in files),
                    "Release manifest has no file inventory")
    excluded_paths = {row["path"].casefold() for row in excluded}
    excluded_hashes = {row["sha256"] for row in excluded}
    inventory, required_paths = payload_inventory(author, policy_files, excluded, jar_patches)
    seen = set()
    for row in files:
        path = prepare.safe_path(row["path"]).as_posix()
        sha256 = row.get("sha256")
        prepare.require(path.casefold() not in excluded_paths and
                        (not isinstance(sha256, str) or sha256.casefold() not in excluded_hashes),
                        f"Release manifest includes excluded author dependency: {path}")
        pin = inventory.get(path)
        prepare.require(pin is not None and path not in seen, f"Release manifest has an unreviewed or repeated file: {path}")
        prepare.require(type(row.get("sizeBytes")) is int and all(row.get(key) == value for key, value in pin.items()),
                        f"Release manifest payload differs: {path}")
        seen.add(path)
    missing = sorted(required_paths - seen)
    prepare.require(not missing, f"Release manifest is missing required payload files: {', '.join(missing[:5])}")


def review_update(pack_source, current, candidate):
    recipe, _, _, _ = source_policy(pack_source, current)
    prepare.require(candidate["client"]["projectId"] == current["client"]["projectId"] and
                    all(candidate["pack"][key] == current["pack"][key] for key in ("minecraftVersion", "loader")),
                    "Review required for a changed author project or game platform")
    patch_inputs(recipe, candidate)
    prepare.overlay_dependency_keys(recipe, candidate)
    additions = prepare.overlay_additions(recipe, pack_source, candidate)
    prepare.overlay_bootstrap(recipe, pack_source, candidate, additions)
    prepare.jar_metadata_patches(recipe, candidate, pack_source)
    return {**recipe, "authorArchiveSha256": candidate["client"]["sha256"]}


def stage_pack(args, author, lock_path):
    _, _, descriptor, _ = source_policy(args.pack_source, author)
    client = author["client"]
    placements = args.pack_source / "tools/curseforge-placements.json"
    prepare.prepare(argparse.Namespace(
        project_id=client["projectId"], file_id=client["fileId"], output=args.output, client_only=False,
        client_archive=args.client_archive, expect=lock_path, java_major=descriptor["javaMajorVersion"],
        pack_source=args.pack_source, placements=placements if placements.exists() else None,
        verified_dependencies=getattr(args, "verified_dependencies", None),
    ))
    verify_stage(args.output, args.pack_source, author)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    stage = commands.add_parser("stage", help="Prepare the exact author archive and required source overlay")
    stage.add_argument("--output", type=Path, required=True)
    stage.add_argument("--client-archive", type=Path)
    stage.add_argument("--locked", action="store_true", help="Require the complete author dependency lock")
    stage.add_argument("--verified-dependencies", type=Path,
                       help="Reuse matching pinned dependency bytes from a game inside the owned run")
    verify = commands.add_parser("verify", help="Check the release manifest against the verified stage and full author lock")
    verify.add_argument("--stage", type=Path, required=True)
    verify.add_argument("--manifest", type=Path, required=True)
    review = commands.add_parser("review-update", help="Propose a new archive binding if edited author inputs are unchanged")
    review.add_argument("--candidate-lock", type=Path, required=True)
    for command in (stage, verify, review):
        command.add_argument("--pack-source", type=Path, required=True)
    args = parser.parse_args()
    try:
        client_path = args.pack_source / CLIENT_LOCK
        client_lock = read_lock(client_path, "client-only")
        if args.command == "review-update":
            candidate = read_lock(args.candidate_lock, "client-only")
            print(json.dumps(review_update(args.pack_source, client_lock, candidate), ensure_ascii=False, indent=2))
            return
        lock_path = client_path
        author = client_lock
        if args.command == "verify" or args.locked:
            lock_path = args.pack_source / COMPLETE_LOCK
            author = read_lock(lock_path, "complete")
            same_author(client_lock, author)
        if args.command == "stage":
            stage_pack(args, author, lock_path)
            print(f"Verified author archive and world-sharing policy: {args.output}")
        else:
            policy_files, excluded = verify_stage(args.stage, args.pack_source, author)
            recipe, _, _, _ = source_policy(args.pack_source, author)
            jar_patches = prepare.jar_metadata_patches(recipe, author, args.pack_source)
            verify_manifest(prepare.read_local_json(args.manifest), policy_files, author, excluded, jar_patches)
            print(f"Verified release payload, policy and required launcher feature: {args.manifest}")
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
