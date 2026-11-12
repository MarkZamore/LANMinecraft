"""Resolve exact locked CurseForge project IDs through FTB's public metadata."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, build_opener

import mod_page_provenance
import prepare
import release_overlay


PROJECT_API = "https://api.modpacks.ch/public/curseforge"
RELEASE_API = "https://api.feed-the-beast.com/v1/modpacks/public/modpack"
FTB_API_HOSTS = {"api.modpacks.ch", "api.feed-the-beast.com"}


def metadata(url):
    host = prepare.official_url(url, FTB_API_HOSTS).hostname
    opener = build_opener(prepare.OfficialRedirect({host}))
    request = Request(url, headers={"Accept": "application/json", "User-Agent": "LANMinecraft/1"})
    with opener.open(request, timeout=20) as response:
        prepare.require(response.status == 200 and response.url == url,
                        "Unexpected public project metadata response")
        return prepare.decode_json(response.read(prepare.MAX_JSON + 1))


def normalized_project(project_id, document):
    prepare.require(isinstance(document, dict) and document.get("status") == "success" and
                    prepare.positive(document.get("id")) and document["id"] == project_id,
                    "FTB returned a different or unavailable CurseForge project")
    links = document.get("links")
    prepare.require(isinstance(links, list), "FTB project has no website links")
    urls = {link["link"] for link in links if isinstance(link, dict) and link.get("type") == "website"
            and isinstance(link.get("link"), str)}
    prepare.require(len(urls) == 1, "FTB project has no unique website link")
    url = urls.pop()
    slug = urlsplit(url).path.rstrip("/").rsplit("/", 1)[-1]
    row = {"id": project_id, "slug": slug, "links": {"websiteUrl": url},
           "metadataUrl": f"{PROJECT_API}/{project_id}"}
    prepare.require(mod_page_provenance.official_project_urls({"data": [row]}) == {project_id: url},
                    "FTB website link is outside official CurseForge mod pages")
    return row


def get_project(project_id):
    endpoint = f"{PROJECT_API}/{project_id}"
    try:
        return normalized_project(project_id, metadata(endpoint)), None
    except HTTPError as error:
        reason = f"HTTP {error.code}"
    except OSError:
        reason = "Network or timeout error"
    except (TypeError, ValueError) as error:
        reason = str(error)
    return None, {"id": project_id, "metadataUrl": endpoint, "reason": reason}


def verify_release(pack_id, version_id, author, document):
    prepare.require(isinstance(document, dict) and document.get("status") == "success" and
                    document.get("parent") == pack_id and document.get("id") == version_id and
                    isinstance(document.get("files"), list), "FTB returned a different release")
    rows = {}
    for row in document["files"]:
        prepare.require(isinstance(row, dict), "Invalid FTB release file")
        curseforge = row.get("curseforge")
        if curseforge is None:
            continue
        prepare.require(isinstance(curseforge, dict), "Invalid FTB CurseForge coordinates")
        ids = (curseforge.get("project"), curseforge.get("file"))
        prepare.require(all(isinstance(value, str) and re.fullmatch(r"[1-9][0-9]*", value) for value in ids),
                        "Invalid FTB CurseForge project or file ID")
        key = tuple(map(int, ids))
        prepare.require(key not in rows, "Duplicate FTB CurseForge file")
        rows[key] = row
    for file in author["files"]:
        key = file["projectId"], file["fileId"]
        row = rows.get(key, {})
        hashes = row.get("hashes")
        prepare.require(row.get("size") == file["sizeBytes"] and isinstance(hashes, dict) and
                        hashes.get("sha256") == file["sha256"], f"FTB release bytes differ for CurseForge {key}")
    return {"metadataUrl": f"{RELEASE_API}/{pack_id}/{version_id}", "name": document.get("name"),
            "sameLockedFiles": len(author["files"])}


def load_cache(path):
    if path is None:
        return {}
    document = prepare.read_local_json(path)
    urls = mod_page_provenance.official_project_urls(document)
    for row in document["data"]:
        prepare.require(row["id"] in urls and row.get("metadataUrl") == f"{PROJECT_API}/{row['id']}",
                        "Invalid cached FTB project provenance")
    return {row["id"]: row for row in document["data"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pack-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="New report file in an owned task run")
    parser.add_argument("--cache", type=Path, help="Reuse validated project records from an earlier report")
    parser.add_argument("--ftb-release", nargs=2, type=int, metavar=("PACK_ID", "VERSION_ID"),
                        help="Cross-check the locked dependency bytes against an official FTB release")
    args = parser.parse_args()
    try:
        output = prepare.check_output(args.output)
        client = release_overlay.read_lock(args.pack_source / release_overlay.CLIENT_LOCK, "client-only")
        lock = args.pack_source / release_overlay.COMPLETE_LOCK
        author = release_overlay.read_lock(lock, "complete")
        release_overlay.same_author(client, author)
        recipe, _, _, _ = release_overlay.source_policy(args.pack_source, author)
        jars = mod_page_provenance.locked_mod_jars(author, recipe)
        needed = {row["projectId"] for row in jars.values() if "projectId" in row}
        prepare.require(all(prepare.positive(project_id) for project_id in needed), "Invalid locked project ID")
        records = load_cache(args.cache)
        release = None
        if args.ftb_release is not None:
            pack_id, version_id = args.ftb_release
            prepare.require(all(prepare.positive(value) for value in args.ftb_release), "Invalid FTB release IDs")
            release = verify_release(pack_id, version_id, author, metadata(f"{RELEASE_API}/{pack_id}/{version_id}"))
        unresolved = []
        with ThreadPoolExecutor(max_workers=prepare.WORKERS) as pool:
            for row, error in pool.map(get_project, sorted(needed - records.keys())):
                if row is None:
                    unresolved.append(error)
                else:
                    records[row["id"]] = row
        report = {"schemaVersion": 1, "projectEndpoint": PROJECT_API,
                  "upstreamLockSha256": prepare.fingerprint(prepare.overlay_bytes(lock))["sha256"],
                  "release": release, "requiredProjectIds": sorted(needed), "unresolved": unresolved,
                  "data": [records[project_id] for project_id in sorted(records)]}
        prepare.write_json(output, report)
        print(json.dumps({"required": len(needed), "resolved": len(needed & records.keys()),
                          "unresolved": unresolved}, ensure_ascii=True))
    except (OSError, KeyError, TypeError, ValueError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
