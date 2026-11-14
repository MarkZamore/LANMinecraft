import json
from io import BytesIO
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile

import prepare
import release_overlay as release


class ReleaseOverlayTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.source = root / "source"
        self.stage = root / "stage"
        for directory in (self.source / "tools", self.source / "launcher", self.stage / "game/config"):
            directory.mkdir(parents=True)
        self.base = {"schemaVersion": 1, "minecraftVersion": "1.12.2",
                     "loader": {"type": "forge", "version": "14.23.5.2860"}, "javaMajorVersion": 8}
        self.config_path = "config/game.cfg"
        self.original = b"# author config\nlan = true\n"
        self.modified = self.original.replace(b"lan = true", b"lan = false")
        self.author = {
            "schemaVersion": 1, "status": "client-only",
            "client": {"projectId": 10, "fileId": 20, "sha256": "a" * 64},
            "pack": {"minecraftVersion": self.base["minecraftVersion"], "loader": self.base["loader"]},
            "declaredDependencies": [],
            "overrides": [{"path": self.config_path, **prepare.fingerprint(self.original)}],
        }
        self.recipe_path = self.source / "tools/overlay.json"
        descriptor = self.source / release.DESCRIPTOR
        self.write_json(descriptor, {**self.base, "preserveWorldSettingsOnShare": True})
        self.recipe = {
            "schemaVersion": 1, "authorArchiveSha256": self.author["client"]["sha256"],
            "descriptorSha256": prepare.fingerprint(descriptor.read_bytes())["sha256"],
            "patches": [{"path": self.config_path, "before": prepare.fingerprint(self.original),
                         "after": prepare.fingerprint(self.modified), "from": "lan = true", "to": "lan = false"}],
        }
        self.write_json(self.recipe_path, self.recipe)
        (self.source / release.OWNERSHIP).write_bytes((self.config_path + "\n").encode())
        self.stage_config = self.stage / "game" / self.config_path
        self.stage_config.write_bytes(self.original)
        self.receipt = {**self.author, "status": "complete", "descriptor": self.base, "files": []}
        self.receipt["overlay"] = prepare.apply_overlay(self.stage / "game", self.source, self.receipt, {})
        self.receipt_path = self.stage / "intake.json"
        self.write_json(self.receipt_path, self.receipt)

    @staticmethod
    def write_json(path, data):
        path.write_text(json.dumps(data), encoding="utf-8")

    def test_release_checks_receipt_and_exact_stage_outputs(self):
        files, excluded = release.verify_stage(self.stage, self.source, self.receipt)
        self.assertEqual(set(files), {release.DESCRIPTOR, release.OWNERSHIP, self.config_path})
        self.assertEqual(excluded, [])

        receipt = {**self.receipt, "overlay": {**self.receipt["overlay"], "requiredLauncherFeatures": []}}
        self.write_json(self.receipt_path, receipt)
        with self.assertRaisesRegex(ValueError, "required launcher feature"):
            release.verify_stage(self.stage, self.source, self.receipt)

        self.write_json(self.receipt_path, self.receipt)
        self.stage_config.write_bytes(self.original)
        with self.assertRaisesRegex(ValueError, "Staged policy output differs"):
            release.verify_stage(self.stage, self.source, self.receipt)

    def test_author_update_preserves_pinned_client_script_and_rejects_changed_inputs(self):
        path = "kubejs/client_scripts/lanmc_policy.js"
        data = b"// priority: 1000\n"
        source = self.source / path
        source.parent.mkdir(parents=True)
        source.write_bytes(data)
        dependency = {"projectId": 11, "fileId": 22}
        required = {**dependency, "required": True}
        self.recipe["clientScripts"] = {
            "dependencies": [dependency],
            "files": [{"path": path, **prepare.fingerprint(data)}],
        }
        self.author["declaredDependencies"] = [required]
        self.write_json(self.recipe_path, self.recipe)
        (self.source / release.OWNERSHIP).write_bytes(f"{self.config_path}\n{path}\n".encode())
        candidate = {**self.author, "client": {**self.author["client"], "fileId": 21, "sha256": "b" * 64}}
        reviewed = release.review_update(self.source, self.author, candidate)
        self.assertEqual(reviewed["clientScripts"], self.recipe["clientScripts"])
        with self.assertRaisesRegex(ValueError, "changed, missing or excluded client script dependency"):
            release.review_update(self.source, self.author,
                                  {**candidate, "declaredDependencies": [{**required, "fileId": 23}]})
        with self.assertRaisesRegex(ValueError, "conflicting client script"):
            release.review_update(self.source, self.author, {**candidate, "overrides": [
                *candidate["overrides"], {"path": path.upper(), **prepare.fingerprint(data)}]})

        self.write_json(self.recipe_path, reviewed)
        stage = self.stage.parent / "updated-stage"
        game = stage / "game"
        (game / self.config_path).parent.mkdir(parents=True)
        (game / self.config_path).write_bytes(self.original)
        artifact = {"path": "mods/client-script-fixture.jar", **prepare.fingerprint(b"jar")}
        (game / "mods").mkdir()
        (game / artifact["path"]).write_bytes(b"jar")
        receipt = {**candidate, "status": "complete", "descriptor": self.base,
                   "files": [{**required, **artifact}]}
        receipt["overlay"] = prepare.apply_overlay(game, self.source, receipt, {})
        self.write_json(stage / "intake.json", receipt)
        self.assertEqual((game / path).read_bytes(), data)
        policy_files, excluded = release.verify_stage(stage, self.source, receipt)
        self.assertEqual(policy_files[path], prepare.fingerprint(data))
        self.assertFalse(excluded)
        (game / path).write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "Staged policy output differs"):
            release.verify_stage(stage, self.source, receipt)

    def test_metadata_patch_keeps_author_pin_and_requires_fixed_jar(self):
        path = "mods/example.jar"
        original_jar = BytesIO()
        with ZipFile(original_jar, "w") as archive:
            archive.writestr("pack.mcmeta", '{"pack":{"pack_format":${pack_format_number}}}')
            archive.writestr("assets/example/data.txt", b"unchanged")
        original = original_jar.getvalue()
        metadata = {"pack": {"description": "Example", "pack_format": 34}}
        license_path = "licenses/Example.txt"
        (self.source / "licenses").mkdir()
        (self.source / license_path).write_bytes(b"Example license")
        modified = prepare.rebuild_jar_metadata(original, metadata, b"Example license")
        with ZipFile(BytesIO(modified)) as archive:
            self.assertEqual(json.loads(archive.read("pack.mcmeta")), metadata)
            self.assertEqual(archive.read("assets/example/data.txt"), b"unchanged")
            self.assertEqual(archive.read("LICENSE"), b"Example license")
        before = prepare.fingerprint(original)
        after = prepare.fingerprint(modified)
        dependency = {"projectId": 11, "fileId": 22, "required": True}
        author = {**self.receipt, "declaredDependencies": [dependency],
                  "files": [{**dependency, "path": path, **before}]}
        recipe = {"jarMetadataPatches": [{"path": path, "projectId": 11, "fileId": 22, "before": before,
                                          "after": after, "metadata": metadata, "license": license_path}]}
        rows = prepare.jar_metadata_patches(recipe, author, self.source)
        inventory, required = release.payload_inventory(author, {}, [], rows)
        self.assertEqual(inventory[path], after)
        self.assertIn(path, required)
        changed = {**author, "files": [{**author["files"][0], "sha256": "f" * 64}]}
        with self.assertRaisesRegex(ValueError, "changed dependency JAR bytes"):
            prepare.jar_metadata_patches(recipe, changed, self.source)

    def test_manifest_requires_feature_and_all_policy_files(self):
        files, _ = release.verify_stage(self.stage, self.source, self.receipt)
        manifest = {"schemaVersion": 2, **self.author["pack"],
                    "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE],
                    "files": [{"path": path, **pin} for path, pin in files.items()]}
        release.verify_manifest(manifest, files, self.receipt)
        with self.assertRaisesRegex(ValueError, "must require"):
            release.verify_manifest({**manifest, "requiredLauncherFeatures": []}, files, self.receipt)
        with self.assertRaisesRegex(ValueError, "missing required payload files"):
            release.verify_manifest({**manifest, "files": manifest["files"][:-1]}, files, self.receipt)

    def test_quest_translation_tracks_exact_source_and_only_owns_russian_table(self):
        english = prepare.QUEST_LANG + "en_us.snbt"
        russian = prepare.RUSSIAN_QUESTS
        original = b'{file.0000000000000001.title: "Book"}'
        translated = '{file.0000000000000001.title: "Книга"}'.encode()
        source_path = self.source / russian
        source_path.parent.mkdir(parents=True)
        source_path.write_bytes(translated)
        (self.stage / "game" / english).parent.mkdir(parents=True)
        (self.stage / "game" / english).write_bytes(original)
        receipt = {**self.receipt, "overrides": [
            *self.receipt["overrides"], {"path": english, **prepare.fingerprint(original)}
        ]}
        recipe = {**self.recipe, "questTranslation": {
            "inputs": {english: prepare.fingerprint(original)}, "output": prepare.fingerprint(translated)
        }}
        self.write_json(self.recipe_path, recipe)
        ownership = "\n".join(sorted([russian, self.config_path])) + "\n"
        (self.source / release.OWNERSHIP).write_bytes(ownership.encode())
        ownership_path = self.stage / "game" / release.OWNERSHIP
        ownership_path.unlink()
        (self.stage / "game" / release.DESCRIPTOR).unlink()
        self.stage_config.write_bytes(self.original)
        receipt["overlay"] = prepare.apply_overlay(self.stage / "game", self.source, receipt, {})
        self.write_json(self.receipt_path, receipt)
        files, _ = release.verify_stage(self.stage, self.source, receipt)
        self.assertEqual(files[russian], prepare.fingerprint(translated))
        self.assertEqual((self.stage / "game" / english).read_bytes(), original)
        self.assertNotIn(english, ownership_path.read_text())
        changed = {**receipt, "overrides": [{"path": english, **prepare.fingerprint(b"new text")}]}
        with self.assertRaisesRegex(ValueError, "changed quest text"):
            prepare.quest_translation(recipe, self.source, changed)
        official = {**receipt, "overrides": [*receipt["overrides"], {"path": russian, **prepare.fingerprint(translated)}]}
        with self.assertRaisesRegex(ValueError, "author's new Russian"):
            prepare.quest_translation(recipe, self.source, official)

    def test_book_localization_keeps_english_and_requires_exact_replacement_and_addition(self):
        english = "patchouli_books/guide/en_us/entries/first.json"
        russian = "patchouli_books/guide/ru_ru/entries/first.json"
        quest = "config/ftbquests/quests/chapters/start.snbt"
        language = "kubejs/assets/example/lang/ru_ru.json"
        original_language = b"{}\r\n"
        original = b'{"name":"Guide"}'
        original_quest = b'{title:"First Steps"}'
        translated = '{"name":"Справочник"}'.encode()
        localized_quest = b'{title:"{example.first_steps}"}'
        additions = {
            russian: translated, quest: localized_quest,
            "kubejs/assets/example/lang/en_us.json": b'{"example.first_steps":"First Steps"}',
            language: '{"example.first_steps":"Первые шаги"}'.encode(),
            "kubejs/assets/example/patchouli_books/guide/ru_ru/categories/start.json": translated,
            "kubejs/assets/example/book/materials/ru_ru/tools/first.json": translated,
            "kubejs/assets/example/guide/_ru_ru/first.md": "## Первые шаги\n".encode(),
        }
        for name, data in additions.items():
            target = self.source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        replacements = {quest: original_quest, language: original_language}
        receipt = {**self.receipt, "overrides": [*self.receipt["overrides"],
            {"path": english, **prepare.fingerprint(original)},
            *({"path": name, **prepare.fingerprint(data)} for name, data in replacements.items())]}
        recipe = {**self.recipe, "bookLocalization": [
            {"path": name, "inputs": {english: prepare.fingerprint(original)}, "output": prepare.fingerprint(data),
             **({"before": prepare.fingerprint(replacements[name])} if name in replacements else {})}
            for name, data in additions.items()
        ]}
        self.write_json(self.recipe_path, recipe)
        (self.source / release.OWNERSHIP).write_bytes(
            ("\n".join(sorted({self.config_path, *additions})) + "\n").encode())
        stage = self.stage / "localized"
        game = stage / "game"
        for name, data in {english: original, self.config_path: self.original, **replacements}.items():
            target = game / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        receipt["overlay"] = prepare.apply_overlay(game, self.source, receipt, {})
        self.write_json(stage / "intake.json", receipt)
        policy, _ = release.verify_stage(stage, self.source, receipt)
        _, required = release.payload_inventory(receipt, policy, [])
        self.assertLessEqual(additions.keys(), required)
        self.assertEqual((game / english).read_bytes(), original)
        self.assertEqual((game / quest).read_bytes(), localized_quest)
        self.assertEqual((game / language).read_bytes(), additions[language])
        changed = {**receipt, "overrides": [row for row in receipt["overrides"] if row["path"] != english]}
        with self.assertRaisesRegex(ValueError, "changed book localization input"):
            prepare.book_localizations(recipe, self.source, changed)
        conflict = {**receipt, "overrides": [
            *receipt["overrides"], {"path": russian, **prepare.fingerprint(translated)}]}
        with self.assertRaisesRegex(ValueError, "conflicting or unsupported"):
            prepare.book_localizations(recipe, self.source, conflict)
        (game / russian).write_bytes(original)
        with self.assertRaisesRegex(ValueError, "Staged policy output differs"):
            release.verify_stage(stage, self.source, receipt)

    def test_book_localization_mod_is_bound_to_the_forge_payload(self):
        path = "mods/guide-localization-1.0.0.jar"
        input_path = "patchouli_books/guide/en_us/entries/first.json"
        english = prepare.fingerprint(b'{"name":"Guide"}')
        author = {**self.receipt, "overrides": [
            *self.receipt["overrides"], {"path": input_path, **english}]}
        target = self.source / path
        target.parent.mkdir()

        def write_mod(extra):
            with ZipFile(target, "w") as jar:
                jar.writestr("META-INF/mods.toml", 'modLoader="javafml"\n[[mods]]\nmodId="localization"\n')
                jar.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n\n")
                jar.writestr("localization.mixins.json", '{"client":["BookMixin"]}')
                for name, value in extra.items():
                    jar.writestr(name, value)
            return {"bookLocalization": [
                {"path": path, "inputs": {input_path: english}, "output": prepare.fingerprint(target.read_bytes())}]}

        recipe = write_mod({})
        self.assertEqual(prepare.book_localizations(recipe, self.source, author), {path: target.read_bytes()})
        with self.assertRaisesRegex(ValueError, "Unexpected book localization mod"):
            prepare.book_localizations(write_mod({"META-INF/jarjar/another.jar": b"any"}), self.source, author)

    def test_book_jar_localization_only_touches_pinned_book_resources(self):
        path = "mods/guide.jar"
        book = "data/guide/patchouli_books/manual/"
        descriptor = book + "book.json"
        english = book + "en_us/entries/first.json"
        russian = book + "ru_ru/entries/first.json"
        original_files = {descriptor: b'{"landing_text":"Start here","version":1}', english: b'{"name":"Guide"}',
                          "guide/Example.class": b"unchanged bytecode"}
        jar = BytesIO()
        with ZipFile(jar, "w") as archive:
            for name, data in original_files.items():
                archive.writestr(name, data)
        original = jar.getvalue()
        entries = {descriptor: b'{"landing_text":"guide.book.landing","version":1}',
                   russian: '{"name":"Справочник"}'.encode(),
                   "LICENSE": b"Exact license text", "NOTICE": b"Modified book variant"}
        descriptor_pin = prepare.fingerprint(original_files[descriptor])
        rebuilt = prepare.rebuild_book_jar(original, entries, {descriptor: descriptor_pin})
        with ZipFile(BytesIO(rebuilt)) as archive:
            self.assertEqual({name: archive.read(name) for name in archive.namelist()}, {**original_files, **entries})
        with self.assertRaisesRegex(ValueError, "Only title and landing"):
            prepare.rebuild_book_jar(original, {**entries, descriptor: b'{"version":2}'}, {descriptor: descriptor_pin})
        dependency = {"projectId": 11, "fileId": 22, "required": True}
        receipt = {**self.receipt, "declaredDependencies": [dependency],
                   "files": [{**dependency, "path": path, **prepare.fingerprint(original)}]}
        recipe = {**self.recipe, "bookJarLocalization": [{
            "path": path, "projectId": 11, "fileId": 22, "before": prepare.fingerprint(original),
            "after": prepare.fingerprint(rebuilt), "entries": []
        }]}
        for name, data in entries.items():
            source = ("licenses/" + name) if name in {"LICENSE", "NOTICE"} else ("tools/book-localization/" + name)
            target = self.source / source
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            recipe["bookJarLocalization"][0]["entries"].append({
                "path": name, "source": source, "output": prepare.fingerprint(data),
                **({"before": descriptor_pin} if name == descriptor else {})
            })
        self.write_json(self.recipe_path, recipe)
        stage = self.stage / "jar-localized"
        game = stage / "game"
        for name, data in {self.config_path: self.original, path: original}.items():
            target = game / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        receipt["overlay"] = prepare.apply_overlay(game, self.source, receipt, {})
        prepare.apply_book_jar_localizations(game, self.source, receipt)
        self.write_json(stage / "intake.json", receipt)
        policy, excluded = release.verify_stage(stage, self.source, receipt)
        checked = prepare.book_jar_localizations(recipe, receipt, self.source)
        files, required = release.payload_inventory(receipt, policy, excluded, checked)
        self.assertEqual(files[path], prepare.fingerprint(rebuilt))
        self.assertIn(path, required)
        next_author = {**self.author, "declaredDependencies": [dependency]}
        reviewed = release.review_update(self.source, receipt, next_author)
        self.assertEqual(reviewed["bookJarLocalization"], checked)
        with self.assertRaisesRegex(ValueError, "changed book localization dependency"):
            release.review_update(self.source, receipt, self.author)
        (game / path).write_bytes(original)
        with self.assertRaisesRegex(ValueError, "Staged author payload differs"):
            release.verify_stage(stage, self.source, receipt)

    def test_pinned_logging_survives_author_update_and_is_required_in_payload(self):
        additions = {prepare.LOGGING_CONFIG: b"<Configuration/>\n",
                     "launcher/jvm-args.txt": b"-Dlog4j.configurationFile=config/log4j2.xml\n"}
        for name, data in additions.items():
            path = self.source / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(data)
        recipe = {**self.recipe, "additions": [{"path": name, **prepare.fingerprint(data)}
                                                 for name, data in additions.items()]}
        self.write_json(self.recipe_path, recipe)
        (self.source / release.OWNERSHIP).write_bytes(
            ("\n".join(sorted({self.config_path, prepare.LOGGING_CONFIG})) + "\n").encode())
        next_stage = self.stage / "with-logging"
        game = next_stage / "game"
        (game / self.config_path).parent.mkdir(parents=True)
        (game / self.config_path).write_bytes(self.original)
        receipt = {**self.receipt, "overlay": prepare.apply_overlay(game, self.source, self.receipt, {})}
        self.write_json(next_stage / "intake.json", receipt)
        policy, _ = release.verify_stage(next_stage, self.source, receipt)
        _, required = release.payload_inventory(receipt, policy, [])
        self.assertLessEqual(additions.keys(), required)

        next_author = {**self.author, "client": {**self.author["client"], "sha256": "b" * 64}}
        self.assertEqual(release.review_update(self.source, self.author, next_author)["additions"], recipe["additions"])
        next_author["overrides"] = [*next_author["overrides"], recipe["additions"][0]]
        with self.assertRaisesRegex(ValueError, "conflicting logging path"):
            release.review_update(self.source, self.author, next_author)

    def test_bootstrap_binds_exact_author_jar_and_is_required_in_payload(self):
        artifact_path = "lanminecraft/checked-bootstrap.jar"
        args_path = "launcher/jvm-args.txt"
        author_jar = b"exact author dependency"
        author_input = {"projectId": 18, "fileId": 28, "path": "mods/provider.jar",
                        **prepare.fingerprint(author_jar)}
        author = {**self.receipt, "declaredDependencies": [{"projectId": 18, "fileId": 28, "required": True}],
                  "files": [{**author_input, "required": True}]}
        artifact = BytesIO()
        with ZipFile(artifact, "w") as jar:
            jar.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nPremain-Class: lanminecraft.Agent\r\n\r\n")
            jar.writestr("lanminecraft/Agent.class", b"\xca\xfe\xba\xbe\x00\x00\x00\x34")
        payloads = {prepare.LOGGING_CONFIG: b"<Configuration/>\n",
                    args_path: f"-Dlog4j.configurationFile=config/log4j2.xml\n-javaagent:{artifact_path}\n".encode(),
                    artifact_path: artifact.getvalue()}
        for name, data in payloads.items():
            path = self.source / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(data)
        recipe = {**self.recipe, "additions": [{"path": name, **prepare.fingerprint(payloads[name])}
                                               for name in prepare.LOGGING_FILES],
                  "bootstrap": {"input": author_input,
                                "artifact": {"path": artifact_path, **prepare.fingerprint(artifact.getvalue())}}}
        self.write_json(self.recipe_path, recipe)
        (self.source / release.OWNERSHIP).write_bytes(
            ("\n".join(sorted({self.config_path, prepare.LOGGING_CONFIG, artifact_path})) + "\n").encode("utf-8"))
        stage = self.stage / "with-bootstrap"
        game = stage / "game"
        for name, data in {self.config_path: self.original, author_input["path"]: author_jar}.items():
            path = game / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        receipt = {**author, "overlay": prepare.apply_overlay(game, self.source, author, {})}
        self.write_json(stage / "intake.json", receipt)
        policy, _ = release.verify_stage(stage, self.source, receipt)
        _, required = release.payload_inventory(receipt, policy, [])
        self.assertIn(artifact_path, required)
        self.assertEqual((game / author_input["path"]).read_bytes(), author_jar)
        self.assertEqual(release.review_update(self.source, author, author)["bootstrap"], recipe["bootstrap"])

        changed = {**author, "files": [{**author["files"][0], "sha256": "0" * 64}]}
        with self.assertRaisesRegex(ValueError, "changed Java 8 bootstrap input bytes"):
            release.review_update(self.source, author, changed)
        self.write_json(stage / "intake.json", {**receipt, "files": changed["files"]})
        with self.assertRaisesRegex(ValueError, "changed Java 8 bootstrap input bytes"):
            release.verify_stage(stage, self.source, {**author, "status": "client-only"})
        self.write_json(stage / "intake.json", receipt)
        client_candidate = {**author, "status": "client-only",
                            "declaredDependencies": [{"projectId": 18, "fileId": 29, "required": True}]}
        with self.assertRaisesRegex(ValueError, "changed Java 8 bootstrap dependency"):
            release.review_update(self.source, author, client_candidate)
        with self.assertRaisesRegex(ValueError, "exact launcher JVM argument"):
            prepare.overlay_bootstrap(recipe, self.source, author, {args_path: b"-javaagent:other.jar\n"})

        (game / artifact_path).write_bytes(b"replaced helper")
        with self.assertRaisesRegex(ValueError, "Staged policy output differs"):
            release.verify_stage(stage, self.source, receipt)

    def test_release_preserves_author_overrides_and_required_and_optional_dependencies(self):
        game = self.stage / "game"
        content = {"structures/authored.nbt": b"structure bytes", "mods/gameplay.jar": b"required mod",
                   "resourcepacks/optional.zip": b"optional resource pack"}
        for path, data in content.items():
            target = game / path
            target.parent.mkdir(parents=True)
            target.write_bytes(data)
        original = {"path": "structures/authored.nbt", **prepare.fingerprint(content["structures/authored.nbt"])}
        required = {"projectId": 111, "fileId": 222, "required": True}
        optional = {"projectId": 333, "fileId": 444, "required": False}
        mod = {**required, "path": "mods/gameplay.jar", **prepare.fingerprint(content["mods/gameplay.jar"])}
        resource = {**optional, "path": "resourcepacks/optional.zip",
                    **prepare.fingerprint(content["resourcepacks/optional.zip"])}
        receipt = {**self.receipt, "overrides": [*self.author["overrides"], original],
                   "declaredDependencies": [required, optional], "files": [mod, resource]}
        self.write_json(self.receipt_path, receipt)
        policy, excluded = release.verify_stage(self.stage, self.source, receipt)

        files = [{"path": path, **pin} for path, pin in policy.items()] + [original, mod]
        manifest = {"schemaVersion": 2, **self.author["pack"],
                    "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE], "files": files}
        release.verify_manifest(manifest, policy, receipt, excluded)
        release.verify_manifest({**manifest, "files": files + [resource]}, policy, receipt, excluded)
        for missing in (original, mod):
            with self.assertRaisesRegex(ValueError, "missing required payload files"):
                release.verify_manifest({**manifest, "files": [row for row in files if row != missing]},
                                        policy, receipt, excluded)
        for unreviewed in ({**original, "path": "saves/QA/level.dat"},
                           {**original, "path": "STRUCTURES/authored.nbt"}, mod):
            with self.assertRaisesRegex(ValueError, "unreviewed or repeated file"):
                release.verify_manifest({**manifest, "files": files + [unreviewed]}, policy, receipt, excluded)
        with self.assertRaisesRegex(ValueError, "manifest payload differs"):
            release.verify_manifest({**manifest, "files": files + [{**resource, "sizeBytes": resource["sizeBytes"] + 1}]},
                                    policy, receipt, excluded)

        for path, data in content.items():
            target = game / path
            target.write_bytes(b"x" + data[1:])
            with self.assertRaisesRegex(ValueError, "Staged author payload differs.*sha256 mismatch"):
                release.verify_stage(self.stage, self.source, receipt)
            target.write_bytes(data + b"x")
            with self.assertRaisesRegex(ValueError, "Staged author payload differs.*larger"):
                release.verify_stage(self.stage, self.source, receipt)
            target.write_bytes(data)

        for dependencies, diagnostic in (([mod, {**resource, "required": True}], "differs from its author declaration"),
                                         ([resource], "missing a required dependency")):
            changed = {**receipt, "files": dependencies}
            self.write_json(self.receipt_path, changed)
            with self.assertRaisesRegex(ValueError, diagnostic):
                release.verify_stage(self.stage, self.source, changed)

    def test_author_refresh_keeps_recipe_when_edits_are_unrelated(self):
        new_archive = {**self.author["client"], "fileId": 21, "sha256": "b" * 64}
        untouched = {"path": "config/unrelated.cfg", **prepare.fingerprint(b"new upstream file")}
        candidate = {**self.author, "client": new_archive, "overrides": [*self.author["overrides"], untouched]}
        proposed = release.review_update(self.source, self.author, candidate)
        self.assertEqual(proposed, {**self.recipe, "authorArchiveSha256": new_archive["sha256"]})
        self.assertEqual(json.loads(self.recipe_path.read_bytes()), self.recipe)

    def test_exact_duplicate_dependency_and_override_share_one_payload_path(self):
        path = "mods/embedded.jar"
        data = b"identical author dependency bytes"
        target = self.stage / "game" / path
        target.parent.mkdir()
        target.write_bytes(data)
        override = {"path": path, **prepare.fingerprint(data)}
        dependency = {"projectId": 111, "fileId": 222, "required": False}
        file = {**dependency, **override}
        receipt = {**self.receipt, "overrides": [*self.author["overrides"], override],
                   "declaredDependencies": [dependency], "files": [file]}
        self.write_json(self.receipt_path, receipt)
        policy, excluded = release.verify_stage(self.stage, self.source, receipt)
        inventory, required = release.payload_inventory(receipt, policy, excluded)
        self.assertEqual(inventory[path], prepare.fingerprint(data))
        self.assertIn(path, required)

        manifest = {"schemaVersion": 2, **self.author["pack"],
                    "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE],
                    "files": [{"path": name, **pin} for name, pin in inventory.items()]}
        release.verify_manifest(manifest, policy, receipt, excluded)
        for change in ({"sha256": "0" * 64}, {"sizeBytes": len(data) + 1}):
            mismatched = {**receipt, "files": [{**file, **change}]}
            with self.assertRaisesRegex(ValueError, "override and dependency differ"):
                release.payload_inventory(mismatched, policy, excluded)

        second = {**dependency, "fileId": 223}
        duplicate = {**receipt, "declaredDependencies": [dependency, second], "files": [file, {**file, **second}]}
        with self.assertRaisesRegex(ValueError, "Duplicate or conflicting path"):
            release.payload_inventory(duplicate, policy, excluded)

    def test_author_refresh_stops_on_changed_inputs_or_platform(self):
        changed = {"path": self.config_path, **prepare.fingerprint(self.original + b"# added comment\n")}
        for overrides in ([], [changed]):
            with self.subTest(overrides=overrides), self.assertRaisesRegex(ValueError, "changed or removed author input"):
                release.review_update(self.source, self.author, {**self.author, "overrides": overrides})
        platform = {**self.author["pack"], "minecraftVersion": "1.16.5"}
        with self.assertRaisesRegex(ValueError, "changed author project or game platform"):
            release.review_update(self.source, self.author, {**self.author, "pack": platform})

    def test_excluded_author_mod_is_checked_and_never_shipped(self):
        pin = {"projectId": 849519, "fileId": 8225255}
        recipe = {**self.recipe, "excludedDependencies": [pin]}
        self.write_json(self.recipe_path, recipe)
        author = {**self.author, "declaredDependencies": [{**pin, "required": True}]}
        with self.assertRaisesRegex(ValueError, "changed or removed author dependency"):
            release.review_update(self.source, author, self.author)

        stage = self.stage / "with-exclusion"
        game = stage / "game"
        (game / "config").mkdir(parents=True)
        (game / self.config_path).write_bytes(self.original)
        target = game / "mods/e4mc.jar"
        target.parent.mkdir()
        content = b"original author jar bytes"
        target.write_bytes(content)
        dependency = {**pin, "path": "mods/e4mc.jar", "required": True, **prepare.fingerprint(content)}
        receipt = {**author, "status": "complete", "descriptor": self.base, "files": [dependency]}
        overlay = prepare.apply_overlay(game, self.source, receipt, {})
        keys = prepare.overlay_dependency_keys(recipe, author)
        excluded = prepare.excluded_dependency_files(keys, receipt["files"])
        with self.assertRaisesRegex(ValueError, "sha256 mismatch"):
            prepare.exclude_dependencies(game, [{**excluded[0], "sha256": "0" * 64}])
        self.assertEqual(target.read_bytes(), content)
        prepare.exclude_dependencies(game, excluded)
        receipt["overlay"] = {**overlay, "excludedDependencies": excluded}
        self.write_json(stage / "intake.json", receipt)

        files, excluded_from_stage = release.verify_stage(stage, self.source, receipt)
        self.assertEqual(excluded_from_stage, excluded)
        self.assertEqual(receipt["files"], [dependency])
        manifest = {"schemaVersion": 2, **author["pack"],
                    "requiredLauncherFeatures": [prepare.REQUIRED_SHARE_FEATURE],
                    "files": [{"path": path, **data} for path, data in files.items()]}
        release.verify_manifest(manifest, files, receipt, excluded_from_stage)
        for extra in ({"path": "MODS/e4mc.jar"}, {"path": "mods/renamed.jar", "sha256": dependency["sha256"]}):
            with self.subTest(extra=extra), self.assertRaisesRegex(ValueError, "includes excluded author dependency"):
                release.verify_manifest({**manifest, "files": [*manifest["files"], extra]}, files, receipt,
                                        excluded_from_stage)
        target.write_bytes(content)
        with self.assertRaisesRegex(ValueError, "remains in staged game"):
            release.verify_stage(stage, self.source, receipt)


if __name__ == "__main__":
    unittest.main()
