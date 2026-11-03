import copy
import unittest

from library_mod_groups import group_presentation


class GroupPresentationTests(unittest.TestCase):
    def setUp(self):
        self.mods = {
            "base": {"id": "base", "parentId": None},
            "root": {"id": "root", "parentId": None},
            "child": {"id": "child", "parentId": "root"},
            "hidden_parent_child": {"id": "hidden_parent_child", "parentId": "base"},
            "helper": {"id": "helper", "isLibrary": True},
        }
        self.authored = {"visibleModIds": ["root", "child", "hidden_parent_child"], "modGroups": [
            {"title": "First", "modIds": ["hidden_parent_child"]},
            {"title": "Second", "modIds": ["root"]},
        ]}

    def test_groups_preserve_metadata_order_and_the_original_tree_without_duplicates(self):
        before = copy.deepcopy(self.mods)
        self.assertEqual({"modGroups": self.authored["modGroups"]}, group_presentation(self.authored, self.mods))
        self.assertEqual(before, self.mods)

    def test_absent_metadata_preserves_the_existing_flat_presentation(self):
        self.assertEqual({}, group_presentation({}, self.mods))

    def test_count_scope_is_independent_of_groups_and_rejects_unknown_values(self):
        for scope in ("visible", "all"):
            with self.subTest(scope=scope):
                self.assertEqual({"modCountScope": scope}, group_presentation({"modCountScope": scope}, self.mods))
                self.assertEqual(scope, group_presentation(self.authored | {"modCountScope": scope}, self.mods)["modCountScope"])
        for scope in (None, "", "ALL", "native", [], 3):
            with self.subTest(scope=scope), self.assertRaisesRegex(ValueError, "modCountScope"):
                group_presentation({"modCountScope": scope}, self.mods)

    def test_no_visible_roots_accepts_an_explicit_empty_group_list(self):
        self.assertEqual({"modGroups": []}, group_presentation({"visibleModIds": [], "modGroups": []}, self.mods))

    def test_invalid_groups_fail_publication(self):
        cases = [None, {}, [{"title": "First", "modIds": ["root"]}],
                 [{"title": "", "modIds": ["root", "hidden_parent_child"]}],
                 [{"title": "First", "modIds": ["root", "child", "hidden_parent_child"]}],
                 [{"title": "First", "modIds": ["base", "root", "hidden_parent_child"]}],
                 [{"title": "First", "modIds": ["helper", "root", "hidden_parent_child"]}],
                 [{"title": "First", "modIds": ["missing", "root", "hidden_parent_child"]}],
                 [{"title": "First", "modIds": ["root", "hidden_parent_child"]}, {"title": "Second", "modIds": ["root"]}],
                 [{"title": "First", "modIds": ["root"]}, {"title": "First", "modIds": ["hidden_parent_child"]}],
                 [{"title": "First", "modIds": None}], [None]]
        for groups in cases:
            with self.subTest(groups=groups), self.assertRaises(ValueError):
                group_presentation(self.authored | {"modGroups": groups}, self.mods)


if __name__ == "__main__":
    unittest.main()
