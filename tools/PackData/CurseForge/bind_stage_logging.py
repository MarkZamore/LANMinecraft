"""Apply a committed logging-only recipe update to an untouched verified game stage."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
from tempfile import TemporaryDirectory

import prepare
import release_overlay as release
from stage_release import git


RECIPE = "tools/overlay.json"


def owned_stage(stage, workspace):
    stage = Path(os.path.abspath(stage))
    runs = workspace / "artifacts/agent-runs"
    prepare.require(stage.is_relative_to(runs) and len(stage.relative_to(runs).parts) >= 2,
                    "Use an existing stage inside an owned agent run")
    prepare.no_links(stage)
    owner = prepare.read_local_json(runs / stage.relative_to(runs).parts[0] / "RUN.json")
    prepare.require(isinstance(owner, dict) and isinstance(owner.get("owner"), str) and owner["owner"].strip(),
                    "The task run has no owner")
    return stage


def git_blob(source, revision, path):
    reference = f"{revision}:{path}"
    prepare.require(0 < int(git(source, "cat-file", "-s", reference)) <= prepare.MAX_JSON,
                    f"Unexpected pack source size: {path}")
    return git(source, "cat-file", "blob", reference)


def assert_pristine(game, inventory):
    seen = set()
    for root, directories, files in os.walk(game):
        root = Path(root)
        for name in directories:
            prepare.no_links(root / name)
        for name in files:
            path = root / name
            relative = path.relative_to(game).as_posix()
            prepare.require(relative in inventory, f"Stage has a file outside the verified author payload: {relative}")
            seen.add(relative)
    prepare.require(seen == inventory.keys(), "Stage files differ from the verified author payload")


def replace(path, data, temporary):
    temporary.write_bytes(data)
    os.replace(temporary, path)


def bind(pack_source, stage, previous, revision, *, workspace=prepare.ROOT):
    stage = owned_stage(stage, workspace)
    prepare.no_links(pack_source)
    prepare.require(all(re.fullmatch("[0-9a-f]{40}", value) for value in (previous, revision)) and previous != revision,
                    "Use two distinct full source Git revisions")
    top = Path(git(pack_source, "rev-parse", "--show-toplevel").decode("utf-8").strip())
    prepare.require(top.resolve() == pack_source.resolve(), "Pack source must be a Git repository root")
    git(pack_source, "merge-base", "--is-ancestor", previous, revision)

    client = release.read_lock(pack_source / release.CLIENT_LOCK, "client-only")
    author = release.read_lock(pack_source / release.COMPLETE_LOCK, "complete")
    release.same_author(client, author)
    recipe, recipe_sha, _, _ = release.source_policy(pack_source, author)
    additions = prepare.overlay_additions(recipe, pack_source, author)
    prepare.require(additions, "The new recipe must pin the two logging files")
    for path in (release.CLIENT_LOCK, release.COMPLETE_LOCK, release.DESCRIPTOR, RECIPE, release.OWNERSHIP, *additions):
        prepare.require(prepare.overlay_bytes(pack_source / path) == git_blob(pack_source, revision, path),
                        f"Pack source differs from the requested revision: {path}")

    prior_recipe_bytes = git_blob(pack_source, previous, RECIPE)
    prior_recipe = prepare.decode_json(prior_recipe_bytes)
    prepare.require(prior_recipe == {key: value for key, value in recipe.items() if key != "additions"},
                    "The revision changes more than the logging additions")
    prior_descriptor = git_blob(pack_source, previous, release.DESCRIPTOR)
    prepare.require(prior_descriptor == prepare.overlay_bytes(pack_source / release.DESCRIPTOR),
                    "The revision changes the game descriptor")
    prior_client = prepare.decode_json(git_blob(pack_source, previous, release.CLIENT_LOCK))
    prior_author = prepare.decode_json(git_blob(pack_source, previous, release.COMPLETE_LOCK))
    release.same_author(client, prior_client)
    release.same_author(author, prior_author)
    prepare.require(author["files"] == prior_author["files"], "The revision changes pinned author dependencies")
    prior_ownership = git_blob(pack_source, previous, release.OWNERSHIP) if prior_recipe["patches"] else None
    ownership = prepare.overlay_bytes(pack_source / release.OWNERSHIP)
    claims = sorted({patch["path"] for patch in recipe["patches"]} | {prepare.LOGGING_CONFIG})
    prepare.require(ownership == ("\n".join(claims) + "\n").encode(), "The revision changes unrelated ownership")

    receipt_path = stage / "intake.json"
    receipt_bytes = prepare.overlay_bytes(receipt_path)
    receipt = release.read_lock(receipt_path, "complete")
    game = prepare.native_path(stage / "game")
    with TemporaryDirectory(prefix=".logging-stage-", dir=stage.parent) as scratch:
        scratch = Path(scratch)
        old_source = scratch / "previous"
        metadata = {RECIPE: prior_recipe_bytes, release.DESCRIPTOR: prior_descriptor}
        if prior_ownership is not None:
            metadata[release.OWNERSHIP] = prior_ownership
        for name, data in metadata.items():
            path = old_source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        prior_policy, excluded = release.verify_stage(stage, old_source, author)
        inventory, _ = release.payload_inventory(author, prior_policy, excluded)
        assert_pristine(game, inventory)

        current_receipt = {**receipt, "overlay": {**receipt["overlay"], "recipeSha256": recipe_sha,
                           "additions": recipe["additions"], "ownership": prepare.fingerprint(ownership)}}
        updated = (json.dumps(current_receipt, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")
        changes = [(game / name, data, None) for name, data in additions.items()]
        changes += [(game / release.OWNERSHIP, ownership, prior_ownership), (receipt_path, updated, receipt_bytes)]
        applied = []
        directories = []
        try:
            for path, data, previous_bytes in changes:
                prepare.no_links(path)
                if not path.parent.exists():
                    path.parent.mkdir()
                    directories.append(path.parent)
                if previous_bytes is None:
                    with path.open("xb") as output:
                        applied.append((path, None))
                        output.write(data)
                else:
                    prepare.require(prepare.overlay_bytes(path) == previous_bytes, f"Stage changed while binding: {path}")
                    replace(path, data, scratch / "swap")
                    applied.append((path, previous_bytes))
            release.verify_stage(stage, pack_source, author)
        except BaseException:
            for path, previous_bytes in reversed(applied):
                if previous_bytes is None:
                    path.unlink()
                else:
                    replace(path, previous_bytes, scratch / "swap")
            for directory in reversed(directories):
                directory.rmdir()
            raise
    return current_receipt["overlay"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack-source", type=Path, required=True)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--previous-revision", required=True)
    parser.add_argument("--revision", required=True)
    args = parser.parse_args()
    try:
        result = bind(args.pack_source, args.stage, args.previous_revision, args.revision)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
