"""Stage an offline text-only update from a verified published library bundle.

This tool never downloads, uploads, resolves tags, runs a pack or rebuilds game
payloads. The caller supplies an authored publication commit and retains the
original manifest separately for rollback.
"""
import argparse
import copy
import hashlib
import io
import json
import pathlib
import re
import stat
import zipfile
import zlib


MAX_BUNDLE_BYTES = 8 * 1024 * 1024
REVISION = re.compile(r"[a-f0-9]{40}\Z")
SHA256 = re.compile(r"[a-f0-9]{64}\Z")
ENTRIES = ("library.json", "preview.jpg")


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key: " + key)
        result[key] = value
    return result


def _invalid_constant(value):
    raise ValueError("Non-finite JSON value: " + value)


def _object(payload, label):
    result = json.loads(payload.decode("utf-8-sig"), object_pairs_hook=_pairs,
                        parse_constant=_invalid_constant)
    if not isinstance(result, dict):
        raise ValueError(label + " must be a JSON object")
    return result


def _revision(value):
    if not isinstance(value, str) or not REVISION.fullmatch(value):
        raise ValueError("Revision must be an exact lowercase 40-character Git commit")
    return value


def _text(authored, field, sentences, russian_only):
    value = authored.get(field)
    maximum = 1000 if field == "summary" else 32000
    if not isinstance(value, str) or len(value) > maximum:
        raise ValueError("Invalid authored " + field)
    if not value.strip():
        if field == "summary" and value == "":
            return value
        raise ValueError("Authored " + field + " must not be blank")
    if sentences is not None and field == "description":
        # Deliberately plain prose, not an NLP parser for abbreviations/markup.
        count = sum(bool(part.strip()) for part in re.split(r"[.!?]+", value))
        if count != sentences or value.rstrip()[-1] not in ".!?":
            raise ValueError(f"Authored {field} must contain {sentences} punctuated sentences")
    if russian_only:
        for character in value:
            russian = "А" <= character <= "я" or character in "Ёё"
            if character.isnumeric() or (character.isalpha() and not russian):
                raise ValueError("Authored " + field + " contains a number or non-Russian letter")
    return value


def _read_bundle(payload):
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            entries = archive.infolist()
            if len(entries) != 2 or {entry.filename for entry in entries} != set(ENTRIES):
                raise ValueError("Library ZIP must contain only library.json and preview.jpg once each")
            for entry in entries:
                mode = entry.external_attr >> 16
                if (entry.is_dir() or stat.S_IFMT(mode) not in (0, stat.S_IFREG) or
                        entry.flag_bits & 1 or entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) or
                        not 0 < entry.file_size <= MAX_BUNDLE_BYTES):
                    raise ValueError("Unsafe or oversized library ZIP entry: " + entry.filename)
            document = _object(archive.read("library.json"), "Published library metadata")
            preview = archive.read("preview.jpg")
    except (zipfile.BadZipFile, RuntimeError, zlib.error) as error:
        raise ValueError("Invalid library ZIP: " + str(error)) from error
    return document, preview


def _json_bytes(document):
    return (json.dumps(document, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")


def prepare_update(manifest, published_bundle, authored, revision, *, sentences=None, russian_only=False):
    """Return (new manifest, new ZIP), preserving all non-text publication data."""
    _revision(revision)
    if sentences is not None and (type(sentences) is not int or sentences <= 0):
        raise ValueError("Sentence count must be a positive integer")
    if not isinstance(manifest, dict) or not isinstance(authored, dict):
        raise ValueError("Manifest and authored content must be JSON objects")
    old_revision = _revision(manifest.get("revision"))
    if revision == old_revision:
        raise ValueError("A description update needs its own authored publication commit")
    descriptor = manifest.get("library")
    if not isinstance(descriptor, dict):
        raise ValueError("Published manifest has no library descriptor")
    digest = descriptor.get("sha256")
    size = descriptor.get("sizeBytes")
    if (not isinstance(digest, str) or not SHA256.fullmatch(digest) or
            descriptor.get("name") != "library-" + digest + ".zip" or
            type(size) is not int or not 0 < size <= MAX_BUNDLE_BYTES or
            len(published_bundle) != size or hashlib.sha256(published_bundle).hexdigest() != digest):
        raise ValueError("Published library name, size or SHA256 mismatch")
    document, preview = _read_bundle(published_bundle)
    if document.get("revision") != old_revision or descriptor.get("revision") != old_revision:
        raise ValueError("Manifest, descriptor and library revisions must all match")
    for field in ("id", "name"):
        if not isinstance(authored.get(field), str) or authored[field] != document.get(field):
            raise ValueError("Authored content targets another library " + field)
    if "relativePath" in authored and authored["relativePath"] != document.get("relativePath"):
        raise ValueError("Authored content targets another library relativePath")
    summary = _text(authored, "summary", sentences, russian_only)
    description = _text(authored, "description", sentences, russian_only)
    updated = copy.deepcopy(document)
    updated.update(summary=summary, description=description, revision=revision)
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, payload in (("library.json", _json_bytes(updated)), ("preview.jpg", preview)):
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.create_system = 0
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, payload, compresslevel=9)
    payload = buffer.getvalue()
    if len(payload) > MAX_BUNDLE_BYTES:
        raise ValueError("Updated library exceeds the launcher's bundle limit")
    digest = hashlib.sha256(payload).hexdigest()
    result = copy.deepcopy(manifest)
    result["revision"] = revision
    result["library"] = copy.deepcopy(descriptor)
    result["library"].update(name="library-" + digest + ".zip", sizeBytes=len(payload),
                              sha256=digest, revision=revision)
    return result, payload


def stage_update(manifest_path, library_path, authored_path, revision, output, **checks):
    manifest_path, library_path, authored_path, output = map(pathlib.Path,
        (manifest_path, library_path, authored_path, output))
    if output.exists():
        raise ValueError("Use a fresh output directory; original inputs must remain available for rollback")
    if library_path.stat().st_size > MAX_BUNDLE_BYTES:
        raise ValueError("Published library exceeds the launcher's bundle limit")
    manifest = _object(manifest_path.read_bytes(), "Published manifest")
    authored = _object(authored_path.read_bytes(), "Authored content")
    updated, bundle = prepare_update(manifest, library_path.read_bytes(), authored, revision, **checks)
    assets = output / "assets"
    assets.mkdir(parents=True)
    (assets / updated["library"]["name"]).write_bytes(bundle)
    # This is staging only; a publisher uploads the immutable ZIP first and the
    # manifest last, after verifying the remote payload's full digest and size.
    (output / "pack-manifest.json").write_bytes(_json_bytes(updated))
    return updated["library"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    parser.add_argument("--library", required=True, type=pathlib.Path)
    parser.add_argument("--content", required=True, type=pathlib.Path)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--out", required=True, type=pathlib.Path)
    parser.add_argument("--sentences", type=int, help="Required sentence count in the long description only")
    parser.add_argument("--russian-only", action="store_true", help="Reject numbers and non-Russian letters in summary and description")
    args = parser.parse_args()
    try:
        result = stage_update(args.manifest, args.library, args.content, args.revision, args.out,
                              sentences=args.sentences, russian_only=args.russian_only)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Library description staging: {error}\n")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
