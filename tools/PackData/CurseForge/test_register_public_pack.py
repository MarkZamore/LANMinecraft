import unittest

import register_public_pack as register


class RegisterPublicPackTests(unittest.TestCase):
    def setUp(self):
        self.allocations = {"schemaVersion": 1, "allocations": [{"id": "minecraft-1"}, {"id": "minecraft-2"}]}
        self.existing = {"id": "minecraft-1", "relativePath": "Existing", "installationPath": "Current",
                         "legacyRelativePaths": ["Earlier"],
                         "source": {"owner": "MarkZamore", "repo": "Existing", "tag": "pack-latest"}}
        self.catalog = {"schemaVersion": 1, "packs": [self.existing]}
        self.entry = {"id": "minecraft-2", "relativePath": "New Pack",
                      "source": {"owner": "MarkZamore", "repo": "New-Pack", "tag": "pack-latest"}}

    def test_appends_once_preserving_existing_entry(self):
        updated = register.append_entry(self.catalog, self.entry, self.allocations)
        self.assertEqual(updated["packs"], [self.existing, self.entry])
        self.assertEqual(self.catalog["packs"], [self.existing])
        self.assertEqual(register.append_entry(updated, self.entry, self.allocations), updated)

    def test_rejects_conflicting_paths_and_ids(self):
        for changes in ({"relativePath": "existing"}, {"relativePath": "CURRENT"},
                        {"relativePath": "earlier"}, {"id": "minecraft-1"}, {"id": "minecraft-3"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                register.append_entry(self.catalog, {**self.entry, **changes}, self.allocations)


if __name__ == "__main__":
    unittest.main()
