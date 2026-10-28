import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import record_library_inventory as recorder


class ActualInventoryInfrastructureTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.pack, self.game = self.root / "pack", self.root / "game"
        self.author = self.jar(self.pack, "author.jar", "author_mod", "1.0")
        self.jar(self.game, "author.jar", "author_mod", "1.0")
        (self.pack / "portable-pack.json").write_text(json.dumps({"minecraftVersion": "1.20.1",
            "loader": {"type": "forge", "version": "47.4.0"}}), encoding="utf-8")
        self.observations = self.root / "observations.jsonl"
        self.loaded = {"minecraft": "1.20.1", "forge": "47.4.0", "author_mod": "1.0"}
        self.write_observations()

    def jar(self, root, filename, mod_id, version):
        target = root / "mods" / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(target, "w") as jar:
            jar.writestr("META-INF/mods.toml", f'[[mods]]\nmodId="{mod_id}"\nversion="{version}"\ndisplayName="{mod_id}"\n')
        return target

    def write_observations(self):
        self.observations.write_text(json.dumps({"kind": "loaded_mod_ids", "utc": "2026-10-28T13:00:00Z",
            "ids": self.loaded, "count": len(self.loaded)}) + "\n", encoding="utf-8")

    def add_transport(self):
        target = self.jar(self.game, "e4steam.jar", "e4steam", "0.3.4")
        self.loaded["e4steam"] = "0.3.4"
        self.write_observations()
        return {"schemaVersion": 1, "files": [{"path": "mods/e4steam.jar", "sizeBytes": target.stat().st_size,
            "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "modIds": ["e4steam"], "version": "0.3.4"}]}

    def inventory(self):
        return json.loads((self.pack / "library/loaded-mods.json").read_text(encoding="utf-8"))

    def test_original_exact_author_only_behavior_is_preserved(self):
        count = recorder.record_inventory(self.pack, self.game, self.observations)
        self.assertEqual(count, 1)
        self.assertNotIn("managedInfrastructure", self.inventory())
        self.assertEqual(self.inventory()["sourceJars"], recorder.jar_hashes(self.pack))

    def test_exact_shared_transport_pin_excludes_only_observed_infrastructure(self):
        pin = self.add_transport()
        count = recorder.record_inventory(self.pack, self.game, self.observations, pin)
        inventory = self.inventory()
        self.assertEqual(count, 1)
        self.assertEqual([x["id"] for x in inventory["mods"]], ["author_mod"])
        self.assertEqual(inventory["sourceJars"], recorder.jar_hashes(self.pack))
        self.assertEqual(inventory["managedInfrastructure"]["files"], pin["files"])
        self.assertEqual(inventory["managedInfrastructure"]["actualAllLoadedIdCount"], 4)
        self.assertFalse(inventory["managedInfrastructure"]["unknownExtraJarsIgnored"])

    def test_extra_transport_without_pin_is_rejected(self):
        self.add_transport()
        with self.assertRaisesRegex(ValueError, "Tested client JARs differ"):
            recorder.record_inventory(self.pack, self.game, self.observations)
        self.assertFalse((self.pack / "library/loaded-mods.json").exists())

    def test_unknown_extra_jar_is_rejected_even_with_transport_pin(self):
        pin = self.add_transport()
        self.jar(self.game, "unknown.jar", "unknown_extra", "1.0")
        with self.assertRaisesRegex(ValueError, "Tested client JARs differ"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_unknown_nested_extra_jar_is_not_silently_ignored(self):
        pin = self.add_transport()
        self.jar(self.game, "nested/unknown.jar", "unknown_extra", "1.0")
        with self.assertRaisesRegex(ValueError, "Tested client JARs differ"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_same_size_modified_transport_is_rejected(self):
        pin = self.add_transport()
        target = self.game / "mods/e4steam.jar"
        target.write_bytes(b"x" * target.stat().st_size)
        with self.assertRaisesRegex(ValueError, "size/SHA256 differs"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_infrastructure_cannot_overlap_author_ids(self):
        pin = self.add_transport()
        pin["files"][0]["modIds"] = ["author_mod"]
        with self.assertRaisesRegex(ValueError, "mod IDs overlap author content"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_infrastructure_cannot_overlap_author_path(self):
        pin = self.add_transport()
        pin["files"][0]["path"] = "mods/author.jar"
        with self.assertRaisesRegex(ValueError, "path overlaps author content"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_actual_runtime_transport_version_must_match_pin(self):
        pin = self.add_transport()
        self.loaded["e4steam"] = "0.3.2"
        self.write_observations()
        with self.assertRaisesRegex(ValueError, "Actual managed infrastructure ID/version differs"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)

    def test_actual_runtime_unknown_mod_id_is_never_excluded(self):
        pin = self.add_transport()
        self.loaded["unexpected"] = "1.0"
        self.write_observations()
        with self.assertRaisesRegex(ValueError, "Actual loaded ID missing from shipped metadata: unexpected"):
            recorder.record_inventory(self.pack, self.game, self.observations, pin)


if __name__ == "__main__":
    unittest.main()
