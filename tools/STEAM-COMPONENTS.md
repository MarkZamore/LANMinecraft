# Shared Steam component delivery

All packs select e4steam from the same startup snapshot. `SteamComponentCatalogService` requests the publisher's `https://raw.githubusercontent.com/MarkZamore/e4steam/main/components.json` once, with a fresh query and no-cache headers. Pack discovery runs beside this small request. The result is frozen before identity/instance services and cache cleanup are created.

A valid network index takes precedence. A rejected/unavailable response leaves the last checked cache intact; a missing or invalid cache uses `Program/Resources/steam-components.json`, the embedded V2 profile. No author download is used as an installation fallback. Author URLs are provenance only.

The source index supplies exact SHA256, sizes, immutable publisher release URLs, loader ranges and capabilities. Validation rejects duplicate/overlapping ranges, loss of existing loader coverage, missing native-UI/authentication capabilities and escaping download coordinates. Every downloaded or cached JAR still passes `ManagedComponentService` size/SHA validation. The six V1 derivatives and older author files remain exact-hash retirement records; unknown conflicting JARs remain conflicts. Previously accepted catalog artifacts are retained in the checked cache history for subsequent migrations.

The same snapshot drives selection, instance preservation, managed installation, identity fingerprint/mapping and current cache IDs. Native UI suppression is a capability rather than a delivery-tag spelling. The catalog does not write pack configs or change game/mod content.

## Future author updates

In `MarkZamore/e4steam`, keep the reviewed source patches and publication verification when importing an author release. Publish immutable verified artifacts, retain the original Steam authentication, verify all applicable loader/Minecraft variants, and then activate the canonical index with the required capabilities. Compatible artifact/profile updates then arrive on the next launcher startup without a new EXE.

This is not permission to assume private APIs of arbitrary future versions. The existing identity/discovery guards still cover reviewed author versions 0.3.2/0.3.4. A new author API or Minecraft mapping can require adapter review and a launcher update. Changing catalog data alone is not proof of that compatibility.

## Verification

`SteamComponentCatalogTests` checks network/cache/embedded precedence, immutable single-flight startup, preserved cache bytes on a bad response, malformed history, all nineteen branches and pin/URL/range guards. Existing transport, instance, identity-cache and cleanup tests cover exact replacement and shared selection. All 23 catalog cases are included in the mandatory `Test-Launcher.ps1 -Profile Release` profile.

This release changes C# selection/capability wiring, not native alias literals or Java adapter code. Existing alias/mapping regressions are rerun. A replay of an older launch state is not a fresh V2 game preflight; the next actual V2 launch regenerates the transport fingerprint and performs the normal native preflight before starting Minecraft. No new game, two-client or authentication-success result is claimed by these source checks.
