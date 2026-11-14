"""Stage an exact official CurseForge client for local pack preparation."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from copy import copy
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tomllib
from tempfile import TemporaryDirectory
import time
from urllib.error import HTTPError, URLError
from urllib.parse import unquote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import zipfile


ROOT = Path(__file__).resolve().parents[3]
WEBSITE = "www.curseforge.com"
CDN_HOSTS = {"mediafilez.forgecdn.net", "media.forgecdn.net", "edge.forgecdn.net"}
API = f"https://{WEBSITE}/api/v1"
MAX_JSON = 8 * 1024 * 1024
MAX_FILE = 2 * 1024**3 - 1
MAX_TREE = 50 * 1024**3
MAX_MEMBERS = 100_000
CHUNK = 1024 * 1024
WORKERS = 5
DEVICE = re.compile(r"(?i:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³]|conin\$|conout\$)")
LOADERS = {"forge", "neoforge", "fabric", "quilt"}
REQUIRED_SHARE_FEATURE = "preserve-steam-world-settings-v1"
LOGGING_CONFIG = "config/log4j2.xml"
LOGGING_FILES = {LOGGING_CONFIG, "launcher/jvm-args.txt"}
CLIENT_SCRIPT_PATH = re.compile(r"kubejs/client_scripts/lanmc_[a-z0-9_]+\.js")
MAX_CLIENT_SCRIPT_BYTES = 64 * 1024
BOOK_PAGE = r"(?:[a-z0-9_.-]+/)*[a-z0-9_.-]+"
BOOK_PAGES = (r"(?:kubejs/assets/[a-z0-9_.-]+/)?patchouli_books/[a-z0-9_.-]+/ru_ru/"
              r"(?:categories|entries|templates)/" + BOOK_PAGE + r"\.json")
BOOK_TINKERS_PAGES = r"kubejs/assets/[a-z0-9_.-]+/book/[a-z0-9_.-]+/ru_ru/" + BOOK_PAGE + r"\.json"
BOOK_GUIDEME_PAGES = r"kubejs/assets/[a-z0-9_.-]+/[a-z0-9_.-]+/_ru_ru/" + BOOK_PAGE + r"\.md"
BOOK_LANGUAGE = r"kubejs/assets/[a-z0-9_.-]+/lang/(?:en_us|ru_ru)\.json"
BOOK_QUESTS = r"config/ftbquests/quests/(?:chapter_groups|chapters/[a-z0-9_.-]+)\.snbt"
BOOK_NOTICE = r"licenses/[a-zA-Z0-9_.-]+\.txt"
BOOK_LOCALIZATION_MOD = r"mods/[a-z0-9]+(?:-[a-z0-9]+)*-localization-[0-9]+(?:\.[0-9]+)+\.jar"
MAX_BOOK_LOCALIZATION_MOD = 256 * 1024
BOOK_JAR_ROOT = r"data/[a-z0-9_.-]+/patchouli_books/[a-z0-9_.-]+/"
BOOK_JAR_PAGES = BOOK_JAR_ROOT + r"ru_ru/(?:categories|entries|templates)/(?:[a-z0-9_.-]+/)*[a-z0-9_.-]+\.json"
BOOK_JAR_DESCRIPTOR = BOOK_JAR_ROOT + r"book\.json"
QUEST_LANG = "config/ftbquests/quests/lang/"
RUSSIAN_QUESTS = QUEST_LANG + "ru_ru.snbt"
BOOTSTRAP_PATH = re.compile(r"lanminecraft/[a-z0-9][a-z0-9-]{0,63}\.jar")
MAX_BOOTSTRAP_BYTES = 256 * 1024
BOOTSTRAP_INPUT_KEYS = {"path", "sizeBytes", "sha256", "projectId", "fileId"}


def require(ok, message):
    if not ok:
        raise ValueError(message)


def unique_pairs(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, f"Duplicate JSON field: {name}")
        result[name] = value
    return result


def decode_json(data):
    require(len(data) <= MAX_JSON, "Oversized JSON input")
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_pairs)


def no_links(path):
    for entry in (path, *path.parents):
        try:
            info = entry.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode) and not (
            getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT),
            f"Linked input or output path: {entry}")


def read_local_json(path):
    no_links(path)
    require(path.is_file(), f"Not a regular input file: {path}")
    with path.open("rb") as source:
        return decode_json(source.read(MAX_JSON + 1))


def check_output(output, workspace=ROOT):
    output = Path(os.path.abspath(output))
    runs = workspace / "artifacts" / "agent-runs"
    require(output.is_relative_to(runs), f"Output must be below {runs}")
    parts = output.relative_to(runs).parts
    require(len(parts) >= 2, "Use a new child directory of an owned agent run")
    no_links(output)
    require(output.parent.is_dir() and not output.exists(), f"Expected new output directory: {output}")
    owner = read_local_json(runs / parts[0] / "RUN.json")
    require(isinstance(owner, dict) and isinstance(owner.get("owner"), str) and owner["owner"].strip(),
            "The task run has no owner")
    return output


def native_path(path):
    if os.name != "nt":
        return path
    absolute = os.path.abspath(path)
    if absolute.startswith("\\\\?\\"):
        return Path(absolute)
    if absolute.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + absolute[2:])
    return Path("\\\\?\\" + absolute)


def safe_path(value):
    require(isinstance(value, str) and value and len(value) <= 4096, "Invalid archive path")
    parts = value.split("/")
    for part in parts:
        require(part not in {"", ".", ".."} and part[-1] not in {".", " "} and
                len(part.encode("utf-16-le")) <= 510 and
                not any(ord(char) < 32 or char in '<>:"\\|?*' for char in part) and
                not DEVICE.fullmatch(part.split(".", 1)[0].rstrip(" ")),
                f"Unsafe Windows path: {value!r}")
    return PurePosixPath(*parts)


def reserve_path(tree, path, directory=False):
    for parent in reversed(path.parents):
        if parent == PurePosixPath("."):
            continue
        name = parent.as_posix()
        key = name.casefold()
        require(tree.get(key, (name, True)) == (name, True), f"Conflicting path: {name}")
        tree[key] = (name, True)
    name = path.as_posix()
    key = name.casefold()
    require(key not in tree or directory and tree[key] == (name, True), f"Duplicate or conflicting path: {name}")
    tree[key] = (name, directory)


def official_url(url, hosts):
    parsed = urlsplit(url)
    require(parsed.scheme == "https" and parsed.hostname in hosts and
            parsed.port in (None, 443) and not parsed.username and not parsed.password,
            "Download redirect is outside official CurseForge HTTPS origins")
    return parsed


class OfficialRedirect(HTTPRedirectHandler):
    def __init__(self, hosts):
        self.hosts = hosts

    def redirect_request(self, request, fp, code, msg, headers, newurl):
        official_url(newurl, self.hosts)
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def open_official(url, hosts, accept):
    official_url(url, hosts)
    opener = build_opener(OfficialRedirect(hosts))
    request = Request(url, headers={"Accept": accept, "User-Agent": "LANMinecraft-pack-preparation/1"})
    for attempt in range(3):
        try:
            response = opener.open(request, timeout=45)
            break
        except HTTPError as error:
            if error.code not in (408, 429, 500, 502, 503, 504) or attempt == 2:
                raise ValueError(f"CurseForge HTTP {error.code}: {url}") from error
        except (TimeoutError, URLError):
            if attempt == 2:
                raise
        time.sleep(attempt + 1)
    try:
        official_url(response.url, hosts)
        require(response.status == 200, f"Unexpected CurseForge status {response.status}: {url}")
    except Exception:
        response.close()
        raise
    return response


def positive(value):
    return type(value) is int and value > 0


def file_record(data, project_id, file_id):
    require(isinstance(data, dict) and positive(data.get("projectId")) and positive(data.get("id")) and
            data["projectId"] == project_id and data["id"] == file_id,
            f"CurseForge metadata is for another file: {project_id}/{file_id}")
    filename = data.get("fileName")
    require(len(safe_path(filename).parts) == 1, f"Invalid file name for {project_id}/{file_id}")
    size = data.get("fileLength")
    require(positive(size) and size <= MAX_FILE, f"Invalid byte length for {project_id}/{file_id}")
    origin_hashes = {}
    for entry in data.get("hashes") or []:
        require(isinstance(entry, dict) and type(entry.get("algo")) is int and entry["algo"] in (1, 2),
                f"Unsupported published hash for {project_id}/{file_id}")
        algorithm, digits = {1: ("sha1", 40), 2: ("md5", 32)}[entry["algo"]]
        value = entry.get("value")
        require(isinstance(value, str) and re.fullmatch(f"[0-9a-fA-F]{{{digits}}}", value) and
                algorithm not in origin_hashes, f"Invalid published hash for {project_id}/{file_id}")
        origin_hashes[algorithm] = value.lower()
    base = f"{API}/mods/{project_id}/files/{file_id}"
    return {"projectId": project_id, "fileId": file_id, "fileName": filename,
            "metadataUrl": base, "sourceUrl": base + "/download", "sizeBytes": size, "originHashes": origin_hashes}


class MissingFileMetadata(ValueError):
    pass


def get_record(project_id, file_id):
    uri = f"{API}/mods/{project_id}/files/{file_id}"
    with open_official(uri, {WEBSITE}, "application/json") as response:
        require(response.url == uri, f"Unexpected file metadata redirect: {uri}")
        payload = decode_json(response.read(MAX_JSON + 1))
    require(isinstance(payload, dict), f"Invalid file metadata: {uri}")
    if "data" in payload and payload["data"] is None:
        raise MissingFileMetadata(f"CurseForge has no metadata for the exact file: {project_id}/{file_id}")
    return file_record(payload.get("data"), project_id, file_id)


def exact_bytes(source, size, output=None, expected=None):
    expected = expected or {}
    algorithms = {name: hashlib.new(name, usedforsecurity=name == "sha256")
                  for name in {"sha256", *expected}}
    count = 0
    while data := source.read(min(CHUNK, size - count + 1)):
        count += len(data)
        require(count <= size, "Source is larger than its exact byte length")
        for algorithm in algorithms.values():
            algorithm.update(data)
        if output is not None:
            output.write(data)
    require(count == size, f"Byte length mismatch: expected {size}, got {count}")
    for name, value in expected.items():
        require(algorithms[name].hexdigest() == value, f"{name} mismatch")
    return algorithms["sha256"].hexdigest()


def pinned_hash(record, pin):
    expected = record["originHashes"].copy()
    if pin is not None:
        for field in ("projectId", "fileId", "fileName", "sourceUrl", "sizeBytes"):
            require(pin.get(field) == record[field], f"Official metadata changed for {record['fileName']}: {field}")
        sha256 = pin.get("sha256")
        require(isinstance(sha256, str) and re.fullmatch("[0-9a-f]{64}", sha256),
                f"Invalid pinned SHA256: {record['fileName']}")
        require(expected.get("sha256", sha256) == sha256,
                f"Official source and author lock disagree on SHA256: {record['fileName']}")
        expected["sha256"] = sha256
    return expected


def verify_cdn(url, record):
    parsed = official_url(url, CDN_HOSTS)
    file_id = record["fileId"]
    # CurseForge may omit leading zeroes in the second CDN path segment.
    parts = unquote(parsed.path, errors="strict").split("/")
    require(len(parts) == 5 and parts[:3] == ["", "files", str(file_id // 1000)] and
            re.fullmatch(r"[0-9]{1,3}", parts[3]) and int(parts[3]) == file_id % 1000 and
            parts[4] == record["fileName"],
            f"Official CDN returned another file: {record['projectId']}/{file_id}")


def acquire(record, destination, pin=None, local=None):
    expected = pinned_hash(record, pin)
    if local is not None:
        no_links(local)
        require(local.is_file(), f"Not a regular source archive: {local}")
        with local.open("rb") as source:
            return exact_bytes(source, record["sizeBytes"], expected=expected)
    with open_official(record["sourceUrl"], {WEBSITE, *CDN_HOSTS}, "application/octet-stream") as response:
        verify_cdn(response.url, record)
        if destination is None:
            return exact_bytes(response, record["sizeBytes"], expected=expected)
        with destination.open("xb") as output:
            return exact_bytes(response, record["sizeBytes"], output, expected)


def archive_members(archive):
    members = archive.infolist()
    require(len(members) <= MAX_MEMBERS, "Too many entries in official client archive")
    tree = {}
    exact_entries = set()
    total = 0
    result = {}
    for info in members:
        name = info.filename
        require(name == info.orig_filename and name not in exact_entries, f"Duplicate or invalid ZIP entry: {name!r}")
        exact_entries.add(name)
        directory = info.is_dir()
        path = safe_path(name[:-1] if directory else name)
        mode = stat.S_IFMT(info.external_attr >> 16)
        require(mode in (0, stat.S_IFDIR if directory else stat.S_IFREG) and not info.flag_bits & 1,
                f"Linked, special or encrypted ZIP entry: {name!r}")
        reserve_path(tree, path, directory)
        require(info.file_size <= MAX_FILE and (not directory or info.file_size == 0),
                f"Invalid ZIP entry size: {name!r}")
        if not directory:
            result[path] = info
            total += info.file_size
            require(total <= MAX_TREE, "Official ZIP exceeds the unpacked data budget")
    return result


def describe(manifest):
    require(isinstance(manifest, dict) and manifest.get("manifestType") == "minecraftModpack" and
            type(manifest.get("manifestVersion")) is int and manifest["manifestVersion"] == 1,
            "Expected a CurseForge Minecraft client export")
    minecraft = manifest.get("minecraft")
    require(isinstance(minecraft, dict), "Client export does not specify Minecraft")
    version = minecraft.get("version")
    require(isinstance(version, str) and re.fullmatch(r"[0-9]+\.[0-9]+(?:\.[0-9]+)?", version),
            "Invalid Minecraft version")
    loaders = minecraft.get("modLoaders")
    require(isinstance(loaders, list) and all(isinstance(row, dict) for row in loaders),
            "Invalid loader manifest")
    primary = [row.get("id") for row in loaders if row.get("primary") is True]
    require(len(primary) == 1 and isinstance(primary[0], str), "Expected exactly one primary loader")
    kind, _, loader_version = primary[0].partition("-")
    require(kind in LOADERS and re.fullmatch(r"[a-zA-Z0-9.][a-zA-Z0-9._+\-]*", loader_version),
            "Invalid primary loader")
    pack = {"name": manifest.get("name"), "version": manifest.get("version"),
            "minecraftVersion": version, "loader": {"type": kind, "version": loader_version}}
    require(all(isinstance(pack[key], str) and pack[key].strip() for key in ("name", "version")),
            "Missing client export name or version")
    declared = manifest.get("files")
    require(isinstance(declared, list) and len(declared) <= MAX_MEMBERS, "Invalid dependency list")
    deps = {}
    for item in declared:
        require(isinstance(item, dict) and positive(item.get("projectID")) and positive(item.get("fileID")) and
                type(item.get("required", True)) is bool, "Invalid client dependency")
        key = (item["projectID"], item["fileID"])
        require(key not in deps, f"Duplicate client dependency: {key}")
        deps[key] = item.get("required", True)
    overrides = manifest.get("overrides")
    require(isinstance(overrides, str), "Missing client override directory")
    return pack, deps, safe_path(overrides) if overrides else None


def take_overrides(archive, members, prefix, game, paths):
    if prefix is None:
        return []
    require(prefix not in members, "Client override path is a regular file")
    records = []
    for name, info in sorted(members.items(), key=lambda entry: entry[0].as_posix()):
        if not name.is_relative_to(prefix):
            continue
        path = name.relative_to(prefix)
        reserve_path(paths, path)
        with archive.open(info) as source:
            if game is None:
                sha256 = exact_bytes(source, info.file_size)
            else:
                target = game.joinpath(*path.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as output:
                    sha256 = exact_bytes(source, info.file_size, output)
        records.append({"path": path.as_posix(), "archivePath": name.as_posix(),
                        "sizeBytes": info.file_size, "sha256": sha256})
    return records


def placement_map(rows, deps):
    require(isinstance(rows, list), "Placements must be a JSON list")
    result = {}
    for row in rows:
        require(isinstance(row, dict) and positive(row.get("projectId")) and positive(row.get("fileId")),
                "Invalid placement identity")
        key = (row["projectId"], row["fileId"])
        require(key in deps and key not in result, f"Unknown or duplicate placement: {key}")
        result[key] = safe_path(row.get("directory"))
    return result


def fallback_records(document, deps):
    require(isinstance(document, dict) and set(document) == {"schemaVersion", "files"} and
            type(document["schemaVersion"]) is int and document["schemaVersion"] == 1 and
            isinstance(document["files"], list), "Invalid CurseForge fallback pins")
    records = {}
    for pin in document["files"]:
        require(isinstance(pin, dict) and
                set(pin) == {"projectId", "fileId", "fileName", "sizeBytes", "sha256", "sourceUrl"} and
                positive(pin["projectId"]) and positive(pin["fileId"]), "Invalid CurseForge fallback file pin")
        key = (pin["projectId"], pin["fileId"])
        require(deps.get(key) is True and key not in records,
                f"Fallback does not match a unique required author dependency: {key[0]}/{key[1]}")
        record = file_record({"projectId": key[0], "id": key[1], "fileName": pin["fileName"],
                              "fileLength": pin["sizeBytes"]}, *key)
        require(record["fileName"].lower().endswith(".jar") and isinstance(pin["sha256"], str) and
                re.fullmatch("[0-9a-f]{64}", pin["sha256"]) and isinstance(pin["sourceUrl"], str),
                "Fallback requires an exact mod JAR and SHA256")
        parsed = official_url(pin["sourceUrl"], CDN_HOSTS)
        require(not parsed.query and not parsed.fragment, "Fallback requires a direct CurseForge CDN file URL")
        verify_cdn(pin["sourceUrl"], record)
        records[key] = {**record, "sourceUrl": pin["sourceUrl"], "originHashes": {"sha256": pin["sha256"]}}
    return records


def dependency_plan(deps, placements, paths, pins, existing_size, fallbacks=None, overrides=None):
    def dependency_record(key):
        try:
            return get_record(*key)
        except MissingFileMetadata:
            if fallbacks and key in fallbacks:
                return fallbacks[key]
            raise

    plan = []
    shared_paths = set()
    keys = sorted(deps)
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        records = list(pool.map(dependency_record, keys))
    unknown = [f"{key[0]}/{key[1]} ({record['fileName']})"
               for key, record in zip(keys, records, strict=True)
               if key not in placements and key not in pins and not record["fileName"].lower().endswith(".jar")]
    require(not unknown, "Explicit placements needed for " + ", ".join(unknown))
    for key, record in zip(keys, records, strict=True):
        required = deps[key]
        directory = placements.get(key)
        pin = pins.get(key)
        if directory is None and pin is not None:
            directory = safe_path(pin.get("path")).parent
            require(directory != PurePosixPath("."), "Pinned dependency must have a directory")
        if directory is None:
            directory = PurePosixPath("mods")
        relative = directory / record["fileName"]
        name = relative.as_posix()
        author = overrides.get(name) if overrides else None
        if author is None:
            reserve_path(paths, relative)
            existing_size += record["sizeBytes"]
        else:
            require(paths.get(name.casefold()) == (name, False) and name not in shared_paths,
                    f"Duplicate or conflicting path: {name}")
            require(author["sizeBytes"] == record["sizeBytes"] and
                    (pin is None or pin.get("sha256") == author["sha256"]),
                    f"Official dependency conflicts with author override: {name}")
            shared_paths.add(name)
        record.update(path=name, required=required)
        if author is not None and pin is None:
            pin = {**record, "sha256": author["sha256"]}
        pinned_hash(record, pin)
        if pin is not None:
            require(pin.get("path") == record["path"] and pin.get("required") is required,
                    f"Dependency placement or requirement changed: {key}")
        require(existing_size <= MAX_TREE, "Resolved pack exceeds the data budget")
        plan.append((relative, record, pin))
    return plan


def write_json(path, document):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(document, output, ensure_ascii=False, sort_keys=True, indent=2)
        output.write("\n")


def acquire_dependency(game, entry, overrides=None, verified_source=None):
    relative, record, pin = entry
    target = game.joinpath(*relative.parts)
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        author = overrides.get(relative.as_posix()) if overrides else None
        if author is not None:
            no_links(target)
            with target.open("rb") as existing:
                exact_bytes(existing, author["sizeBytes"], expected={"sha256": author["sha256"]})
        local = verified_source.joinpath(*relative.parts) if verified_source is not None else None
        if local is not None:
            no_links(local)
        if local is not None and local.exists():
            require(pin is not None and local.is_file(), f"Expected a regular pinned dependency: {relative}")
            expected = pinned_hash(record, pin)
            with local.open("rb") as source:
                if author is None:
                    with target.open("xb") as output:
                        sha256 = exact_bytes(source, record["sizeBytes"], output, expected)
                else:
                    sha256 = exact_bytes(source, record["sizeBytes"], expected=expected)
        else:
            sha256 = acquire(record, None if author is not None else target, pin)
        require(author is None or sha256 == author["sha256"],
                f"Official dependency conflicts with author override: {relative}")
    except (OSError, ValueError) as error:
        raise ValueError(f"Dependency {record['projectId']}/{record['fileId']}: {error}") from error
    return {**record, "sha256": sha256}


def overlay_bytes(path):
    no_links(path)
    require(path.is_file(), f"Not a regular overlay input: {path}")
    with path.open("rb") as source:
        data = source.read(MAX_JSON + 1)
    require(len(data) <= MAX_JSON, f"Oversized overlay input: {path}")
    return data


def fingerprint(data):
    return {"sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def book_localization_pin(pin, fields=(), limit=MAX_JSON):
    return (isinstance(pin, dict) and set(pin) == {"sizeBytes", "sha256", *fields} and
            positive(pin["sizeBytes"]) and pin["sizeBytes"] <= limit and
            isinstance(pin["sha256"], str) and re.fullmatch("[a-f0-9]{64}", pin["sha256"]))


def matches_book_pin(pin, original):
    return original is not None and all(original.get(key) == value for key, value in pin.items())


def book_localization_jar_input(path, pin, author):
    key = pin["projectId"], pin["fileId"]
    require(all(map(positive, key)) and path.is_relative_to("mods") and len(path.parts) == 2 and
            path.suffix == ".jar" and any(row["required"] is True and
            (row["projectId"], row["fileId"]) == key for row in author["declaredDependencies"]),
            f"Review required for changed book localization dependency: {path}")
    if "files" in author:
        originals = [row for row in author["files"] if row["path"] == path.as_posix()]
        require(len(originals) == 1 and originals[0]["required"] is True and matches_book_pin(pin, originals[0]),
                f"Review required for changed book localization JAR bytes: {path}")


def book_localization_addition(path):
    return any(re.fullmatch(pattern, path) for pattern in
               (BOOK_PAGES, BOOK_TINKERS_PAGES, BOOK_GUIDEME_PAGES, BOOK_LANGUAGE, BOOK_NOTICE,
                BOOK_LOCALIZATION_MOD))


def validate_book_localization_mod(data, author):
    require(author["pack"]["loader"]["type"] == "forge" and 0 < len(data) <= MAX_BOOK_LOCALIZATION_MOD,
            "Book localization mod requires Forge and a bounded JAR")
    with zipfile.ZipFile(BytesIO(data)) as jar:
        members = archive_members(jar)
        names = {path.as_posix() for path in members}
        require(sum(member.file_size for member in members.values()) <= MAX_BOOK_LOCALIZATION_MOD and
                {"META-INF/mods.toml", "META-INF/MANIFEST.MF"} <= names,
                "Invalid book localization mod JAR")
        metadata = tomllib.loads(jar.read("META-INF/mods.toml").decode("utf-8"))
        mods = metadata.get("mods", [])
        require(metadata.get("modLoader") == "javafml" and isinstance(mods, list) and len(mods) == 1,
                "Book localization JAR must declare a single Forge mod")
        allowed_entries = r"(?:[a-zA-Z_0-9$/]+\.class|[a-z0-9_.-]+\.mixins\.json|" \
                          r"assets/[a-z0-9_.-]+/lang/(?:en_us|ru_ru)\.json|LICENSE(?:\.txt)?|pack\.mcmeta)"
        require(all(re.fullmatch(allowed_entries, name) for name in names - {"META-INF/mods.toml", "META-INF/MANIFEST.MF"}),
                "Unexpected book localization mod JAR member")
        require(not re.search(r"(?im)^(?:premain-class|agent-class|launcher-agent-class|class-path):",
                              jar.read("META-INF/MANIFEST.MF").decode("utf-8")),
                "Book localization mod must load through Forge")


def book_localizations(recipe, pack_source, author):
    rows = recipe.get("bookLocalization", [])
    if "bookLocalization" not in recipe:
        return {}
    require(isinstance(rows, list) and rows, "Expected nonempty book localization recipe")
    overrides = {row["path"].casefold(): row for row in author["overrides"]}
    destinations = {}
    localized = {}
    keys = {"path", "inputs", "output"}
    for row in rows:
        require(isinstance(row, dict) and set(row) in (keys, keys | {"before"}),
                "Invalid book localization entry")
        path = safe_path(row["path"])
        name = path.as_posix()
        reserve_path(destinations, path)
        if "before" in row:
            original = overrides.get(name.casefold())
            replaceable = re.fullmatch(BOOK_QUESTS, name) or re.fullmatch(BOOK_LANGUAGE, name)
            require(replaceable and book_localization_pin(row["before"]) and
                    matches_book_pin({**row["before"], "path": name}, original),
                    f"Review required for changed book localization replacement: {name}")
        else:
            require(book_localization_addition(name) and name.casefold() not in overrides,
                    f"Review required for duplicate, conflicting or unsupported book localization output: {name}")
        inputs = row["inputs"]
        require(isinstance(inputs, dict) and inputs, f"Missing book localization inputs: {name}")
        for source, pin in inputs.items():
            source_path = safe_path(source)
            if book_localization_pin(pin, ("projectId", "fileId"), MAX_FILE):
                book_localization_jar_input(source_path, pin, author)
            else:
                original = overrides.get(source.casefold())
                require(book_localization_pin(pin) and matches_book_pin({**pin, "path": source}, original),
                        f"Review required for changed book localization input: {source}")
        data = overlay_bytes(pack_source / path)
        require(book_localization_pin(row["output"]) and fingerprint(data) == row["output"],
                f"Book localization differs from its pin: {name}")
        if path.suffix == ".json":
            content = decode_json(data)
            require(isinstance(content, dict) and content, f"Expected book localization JSON object: {name}")
            if re.fullmatch(BOOK_LANGUAGE, name):
                require(all(isinstance(value, str) for value in content.values()), f"Expected language strings: {name}")
        if re.fullmatch(BOOK_LOCALIZATION_MOD, name):
            validate_book_localization_mod(data, author)
        localized[name] = data
    return localized


def overlay_additions(recipe, pack_source, author):
    if "additions" not in recipe:
        return {}
    rows = recipe["additions"]
    require(isinstance(rows, list) and len(rows) == len(LOGGING_FILES), "Expected the two pack logging files")
    author_paths = {row["path"].casefold() for row in author["overrides"]}
    additions = {}
    for row in rows:
        require(isinstance(row, dict) and set(row) == {"path", "sizeBytes", "sha256"}, "Invalid logging pin")
        name = safe_path(row["path"]).as_posix()
        require(name in LOGGING_FILES and name not in additions and name.casefold() not in author_paths,
                f"Review required for a duplicate or conflicting logging path: {name}")
        data = overlay_bytes(pack_source / name)
        require(data and type(row["sizeBytes"]) is int and fingerprint(data) ==
                {key: row[key] for key in ("sizeBytes", "sha256")}, f"Logging file differs from its pin: {name}")
        additions[name] = data
    return additions


def client_scripts(recipe, pack_source, author):
    if "clientScripts" not in recipe:
        return {}
    policy = recipe["clientScripts"]
    require(isinstance(policy, dict) and set(policy) == {"dependencies", "files"},
            "Invalid KubeJS client script overlay")
    dependencies = policy["dependencies"]
    require(isinstance(dependencies, list) and dependencies and
            all(isinstance(row, dict) and set(row) == {"projectId", "fileId"} and
                all(positive(value) for value in row.values()) for row in dependencies),
            "Invalid required client script dependencies")
    required = {(row["projectId"], row["fileId"]) for row in dependencies}
    require(len(required) == len(dependencies), "Duplicate client script dependency")
    excluded = set(overlay_dependency_keys(recipe, author))
    available = {(row["projectId"], row["fileId"]) for row in author["declaredDependencies"]
                 if row["required"] is True}
    require(required <= available - excluded, "Review required for changed, missing or excluded client script dependency")
    authored = {row["path"].casefold() for row in [*author["overrides"], *author.get("files", [])]}
    rows = policy["files"]
    require(isinstance(rows, list) and rows, "Missing KubeJS client scripts")
    scripts = {}
    for row in rows:
        require(isinstance(row, dict) and set(row) == {"path", "sizeBytes", "sha256"}, "Invalid client script pin")
        name = safe_path(row["path"]).as_posix()
        require(CLIENT_SCRIPT_PATH.fullmatch(name) and name not in scripts and name.casefold() not in authored,
                f"Review required for a duplicate or conflicting client script: {name}")
        data = overlay_bytes(pack_source / name)
        require(0 < len(data) <= MAX_CLIENT_SCRIPT_BYTES and type(row["sizeBytes"]) is int
                and fingerprint(data) == {key: row[key] for key in ("sizeBytes", "sha256")},
                f"Client script differs from its pin: {name}")
        scripts[name] = data
    return scripts


def quest_translation(recipe, pack_source, author):
    if "questTranslation" not in recipe:
        return {}
    translation = recipe["questTranslation"]
    require(isinstance(translation, dict) and set(translation) == {"inputs", "output"},
            "Invalid FTB Quests translation recipe")
    inputs = translation["inputs"]
    require(isinstance(inputs, dict) and QUEST_LANG + "en_us.snbt" in inputs and
            set(inputs) <= {QUEST_LANG + "en_us.snbt", QUEST_LANG + "zh_cn.snbt"},
            "Expected the pinned FTB Quests source languages")
    overrides = {row["path"].casefold(): {key: row[key] for key in ("sizeBytes", "sha256")}
                 for row in author["overrides"]}
    require(RUSSIAN_QUESTS not in overrides, "Review required for the author's new Russian quest translation")
    for path, pin in inputs.items():
        require(path in overrides and overrides[path] == pin, f"Review required for changed quest text: {path}")
    russian = overlay_bytes(pack_source / RUSSIAN_QUESTS)
    require(russian and fingerprint(russian) == translation["output"], "Russian quest text differs from its pin")
    return {RUSSIAN_QUESTS: russian}


def overlay_bootstrap(recipe, pack_source, author, additions):
    if "bootstrap" not in recipe:
        return {}
    bootstrap = recipe["bootstrap"]
    require(isinstance(bootstrap, dict) and set(bootstrap) == {"input", "artifact"}, "Invalid Java 8 bootstrap recipe")
    source, artifact = bootstrap["input"], bootstrap["artifact"]
    pin_keys = {"path", "sizeBytes", "sha256"}
    require(isinstance(source, dict) and set(source) == BOOTSTRAP_INPUT_KEYS and
            isinstance(artifact, dict) and set(artifact) == pin_keys and
            all(positive(pin["sizeBytes"]) and isinstance(pin["sha256"], str) and
                re.fullmatch("[a-f0-9]{64}", pin["sha256"]) for pin in (source, artifact)) and
            all(positive(source[key]) for key in ("projectId", "fileId")), "Invalid Java 8 bootstrap pins")
    source_path = safe_path(source["path"])
    name = safe_path(artifact["path"]).as_posix()
    platform = author["pack"]
    require(platform["minecraftVersion"] == "1.12.2" and platform["loader"]["type"] == "forge" and
            source_path.is_relative_to("mods") and len(source_path.parts) == 2 and source_path.suffix == ".jar" and
            BOOTSTRAP_PATH.fullmatch(name) and artifact["sizeBytes"] <= MAX_BOOTSTRAP_BYTES,
            "Unsupported Java 8 bootstrap platform or paths")
    key = source["projectId"], source["fileId"]
    declared = [row for row in author["declaredDependencies"] if row["projectId"] == key[0]]
    require(declared == [{"projectId": key[0], "fileId": key[1], "required": True}] and
            key not in overlay_dependency_keys(recipe, author), "Review required for a changed Java 8 bootstrap dependency")
    authored = [*author["overrides"], *author.get("files", [])]
    if author.get("status") == "complete":
        matched = [row for row in author["files"] if row["projectId"] == key[0]]
        require(len(matched) == 1 and matched[0]["required"] is True and
                all(matched[0][field] == value for field, value in source.items()),
                "Review required for changed Java 8 bootstrap input bytes")
    require(not any(row["path"].casefold() == name.casefold() for row in authored),
            "Java 8 bootstrap artifact collides with an author file")
    args = additions.get("launcher/jvm-args.txt", b"").decode("utf-8").splitlines()
    require([line.strip() for line in args if line.lstrip().startswith("-javaagent:")] == [f"-javaagent:{name}"],
            "Java 8 bootstrap needs its exact launcher JVM argument")
    data = overlay_bytes(pack_source / name)
    require(fingerprint(data) == {key: artifact[key] for key in ("sizeBytes", "sha256")},
            "Java 8 bootstrap artifact differs from its pin")
    try:
        validate_bootstrap_jar(data)
    except zipfile.BadZipFile as error:
        raise ValueError("Invalid Java 8 bootstrap JAR") from error
    return {name: data}


def validate_bootstrap_jar(data):
    with zipfile.ZipFile(BytesIO(data)) as jar:
        entries = [info for info in jar.infolist() if not info.is_dir()]
        names = {info.filename for info in entries}
        manifest = "META-INF/MANIFEST.MF"
        require(len(entries) == len(names) and manifest in names and
                sum(info.file_size for info in entries) <= MAX_BOOTSTRAP_BYTES,
                "Invalid Java 8 bootstrap JAR members")
        for info in jar.infolist():
            safe_path(info.filename.rstrip("/"))
        attributes = jar.read(manifest).decode("utf-8").replace("\r\n ", "").replace("\n ", "").splitlines()
        require(all(not line or line.startswith(("Manifest-Version: ", "Premain-Class: ", "Created-By: "))
                    for line in attributes), "Unexpected Java 8 bootstrap manifest attribute")
        premain = [line[len("Premain-Class: "):] for line in attributes if line.startswith("Premain-Class: ")]
        classes = names - {manifest}
        require(len(premain) == 1 and premain[0].startswith("lanminecraft.") and
                premain[0].replace(".", "/") + ".class" in classes and
                all(re.fullmatch(r"lanminecraft/(?:[A-Za-z0-9_$]+/)*[A-Za-z0-9_$]+\.class", name) for name in classes),
                "Java 8 bootstrap JAR must contain only its named launcher helper classes")
        for name in classes:
            with jar.open(name) as stream:
                header = stream.read(8)
            require(len(header) == 8 and header[:4] == b"\xca\xfe\xba\xbe" and int.from_bytes(header[6:], "big") <= 52,
                    "Java 8 bootstrap JAR contains unsupported bytecode")


def overlay_dependency_keys(recipe, author):
    items = recipe.get("excludedDependencies", [])
    require(isinstance(items, list), "Invalid author dependency exclusions")
    if not items:
        return []
    declared = {(row["projectId"], row["fileId"]) for row in author["declaredDependencies"]
                if row["required"] is True}
    keys = []
    for item in items:
        require(isinstance(item, dict) and set(item) == {"projectId", "fileId"} and
                positive(item["projectId"]) and positive(item["fileId"]), "Invalid author dependency exclusion")
        key = (item["projectId"], item["fileId"])
        require(key not in keys and key in declared,
                f"Review required for changed or removed author dependency: {key[0]}/{key[1]}")
        keys.append(key)
    return keys


def jar_metadata_patches(recipe, author, pack_source):
    rows = recipe.get("jarMetadataPatches", [])
    require(isinstance(rows, list), "Invalid dependency JAR metadata patches")
    if not rows:
        return []
    declared = {(row["projectId"], row["fileId"]) for row in author["declaredDependencies"]
                if row["required"] is True}
    excluded = set(overlay_dependency_keys(recipe, author))
    overrides = {row["path"].casefold() for row in author["overrides"]}
    resolved = {row["path"]: row for row in author.get("files", [])}
    paths = {}
    patched = set()
    for row in rows:
        require(isinstance(row, dict) and set(row) == {
            "path", "projectId", "fileId", "before", "after", "metadata", "license"
        }, "Invalid dependency JAR metadata patch")
        path = safe_path(row["path"])
        reserve_path(paths, path)
        key = row["projectId"], row["fileId"]
        require(len(path.parts) == 2 and path.parts[0] == "mods" and path.suffix == ".jar" and
                row["path"].casefold() not in overrides and key in declared and key not in excluded and key not in patched,
                f"Review required for changed dependency JAR: {path}")
        patched.add(key)
        for pin in (row["before"], row["after"]):
            require(isinstance(pin, dict) and set(pin) == {"sizeBytes", "sha256"} and
                    positive(pin["sizeBytes"]) and pin["sizeBytes"] <= MAX_JSON and
                    isinstance(pin["sha256"], str) and re.fullmatch("[0-9a-f]{64}", pin["sha256"]),
                    f"Invalid dependency JAR pin: {path}")
        if "files" in author:
            original = resolved.get(row["path"])
            require(original is not None and (original["projectId"], original["fileId"]) == key and
                    row["before"] == {field: original[field] for field in ("sizeBytes", "sha256")},
                    f"Review required for changed dependency JAR bytes: {path}")
        metadata = row["metadata"]
        require(isinstance(metadata, dict) and isinstance(metadata.get("pack"), dict) and
                positive(metadata["pack"].get("pack_format")), f"Invalid replacement pack.mcmeta: {path}")
        license_path = safe_path(row["license"])
        require(license_path.parts[0] == "licenses" and len(license_path.parts) == 2,
                f"Invalid dependency JAR license path: {path}")
        require(overlay_bytes(pack_source / license_path), f"Empty dependency JAR license: {path}")
    return rows


def book_jar_localizations(recipe, author, pack_source, metadata_patches=()):
    rows = recipe.get("bookJarLocalization", [])
    if "bookJarLocalization" not in recipe:
        return []
    require(isinstance(rows, list) and rows, "Expected nonempty dependency JAR book localizations")
    excluded = set(overlay_dependency_keys(recipe, author))
    claimed = {(row["projectId"], row["fileId"]) for row in metadata_patches}
    overrides = {row["path"].casefold() for row in author["overrides"]}
    paths = {}
    for row in rows:
        require(isinstance(row, dict) and set(row) == {"path", "projectId", "fileId", "before", "after", "entries"},
                "Invalid dependency JAR book localization")
        path = safe_path(row["path"])
        reserve_path(paths, path)
        key = row["projectId"], row["fileId"]
        require(book_localization_pin(row["before"]) and book_localization_pin(row["after"]) and
                row["path"].casefold() not in overrides and key not in excluded and key not in claimed,
                f"Conflicting localization or invalid dependency JAR pins: {path}")
        claimed.add(key)
        book_localization_jar_input(path, {**row["before"], "projectId": key[0], "fileId": key[1]}, author)
        book_jar_resources(pack_source, row["entries"])
    return rows


def book_jar_resources(pack_source, entries):
    require(isinstance(entries, list) and entries, "Expected localized dependency JAR book files")
    paths = {}
    resources = {}
    keys = {"path", "source", "output"}
    for entry in entries:
        require(isinstance(entry, dict) and set(entry) in (keys, keys | {"before"}),
                "Invalid dependency JAR book member")
        path = safe_path(entry["path"])
        source = safe_path(entry["source"])
        name = path.as_posix()
        reserve_path(paths, path)
        if name in {"LICENSE", "NOTICE", "LICENSE.txt", "NOTICE.txt"}:
            valid = "before" not in entry and source.parts[0] == "licenses" and len(source.parts) == 2
        elif "before" in entry:
            valid = re.fullmatch(BOOK_JAR_DESCRIPTOR, name) and book_localization_pin(entry["before"])
        else:
            valid = re.fullmatch(BOOK_JAR_PAGES, name)
        require(valid and (source.parts[0] == "licenses" or source == PurePosixPath("tools/book-localization") / path),
                f"Unsupported dependency JAR book member: {name}")
        data = overlay_bytes(pack_source / source)
        require(book_localization_pin(entry["output"]) and fingerprint(data) == entry["output"],
                f"Dependency JAR resource differs from its pin: {name}")
        if path.suffix == ".json":
            content = decode_json(data)
            require(isinstance(content, dict) and content, f"Expected book resource JSON object: {name}")
        resources[name] = data
    require(any(re.fullmatch(BOOK_JAR_PAGES, name) for name in resources), "Expected translated dependency JAR pages")
    return resources


def rebuild_book_jar(original, resources, before=None):
    before = before or {}
    output = BytesIO()
    with zipfile.ZipFile(BytesIO(original)) as source, zipfile.ZipFile(output, "w") as jar:
        archive_members(source)
        members = source.infolist()
        require(sum(member.file_size for member in members) <= 64 * MAX_JSON, "Oversized unpacked dependency JAR")
        paths = {}
        for member in members:
            name = member.filename
            upper = name.upper()
            require(not (upper.startswith("META-INF/") and
                         (upper.endswith((".SF", ".RSA", ".DSA", ".EC")) or upper.rsplit("/", 1)[-1].startswith("SIG-"))),
                    "Cannot modify a signed dependency JAR")
            reserve_path(paths, safe_path(name.rstrip("/")), member.is_dir())
        for name in resources:
            if name not in before:
                reserve_path(paths, safe_path(name))
        for notice in ("LICENSE", "NOTICE"):
            require(any(name in paths and paths[name][1] is False for name in
                        (notice.casefold(), notice.casefold() + ".txt")),
                    f"Expected dependency JAR {notice} (existing or pinned addition)")
        for name, pin in before.items():
            old_bytes = source.read(name)
            require(fingerprint(old_bytes) == pin, f"Dependency JAR book descriptor differs: {name}")
            old = decode_json(old_bytes)
            new = decode_json(resources[name])
            translatable = {key: new[key] for key in ("name", "landing_text") if key in new and new[key] != old.get(key)}
            require(translatable and all(isinstance(value, str) and re.fullmatch(r"[a-z0-9_.-]+", value)
                                         for value in translatable.values()) and {**old, **translatable} == new,
                    f"Only title and landing translation keys may change in a dependency JAR: {name}")
        jar.comment = source.comment
        for member in members:
            data = resources[member.filename] if member.filename in before else source.read(member)
            jar.writestr(copy(member), data, compress_type=zipfile.ZIP_STORED)
        for name in sorted(resources.keys() - before.keys()):
            jar.writestr(zipfile.ZipInfo(name), resources[name])
    return output.getvalue()


def apply_book_jar_localizations(game, pack_source, receipt):
    recipe = receipt["overlay"]
    rows = book_jar_localizations(recipe, receipt, pack_source, recipe.get("jarMetadataPatches", ()))
    for row in rows:
        target = game / safe_path(row["path"])
        original = overlay_bytes(target)
        require(fingerprint(original) == row["before"], f"Dependency JAR differs: {row['path']}")
        resources = book_jar_resources(pack_source, row["entries"])
        before = {entry["path"]: entry["before"] for entry in row["entries"] if "before" in entry}
        modified = rebuild_book_jar(original, resources, before)
        require(fingerprint(modified) == row["after"], f"Localized dependency JAR differs: {row['path']}")
        target.write_bytes(modified)


def rebuild_jar_metadata(original, metadata, license_bytes):
    output = BytesIO()
    with zipfile.ZipFile(BytesIO(original)) as source, zipfile.ZipFile(output, "w") as jar:
        archive_members(source)
        names = source.namelist()
        require("pack.mcmeta" in names and "LICENSE" not in names,
                "Expected pack.mcmeta and no root LICENSE in the dependency JAR")
        require(not any(name.upper().startswith("META-INF/") and
                        (name.upper().endswith((".SF", ".RSA", ".DSA", ".EC")) or
                         name.upper().rsplit("/", 1)[-1].startswith("SIG-")) for name in names),
                "Cannot modify a signed dependency JAR")
        fixed_metadata = (json.dumps(metadata, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")
        for member in source.infolist():
            data = fixed_metadata if member.filename == "pack.mcmeta" else source.read(member)
            jar.writestr(copy(member), data, compress_type=zipfile.ZIP_STORED)
        jar.writestr(zipfile.ZipInfo("LICENSE"), license_bytes)
    return output.getvalue()


def apply_jar_metadata_patches(game, pack_source, receipt):
    rows = jar_metadata_patches(receipt["overlay"], receipt, pack_source)
    for row in rows:
        target = game / safe_path(row["path"])
        original = overlay_bytes(target)
        require(fingerprint(original) == row["before"], f"Dependency JAR differs: {row['path']}")
        modified = rebuild_jar_metadata(original, row["metadata"], overlay_bytes(pack_source / row["license"]))
        require(fingerprint(modified) == row["after"], f"Patched dependency JAR differs: {row['path']}")
        target.write_bytes(modified)


def excluded_dependency_files(keys, files):
    matching = [row for row in files if (row["projectId"], row["fileId"]) in keys]
    require(len(matching) == len(keys), "Missing or duplicate checked author dependency")
    selected = {(row["projectId"], row["fileId"]): row for row in matching}
    result = []
    for key in keys:
        row = selected.get(key)
        require(row is not None and row.get("required") is True and positive(row.get("sizeBytes")) and
                isinstance(row.get("sha256"), str) and re.fullmatch("[0-9a-f]{64}", row["sha256"]),
                f"Missing checked author dependency: {key[0]}/{key[1]}")
        path = safe_path(row.get("path"))
        require(path.is_relative_to("mods") and path.suffix.lower() == ".jar",
                f"An excluded author dependency must be a mod JAR: {path}")
        result.append({field: row[field] for field in ("projectId", "fileId", "path", "sizeBytes", "sha256")})
    return result


def exclude_dependencies(game, rows):
    targets = []
    for row in rows:
        target = game.joinpath(*safe_path(row["path"]).parts)
        no_links(target)
        require(target.is_file(), f"Expected checked author dependency to exclude: {row['path']}")
        with target.open("rb") as source:
            exact_bytes(source, row["sizeBytes"], expected={"sha256": row["sha256"]})
        targets.append(target)
    for target in targets:
        target.unlink()


def apply_overlay(game, pack_source, receipt, paths):
    raw = overlay_bytes(pack_source / "tools/overlay.json")
    recipe = decode_json(raw)
    require(isinstance(recipe, dict) and set(recipe) - {
                "excludedDependencies", "additions", "bootstrap", "jarMetadataPatches", "questTranslation",
                "bookLocalization", "bookJarLocalization", "clientScripts"} ==
            {"schemaVersion", "authorArchiveSha256", "descriptorSha256", "patches"} and
            type(recipe["schemaVersion"]) is int and recipe["schemaVersion"] == 1 and
            isinstance(recipe["patches"], list), "Invalid author overlay recipe")
    require(recipe["authorArchiveSha256"] == receipt["client"]["sha256"],
            "Overlay does not match the pinned author archive")
    dependency_keys = overlay_dependency_keys(recipe, receipt)
    jar_patches = jar_metadata_patches(recipe, receipt, pack_source)
    book_jars = book_jar_localizations(recipe, receipt, pack_source, jar_patches)
    additions = overlay_additions(recipe, pack_source, receipt)
    scripts = client_scripts(recipe, pack_source, receipt)
    bootstrap = overlay_bootstrap(recipe, pack_source, receipt, additions)
    quests = quest_translation(recipe, pack_source, receipt)
    localized = book_localizations(recipe, pack_source, receipt)
    localized_replacements = {row["path"]: row["before"] for row in recipe.get("bookLocalization", []) if "before" in row}
    localized_additions = localized.keys() - localized_replacements.keys()
    for name in localized_additions:
        reserve_path(paths, safe_path(name))
    for name in additions.keys() | bootstrap.keys() | quests.keys() | scripts.keys():
        reserve_path(paths, safe_path(name))
    descriptor = overlay_bytes(pack_source / "portable-pack.json")
    descriptor_pin = fingerprint(descriptor)
    descriptor_data = decode_json(descriptor)
    require(descriptor_pin["sha256"] == recipe["descriptorSha256"] and
            isinstance(descriptor_data, dict) and descriptor_data.get("preserveWorldSettingsOnShare") is True and
            descriptor_data == {**receipt["descriptor"], "preserveWorldSettingsOnShare": True},
            "Overlay descriptor differs from the pinned platform or share policy")

    author_files = {row["path"]: row for row in receipt["overrides"]}
    edits = {}
    applied = []
    for patch in recipe["patches"]:
        common_keys = {"path", "before", "after"}
        require(isinstance(patch, dict) and set(patch) in
                (common_keys | {"from", "to"}, common_keys | {"replacements"}),
                "Invalid author overlay patch")
        relative = safe_path(patch["path"])
        name = relative.as_posix()
        author = author_files.get(name)
        require(name not in edits and author is not None and
                patch["before"] == {key: author[key] for key in ("sizeBytes", "sha256")},
                f"Overlay input is not a unique pinned author file: {name}")
        target = game.joinpath(*relative.parts)
        old = overlay_bytes(target)
        require(fingerprint(old) == patch["before"], f"Staged overlay input differs: {name}")
        replacements = patch["replacements"] if "replacements" in patch else [{"from": patch["from"], "to": patch["to"]}]
        require(isinstance(replacements, list) and replacements, f"Missing overlay replacements: {name}")
        new = old
        for replacement in replacements:
            require(isinstance(replacement, dict) and set(replacement) == {"from", "to"} and
                    isinstance(replacement["from"], str) and replacement["from"] and
                    isinstance(replacement["to"], str) and replacement["from"] != replacement["to"],
                    f"Invalid replacement in overlay: {name}")
            old_text, new_text = replacement["from"].encode("utf-8"), replacement["to"].encode("utf-8")
            require(new.count(old_text) == 1, f"Expected one overlay replacement in {name}")
            new = new.replace(old_text, new_text, 1)
        require(fingerprint(new) == patch["after"], f"Overlay output differs from its pin: {name}")
        edits[name] = (target, new)
        applied.append({"path": name, "before": patch["before"], "after": patch["after"]})
    for name, pin in localized_replacements.items():
        target = game / safe_path(name)
        require(name not in edits and fingerprint(overlay_bytes(target)) == pin,
                f"Conflicting or changed staged book localization input: {name}")
        edits[name] = (target, localized[name])

    result = {"recipeSha256": fingerprint(raw)["sha256"], "descriptor": descriptor_pin, "patches": applied,
              "requiredLauncherFeatures": [REQUIRED_SHARE_FEATURE]}
    if dependency_keys:
        result["excludedDependencies"] = recipe["excludedDependencies"]
    owned_names = set(edits)
    if jar_patches:
        result["jarMetadataPatches"] = jar_patches
    if book_jars:
        result["bookJarLocalization"] = book_jars
    if additions:
        owned_names.add(LOGGING_CONFIG)
        result["additions"] = recipe["additions"]
    if scripts:
        owned_names.update(scripts)
        result["clientScripts"] = recipe["clientScripts"]
    if bootstrap:
        owned_names.update(bootstrap)
        result["bootstrap"] = recipe["bootstrap"]
    if quests:
        owned_names.update(quests)
        result["questTranslation"] = recipe["questTranslation"]
    if localized:
        owned_names.update(localized)
        result["bookLocalization"] = recipe["bookLocalization"]
    if owned_names:
        owned_path = PurePosixPath("launcher/pack-owned.txt")
        reserve_path(paths, owned_path)
        owned = overlay_bytes(pack_source.joinpath(*owned_path.parts))
        require(owned == ("\n".join(sorted(owned_names)) + "\n").encode("utf-8"),
                "Pack ownership must list exactly the patched files and owned overlay artifacts")
        target = game.joinpath(*owned_path.parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(owned)
        result["ownership"] = fingerprint(owned)
    for target, data in edits.values():
        target.write_bytes(data)
    for name, data in {**additions, **bootstrap, **quests, **scripts,
                       **{name: localized[name] for name in localized_additions}}.items():
        target = game / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(data)
    with (game / "portable-pack.json").open("xb") as output:
        output.write(descriptor)
    return result


def prepare(args):
    out = check_output(args.output)
    expected = read_local_json(args.expect) if args.expect is not None else {}
    if args.expect is not None:
        require(isinstance(expected, dict) and expected.get("schemaVersion") == 1 and
                expected.get("status") in {"client-only", "complete"} and isinstance(expected.get("client"), dict),
                "Expected a valid earlier intake receipt")
    verified_source = getattr(args, "verified_dependencies", None)
    if verified_source is not None:
        require(not args.client_only and args.client_archive is not None and expected.get("status") == "complete",
                "Reuse requires the exact local author archive and a complete dependency lock")
        verified_source = Path(os.path.abspath(verified_source))
        runs = ROOT / "artifacts" / "agent-runs"
        run = runs / out.relative_to(runs).parts[0]
        no_links(verified_source)
        require(verified_source.is_relative_to(run) and verified_source.is_dir(),
                "Verified dependency source must be within the same owned run")
        verified_source = native_path(verified_source)
    client = get_record(args.project_id, args.file_id)
    require(client["fileName"].lower().endswith(".zip"), "Client file is not a ZIP export")
    with TemporaryDirectory(prefix=".curseforge-", dir=native_path(out.parent)) as temporary:
        stage = Path(temporary) / "stage"
        stage.mkdir()
        source = args.client_archive if args.client_archive is not None else stage / "client.zip"
        client["sha256"] = acquire(client, source, expected.get("client"), args.client_archive)
        game = None if args.client_only else stage / "game"
        if game is not None:
            game.mkdir()
        paths = {}
        descriptor_path = PurePosixPath("portable-pack.json")
        reserve_path(paths, descriptor_path)
        with zipfile.ZipFile(source) as archive:
            members = archive_members(archive)
            entry = members.get(PurePosixPath("manifest.json"))
            require(entry is not None and entry.file_size <= MAX_JSON, "Missing or oversized CurseForge manifest.json")
            pack, deps, prefix = describe(decode_json(archive.read(entry)))
            overrides = take_overrides(archive, members, prefix, game, paths)
        receipt = {"schemaVersion": 1, "status": "client-only", "client": client, "pack": pack,
                   "declaredDependencies": [{"projectId": project, "fileId": file, "required": required}
                                            for (project, file), required in sorted(deps.items())],
                   "overrides": overrides}
        if expected:
            for field in ("pack", "declaredDependencies", "overrides"):
                require(expected.get(field) == receipt[field], f"Pinned author export changed: {field}")
        if game is not None:
            descriptor = {"schemaVersion": 1, "minecraftVersion": pack["minecraftVersion"], "loader": pack["loader"]}
            if args.java_major is not None:
                descriptor["javaMajorVersion"] = args.java_major
            pinned = {}
            if expected.get("status") == "complete":
                require(expected.get("descriptor") == descriptor, "Pinned launcher descriptor changed")
                expected_files = expected.get("files")
                require(isinstance(expected_files, list) and all(isinstance(row, dict) and
                        positive(row.get("projectId")) and positive(row.get("fileId")) for row in expected_files),
                        "Invalid pinned dependency inventory")
                pinned = {(row["projectId"], row["fileId"]): row for row in expected_files}
                require(len(pinned) == len(expected_files) and pinned.keys() == deps.keys(),
                        "Pinned dependency inventory does not match the author manifest")
            receipt["descriptor"] = descriptor
            if args.pack_source is None:
                write_json(game / descriptor_path, descriptor)
            else:
                receipt["overlay"] = apply_overlay(game, args.pack_source, receipt, paths)
            overlay = receipt.get("overlay")
            excluded_keys = overlay_dependency_keys(overlay, receipt) if overlay else []
            if "overlay" in expected:
                pinned_overlay = overlay
                if excluded_keys:
                    pinned_overlay = {**overlay, "excludedDependencies": excluded_dependency_files(
                        excluded_keys, expected["files"])}
                require(expected["overlay"] == pinned_overlay, "Pinned author overlay changed")
            placements = placement_map(read_local_json(args.placements) if args.placements else [], deps)
            fallback_path = args.pack_source / "tools/curseforge-fallbacks.json" if args.pack_source else None
            fallbacks = fallback_records(read_local_json(fallback_path), deps) if fallback_path and fallback_path.exists() else {}
            override_files = {row["path"]: row for row in overrides}
            plan = dependency_plan(deps, placements, paths, pinned,
                                   sum(row["sizeBytes"] for row in overrides), fallbacks, override_files)
            with ThreadPoolExecutor(max_workers=WORKERS) as pool:
                files = list(pool.map(lambda entry: acquire_dependency(game, entry, override_files, verified_source), plan))
            receipt.update(status="complete", files=files)
            if overlay and "jarMetadataPatches" in overlay:
                apply_jar_metadata_patches(game, args.pack_source, receipt)
            if overlay and "bookLocalization" in overlay:
                book_localizations(overlay, args.pack_source, receipt)
            if overlay and "bookJarLocalization" in overlay:
                apply_book_jar_localizations(game, args.pack_source, receipt)
            if overlay and "bootstrap" in overlay:
                overlay_bootstrap(overlay, args.pack_source, receipt,
                                  overlay_additions(overlay, args.pack_source, receipt))
            if excluded_keys:
                excluded = excluded_dependency_files(excluded_keys, files)
                exclude_dependencies(game, excluded)
                overlay["excludedDependencies"] = excluded
        write_json(stage / "intake.json", receipt)
        check_output(out)
        no_links(stage)
        require(stage.resolve().is_relative_to(native_path(out.parent.resolve())), "Staging directory escaped the task run")
        stage.rename(native_path(out))
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-id", type=int, required=True)
    parser.add_argument("--file-id", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New directory below an owned agent run")
    parser.add_argument("--client-only", action="store_true", help="Audit the author archive without fetching dependencies")
    parser.add_argument("--client-archive", type=Path, help="Reuse an already downloaded official ZIP without copying it")
    parser.add_argument("--expect", type=Path, help="Earlier intake.json with exact verified byte hashes")
    parser.add_argument("--placements", type=Path, help="JSON list of explicit projectId/fileId/directory assignments")
    parser.add_argument("--java-major", type=int, help="Game Java major from the verified runtime profile")
    parser.add_argument("--pack-source", type=Path, help="Apply the pinned share-world overlay from this pack repository")
    args = parser.parse_args()
    try:
        require(positive(args.project_id) and positive(args.file_id), "CurseForge IDs must be positive")
        require(args.java_major is None or positive(args.java_major), "Java major must be positive")
        require(args.pack_source is None or not args.client_only and args.java_major is not None,
                "A pack source requires full preparation with an explicit game Java major")
        receipt = prepare(args)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")
    print(json.dumps({"status": receipt["status"], "client": receipt["client"]["fileName"],
                      "minecraftVersion": receipt["pack"]["minecraftVersion"], "loader": receipt["pack"]["loader"],
                      "dependencies": len(receipt["declaredDependencies"]), "overrides": len(receipt["overrides"])}))


if __name__ == "__main__":
    main()
