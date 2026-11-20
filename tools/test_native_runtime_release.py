import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

import native_runtime_release as release


FEATURES = ["native-runtime-v1", "native-steam-v1", "minecraft-26-world-layout-v1", "native-launcher-notices-v1"]
BOOTSTRAP = "native/bootstrap.exe"
CLIENT = "native/client.exe"
MOD = "native/horizon-mods/sample.hmod"


class NativeRuntimeReleaseTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.common = self.root / "common"
        self.common.mkdir()
        self.emit(self.common, BOOTSTRAP, b"bootstrap")
        self.emit(self.common, CLIENT, b"fixed common client")
        self.emit(self.common, "native/steam_api64.dll", b"steam library")
        self.spec = {"schemaVersion": 1, "id": "example-runtime", "version": "v2.1", "platform": "windows-x64",
                     "source": {"owner": "Example", "repo": "Binaries", "tag": "v2.1"}}
        family = {key: self.spec[key] for key in ("schemaVersion", "id", "version", "platform")}
        self.emit(self.common, release.FAMILY, {**family, "minecraftVersion": "26.3"})
        self.spec["files"] = release.rows(self.files(self.common))
        self.runtime = self.root / "runtime"
        self.contract = release.stage_runtime(self.common, self.spec, self.runtime)

    @staticmethod
    def emit(root, relative, value):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(value, bytes):
            path.write_bytes(value)
        else:
            release.write(path, value)

    @staticmethod
    def files(root):
        return {path.relative_to(root).as_posix(): release.identity(path) for path in root.rglob("*") if path.is_file()}

    def candidate(self, pack_id, *, modded=False, different_client=False):
        candidate = self.root / f"candidate-{pack_id}"
        shutil.copytree(self.common, candidate)
        if different_client:
            self.emit(candidate, CLIENT, b"different compiled client")
        raw = {"schemaVersion": 2, "minecraftVersion": "26.3", "nativeRuntime": {
            "executable": BOOTSTRAP, "adapter": "portable-native", "platform": "windows-x64",
            "version": f"fixture-{pack_id}"}}
        bound = release.bind_descriptor(raw, self.contract, self.files(self.common), "26.3")
        self.emit(candidate, release.DESCRIPTOR, bound)
        content = {"id": pack_id, "name": f"{pack_id} fixture", "relativePath": f"Profile {pack_id}",
                   "summary": "A compact native pack.", "description": "A compact native pack used for verification.",
                   "nativeProject": {"name": "Example", "version": "v2.1", "revision": "a" * 40}}
        self.emit(candidate, "native/presentation.json", {"name": content["name"]})
        self.emit(candidate, "launcher/controls-preset.txt", b"keyboard.only-profile\n")
        mods, pages = [], {}
        if modded:
            self.emit(candidate, MOD, b"pack-owned mod")
            mods.append({"id": "sample", "name": "Sample", "version": "1", "source": MOD})
            pages["sample"] = "https://modrinth.com/mod/sample"
        payload = self.files(candidate)
        inventory = {"schemaVersion": 1, "minecraftVersion": "26.3", "nativeRuntime": bound["nativeRuntime"],
                     "nativeFiles": {name: record["sha256"] for name, record in payload.items() if name.startswith("native/")},
                     "mods": mods}
        self.emit(candidate, "library/content.json", content)
        self.emit(candidate, "library/loaded-mods.json", inventory)
        self.emit(candidate, "library/mod-pages.json", pages)
        self.emit(candidate, "library/preview.jpg", b"\xff\xd8\xffpreview")

        source = self.root / f"source-{pack_id}"
        source.mkdir()
        shutil.copytree(candidate / "library", source / "library")
        shutil.copyfile(candidate / release.DESCRIPTOR, source / release.DESCRIPTOR)
        self.emit(source, "tools/distribution-lock.json", {"files": payload})
        for command in (("init", "--quiet"), ("config", "core.autocrlf", "false"), ("add", "."),
                        ("-c", "user.name=Test", "-c", "user.email=test@example.org", "commit", "--quiet", "-m", "fixture")):
            subprocess.run(["git", *command], cwd=source, check=True, capture_output=True)
        features = {"schemaVersion": 2, "packId": pack_id, "relativePath": content["relativePath"],
                    "requiredLauncherFeatures": FEATURES}
        return candidate, source, features

    def migrate(self, pack_id, *, modded=False, different_client=False):
        candidate, source, features = self.candidate(pack_id, modded=modded, different_client=different_client)
        out = self.root / f"dist-{pack_id}"
        manifest = release.migrate_pack(candidate, source, self.runtime, features, out, [MOD] if modded else [])
        return out, manifest

    def test_same_runtime_two_independent_packs_and_reproducible_output(self):
        plain, base = self.migrate("fixture-plain")
        modded, mods = self.migrate("fixture-modded", modded=True)
        report = release.verify_family(self.runtime, (plain, modded))
        self.assertEqual([0, 1], [row["modCount"] for row in report["packs"]])
        self.assertEqual(self.contract["archive"], report["runtime"])
        self.assertNotEqual(base["nativeRuntime"]["version"], mods["nativeRuntime"]["version"])
        for dist, manifest in ((plain, base), (modded, mods)):
            self.assertEqual([*FEATURES, release.FEATURE], manifest["requiredLauncherFeatures"])
            self.assertNotIn(CLIENT, release.file_map(manifest["files"]))
            descriptor_asset = next(asset for asset in manifest["assets"] if release.DESCRIPTOR in asset["covers"])
            with zipfile.ZipFile(dist / "assets" / descriptor_asset["name"]) as archive:
                self.assertEqual([release.DESCRIPTOR], archive.namelist())
                loaded = json.loads(archive.read(release.DESCRIPTOR))
            self.assertEqual(self.contract, loaded["nativeRuntime"]["sharedRuntime"])
        self.assertIn([MOD], [asset["covers"] for asset in mods["assets"]])
        repeated = self.root / "runtime-repeated"
        reversed_spec = {**self.spec, "files": list(reversed(self.spec["files"]))}
        self.assertEqual(self.contract, release.stage_runtime(self.common, reversed_spec, repeated))
        self.assertEqual(self.files(self.runtime), self.files(repeated))

    def test_different_client_rejected_before_writing_distribution(self):
        with self.assertRaisesRegex(ValueError, "differs from the shared runtime bytes"):
            self.migrate("fixture-diverged", different_client=True)
        self.assertFalse((self.root / "dist-fixture-diverged").exists())

    def test_runtime_tag_platform_and_family_marker_are_bound(self):
        with self.assertRaisesRegex(ValueError, "immutable release tag"):
            release.validate_runtime({**self.spec, "source": {**self.spec["source"], "tag": "pack-latest"}}, staged=False)
        with self.assertRaisesRegex(ValueError, "schema or platform"):
            release.validate_runtime({**self.spec, "platform": "linux-x64"}, staged=False)
        self.emit(self.common, release.FAMILY, {"schemaVersion": 1, "id": self.spec["id"], "version": "unrelated-v2",
                                               "platform": "windows-x64", "minecraftVersion": "26.3"})
        with self.assertRaisesRegex(ValueError, "family marker differs"):
            release.stage_runtime(self.common, {**self.spec, "files": release.rows(self.files(self.common))},
                                  self.root / "mismatched-family")

    def test_raw_zip_and_runtime_pin_divergence_are_rejected(self):
        overlay, _ = self.migrate("fixture-overlay")
        alternate = self.root / "alternate-runtime"
        self.emit(self.common, CLIENT, b"another exact common client")
        release.stage_runtime(self.common, {**self.spec, "files": release.rows(self.files(self.common))}, alternate)
        with self.assertRaisesRegex(ValueError, "release manifest and runtime contract differ"):
            release.verify_family(alternate, [overlay])
        path = self.runtime / "assets" / self.contract["archive"]["name"]
        data = path.read_bytes()
        path.write_bytes(data[:50] + bytes([data[50] ^ 1]) + data[51:])
        with self.assertRaisesRegex(ValueError, "approved SHA-256 or size"):
            release.verify_family(self.runtime, [overlay])

    def test_case_alias_and_file_directory_overlap_are_rejected(self):
        common = self.files(self.common)
        for different in (CLIENT.upper(), CLIENT + "/child"):
            with self.subTest(different=different), self.assertRaises(ValueError):
                release.assert_disjoint(common, {different: common[CLIENT]})


if __name__ == "__main__":
    unittest.main()
