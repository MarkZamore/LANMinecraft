# Official mod project pages

`mod-pages.json` maps each stable mod ID from the shipped jars to the official project page.
The library generator writes it as `websiteUrl`, for gameplay mods, addons and libraries alike.
A new mod without a verified HTTPS project page fails metadata generation.

Use CurseForge first; if the same project is not published there, use Modrinth;
otherwise use its author's official website or source/release repository.
Match the actual jar/hash and author, especially for forks and similarly named projects.
Never guess a slug, substitute an unrelated project, or link to search results, mirrors,
issue trackers or direct jar downloads. A bundled component with no standalone project
may link to the original author's pack; record that exception explicitly.

Page lookup belongs to catalogue preparation. Launcher navigation and clicks do not
search hosting sites, check updates, download game files or change the active pack.
After changing a jar or adding a pack, update the mapping and run the generator tests
and a fresh build from actual jars. Do not commit API keys or private cache files.

Unavailable author pages use `{ "url": "https://…", "available": false }`. The
launcher keeps the name as plain text; a future verified replacement can enable it
through metadata without updating the executable. Check each pack library README
for bundled-only components and unavailable source exceptions.
