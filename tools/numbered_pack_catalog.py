"""Project the published pack index to the numbered-ID endpoint."""

import argparse
import json
from pathlib import Path
import re


def unique_object(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise ValueError(f"Duplicate JSON field: {name}")
        result[name] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def schema_one_rows(document, name):
    if (not isinstance(document, dict) or type(document.get("schemaVersion")) is not int
            or document["schemaVersion"] != 1 or not isinstance(document.get(name), list)):
        raise ValueError(f"Expected schemaVersion 1 and an array named {name}")
    return document[name]


def catalog_id(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 100:
        raise ValueError(f"Invalid pack ID: {value!r}")
    return value


def allocated_ids(allocations):
    result = {}
    for ordinal, entry in enumerate(schema_one_rows(allocations, "allocations"), start=1):
        canonical = f"minecraft-{ordinal}"
        if (not isinstance(entry, dict) or entry.get("id") != canonical
                or entry.keys() - {"id", "legacyId", "allowCanonicalInput"}):
            raise ValueError(f"Expected allocation {canonical}; keep every earlier allocation in order")
        allow_canonical = entry.get("allowCanonicalInput", False)
        if type(allow_canonical) is not bool:
            raise ValueError(f"Invalid allowCanonicalInput for {canonical}")
        if "legacyId" in entry:
            legacy = catalog_id(entry["legacyId"])
            key = legacy.casefold()
            if key in result or re.fullmatch(r"minecraft-[0-9]+", key):
                raise ValueError(f"Duplicate or numbered legacy ID: {legacy}")
            result[key] = canonical
        if "legacyId" not in entry or allow_canonical:
            result[canonical] = canonical
    return result


def project_catalog(index, allocations):
    ids = allocated_ids(allocations)
    seen = set()
    projected = []
    for entry in schema_one_rows(index, "packs"):
        if not isinstance(entry, dict) or "legacyId" in entry:
            raise ValueError("Input must be a v1 catalog without legacyId fields")
        old_id = catalog_id(entry.get("id"))
        canonical = ids.get(old_id.casefold())
        if canonical is None:
            raise ValueError(f"No numbered allocation for {old_id}")
        if canonical in seen:
            raise ValueError(f"Duplicate catalog allocation: {canonical}")
        seen.add(canonical)
        updated = {"id": canonical}
        if old_id.casefold() != canonical:
            updated["legacyId"] = old_id
        updated.update((field, value) for field, value in entry.items() if field != "id")
        projected.append(updated)
    return {**index, "packs": projected}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path, help="Input catalog.json for older launchers")
    parser.add_argument("output", type=Path, help="Output v2/catalog.json")
    args = parser.parse_args()
    allocation_path = Path(__file__).with_name("numbered-pack-ids.json")
    try:
        if (args.catalog.resolve() == args.output.resolve()
                or (args.output.exists() and args.output.samefile(args.catalog))
                or args.output.resolve() == allocation_path.resolve()):
            raise ValueError("The output must be separate from the input and the allocation file")
        projected = project_catalog(read_json(args.catalog), read_json(allocation_path))
        payload = json.dumps(projected, ensure_ascii=False, allow_nan=False, indent=2) + "\n"
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(payload.encode("utf-8"))
    except (OSError, ValueError) as exc:
        parser.exit(1, f"{parser.prog}: {exc}\n")


if __name__ == "__main__":
    main()
