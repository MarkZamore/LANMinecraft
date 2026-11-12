import hashlib
from io import BytesIO
import json
from pathlib import Path, PurePosixPath
import stat
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch
from zipfile import ZipFile, ZipInfo

import prepare


class SourceTests(unittest.TestCase):
    def test_rejects_nonportable_paths_and_case_collisions(self):
        for name in ("../mods/test.jar", "/mods/test.jar", "mods\\test.jar",
                     "mods/COM¹.txt", "mods/a:stream", "mods/sub /test.jar", "mods/x."):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Unsafe Windows path"):
                prepare.safe_path(name)
        tree = {}
        prepare.reserve_path(tree, PurePosixPath("config/test.cfg"))
        with self.assertRaisesRegex(ValueError, "Conflicting path"):
            prepare.reserve_path(tree, PurePosixPath("Config/other.cfg"))

    def test_rejects_linked_zip_entry(self):
        stream = BytesIO()
        link = ZipInfo("overrides/config/link.cfg")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        with ZipFile(stream, "w") as archive:
            archive.writestr(link, "outside.cfg")
        with ZipFile(stream) as archive, self.assertRaisesRegex(ValueError, "Linked"):
            prepare.archive_members(archive)

    def test_audits_authored_map_without_writing_files(self):
        stream = BytesIO()
        path = "overrides/saves/Starter/level.dat"
        data = b"authored map template"
        with ZipFile(stream, "w") as archive:
            archive.writestr(path, data)
            archive.writestr("modlist.html", "reference outside overrides")
        with ZipFile(stream) as archive:
            rows = prepare.take_overrides(archive, prepare.archive_members(archive),
                                          PurePosixPath("overrides"), None, {})
        self.assertEqual(rows, [{"path": "saves/Starter/level.dat", "archivePath": path,
                                 "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}])

    def test_matches_exact_official_metadata_and_cdn_file(self):
        data = {"projectId": 12, "id": 1234, "fileName": "author pack.zip", "fileLength": 3,
                "hashes": [{"algo": 1, "value": hashlib.sha1(b"zip", usedforsecurity=False).hexdigest()}]}
        record = prepare.file_record(data, 12, 1234)
        prepare.verify_cdn("https://mediafilez.forgecdn.net/files/1/234/author%20pack.zip", record)
        old_record = prepare.file_record({**data, "id": 1058}, 12, 1058)
        prepare.verify_cdn("https://mediafilez.forgecdn.net/files/1/58/author%20pack.zip", old_record)
        prepare.verify_cdn("https://mediafilez.forgecdn.net/files/1/058/author%20pack.zip", old_record)
        with self.assertRaisesRegex(ValueError, "another file"):
            prepare.verify_cdn("https://mediafilez.forgecdn.net/files/1/0058/author%20pack.zip", old_record)
        with self.assertRaisesRegex(ValueError, "another file"):
            prepare.verify_cdn("https://mediafilez.forgecdn.net/files/1/235/author%20pack.zip", record)
        with self.assertRaisesRegex(ValueError, "outside official"):
            prepare.verify_cdn("https://mediafilez.forgecdn.net.example/files/1/234/author%20pack.zip", record)
        with self.assertRaisesRegex(ValueError, "another file"):
            prepare.file_record(data, 11, 1234)
        with self.assertRaisesRegex(ValueError, "sha1 mismatch"):
            prepare.exact_bytes(BytesIO(b"Zip"), 3, expected=record["originHashes"])

    def test_exact_stream_rejects_short_extra_and_changed_bytes(self):
        content = b"author content"
        pin = {"sha256": hashlib.sha256(content).hexdigest()}
        self.assertEqual(prepare.exact_bytes(BytesIO(content), len(content), expected=pin), pin["sha256"])
        for actual, message in ((content[:-1], "Byte length mismatch"),
                                (content + b".", "larger"), (content.upper(), "sha256 mismatch")):
            with self.subTest(actual=actual), self.assertRaisesRegex(ValueError, message):
                prepare.exact_bytes(BytesIO(actual), len(content), expected=pin)

    def test_reuses_only_exact_pinned_dependency_bytes(self):
        content = b"verified author dependency"
        relative = PurePosixPath("mods/author.jar")
        record = prepare.file_record({"projectId": 12, "id": 1234, "fileName": "author.jar",
                                      "fileLength": len(content)}, 12, 1234)
        record.update(path=relative.as_posix(), required=True)
        pin = {**record, **prepare.fingerprint(content)}
        entry = relative, record, pin
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "tested"
            cached = source.joinpath(*relative.parts)
            cached.parent.mkdir(parents=True)
            cached.write_bytes(content)
            with patch.object(prepare, "acquire", side_effect=AssertionError("Unexpected network fetch")):
                out = root / "release"
                self.assertEqual(pin, prepare.acquire_dependency(out, entry, verified_source=source))
                self.assertEqual(content, out.joinpath(*relative.parts).read_bytes())
                cached.write_bytes(content.upper())
                with self.assertRaisesRegex(ValueError, "sha256 mismatch"):
                    prepare.acquire_dependency(root / "invalid", entry, verified_source=source)

    def test_missing_file_metadata_is_only_an_explicit_null(self):
        uri = f"{prepare.API}/mods/12/files/1234"
        for payload, expected in (({"data": None}, prepare.MissingFileMetadata),
                                  ({}, ValueError), ({"data": {"projectId": 13}}, ValueError)):
            with self.subTest(payload=payload):
                response = BytesIO(json.dumps(payload).encode())
                response.url = uri
                with patch.object(prepare, "open_official", return_value=response), self.assertRaises(expected) as caught:
                    prepare.get_record(12, 1234)
                self.assertIs(type(caught.exception), expected)

    def test_fallback_is_exact_required_file_and_only_used_for_null_metadata(self):
        key = (12, 1234)
        content = b"author mod bytes"
        pin = {"projectId": key[0], "fileId": key[1], "fileName": "author.jar",
               "sourceUrl": "https://mediafilez.forgecdn.net/files/1/234/author.jar",
               **prepare.fingerprint(content)}
        deps = {key: True}

        def records(row=pin, declared=deps):
            return prepare.fallback_records({"schemaVersion": 1, "files": [row]}, declared)

        for source in (pin["sourceUrl"].replace("/234/", "/235/"), pin["sourceUrl"].replace("author.jar", "other.jar")):
            with self.subTest(source=source), self.assertRaisesRegex(ValueError, "another file"):
                records({**pin, "sourceUrl": source})
        with self.assertRaisesRegex(ValueError, "unique required author dependency"):
            records(declared={key: False})
        with self.assertRaisesRegex(ValueError, "exact mod JAR and SHA256"):
            records({**pin, "sha256": "unknown"})

        fallbacks = records()
        original = prepare.file_record({"projectId": key[0], "id": key[1], "fileName": "author.jar",
                                        "fileLength": len(content)}, *key)
        with patch.object(prepare, "get_record", return_value=original):
            live = prepare.dependency_plan(deps, {}, {}, {}, 0, fallbacks)[0][1]
            self.assertEqual(live["sourceUrl"], f"{prepare.API}/mods/12/files/1234/download")
        with patch.object(prepare, "get_record", side_effect=ValueError("network request failed")):
            with self.assertRaisesRegex(ValueError, "network request failed"):
                prepare.dependency_plan(deps, {}, {}, {}, 0, fallbacks)
        with patch.object(prepare, "get_record", side_effect=prepare.MissingFileMetadata("explicit null")):
            fallback = prepare.dependency_plan(deps, {}, {}, {}, 0, fallbacks)[0][1]
            self.assertEqual(fallback["sourceUrl"], pin["sourceUrl"])
            expected = prepare.pinned_hash(fallback, None)
            self.assertEqual(prepare.exact_bytes(BytesIO(content), len(content), expected=expected), pin["sha256"])
            with self.assertRaisesRegex(ValueError, "sha256 mismatch"):
                prepare.exact_bytes(BytesIO(content.upper()), len(content), expected=expected)
            with self.assertRaisesRegex(ValueError, "disagree on SHA256"):
                prepare.pinned_hash(fallback, {**fallback, "sha256": "0" * 64})

    def test_rejects_unassigned_dependencies_and_pins_optional_identity(self):
        files = {(12, 1234): True, (13, 1235): False}

        def record(project, file):
            name = "author.jar" if project == 12 else "map.zip"
            return prepare.file_record({"projectId": project, "id": file, "fileName": name, "fileLength": 3},
                                       project, file)

        with patch.object(prepare, "get_record", side_effect=record):
            with self.assertRaisesRegex(ValueError, "13/1235"):
                prepare.dependency_plan(files, {}, {}, {}, 0)
            plan = prepare.dependency_plan(files, {(13, 1235): PurePosixPath("resourcepacks")}, {}, {}, 0)
            self.assertEqual([row["path"] for _, row, _ in plan], ["mods/author.jar", "resourcepacks/map.zip"])
            self.assertIs(plan[1][1]["required"], False)
            original = {**plan[1][1], "sha256": "a" * 64}
            with self.assertRaisesRegex(ValueError, "placement or requirement changed"):
                prepare.dependency_plan(files, {(13, 1235): PurePosixPath("saves")}, {},
                                        {(13, 1235): original}, 0)

    def test_same_path_override_is_shared_only_after_independent_sha_verification(self):
        contents = b"author pack"
        deps = {(12, 1234): True}
        placements = {(12, 1234): PurePosixPath("resourcepacks")}
        relative = PurePosixPath("resourcepacks/author.zip")
        overrides = {relative.as_posix(): prepare.fingerprint(contents)}
        path_map = {}
        prepare.reserve_path(path_map, relative)
        record = prepare.file_record({"projectId": 12, "id": 1234, "fileName": "author.zip",
                                      "fileLength": len(contents)}, 12, 1234)
        with patch.object(prepare, "get_record", return_value={**record, "sizeBytes": len(contents) + 1}):
            with self.assertRaisesRegex(ValueError, "conflicts with author override"):
                prepare.dependency_plan(deps, placements, path_map, {}, len(contents), overrides=overrides)
        with patch.object(prepare, "get_record", return_value=record):
            entry = prepare.dependency_plan(deps, placements, path_map, {}, prepare.MAX_TREE,
                                            overrides=overrides)[0]
        with TemporaryDirectory() as temporary:
            game = Path(temporary)
            target = game.joinpath(*relative.parts)
            target.parent.mkdir()
            target.write_bytes(contents)
            for data in (contents, contents.upper()):
                stream = BytesIO(data)
                stream.url = "https://mediafilez.forgecdn.net/files/1/234/author.zip"
                with patch.object(prepare, "open_official", return_value=stream):
                    if data == contents:
                        acquired = prepare.acquire_dependency(game, entry, overrides)
                        self.assertEqual(acquired["sha256"], overrides[relative.as_posix()]["sha256"])
                    else:
                        with self.assertRaisesRegex(ValueError, "sha256 mismatch"):
                            prepare.acquire_dependency(game, entry, overrides)
                self.assertEqual(target.read_bytes(), contents)
                self.assertEqual(list(target.parent.iterdir()), [target])

    def test_share_overlay_requires_pinned_archive_descriptor_and_config_bytes(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, game = root / "source", root / "game"
            for directory in (source / "tools", source / "launcher", game / "config"):
                directory.mkdir(parents=True)
            base = {"schemaVersion": 1, "minecraftVersion": "1.12.2",
                    "loader": {"type": "forge", "version": "14.23.5.2860"}, "javaMajorVersion": 8}
            descriptor = json.dumps({**base, "preserveWorldSettingsOnShare": True}).encode()
            (source / "portable-pack.json").write_bytes(descriptor)
            relative = "config/Author Tweaks.cfg"
            original = b"comment\r\nlan = true\r\nkeep = true\r\ncheats = true\r\n"
            modified = b"comment\r\nlan = false\r\nkeep = true\r\ncheats = false\r\n"
            target = game / relative
            target.write_bytes(original)
            before, after = prepare.fingerprint(original), prepare.fingerprint(modified)
            receipt = {"client": {"sha256": "a" * 64}, "descriptor": base,
                       "overrides": [{"path": relative, **before}]}
            replacements = [{"from": "lan = true", "to": "lan = false"},
                            {"from": "cheats = true", "to": "cheats = false"}]
            edit = {"path": relative, "before": before, "after": after, "replacements": replacements}
            recipe = {"schemaVersion": 1, "authorArchiveSha256": "a" * 64,
                      "descriptorSha256": prepare.fingerprint(descriptor)["sha256"], "patches": [edit]}
            recipe_path = source / "tools/overlay.json"
            ownership = (relative + "\n").encode()
            (source / "launcher/pack-owned.txt").write_bytes(ownership)

            for wrong, error in (({**recipe, "authorArchiveSha256": "b" * 64}, "pinned author archive"),
                                 ({**recipe, "descriptorSha256": "b" * 64}, "pinned platform"),
                                 ({**recipe, "patches": [{**edit, "after": before}]}, "output differs"),
                                 ({**recipe, "patches": [{**edit, "replacements": replacements[:1]}]}, "output differs"),
                                 ({**recipe, "patches": [{**edit, "replacements": replacements + replacements[:1]}]},
                                  "Expected one overlay replacement")):
                with self.subTest(error=error):
                    recipe_path.write_text(json.dumps(wrong))
                    with self.assertRaisesRegex(ValueError, error):
                        prepare.apply_overlay(game, source, receipt, {})
                    self.assertEqual(target.read_bytes(), original)
                    self.assertFalse((game / "portable-pack.json").exists())

            recipe_path.write_text(json.dumps(recipe))
            changed_input = original.replace(b"keep = true", b"keep = false")
            for updated_receipt, error in (
                ({**receipt, "client": {"sha256": "b" * 64}}, "pinned author archive"),
                ({**receipt, "overrides": [{"path": relative, **prepare.fingerprint(changed_input)}]},
                 "pinned author file"),
            ):
                target.write_bytes(changed_input)
                with self.assertRaisesRegex(ValueError, error):
                    prepare.apply_overlay(game, source, updated_receipt, {})
                self.assertEqual(target.read_bytes(), changed_input)
                self.assertFalse((game / "portable-pack.json").exists())

            target.write_bytes(modified)
            with self.assertRaisesRegex(ValueError, "Staged overlay input differs"):
                prepare.apply_overlay(game, source, receipt, {})
            target.write_bytes(original)
            applied = prepare.apply_overlay(game, source, receipt, {})
            self.assertEqual(applied["patches"], [{"path": relative, "before": before, "after": after}])
            self.assertEqual(target.read_bytes(), modified)
            self.assertEqual((game / "portable-pack.json").read_bytes(), descriptor)
            self.assertEqual((game / "launcher/pack-owned.txt").read_bytes(), ownership)


if __name__ == "__main__":
    unittest.main()
