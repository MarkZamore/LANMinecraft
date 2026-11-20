#!/usr/bin/env python3
"""Build and verify immutable native runtime and pack overlay release assets."""

import argparse
from collections.abc import Callable
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess
import tempfile
import zipfile

import pack_library


FEATURE = "native-shared-runtime-v1"
CONTRACT = "shared-native-runtime.json"
DESCRIPTOR = "portable-pack.json"
FAMILY = "native/runtime-family.json"
MANIFEST = "pack-manifest.json"
PLATFORM = "windows-x64"
PACK_NATIVE_FIELDS = ("executable", "platform", "adapter", "version")
OVERLAY_PATHS = ("native/presentation.json", "native/native-mods.json", "native/horizon-mods/", "native/horizon-native-mods/")
ASSET_FIELDS = {"name", "sha256", "sizeBytes"}
RUNTIME_FIELDS = {"schemaVersion", "id", "version", "platform", "source", "files"}
# Consumer limits in PackArchiveService, PortablePackSyncService and SharedNativeRuntimeStore.
MAX_ENTRIES = 100_000
MAX_ARCHIVE_BYTES = 8 << 30
MAX_UNPACKED_BYTES = 16 << 30
MAX_DESCRIPTOR_BYTES = 8 << 20
MAX_FAMILY_MARKER_BYTES = 16 << 10


def require(value, message):
    if not value:
        raise ValueError(message)


def read(path):
    return json.loads(path.read_bytes())


def write(path, document):
    path.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def safe_path(value):
    require(isinstance(value, str) and 0 < len(value) <= 512 and "\\" not in value, f"Invalid path: {value!r}")
    path = PurePosixPath(value)
    require(path.parts and not path.is_absolute() and path.as_posix() == value
            and all(part and part not in {".", ".."} and not part.endswith((".", " "))
                    and not PureWindowsPath(part).is_reserved()
                    and all(ord(c) >= 32 and c not in '<>:"|?*' for c in part) for part in path.parts),
            f"Invalid Windows relative path: {value!r}")
    return path


def safe_name(value):
    path = safe_path(value)
    require(len(path.parts) == 1, f"Expected a plain asset name: {value!r}")
    return value


def identifier(value, label):
    require(isinstance(value, str) and re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}", value),
            f"Invalid {label}: {value!r}")
    return value


def check_record(row):
    require(type(row["sizeBytes"]) is int and row["sizeBytes"] >= 0, "Invalid file size")
    require(isinstance(row["sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", row["sha256"]), "Invalid SHA-256")
    return {field: row[field] for field in ("sizeBytes", "sha256")}


def file_map(rows):
    require(isinstance(rows, list) and rows, "File inventory is empty")
    result, seen = {}, set()
    for row in rows:
        value = safe_path(row["path"]).as_posix()
        require(value.casefold() not in seen, f"Duplicate file: {value}")
        seen.add(value.casefold())
        result[value] = check_record(row)
    for value in seen:
        require(not any(parent.as_posix() in seen for parent in PurePosixPath(value).parents),
                f"File is also a parent directory: {value}")
    return result


def rows(mapping):
    return [{"path": path, **mapping[path]} for path in sorted(mapping)]


def identity(path):
    with path.open("rb") as stream:
        return {"sizeBytes": path.stat().st_size, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}


def checked_file(root, value, expected=None):
    relative = safe_path(value)
    path = root
    require(not path.is_symlink() and not path.is_junction(), f"Linked input root: {root}")
    for part in relative.parts:
        path = path / part
        require(not path.is_symlink() and not path.is_junction(), f"Linked input path: {path}")
    require(path.is_file(), f"Missing input file: {path}")
    if expected is not None:
        require(identity(path) == expected, f"Input bytes differ from the approved SHA-256 or size: {value}")
    return path


def validate_runtime(spec, *, staged=True):
    expected = RUNTIME_FIELDS | ({"archive"} if staged else set())
    require(isinstance(spec, dict) and set(spec) == expected and type(spec["schemaVersion"]) is int
            and spec["schemaVersion"] == 1 and spec["platform"] == PLATFORM, "Invalid shared runtime schema or platform")
    identifier(spec["id"], "runtime ID")
    identifier(spec["version"], "runtime version")
    source = spec["source"]
    require(isinstance(source, dict) and set(source) == {"owner", "repo", "tag"}, "Invalid runtime GitHub release source")
    for key, value in source.items():
        identifier(value, f"runtime source {key}")
    moving = {"latest", "nightly", "main", "master", "pack-latest"}
    require(spec["version"].casefold() not in moving and source["tag"].casefold() not in moving,
            "The shared runtime requires a versioned immutable release tag")
    files = file_map(spec["files"])
    require(len(files) <= MAX_ENTRIES and sum(record["sizeBytes"] for record in files.values()) <= MAX_UNPACKED_BYTES,
            "The shared runtime exceeds the launcher's inventory limits")
    require(FAMILY in files and 0 < files[FAMILY]["sizeBytes"] <= MAX_FAMILY_MARKER_BYTES,
            f"The shared runtime must cover a valid {FAMILY}")
    for path in files:
        lower = path.casefold()
        require(path.startswith("native/") and not any(lower == item or item.endswith("/") and lower.startswith(item)
                                                        for item in OVERLAY_PATHS),
                f"Pack-owned data cannot be stored in the shared runtime: {path}")
    if staged:
        archive = spec["archive"]
        require(isinstance(archive, dict) and set(archive) == ASSET_FIELDS, "Invalid shared runtime archive")
        digest = check_record(archive)["sha256"]
        require(0 < archive["sizeBytes"] <= MAX_ARCHIVE_BYTES and safe_name(archive["name"]) == f"native-runtime-{digest}.zip",
                "The runtime ZIP name must contain its exact SHA-256")
    return files


def validate_family(value, spec):
    require(isinstance(value, dict) and set(value) == {"schemaVersion", "id", "version", "platform", "minecraftVersion"}
            and type(value["schemaVersion"]) is int and value["schemaVersion"] == 1
            and all(value[key] == spec[key] for key in ("id", "version", "platform")),
            "The runtime family marker differs from the shared runtime contract")
    return identifier(value["minecraftVersion"], "family Minecraft version")


def asset_file(dist, asset):
    return checked_file(dist, "assets/" + safe_name(asset["name"]), check_record(asset))


def verify_zip(path, expected, *, read_member=None):
    with zipfile.ZipFile(path) as archive:
        names, seen = set(), set()
        for entry in archive.infolist():
            name = safe_path(entry.filename).as_posix()
            mode = (entry.external_attr >> 16) & 0o170000
            require(entry.orig_filename == entry.filename and not entry.is_dir() and mode in (0, 0o100000)
                    and name in expected and name.casefold() not in seen,
                    f"Unexpected, linked or duplicate ZIP entry: {name}")
            names.add(name)
            seen.add(name.casefold())
            require(entry.file_size == expected[name]["sizeBytes"], f"Wrong ZIP member size: {name}")
            with archive.open(entry) as stream:
                require(hashlib.file_digest(stream, "sha256").hexdigest() == expected[name]["sha256"],
                        f"Wrong ZIP member SHA-256: {name}")
        require(names == expected.keys(), "The ZIP does not contain its complete declared file set")
        if read_member is not None:
            return archive.read(read_member)


def write_zip(assets, prefix, sources):
    temporary = assets / "payload.pending"
    with zipfile.ZipFile(temporary, "x", zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(sources):
            path = sources[name]
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.file_size = path.stat().st_size
            with path.open("rb") as source, archive.open(entry, "w") as entry_stream:
                shutil.copyfileobj(source, entry_stream, length=1024 * 1024)
    record = identity(temporary)
    name = f"{prefix}-{record['sha256']}.zip"
    temporary.rename(assets / name)
    return {"name": name, **record}


def output_directory(path, stage: Callable[[Path], object]):
    require(not path.exists() and not path.is_symlink(), "Use a fresh output directory")
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=path.parent) as temporary:
        output = Path(temporary) / "distribution"
        (output / "assets").mkdir(parents=True)
        result = stage(output)
        output.rename(path)
        return result


def stage_runtime(candidate, spec, out):
    files = validate_runtime(spec, staged=False)
    sources = {path: checked_file(candidate, path, record) for path, record in files.items()}
    validate_family(read(sources[FAMILY]), spec)

    def stage(output):
        archive = write_zip(output / "assets", "native-runtime", sources)
        result = {**spec, "archive": archive, "files": rows(files)}
        validate_runtime(result)
        verify_zip(asset_file(output, archive), files)
        write(output / CONTRACT, result)
        return result

    return output_directory(out, stage)


def read_runtime(dist):
    spec = read(checked_file(dist, CONTRACT))
    files = validate_runtime(spec)
    marker = verify_zip(asset_file(dist, spec["archive"]), files, read_member=FAMILY)
    return spec, files, validate_family(json.loads(marker), spec)


def bind_descriptor(descriptor, spec, files, minecraft):
    require(descriptor.get("schemaVersion") == 2 and descriptor.get("minecraftVersion") == minecraft
            and all(key not in descriptor for key in ("loader", "clientJar", "javaMajorVersion", "preserveWorldSettingsOnShare")),
            "Expected a native portable-pack descriptor with schemaVersion 2")
    native = descriptor["nativeRuntime"]
    require(isinstance(native, dict) and set(PACK_NATIVE_FIELDS) <= native.keys()
            and native["platform"] == spec["platform"] and native["adapter"] == "portable-native", "Invalid pack native runtime")
    identifier(native["version"], "pack Steam variant")
    executable = safe_path(native["executable"])
    require(executable.suffix.casefold() == ".exe" and executable.as_posix() in files
            and (executable.parent / "steam_api64.dll").as_posix() in files,
            "The pack executable and sibling Steam library must belong to the shared runtime")
    return {**descriptor, "nativeRuntime": {**native, "sharedRuntime": spec}}


def exact_source(root, tracked):
    def git(*args):
        return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()

    revision = git("rev-parse", "HEAD")
    require(Path(git("rev-parse", "--show-toplevel")).resolve() == root.resolve()
            and re.fullmatch(r"[0-9a-f]{40}", revision) and not git("status", "--porcelain", "--untracked-files=all"),
            "Prepare the pack from a clean checkout of its exact release commit")
    git("ls-files", "--error-unmatch", "--", *tracked)
    return revision


def assert_disjoint(common, overlay):
    # Check aliases and file/directory conflicts across both ownership domains.
    file_map(rows(common) + rows(overlay))


def pack_assets(output, pack_id, overlay_sources, standalone):
    require(len(set(standalone)) == len(standalone) and all(path in overlay_sources for path in standalone),
            "Every standalone path must be an overlay file and appear once")
    require(all(path != DESCRIPTOR and not path.startswith("launcher/") for path in standalone),
            "Descriptor and launcher metadata use their own archives")
    groups = [("descriptor", [DESCRIPTOR]), ("launcher", [path for path in overlay_sources if path.startswith("launcher/")])]
    groups.extend((f"extra-{index}", [path]) for index, path in enumerate(standalone, 1))
    separate = {path for _, group in groups for path in group}
    groups.append(("overlay", [path for path in overlay_sources if path not in separate]))
    result = []
    for label, paths in groups:
        if paths:
            asset = write_zip(output / "assets", f"{pack_id}-{label}", {path: overlay_sources[path] for path in paths})
            result.append({"kind": "zipRoot", **asset, "covers": sorted(paths)})
    return result


def migrate_pack(candidate, source, runtime, features, out, standalone):
    spec, common, minecraft = read_runtime(runtime)
    metadata = [DESCRIPTOR, "library/content.json", "library/loaded-mods.json", "library/preview.jpg"]
    pages = "library/mod-pages.json"
    if (candidate / pages).exists() or (source / pages).exists():
        metadata.append(pages)
    lock_path = "tools/distribution-lock.json"
    revision = exact_source(source, (*metadata, lock_path))
    approved = read(checked_file(source, lock_path))["files"]
    full = file_map(rows(approved))
    require(all(full.get(path) == expected for path, expected in common.items()),
            "The approved pack differs from the shared runtime bytes. Rebuild it for this exact runtime first.")
    candidate_files = {path: checked_file(candidate, path, expected) for path, expected in full.items()}
    for path in metadata:
        require(identity(checked_file(source, path)) == identity(checked_file(candidate, path)),
                f"The candidate differs from its exact source checkout: {path}")
    descriptor = read(candidate_files[DESCRIPTOR])
    require(bind_descriptor(descriptor, spec, common, minecraft) == descriptor,
            "Commit the exact shared runtime contract in portable-pack.json before packaging")
    content = read(checked_file(source, "library/content.json"))
    pack_id = identifier(content["id"], "pack ID")
    inherited = publish_features(features, pack_id, content["relativePath"])
    overlay = {path: expected for path, expected in full.items() if path not in common}
    assert_disjoint(common, overlay)
    require("native/presentation.json" in overlay, "The pack must supply its own presentation")

    def stage(output):
        assets = pack_assets(output, pack_id, {path: candidate_files[path] for path in overlay}, standalone)
        library = pack_library.stage_library(candidate, output, revision, shipped=full.keys())
        manifest = {"schemaVersion": 2, "revision": revision, "packId": pack_id, "name": content["name"],
                    "relativePath": content["relativePath"], "minecraftVersion": descriptor["minecraftVersion"],
                    "nativeRuntime": {key: descriptor["nativeRuntime"][key] for key in PACK_NATIVE_FIELDS},
                    "requiredLauncherFeatures": inherited,
                    "modCount": len(read(checked_file(candidate, "library/loaded-mods.json"))["mods"]),
                    "files": rows(overlay), "assets": assets, "library": library}
        write(output / MANIFEST, manifest)
        verify_pack(output, spec, common, minecraft)
        require(exact_source(source, (*metadata, lock_path)) == revision, "Pack source changed during export")
        return manifest

    return output_directory(out, stage)


def publish_features(manifest, pack_id, relative_path):
    require(manifest["schemaVersion"] == 2 and manifest["packId"] == pack_id and manifest["relativePath"] == relative_path,
            "The feature manifest identifies a different pack")
    value = manifest["requiredLauncherFeatures"]
    require(isinstance(value, list) and all(isinstance(item, str) and item for item in value)
            and len(set(value)) == len(value)
            and {"native-runtime-v1", "native-steam-v1", "minecraft-26-world-layout-v1"} <= set(value),
            "The feature manifest must retain the pack's required native launcher features")
    return value if FEATURE in value else [*value, FEATURE]


def verify_pack(dist, spec, common, minecraft):
    manifest = read(checked_file(dist, MANIFEST))
    require(re.fullmatch(r"[0-9a-f]{40}", manifest["revision"])
            and publish_features(manifest, manifest["packId"], manifest["relativePath"]) == manifest["requiredLauncherFeatures"],
            "Invalid shared pack manifest or required feature")
    overlay = file_map(manifest["files"])
    assert_disjoint(common, overlay)
    require({DESCRIPTOR, "native/presentation.json"} <= overlay.keys() and overlay[DESCRIPTOR]["sizeBytes"] <= MAX_DESCRIPTOR_BYTES,
            "Pack descriptor or presentation missing, or descriptor exceeds its size limit")
    covered, names, descriptor = set(), set(), None
    for asset in manifest["assets"]:
        name = safe_name(asset["name"])
        paths = asset["covers"]
        require(asset["kind"] == "zipRoot" and isinstance(paths, list) and paths and len(set(paths)) == len(paths)
                and set(paths) <= overlay.keys() and not (set(paths) & covered)
                and name.casefold() not in names and name.endswith(f"-{asset['sha256']}.zip"),
                "Invalid shared pack archive name or coverage")
        names.add(name.casefold())
        covered.update(paths)
        member = DESCRIPTOR if DESCRIPTOR in paths else None
        if member is not None:
            require(paths == [DESCRIPTOR] and asset["sizeBytes"] <= MAX_DESCRIPTOR_BYTES,
                    "Portable descriptor must have its own small archive")
        data = verify_zip(asset_file(dist, asset), {path: overlay[path] for path in paths}, read_member=member)
        if data is not None:
            descriptor = json.loads(data)
    require(covered == overlay.keys() and descriptor is not None, "Incomplete pack overlay coverage")
    require(bind_descriptor(descriptor, spec, common, minecraft) == descriptor
            and manifest["nativeRuntime"] == {key: descriptor["nativeRuntime"][key] for key in PACK_NATIVE_FIELDS}
            and manifest["minecraftVersion"] == descriptor["minecraftVersion"],
            "Pack descriptor, release manifest and runtime contract differ")
    library = manifest["library"]
    archive = asset_file(dist, library)
    require(library["revision"] == manifest["revision"], "Library has a different pack revision")
    pack_library.verify_library(dist, library)
    with zipfile.ZipFile(archive) as payload:
        card = json.loads(payload.read("library.json"))
    require(card["id"] == manifest["packId"] and card["relativePath"] == manifest["relativePath"]
            and card["name"] == manifest["name"] and card["minecraftVersion"] == manifest["minecraftVersion"]
            and type(manifest["modCount"]) is int and len(card["mods"]) == manifest["modCount"],
            "Library identifies a different pack")
    return manifest


def verify_family(runtime, distributions):
    spec, files, minecraft = read_runtime(runtime)
    packs = [verify_pack(dist, spec, files, minecraft) for dist in distributions]
    require(len({pack["packId"].casefold() for pack in packs}) == len(packs)
            and len({pack["relativePath"].casefold() for pack in packs}) == len(packs),
            "Pack IDs and installation paths must remain independent")
    return {"status": "PASS", "runtime": spec["archive"], "files": len(files),
            "packs": [{field: pack[field] for field in ("packId", "revision", "modCount")} for pack in packs]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    runtime = commands.add_parser("stage-runtime", help="package the exact common native files from an approved inventory")
    for name in ("candidate", "spec", "out"):
        runtime.add_argument(f"--{name}", required=True, type=Path)
    bind = commands.add_parser("bind-descriptor", help="print the exact source portable-pack.json for migration")
    for name in ("descriptor", "runtime"):
        bind.add_argument(f"--{name}", required=True, type=Path)
    migrate = commands.add_parser("migrate-pack", help="stage a committed pack overlay bound to the common runtime")
    for name in ("candidate", "source", "runtime", "out"):
        migrate.add_argument(f"--{name}", required=True, type=Path)
    migrate.add_argument("--features-from", required=True, type=Path,
                         help="verified prepared or previous pack manifest with the exact required features")
    migrate.add_argument("--standalone", action="append", default=[], help="keep this overlay path in its own archive")
    verify = commands.add_parser("verify-family", help="verify prepared pack overlays against one exact common runtime")
    verify.add_argument("--runtime", required=True, type=Path)
    verify.add_argument("--pack", required=True, action="append", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "stage-runtime":
            report = stage_runtime(args.candidate, read(args.spec), args.out)
        elif args.command == "bind-descriptor":
            spec, files, minecraft = read_runtime(args.runtime)
            report = bind_descriptor(read(args.descriptor), spec, files, minecraft)
        elif args.command == "migrate-pack":
            report = migrate_pack(args.candidate, args.source, args.runtime, read(args.features_from), args.out, args.standalone)
        else:
            report = verify_family(args.runtime, args.pack)
    except (AssertionError, KeyError, OSError, TypeError, ValueError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Native runtime release: {error}\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
