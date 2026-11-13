import unittest
from pathlib import Path

from numbered_pack_catalog import project_catalog, read_json


class NumberedPackCatalogTests(unittest.TestCase):
    def setUp(self):
        self.allocations = read_json(Path(__file__).with_name("numbered-pack-ids.json"))

    def test_preserves_membership_order_paths_and_sources(self):
        java = {
            "id": "minecraft-26-2",
            "relativePath": "Minecraft 26.3",
            "installationPath": "Minecraft Java Edition",
            "legacyRelativePaths": ["Minecraft 26.2"],
            "source": {"owner": "MarkZamore", "repo": "Minecraft-Java-Edition", "tag": "pack-latest"},
            "legacySources": [{"owner": "MarkZamore", "repo": "Minecraft-26.3", "tag": "pack-latest"}],
        }
        wolves = {
            "id": "better-than-wolves",
            "relativePath": "Better Than Wolves",
            "source": {"owner": "MarkZamore", "repo": "Better-Than-Wolves", "tag": "pack-latest"},
        }
        source = {"schemaVersion": 1, "packs": [wolves, java]}
        self.assertEqual(project_catalog(source, self.allocations), {
            "schemaVersion": 1,
            "packs": [
                {**wolves, "numberedId": "minecraft-8"},
                {**java, "numberedId": "minecraft-3"},
            ],
        })
        self.assertEqual(java["id"], "minecraft-26-2")
        self.assertEqual(wolves["id"], "better-than-wolves")

    def test_accepts_reserved_and_future_numbers_on_first_publication(self):
        source = {"schemaVersion": 1, "packs": [
            {"id": "minecraft-9"}, {"id": "minecraft-rust-mods"}, {"id": "minecraft-11"},
        ]}
        self.assertEqual(project_catalog(source, self.allocations)["packs"], [
            {"id": "minecraft-9"},
            {"id": "minecraft-rust-mods", "numberedId": "minecraft-10"},
            {"id": "minecraft-11"},
        ])
        self.assertEqual(project_catalog({"schemaVersion": 1, "packs": []}, self.allocations)["packs"], [])

    def test_rejects_unallocated_and_duplicate_entries(self):
        first_unallocated = f"minecraft-{len(self.allocations['allocations']) + 1}"
        for ids in ([first_unallocated], ["unknown-pack"], ["minecraft-9", "minecraft-rust-edition"],
                    ["all-the-mods-10", "ALL-THE-MODS-10"]):
            with self.subTest(ids=ids), self.assertRaises(ValueError):
                project_catalog({"schemaVersion": 1, "packs": [{"id": pack_id} for pack_id in ids]}, self.allocations)

    def test_verifies_existing_numbers_and_is_idempotent(self):
        source = {"schemaVersion": 1, "packs": [{"id": "all-the-mods-10"}, {"id": "minecraft-9"}]}
        numbered = project_catalog(source, self.allocations)
        self.assertEqual(project_catalog(numbered, self.allocations), numbered)
        numbered["packs"][0]["numberedId"] = "minecraft-2"
        with self.assertRaisesRegex(ValueError, "Incorrect numberedId"):
            project_catalog(numbered, self.allocations)

    def test_rejects_reused_and_skipped_numbers(self):
        after_gap = f"minecraft-{len(self.allocations['allocations']) + 2}"
        for number in ("minecraft-8", after_gap):
            with self.subTest(number=number), self.assertRaises(ValueError):
                invalid = {**self.allocations, "allocations": [*self.allocations["allocations"], {"id": number}]}
                project_catalog({"schemaVersion": 1, "packs": []}, invalid)


if __name__ == "__main__":
    unittest.main()
