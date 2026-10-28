# Curated mod lists in a pack card

`library/content.json` may select a presentation list without changing the full
installed inventory or the pack's loaded-mod counter:

```json
{
  "modsTitle": "Дополнительные моды",
  "baseModIds": ["base_author_mod"],
  "visibleModIds": ["requested_author_addition", "requested_author_addon"]
}
```

Keep the usual ID, name, description and other fields alongside these fields.
The title defaults to `Моды`. Omitting `visibleModIds` preserves the complete
existing mod/library tree. An empty list deliberately shows no entries. With a
selection, only those IDs appear; an addon of a hidden base becomes a root entry.
An empty auxiliary-library section is hidden.

Record `baseModIds` from the original author pack before adding or replacing any
JAR. These stable IDs keep an updated original mod from becoming an addition.
Only explicitly requested author projects and separately requested addons belong
in `visibleModIds`; do not include automatically selected dependencies or bundled
libraries. Both lists must have unique nonempty IDs. The generator rejects a
selected base ID, missing/unloaded ID or auxiliary library.

A filtered card requires `library/loaded-mods.json`, recorded by
`record_library_inventory.py` from the actual tested client. Its source-JAR
SHA256, Minecraft and loader values must match the final pack. The library bundle
still contains every actual loaded mod, including dependencies and selected
JarJar mod IDs. The launcher counts unique loaded IDs from that complete list,
independently of the presentation filter. Non-mod language providers and the
Minecraft/loader platform entries are not counted as pack mods.

Official links remain required for the entire inventory, including hidden base
mods and dependencies. The filter does not change manifest files, installation,
memory estimation or update ownership. It requires a launcher release supporting
these metadata fields; older executables ignore the filter.

Run generator checks with `python -m unittest discover -s tools -p test_pack_library.py`.
`CatalogPresentationTests` checks the launcher tree, counter and bound heading.
