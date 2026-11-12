import hashlib
import json
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest
import zipfile

import prepare
import release_overlay
import stage_release
from pack_library import read_pack_mods


class StageReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "pack"
        self.stage = self.root / "stage"
        self.game = self.stage / "game"
        for path in (self.source / "tools", self.source / "library", self.source / "launcher", self.game / "mods",
                     self.game / "config", self.game / "structures", self.game / "saves/QA", self.game / "resourcepacks"):
            path.mkdir(parents=True)
        self.mod_path = "mods/author.jar"
        with zipfile.ZipFile(self.game / self.mod_path, "w") as archive:
            archive.writestr("META-INF/mods.toml", '[[mods]]\nmodId="author"\ndisplayName="Author Mod"\nversion="1"\n')
        self.cfg = "config/lan.cfg"
        self.structure = "structures/author.nbt"
        original_cfg = b"lan=true\n"
        (self.game / self.cfg).write_bytes(original_cfg)
        (self.game / self.structure).write_bytes(b"real content")
        (self.game / "saves/QA/level.dat").write_bytes(b"QA save must not ship")
        self.optional = "resourcepacks/optional.zip"
        (self.game / self.optional).write_bytes(b"optional game file")
        base = {"schemaVersion": 1, "minecraftVersion": "1.20.1", "loader": {"type": "forge", "version": "47.4.0"},
                "javaMajorVersion": 17}
        descriptor = self.source / release_overlay.DESCRIPTOR
        prepare.write_json(descriptor, {**base, "preserveWorldSettingsOnShare": True})
        required = {"projectId": 11, "fileId": 21, "required": True}
        optional = {"projectId": 12, "fileId": 22, "required": False}
        author = {"schemaVersion": 1, "status": "client-only", "client": {"sha256": "a" * 64},
                  "pack": {key: base[key] for key in ("minecraftVersion", "loader")},
                  "declaredDependencies": [required, optional],
                  "overrides": [{"path": path, **prepare.fingerprint((self.game / path).read_bytes())}
                                for path in (self.cfg, self.structure)]}
        recipe = {"schemaVersion": 1, "authorArchiveSha256": "a" * 64,
                  "descriptorSha256": prepare.fingerprint(descriptor.read_bytes())["sha256"],
                  "patches": [{"path": self.cfg, "from": "true", "to": "false",
                               "before": prepare.fingerprint(original_cfg), "after": prepare.fingerprint(b"lan=false\n")}]}
        prepare.write_json(self.source / "tools/overlay.json", recipe)
        (self.source / release_overlay.OWNERSHIP).write_bytes((self.cfg + "\n").encode())
        receipt = {**author, "status": "complete", "descriptor": base,
                   "files": [{**row, "path": path, **prepare.fingerprint((self.game / path).read_bytes())}
                             for row, path in ((required, self.mod_path), (optional, self.optional))]}
        receipt["overlay"] = prepare.apply_overlay(self.game, self.source, receipt, {})
        self.receipt = self.stage / "intake.json"
        prepare.write_json(self.receipt, receipt)
        prepare.write_json(self.source / release_overlay.CLIENT_LOCK, author)
        prepare.write_json(self.source / release_overlay.COMPLETE_LOCK, receipt)

        self.pack_id = "minecraft-12"
        prepare.write_json(self.source / "library/content.json", {"id": self.pack_id, "relativePath": "Fixture Pack",
                           "name": "Fixture Pack", "summary": "", "description": "A reviewed test pack."})
        prepare.write_json(self.source / "library/mod-pages.json", {"author": "https://example.org/author"})
        self.inventory_path = self.source / "library/loaded-mods.json"
        self.native_inventory = {"schemaVersion": 1, **author["pack"],
                                 "sourceJars": {self.mod_path: receipt["files"][0]["sha256"]},
                                 "mods": [{"id": "author", "name": "Native Author", "version": "1"}]}
        prepare.write_json(self.inventory_path, self.native_inventory)
        (self.source / "library/preview.jpg").write_bytes(b"\xff\xd8\xfftest preview\xff\xd9")
        self.git("init", "--quiet")
        self.git("config", "core.autocrlf", "false")
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.org", "commit", "--quiet", "-m", "Pinned sources")
        self.revision = self.git("rev-parse", "HEAD").strip().decode("ascii")

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.source), *args])

    def test_release_assets_are_deterministic_and_exactly_cover_approved_paths(self):
        left, right = self.root / "left", self.root / "right"
        one = stage_release.build_release(self.source, self.stage, left, self.revision, self.pack_id)
        two = stage_release.build_release(self.source, self.stage, right, self.revision, self.pack_id)
        self.assertEqual(one, two)
        self.assertEqual(one["packId"], self.pack_id)
        self.assertEqual(one["revision"], self.revision)
        self.assertEqual(one["modCount"], 1)
        self.assertEqual(one["requiredLauncherFeatures"], [prepare.REQUIRED_SHARE_FEATURE])
        expected = {self.mod_path, self.cfg, self.structure, release_overlay.DESCRIPTOR, release_overlay.OWNERSHIP}
        self.assertEqual({row["path"] for row in one["files"]}, expected)
        self.assertEqual((left / "pack-manifest.json").read_bytes(), (right / "pack-manifest.json").read_bytes())
        for asset in [*one["assets"], one["library"]]:
            path = left / "assets" / asset["name"]
            data = path.read_bytes()
            self.assertEqual(data, (right / "assets" / asset["name"]).read_bytes())
            self.assertEqual(asset["sha256"], hashlib.sha256(data).hexdigest())
            self.assertEqual(asset["sizeBytes"], len(data))
            if asset.get("kind") == "jar":
                self.assertEqual(data, (self.game / asset["covers"][0]).read_bytes())
            elif asset.get("kind") == "zipRoot":
                with zipfile.ZipFile(path) as archive:
                    self.assertEqual(archive.namelist(), asset["covers"])
                    for relative in asset["covers"]:
                        self.assertEqual(archive.read(relative), (self.game / relative).read_bytes())
        self.assertNotIn(one["library"]["name"], {row["name"] for row in one["assets"]})

    def test_refuses_missing_overlay_and_uncommitted_library_metadata(self):
        out = self.root / "blocked"
        receipt = prepare.read_local_json(self.receipt)
        broken = {key: value for key, value in receipt.items() if key != "overlay"}
        self.receipt.write_text(json.dumps(broken), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "overlay receipt"):
            stage_release.build_release(self.source, self.stage, out, self.revision, self.pack_id)
        self.assertFalse(out.exists())
        self.receipt.write_text(json.dumps(receipt), encoding="utf-8")
        self.inventory_path.unlink()
        with self.assertRaisesRegex(ValueError, "Missing required pack source file"):
            stage_release.build_release(self.source, self.stage, out, self.revision, self.pack_id)
        self.assertFalse(out.exists())
        prepare.write_json(self.inventory_path, {**self.native_inventory, "notes": "uncommitted change"})
        with self.assertRaisesRegex(ValueError, "differs from the requested commit"):
            stage_release.build_release(self.source, self.stage, out, self.revision, self.pack_id)
        self.assertFalse(out.exists())

    def test_optional_author_file_must_be_selected_explicitly(self):
        out = self.root / "optional"
        with self.assertRaisesRegex(ValueError, "exact optional author paths"):
            stage_release.build_release(self.source, self.stage, out, self.revision, self.pack_id, [self.structure])
        self.assertFalse(out.exists())
        manifest = stage_release.build_release(self.source, self.stage, out, self.revision, self.pack_id, [self.optional])
        self.assertIn(self.optional, {row["path"] for row in manifest["files"]})

    def test_legacy_forge_inventory_uses_the_actual_origin_jar(self):
        descriptor_path = self.game / release_overlay.DESCRIPTOR
        runtime = prepare.read_local_json(descriptor_path)
        runtime["minecraftVersion"] = "1.12.2"
        descriptor_path.unlink()
        prepare.write_json(descriptor_path, runtime)
        with zipfile.ZipFile(self.game / self.mod_path, "w") as archive:
            archive.writestr("fixture.txt", "Legacy mod without metadata")
        inventory = {**self.native_inventory, "minecraftVersion": "1.12.2",
                     "sourceJars": {self.mod_path: prepare.fingerprint((self.game / self.mod_path).read_bytes())["sha256"]},
                     "mods": [{"id": "legacy", "name": "Legacy Actual", "version": "1", "source": self.mod_path}]}
        self.inventory_path.unlink()
        prepare.write_json(self.inventory_path, inventory)
        mods = read_pack_mods(self.game, [self.mod_path], metadata_root=self.source)
        self.assertEqual(list(mods), ["legacy"])
        self.assertTrue(mods["legacy"]["isLoadedMod"])
        self.inventory_path.unlink()
        prepare.write_json(self.inventory_path, {**inventory, "mods": [{**inventory["mods"][0], "source": "mods/unverified.jar"}]})
        with self.assertRaisesRegex(ValueError, "unbound legacy Forge mod"):
            read_pack_mods(self.game, [self.mod_path], metadata_root=self.source)


if __name__ == "__main__":
    unittest.main()
