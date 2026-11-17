import json
from pathlib import Path
import tempfile
import unittest

from policy import ARTIFACT, JVM, OWNED, SELECTOR, fingerprint
from stage_update import checked_payload, policy_files, stage, zip_bytes


class UpdateTests(unittest.TestCase):
    def test_minimal_transaction_preserves_all_other_files_and_metadata(self):
        jvm = b"-Dkeep=true\n"
        owned = b"config/log4j2.xml\n"
        launcher = {JVM: jvm, OWNED: owned, "launcher/options-default.txt": b"personal-default\n"}
        raw = zip_bytes(launcher)
        asset = {"name": "old.zip", "kind": "zipRoot", **fingerprint(raw), "covers": sorted(launcher)}
        card = {"revision": "a" * 40, "mods": [{"id": "unchanged"}]}
        library_raw = zip_bytes({"library.json": json.dumps(card).encode(), "preview.jpg": b"preview"})
        library = {"name": "old-library.zip", **fingerprint(library_raw), "revision": "a" * 40}
        manifest = {"schemaVersion": 2, "revision": "a" * 40, "modCount": 1, "requiredLauncherFeatures": ["retained"],
                    "files": [{"path": name, **fingerprint(data)} for name, data in launcher.items()],
                    "assets": [asset], "library": library}
        replacements = policy_files(jvm, owned, b"agent-fixture")
        with tempfile.TemporaryDirectory() as temporary:
            result = stage(manifest, {"old.zip": raw, "old-library.zip": library_raw}, replacements,
                           "b" * 40, Path(temporary) / "stage")
            files = {row["path"]: row for row in result["files"]}
            self.assertEqual(files["launcher/options-default.txt"]["sha256"], fingerprint(launcher["launcher/options-default.txt"])["sha256"])
            self.assertEqual(result["modCount"], 1)
            self.assertEqual(result["requiredLauncherFeatures"], ["retained"])
            self.assertEqual(result["revision"], "b" * 40)
            self.assertIn(SELECTOR.encode(), replacements[JVM])
            for item in result["assets"]:
                checked_payload(item, (Path(temporary) / "stage/assets" / item["name"]).read_bytes(),
                                {name: {key: row[key] for key in ("sizeBytes", "sha256")} for name, row in files.items()})

    def test_existing_broad_ownership_is_preserved_and_duplicate_selector_refused(self):
        owned = b"# pack\r\nconfig/**\r\n"
        self.assertEqual(policy_files(b"-Dkeep=true\r\n", owned, b"agent")[OWNED], owned)
        with self.assertRaisesRegex(ValueError, "already installed"):
            policy_files((SELECTOR + "\n").encode(), owned, b"agent")

    def test_asset_hash_and_coverage_fail_closed(self):
        raw = zip_bytes({"config/a": b"a"})
        asset = {"name": "a.zip", "kind": "zipRoot", **fingerprint(raw), "covers": ["config/a"]}
        with self.assertRaisesRegex(ValueError, "hash changed"):
            checked_payload(asset, raw + b"changed")
        with self.assertRaisesRegex(ValueError, "coverage"):
            checked_payload({**asset, "covers": ["other"]}, raw)


if __name__ == "__main__":
    unittest.main()
