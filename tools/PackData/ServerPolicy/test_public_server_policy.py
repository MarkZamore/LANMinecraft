import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import public_server_policy as policy


class PolicyTests(unittest.TestCase):
    def setUp(self):
        results = Path(__file__).resolve().parents[3] / "Program.Tests/TestResults"
        results.mkdir(parents=True, exist_ok=True)
        self.scratch = tempfile.TemporaryDirectory(prefix="ServerPolicy-", dir=results)
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        (self.root / "tools").mkdir()

    def prepare(self, name="config/cataclysm.toml", content=b'"AddedServerlist(on/off)" = false\n'):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        self.manifest = {"files": [{"path": name, "sizeBytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}]}
        (self.root / "tools/public-server-policy.json").write_text(json.dumps({"schemaVersion": 1,
            "policy": policy.POLICY, "reviewedInventorySha256": policy.inventory_sha256(self.manifest["files"])}))

    def test_manual_server_list_is_never_read_or_changed(self):
        self.prepare()
        personal = self.root / "servers.dat"
        personal.write_bytes(b"player-owned NBT\x00\xff")
        before = personal.read_bytes()
        self.assertEqual("PASS", policy.verify(self.root, self.manifest)["status"])
        self.assertEqual(before, personal.read_bytes())

    def test_an_author_update_new_file_or_missing_policy_blocks_publication(self):
        self.prepare()
        for change in ("hash", "new-file", "missing"):
            with self.subTest(change=change):
                self.prepare()
                if change == "hash": self.manifest["files"][0]["sha256"] = "a" * 64
                if change == "new-file": self.manifest["files"].append({"path": "mods/new.jar", "sizeBytes": 1, "sha256": "b" * 64})
                if change == "missing": (self.root / "tools/public-server-policy.json").unlink()
                with self.assertRaises((ValueError, FileNotFoundError)):
                    policy.verify(self.root, self.manifest)

    def test_known_server_sources_fail_even_with_a_recomputed_inventory(self):
        for name, raw in (("servers.dat", b"seed"), ("defaults/servers.dat_old", b"seed"),
                          ("config/menu.txt", b"[action_type:joinserver] = public.example"),
                          ("config/cataclysm.toml", b'"AddedServerlist(on/off)" = true\n')):
            with self.subTest(name=name):
                self.prepare(name, raw)
                with self.assertRaises(ValueError): policy.verify(self.root, self.manifest)

    def test_actual_text_cannot_differ_from_the_reviewed_manifest(self):
        self.prepare()
        (self.root / "config/cataclysm.toml").write_text("changed")
        with self.assertRaises(ValueError): policy.verify(self.root, self.manifest)

    def test_unsafe_paths_and_windows_duplicates_are_refused(self):
        for names in (("../outside.txt",), ("config:a.txt",), ("config//a.txt",), ("mods/a.jar", "Mods/A.jar")):
            with self.subTest(names=names), self.assertRaises(ValueError):
                policy.inventory_sha256([{"path": x, "sizeBytes": 0, "sha256": "0" * 64} for x in names])


if __name__ == "__main__":
    unittest.main()
