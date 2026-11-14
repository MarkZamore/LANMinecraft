# Curated mod lists in a pack card

Native packs may set `"showNativeProject": false` in `library/content.json` to
show the standard Minecraft version and mod count and hide the official project
link in the detail view. The verified `nativeProject` provenance remains required.
The default is `true` for existing cards. The summary and description are authored
by the pack as before.

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
JarJar mod IDs. `CatalogPack.ModCount` retains that full native inventory.
Player-facing counts use `DisplayModCount`. The optional `modCountScope` accepts
only `visible` (the default) or `all`. `visible` counts unique entries in the
actual main mod tree, including all nested addons even when collapsed. `all`
counts all unique non-library content IDs before the visibility filter, while
the card can still show only selected additions. Auxiliary libraries never
enter either content count. Groups never multiply the count. Published
manifest `modCount`, installation checks and memory estimation keep the complete
native inventory and remain independent of presentation. Non-mod language
providers and Minecraft/loader platform entries are not pack mods.

For a Forge compatibility loader that registers a Fabric mod, record the
native `id`, `version` and `nativeSourceFile` and the shipped outer `source`
in `library/loaded-mods.json`. Its `forgeFabricSource` object contains the
declared `nestedPaths` chain, or an empty list for a top-level mod, and the
SHA256 of the selected original JAR. The generator verifies those bytes,
the native SRG source basename and the normalized ID and version against the
original Fabric metadata. Document the observed remapped JAR hash in the
pack's provenance. Remapped runtime files are never release inputs.

Official links remain required for the entire inventory, including hidden base
mods and dependencies. The filter does not change manifest files, installation,
memory estimation or update ownership. It requires a launcher release supporting
these metadata fields; older executables ignore the filter.

Run generator checks with `python -m unittest discover -s tools -p test_pack_library.py`.
`CatalogPresentationTests` checks the launcher tree, counter and bound heading.

## Optional grouped mod trees

`modGroups` is an optional ordered array of `{ "title": "Category", "modIds":
["visible_root"] }` objects in `library/content.json`. A pack can also use its
own `modsTitle`. Categories are presentation metadata, not launcher pack rules.
Omitting `modGroups` retains the usual flat list and does not require a new
launcher feature for installation or play. Older launchers ignore the groups.

Every visible non-library tree root must occur exactly once. An addon whose
parent is visible inherits that root's group; an addon of a hidden parent is a
root and must be listed itself. Unknown, hidden, library, repeated or non-root
IDs and incomplete coverage reject publication. Groups preserve metadata order;
items retain normal mod sorting, addon arrows and individual official links.
Only category headings reuse the auxiliary-library heading style. The separate
auxiliary-library section stays unchanged and disappears when empty.

Producers import `group_presentation` from `library_mod_groups` and call
`presentation.update(group_presentation(authored, mods))` after decorating all
rows with `parentId` and `isLibrary` and checking addon cycles. Retain this helper
and hook when importing a new author release. The helper only validates and
copies metadata; it never changes runtime files or the loaded inventory.

Run `python -B -m unittest discover -s tools -p test_library_mod_groups.py` and the
usual `test_pack_library.py` suite. Launcher `CatalogModGroupTests` checks the
same root contract, backward compatibility, normal templates and separate
link/arrow actions in grouped and flat cards at multiple DPI values.
