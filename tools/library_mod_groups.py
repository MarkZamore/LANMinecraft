"""Validate optional pack-authored groups after normal addon/library decoration.

This source-time helper never changes the installed inventory or addon tree.
Call presentation.update(group_presentation(authored, mods)) before serialization.
"""


def group_presentation(authored, mods):
    presentation = {}
    if "modCountScope" in authored:
        scope = authored["modCountScope"]
        if scope not in ("visible", "all"):
            raise ValueError("modCountScope must be visible or all")
        presentation["modCountScope"] = scope
    if "modGroups" not in authored:
        return presentation
    groups = authored["modGroups"]
    if not isinstance(groups, list) or len(groups) > 100:
        raise ValueError("modGroups must be an array with at most 100 groups")
    visible = authored.get("visibleModIds")
    selected = None if visible is None else set(visible)
    presented = {key: mod for key, mod in mods.items() if selected is None or key in selected}
    roots = {key for key, mod in presented.items() if not mod.get("isLibrary", False)
             and mod.get("parentId") not in presented}
    used, titles, result = set(), set(), []
    for group in groups:
        if not isinstance(group, dict):
            raise ValueError("Invalid mod group")
        title, ids = group.get("title"), group.get("modIds")
        if (not isinstance(title, str) or not title.strip() or len(title) > 120 or title in titles
                or not isinstance(ids, list) or len(ids) > 10000):
            raise ValueError("Invalid mod group title or modIds")
        titles.add(title)
        for mod_id in ids:
            if not isinstance(mod_id, str) or not mod_id.strip() or mod_id not in roots or mod_id in used:
                raise ValueError("Invalid, repeated or non-root grouped mod ID: " + str(mod_id))
            used.add(mod_id)
        result.append({"title": title, "modIds": list(ids)})
    if used != roots:
        raise ValueError("Mod groups must cover every visible non-library root exactly once")
    return {**presentation, "modGroups": result}
