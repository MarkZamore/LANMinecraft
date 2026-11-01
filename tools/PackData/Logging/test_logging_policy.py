import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("network_logging_policy", HERE / "prepare_logging.py")
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class LoggingPolicyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.pack = Path(self.temp.name) / "Source Pack"
        self.pack.mkdir()
        self.write("portable-pack.json", b'{"minecraftVersion":"1.20.1"}')
        self.template = (HERE / "log4j2.xml").read_bytes()
        text = self.template.decode("utf-8-sig")
        start = text.index("        <!-- Preserve only Connection's caught throwable diagnostics")
        end = text.index("</Logger>", start) + len("</Logger>")
        end += 2 if text[end:end + 2] == "\r\n" else 1
        self.baseline = (text[:start] + text[end:]).encode("utf-8")
        self.assertNotIn(b'name="net.minecraft.network.Connection"', self.baseline)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, relative, data):
        path = self.pack / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def snapshot(self):
        return {path.relative_to(self.pack).as_posix(): (path.read_bytes(), path.stat().st_mtime_ns)
                for path in self.pack.rglob("*") if path.is_file()}

    def seed(self, config=None):
        self.write(policy.CONFIG, self.baseline if config is None else config)
        self.write(policy.JVM, b"# author flag\r\n-Dauthor.choice=true\r\n")
        self.write(policy.OWNED, b"# author ownership\r\nconfig/author.toml\r\n")

    def test_prepare_preserves_custom_xml_comments_other_flags_and_line_endings(self):
        original = self.baseline.replace(b"\r\n", b"\n").replace(b"<Loggers>", b'<Loggers>\n        <!-- author comment -->\n        <Logger name="author.example" level="INFO"/>')
        original = b"\xef\xbb\xbf" + original.replace(b"\n", b"\r\n")
        self.seed(original)
        result = policy.prepare(self.pack)
        expected = original.replace(b'        <Root level="INFO">', policy.BLOCK.replace("\n", "\r\n").encode() + b'        <Root level="INFO">')
        self.assertEqual(expected, (self.pack / policy.CONFIG).read_bytes())
        self.assertEqual(b"# author flag\r\n-Dauthor.choice=true\r\n" + policy.SELECTOR.encode() + b"\r\n", (self.pack / policy.JVM).read_bytes())
        self.assertEqual(b"# author ownership\r\nconfig/author.toml\r\nconfig/log4j2.xml\r\n", (self.pack / policy.OWNED).read_bytes())
        self.assertEqual(result["policy"], policy.verify(self.pack))

    def test_prepare_is_byte_and_timestamp_idempotent_and_verify_is_read_only(self):
        self.seed()
        policy.prepare(self.pack)
        before = self.snapshot()
        self.assertEqual([], policy.prepare(self.pack)["changed"])
        policy.verify(self.pack)
        self.assertEqual(before, self.snapshot())

    def test_approved_existing_config_remains_exact_and_config_tree_claim_is_preserved(self):
        self.seed(self.template)
        owned = b"# author\nconfig/**\n"
        self.write(policy.OWNED, owned)
        policy.prepare(self.pack)
        self.assertEqual(self.template, (self.pack / policy.CONFIG).read_bytes())
        self.assertEqual(owned, (self.pack / policy.OWNED).read_bytes())
        self.assertEqual(policy.REFERENCE_SHA256, policy.verify(self.pack)["config"]["sha256"])

    def test_custom_connection_logger_collision_is_rejected_without_changes(self):
        for collision in (
            b'<Logger name="net.minecraft.network.Connection" level="DEBUG"/>',
            b'<Logger name="net.minecraft.network" level="DEBUG"/>',
        ):
            with self.subTest(collision=collision):
                self.seed(self.baseline.replace(b"<Loggers>", b"<Loggers>\n" + collision))
                before = self.snapshot()
                with self.assertRaises(ValueError):
                    policy.prepare(self.pack)
                self.assertEqual(before, self.snapshot())

    def test_prepare_refuses_conflicting_or_duplicate_jvm_selector_without_changes(self):
        for selectors in (policy.SELECTOR + "\n" + policy.SELECTOR,
                          "-Dlog4j.configurationFile=config/author.xml"):
            with self.subTest(selectors=selectors):
                self.seed()
                self.write(policy.JVM, selectors.encode())
                before = self.snapshot()
                with self.assertRaises(ValueError):
                    policy.prepare(self.pack)
                self.assertEqual(before, self.snapshot())

    def test_verify_rejects_missing_duplicate_or_changed_selector_without_repair(self):
        self.seed()
        policy.prepare(self.pack)
        for flags in (b"-Dauthor=true\n", (policy.SELECTOR + "\n" + policy.SELECTOR).encode(),
                      b"-Dlog4j.configurationFile=config/another.xml\n"):
            with self.subTest(flags=flags):
                self.write(policy.JVM, flags)
                before = self.snapshot()
                with self.assertRaises(ValueError):
                    policy.verify(self.pack)
                self.assertEqual(before, self.snapshot())

    def test_verify_rejects_unbounded_filters_root_console_file_and_rollover(self):
        self.seed(self.template)
        policy.prepare(self.pack)
        replacements = (
            (b'onMismatch="DENY"', b'onMismatch="NEUTRAL"'),
            (policy.MESSAGE_PATTERN.encode(), b".*"),
            (b'<Root level="INFO">', b'<Root level="DEBUG">'),
            (b'<AppenderRef ref="SysOut" level="INFO"/>', b'<AppenderRef ref="SysOut"/>'),
            (b'fileName="logs/latest.log"', b'fileName="logs/debug.log"'),
            (b'size="32 MB"', b'size="512 MB"'),
            (b'max="5"', b'max="50"'),
        )
        for old, new in replacements:
            with self.subTest(replacement=old):
                self.write(policy.CONFIG, self.template.replace(old, new))
                before = self.snapshot()
                with self.assertRaises(ValueError):
                    policy.verify(self.pack)
                self.assertEqual(before, self.snapshot())

    def test_verify_rejects_missing_receipt_xml_ownership_and_changed_pins(self):
        self.seed()
        policy.prepare(self.pack)
        for relative in (policy.CONFIG, policy.RECEIPT, policy.OWNED):
            with self.subTest(missing=relative):
                path = self.pack / relative
                data = path.read_bytes()
                path.unlink()
                with self.assertRaises(ValueError):
                    policy.verify(self.pack)
                path.write_bytes(data)
        self.write(policy.OWNED, b"config/author.toml\nconfig/*\n")
        with self.assertRaises(ValueError):
            policy.verify(self.pack)
        self.write(policy.OWNED, b"config/log4j2.xml\n")
        data = json.loads((self.pack / policy.RECEIPT).read_bytes())
        data["config"]["sha256"] = "0" * 64
        self.write(policy.RECEIPT, json.dumps(data).encode())
        with self.assertRaises(ValueError):
            policy.verify(self.pack)

    def test_new_pack_requires_explicit_template_and_creates_only_exact_claim(self):
        before = self.snapshot()
        with self.assertRaises(ValueError):
            policy.prepare(self.pack)
        self.assertEqual(before, self.snapshot())
        policy.prepare(self.pack, HERE / "log4j2.xml")
        self.assertEqual(self.template, (self.pack / policy.CONFIG).read_bytes())
        self.assertEqual(b"config/log4j2.xml\n", (self.pack / policy.OWNED).read_bytes())

    def test_author_update_reapplies_policy_then_publication_verifies_new_receipt(self):
        self.seed()
        policy.prepare(self.pack)
        updated_author = self.baseline.replace(b'<Logger level="INFO" name="blur"/>', b'<Logger level="WARN" name="blur"/>')
        self.write(policy.CONFIG, updated_author)
        with self.assertRaises(ValueError):
            policy.verify(self.pack)
        policy.prepare(self.pack)
        self.assertIn(b'<Logger level="WARN" name="blur"/>', (self.pack / policy.CONFIG).read_bytes())
        policy.verify(self.pack)

    def test_unsupported_native_version_and_installed_pack_are_never_prepared(self):
        self.seed()
        self.write("portable-pack.json", b'{"minecraftVersion":"1.12.2"}')
        before = self.snapshot()
        with self.assertRaises(ValueError):
            policy.prepare(self.pack)
        self.assertEqual(before, self.snapshot())
        self.write("portable-pack.json", b'{"minecraftVersion":"1.20.1"}')
        self.write(".portable-instance.json", b"{}")
        before = self.snapshot()
        with self.assertRaises(ValueError):
            policy.prepare(self.pack)
        self.assertEqual(before, self.snapshot())

    def test_copied_helper_is_standalone_and_verify_cli_never_changes_source(self):
        self.seed()
        policy.prepare(self.pack)
        self.write("tools/network_logging_policy.py", (HERE / "prepare_logging.py").read_bytes())
        before = self.snapshot()
        result = subprocess.run([sys.executable, str(self.pack / "tools/network_logging_policy.py"), "verify", "--pack", str(self.pack)],
                                text=True, capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(before, self.snapshot())


if __name__ == "__main__":
    unittest.main()
