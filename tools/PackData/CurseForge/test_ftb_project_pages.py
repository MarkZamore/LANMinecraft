import unittest

import ftb_project_pages as projects
import mod_page_provenance


class FtbProjectPageTests(unittest.TestCase):
    def test_project_page_accepts_only_exact_id_and_official_mod_page(self):
        url = "https://www.curseforge.com/minecraft/mc-mods/bonsai-trees"
        document = {"id": 278993, "status": "success", "links": [{"type": "website", "link": url}]}
        row = projects.normalized_project(278993, document)
        self.assertEqual(mod_page_provenance.official_project_urls({"data": [row]}), {278993: url})
        self.assertEqual(row["metadataUrl"], f"{projects.PROJECT_API}/278993")
        with self.assertRaisesRegex(ValueError, "different or unavailable"):
            projects.normalized_project(278994, document)
        with self.assertRaisesRegex(ValueError, "outside official"):
            projects.normalized_project(278993, {**document, "links": [{"type": "website", "link": url + "/files/1"}]})

    def test_release_crosscheck_uses_file_ids_size_and_hash_not_names(self):
        digest = "a" * 64
        locked = {"projectId": 278993, "fileId": 5632262, "fileName": "author.jar", "sizeBytes": 12, "sha256": digest}
        row = {"curseforge": {"project": "278993", "file": "5632262"}, "name": "renamed.jar", "size": 12,
               "hashes": {"sha256": digest}}
        author = {"files": [locked]}
        release = {"status": "success", "parent": 99, "id": 100318, "name": "1.12.0", "files": [row]}
        self.assertEqual(projects.verify_release(99, 100318, author, release)["sameLockedFiles"], 1)
        row["hashes"]["sha256"] = "b" * 64
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            projects.verify_release(99, 100318, author, release)


if __name__ == "__main__":
    unittest.main()
