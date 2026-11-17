# Shared Steam component delivery

Steam compatibility data belongs to `MarkZamore/LANMinecraft-Components`, in `data/steam-components.json`. The generated runtime component catalog carries the reviewed Steam builds, exact historical cleanup pins and coverage. Steam payloads remain immutable release assets in `MarkZamore/e4steam`. See [runtime component delivery](RUNTIME-COMPONENTS.md) for the complete lifecycle.

`RuntimeComponentService` obtains a fresh catalog before preparing a game. One checked snapshot drives Java and Steam selection, managed installation, instance preservation, identity fingerprints and mappings. Startup loads verified cached metadata before creating its consumers and starting cleanup. The first transition requires the network; a prepared pack retains its checked selection for offline launch. The launcher contains no embedded Steam catalog or implicit historical pin fallback.

Validation checks exact SHA256, sizes, immutable publisher release URLs, loader ranges, provenance and required native-UI/authentication capabilities. Downloaded and cached JARs pass `ManagedComponentService` size/SHA validation. Previously published builds remain exact-hash retirement records; unknown conflicting JARs remain conflicts. Catalog generation carries published current entries into history so metadata updates retain the evidence needed to recognize old installed files. History can grow without changing the launcher.

Optional `java8Builds` entries describe reviewed source-built Forge transports for packs declaring `preserveWorldSettingsOnShare`. A `source-built-legacy-port` describes a platform without an author JAR, including the reviewed Legacy Fabric 1.6.4 transport. These entries retain exact upstream and publisher source revisions and class hashes. Missing optional entries supply no hidden built-in selection. Existing author artifacts retain their byte-identical or reviewed source-derivative provenance requirements.

The catalog selects infrastructure. Pack configurations and game/mod content stay with their pack owners. Native UI suppression is a declared capability rather than a delivery-tag spelling.

## Future updates

Publish and verify immutable Steam payloads in `MarkZamore/e4steam`, then update the reviewed data in `LANMinecraft-Components`. Its component candidate CI checks newly built components against the launcher contract before a new immutable component release and current catalog pointer are published. Existing release assets and historical hashes are never overwritten. Compatible data and Java component updates become available on the next preparation without rebuilding the launcher EXE.

New private APIs, authentication behavior or Minecraft mappings require corresponding source review and tests. A catalog update alone does not establish runtime compatibility.

## Verification

`SteamComponentCatalogTests`, `SteamTransportModTests` and related instance/identity/cleanup tests verify reviewed coverage, source provenance, byte pins, range/capability validation and shared selection. `RuntimeComponentServiceTests` cover checked cache/installed fallback, recovery, snapshot isolation, shared downloads and pack renames. Contract tests use the external fixture selected by `tools/Prepare-ComponentTests.ps1`; candidate CI selects the newly built catalog and artifacts. Required profiles are documented in [TESTING.md](../Program.Tests/TESTING.md).

Native acceptance must identify the exact runtime, component hashes and scenarios. A saved alias replay does not replace an actual new game preflight, and source checks alone establish no Steam guest connection or authentication-success result.
