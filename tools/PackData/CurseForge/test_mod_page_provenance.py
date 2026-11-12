from io import BytesIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile

import mod_page_provenance as provenance
import prepare


def jar(entries):
    raw = BytesIO()
    with ZipFile(raw, "w") as archive:
        for path, content in entries.items():
            archive.writestr(path, content)
    return raw.getvalue()


def forge_metadata(mod_id):
    return f'[[mods]]\nmodId = "{mod_id}"\ndisplayName = "{mod_id}"\nversion = "1"\n'


class ModPageProvenanceTests(unittest.TestCase):
    def test_official_page_requires_exact_project_slug_and_canonical_mod_url(self):
        def record(project_id, url, **fields):
            return {"id": project_id, "slug": "mod-one", "links": {"websiteUrl": url}, **fields}

        url = "https://www.curseforge.com/minecraft/mc-mods/mod-one"
        records = [record(11, url), record(12, url + "/files/65"), record(13, url + "?id=14"),
                   record(14, url.replace("mod-one", "another-mod")), record(15, url, isAvailable=False),
                   record(16, url.replace("www.curseforge.com", "www.curseforge.com.example"))]
        self.assertEqual(provenance.official_project_urls({"data": records}), {11: url})
        with self.assertRaisesRegex(ValueError, "repeated CurseForge project ID"):
            provenance.official_project_urls({"data": [records[0], records[0]]})

    def test_pinned_top_level_mod_and_missing_or_nested_origins(self):
        first = jar({"META-INF/mods.toml": forge_metadata("direct")})
        inner = jar({"META-INF/mods.toml": forge_metadata("nested")})
        second = jar({"META-INF/mods.toml": forge_metadata("bundled"),
                      "META-INF/jarjar/metadata.json": json.dumps({"jars": [{"path": "libs/inner.jar"}]}),
                      "libs/inner.jar": inner})
        with TemporaryDirectory() as temporary:
            game = Path(temporary)
            (game / "mods").mkdir()
            direct = "mods/author.jar"
            bundled = "mods/bundled.jar"
            (game / direct).write_bytes(first)
            (game / bundled).write_bytes(second)
            rows = {direct: {**prepare.fingerprint(first), "projectId": 42, "fileId": 402},
                    bundled: prepare.fingerprint(second)}
            url = "https://www.curseforge.com/minecraft/mc-mods/mod-one"
            candidates, unresolved, unclassified = provenance.find_candidates(
                game, rows, "forge", False, {"direct": direct, "bundled": bundled, "nested": None, "missing": None},
                {42: url})
            self.assertEqual(candidates["direct"]["websiteUrl"], url)
            self.assertEqual(candidates["direct"]["fileId"], 402)
            self.assertEqual(unresolved["bundled"]["reason"], "bundled JAR requires source review")
            self.assertEqual(unresolved["nested"]["candidateJars"], [bundled])
            self.assertEqual(unresolved["missing"]["candidateJars"], [])
            self.assertFalse(unclassified)
            (game / direct).write_bytes(first[:-1] + b"x")
            with self.assertRaisesRegex(ValueError, "changed pinned mod JAR"):
                provenance.find_candidates(game, rows, "forge", False, None, {})

    def test_legacy_requires_bound_native_inventory_with_jar_origin(self):
        data = jar({"mcmod.info": json.dumps({"modList": [{"modid": "actual"}]})})
        relative = "mods/legacy.jar"
        rows = {relative: {**prepare.fingerprint(data), "projectId": 42, "fileId": 402}}
        with TemporaryDirectory() as temporary:
            game = Path(temporary)
            (game / "mods").mkdir()
            (game / relative).write_bytes(data)
            inventory = {"schemaVersion": 1, "upstreamLockSha256": "a" * 64, "mods": [{"id": "actual"}]}
            urls = {42: "https://www.curseforge.com/minecraft/mc-mods/mod-one"}
            with self.assertRaisesRegex(ValueError, "not bound"):
                provenance.inventory_mods(inventory, "b" * 64, rows)
            observed = provenance.inventory_mods(inventory, "a" * 64, rows)
            candidates, unresolved, _ = provenance.find_candidates(game, rows, "forge", True, observed, urls)
            self.assertFalse(candidates)
            self.assertEqual(unresolved["actual"]["reason"], "legacy runtime origin required")
            inventory["mods"][0]["source"] = relative
            observed = provenance.inventory_mods(inventory, "a" * 64, rows)
            candidates, unresolved, _ = provenance.find_candidates(game, rows, "forge", True, observed, urls)
            self.assertEqual(candidates["actual"]["path"], relative)
            self.assertFalse(unresolved)


if __name__ == "__main__":
    unittest.main()
