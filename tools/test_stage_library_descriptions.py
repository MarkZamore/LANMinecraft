import copy
import hashlib
import io
import json
import pathlib
import stat
import struct
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile

from stage_library_descriptions import prepare_update, stage_update


OLD = "a" * 40
NEW = "b" * 40
STORY = "Вы проснётесь вдали от дома. Найдёте укрытие среди скал. Отправитесь за редкими припасами. Встретите опасных противников. Построите место для возвращения."
SUMMARY = "Здесь вас ждёт неспешное выживание."


def payload(entries):
    output = io.BytesIO()
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)  # Deliberate duplicate-entry negative controls.
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, value in entries:
                archive.writestr(name, value)
    return output.getvalue()


def descriptor(bundle):
    digest = hashlib.sha256(bundle).hexdigest()
    return {"name": "library-" + digest + ".zip", "sha256": digest,
            "sizeBytes": len(bundle), "revision": OLD, "futureMetadata": {"retain": True}}


class DescriptionStagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.preview = b"unchanged published preview bytes"
        self.document = {"schemaVersion": 1, "id": "stable-id", "name": "Pack Name",
            "relativePath": "Published Folder", "summary": "Old summary", "description": "Old description",
            "revision": OLD, "minecraftVersion": "26.3", "loader": "native", "showNativeProject": False,
            "nativeProject": {"name": "Native Engine", "revision": "c" * 40, "version": "1.0"},
            "visibleModIds": ["project"], "modGroups": [{"title": "Развитие", "modIds": ["project"]}],
            "mods": [{"id": "project", "name": "English Mod Name", "version": "2.1",
                      "websiteUrl": "https://example.invalid/project", "isLibrary": False}],
            "futureExtension": {"nested": [1, False, None, "preserve"]}}
        self.bundle = payload([("library.json", json.dumps(self.document).encode()), ("preview.jpg", self.preview)])
        self.manifest = {"schemaVersion": 2, "revision": OLD, "packId": "stable-id", "name": "Pack Name",
            "requiredLauncherFeatures": ["native-runtime-v1"], "modCount": 1,
            "nativeRuntime": {"version": "unchanged", "executable": "native/client.exe"},
            "assets": [{"name": "existing-game.zip", "sha256": "d" * 64, "sizeBytes": 200}],
            "files": [{"path": "native/client.exe", "sha256": "e" * 64, "sizeBytes": 100}],
            "legacyFiles": [{"path": "retired.jar", "sha256": "f" * 64, "sizeBytes": 3}],
            "futureExtension": {"nested": [True, "unchanged"]}, "library": descriptor(self.bundle)}
        self.authored = {"id": "stable-id", "name": "Pack Name", "relativePath": "Published Folder",
                         "summary": SUMMARY, "description": STORY}

    def prepare(self, **checks):
        return prepare_update(self.manifest, self.bundle, self.authored, NEW, **checks)

    def test_only_three_library_text_revision_fields_and_manifest_reference_change(self):
        manifest_before, document_before = copy.deepcopy(self.manifest), copy.deepcopy(self.document)
        updated, bundle = self.prepare(sentences=5, russian_only=True)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            self.assertEqual(["library.json", "preview.jpg"], archive.namelist())
            self.assertEqual(self.preview, archive.read("preview.jpg"))
            document = json.loads(archive.read("library.json"))
            for entry in archive.infolist():
                self.assertEqual((1980, 1, 1, 0, 0, 0), entry.date_time)
                self.assertEqual(0, entry.create_system)
        self.assertEqual(SUMMARY, document.pop("summary"))
        self.assertEqual(STORY, document.pop("description"))
        self.assertEqual(NEW, document.pop("revision"))
        for field in ("summary", "description", "revision"):
            document_before.pop(field)
        self.assertEqual(document_before, document)
        old_rest = {key: value for key, value in self.manifest.items() if key not in ("revision", "library")}
        new_rest = {key: value for key, value in updated.items() if key not in ("revision", "library")}
        self.assertEqual(old_rest, new_rest)
        self.assertEqual(NEW, updated["revision"])
        self.assertEqual(NEW, updated["library"]["revision"])
        self.assertEqual(hashlib.sha256(bundle).hexdigest(), updated["library"]["sha256"])
        self.assertEqual(len(bundle), updated["library"]["sizeBytes"])
        self.assertEqual({"retain": True}, updated["library"]["futureMetadata"])
        self.assertEqual(manifest_before, self.manifest)
        self.assertEqual((updated, bundle), self.prepare(sentences=5, russian_only=True))

    def test_stage_preserves_inputs_and_never_overwrites_existing_output(self):
        paths = [self.root / name for name in ("published.json", "published.zip", "content.json")]
        sources = [json.dumps(self.manifest).encode(), self.bundle, json.dumps(self.authored).encode()]
        for path, source in zip(paths, sources):
            path.write_bytes(source)
        output = self.root / "staged"
        result = stage_update(*paths, NEW, output, sentences=5, russian_only=True)
        self.assertTrue((output / "assets" / result["name"]).is_file())
        self.assertEqual(NEW, json.loads((output / "pack-manifest.json").read_bytes())["revision"])
        self.assertEqual(sources, [path.read_bytes() for path in paths])
        with self.assertRaisesRegex(ValueError, "fresh output"):
            stage_update(*paths, NEW, output)
        self.assertEqual(sources, [path.read_bytes() for path in paths])

    def test_published_hash_size_filename_and_all_revision_pairs_are_required(self):
        cases = [("sha256", "0" * 64), ("sizeBytes", len(self.bundle) + 1),
                 ("sizeBytes", True), ("name", "../library.zip"), ("revision", "c" * 40)]
        for field, value in cases:
            with self.subTest(field=field, value=value):
                manifest = copy.deepcopy(self.manifest)
                manifest["library"][field] = value
                with self.assertRaises(ValueError):
                    prepare_update(manifest, self.bundle, self.authored, NEW)
        altered = copy.deepcopy(self.document)
        altered["revision"] = "c" * 40
        other_bundle = payload([("library.json", json.dumps(altered)), ("preview.jpg", self.preview)])
        manifest = {**self.manifest, "library": descriptor(other_bundle)}
        with self.assertRaisesRegex(ValueError, "revisions"):
            prepare_update(manifest, other_bundle, self.authored, NEW)
        with self.assertRaisesRegex(ValueError, "SHA256"):
            prepare_update(self.manifest, self.bundle + b"tampered", self.authored, NEW)

    def test_zip_paths_duplicates_directories_links_and_missing_entries_are_rejected(self):
        doc = json.dumps(self.document)
        link = zipfile.ZipInfo("preview.jpg")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        special = zipfile.ZipInfo("preview.jpg")
        special.create_system = 3
        special.external_attr = (stat.S_IFIFO | 0o600) << 16
        cases = [[], [("library.json", doc)], [("library.json", doc), ("../preview.jpg", self.preview)],
                 [("library.json", doc), ("preview.jpg/", self.preview)],
                 [("library.json", doc), ("library.json", doc)],
                 [("library.json", doc), ("preview.jpg", self.preview), ("other", b"x")],
                 [("library.json", doc), ("preview.jpg", b"")],
                 [("library.json", doc), (link, b"target")], [("library.json", doc), (special, b"x")]]
        for entries in cases:
            with self.subTest(entries=[str(row[0]) for row in entries]):
                bundle = payload(entries)
                with self.assertRaises(ValueError):
                    prepare_update({**self.manifest, "library": descriptor(bundle)}, bundle, self.authored, NEW)

    def test_duplicate_json_keys_nonfinite_metadata_and_corrupt_zip_are_rejected(self):
        invalid = [b'{"revision":"' + OLD.encode() + b'","revision":"' + OLD.encode() + b'"}',
                   b'{"value":NaN}']
        for document in invalid:
            with self.subTest(document=document):
                bundle = payload([("library.json", document), ("preview.jpg", self.preview)])
                with self.assertRaises(ValueError):
                    prepare_update({**self.manifest, "library": descriptor(bundle)}, bundle, self.authored, NEW)
        bundle = b"not a ZIP file"
        with self.assertRaisesRegex(ValueError, "Invalid library ZIP"):
            prepare_update({**self.manifest, "library": descriptor(bundle)}, bundle, self.authored, NEW)

    def test_encrypted_unsupported_and_declared_oversize_entries_fail_before_reading(self):
        encrypted = bytearray(self.bundle)
        local = encrypted.index(b"PK\x03\x04")
        central = encrypted.index(b"PK\x01\x02")
        struct.pack_into("<H", encrypted, local + 6, 1)
        struct.pack_into("<H", encrypted, central + 8, 1)
        oversize = bytearray(self.bundle)
        struct.pack_into("<I", oversize, oversize.index(b"PK\x01\x02") + 24, 8 * 1024 * 1024 + 1)
        unsupported = io.BytesIO()
        with zipfile.ZipFile(unsupported, "w", zipfile.ZIP_BZIP2) as archive:
            archive.writestr("library.json", json.dumps(self.document))
            archive.writestr("preview.jpg", self.preview)
        for bundle in (bytes(encrypted), bytes(oversize), unsupported.getvalue()):
            with self.subTest(digest=hashlib.sha256(bundle).hexdigest()):
                with self.assertRaisesRegex(ValueError, "Unsafe or oversized"):
                    prepare_update({**self.manifest, "library": descriptor(bundle)}, bundle, self.authored, NEW)

    def test_wrong_authored_target_and_invalid_revisions_fail(self):
        for field in ("id", "name", "relativePath"):
            with self.subTest(field=field):
                with self.assertRaisesRegex(ValueError, "another library"):
                    prepare_update(self.manifest, self.bundle, {**self.authored, field: "Different"}, NEW)
        for revision in (None, "", "short", "B" * 40, "g" * 40, OLD, "../" + NEW):
            with self.subTest(revision=revision):
                with self.assertRaises(ValueError):
                    prepare_update(self.manifest, self.bundle, self.authored, revision)

    def test_language_sentence_checks_are_optional_and_empty_summary_is_allowed(self):
        # The short card summary stays one sentence; --sentences applies only
        # to the long player-facing description.
        self.prepare(sentences=5, russian_only=True)
        for field in ("summary", "description"):
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    prepare_update(self.manifest, self.bundle, {**self.authored, field: "English 26.3"}, NEW, russian_only=True)
        self.authored["summary"] = ""
        self.prepare(sentences=5, russian_only=True)
        for text in (STORY.replace("Вы", "You", 1), STORY.replace("Вы", "Вы 26.3", 1),
                     STORY.replace("Вы", "Вы Ⅲ", 1), STORY.replace("Вы", "Εσείς", 1)):
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    prepare_update(self.manifest, self.bundle, {**self.authored, "description": text}, NEW, russian_only=True)
        for text in ("Одно предложение.", STORY.rstrip("."), "", " "):
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    prepare_update(self.manifest, self.bundle, {**self.authored, "description": text}, NEW, sentences=5)
        for field in ("summary", "description"):
            for value in (None, 5, [], "я" * 32001):
                with self.subTest(field=field, value=type(value).__name__):
                    with self.assertRaises(ValueError):
                        prepare_update(self.manifest, self.bundle, {**self.authored, field: value}, NEW)
        self.authored["description"] = "English description with version 26.3"
        self.prepare()

    def test_cli_reports_invalid_revision_without_creating_output(self):
        paths = [self.root / name for name in ("published.json", "published.zip", "content.json")]
        for path, source in zip(paths, [json.dumps(self.manifest).encode(), self.bundle, json.dumps(self.authored).encode()]):
            path.write_bytes(source)
        output = self.root / "staged"
        result = subprocess.run([sys.executable, "-B", str(pathlib.Path(__file__).with_name("stage_library_descriptions.py")),
            "--manifest", str(paths[0]), "--library", str(paths[1]), "--content", str(paths[2]),
            "--revision", "not-a-revision", "--out", str(output)], capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Revision must", result.stderr)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
