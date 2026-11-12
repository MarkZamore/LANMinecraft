import json
from unittest.mock import patch
import unittest

import bind_stage_logging as bind
import prepare
import release_overlay as release
import test_stage_release


JVM_ARGS = "launcher/jvm-args.txt"


class BindStageLoggingTests(unittest.TestCase):
    def setUp(self):
        fixture = test_stage_release.StageReleaseTests()
        self.addCleanup(fixture.doCleanups)
        fixture.setUp()
        self.fixture = fixture
        self.source = fixture.source
        self.stage = fixture.root / "artifacts/agent-runs/owned/stage"
        self.stage.parent.mkdir(parents=True)
        prepare.write_json(self.stage.parent / "RUN.json", {"owner": "fixture"})
        fixture.stage.rename(self.stage)
        game = self.stage / "game"
        (game / "saves/QA/level.dat").unlink()
        self.prior_receipt = (self.stage / "intake.json").read_bytes()
        self.prior_ownership = (game / release.OWNERSHIP).read_bytes()
        recipe = prepare.read_local_json(self.source / bind.RECIPE)
        additions = {prepare.LOGGING_CONFIG: b"<Configuration/>", JVM_ARGS: b"-Dfixture=true\n"}
        recipe["additions"] = []
        for name, data in additions.items():
            source = self.source / name
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(data)
            recipe["additions"].append({"path": name, **prepare.fingerprint(data)})
        (self.source / bind.RECIPE).write_text(json.dumps(recipe), encoding="utf-8")
        (self.source / release.OWNERSHIP).write_bytes((fixture.cfg + "\n" + prepare.LOGGING_CONFIG + "\n").encode())
        fixture.git("add", ".")
        fixture.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.org", "commit", "--quiet", "-m", "Logging only")
        self.current = fixture.git("rev-parse", "HEAD").strip().decode("ascii")

    def bind_stage(self):
        return bind.bind(self.source, self.stage, self.fixture.revision, self.current, workspace=self.fixture.root)

    def test_binds_only_logging_files_and_preserves_author_bytes(self):
        receipt = self.bind_stage()
        game = self.stage / "game"
        self.assertEqual(len(receipt["additions"]), 2)
        self.assertEqual((game / self.fixture.structure).read_bytes(), b"real content")
        self.assertEqual((game / self.fixture.cfg).read_bytes(), b"lan=false\n")
        self.assertEqual((game / prepare.LOGGING_CONFIG).read_bytes(), b"<Configuration/>")
        self.assertEqual((game / JVM_ARGS).read_bytes(), b"-Dfixture=true\n")

    def test_refuses_game_generated_files_without_writing(self):
        (self.stage / "game/saves/QA/level.dat").write_bytes(b"QA world")
        with self.assertRaisesRegex(ValueError, "outside the verified author payload"):
            self.bind_stage()
        self.assert_unmodified()

    def test_rolls_back_after_post_write_verification_failure(self):
        verify = release.verify_stage

        def fail_current(stage, source, author):
            if source == self.source:
                raise ValueError("fixture verification failure")
            return verify(stage, source, author)

        with patch.object(release, "verify_stage", side_effect=fail_current):
            with self.assertRaisesRegex(ValueError, "fixture verification failure"):
                self.bind_stage()
        self.assert_unmodified()

    def assert_unmodified(self):
        game = self.stage / "game"
        self.assertEqual((self.stage / "intake.json").read_bytes(), self.prior_receipt)
        self.assertEqual((game / release.OWNERSHIP).read_bytes(), self.prior_ownership)
        self.assertFalse((game / prepare.LOGGING_CONFIG).exists())
        self.assertFalse((game / JVM_ARGS).exists())


if __name__ == "__main__":
    unittest.main()
