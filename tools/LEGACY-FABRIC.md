# Legacy Fabric runtime

An explicitly prepared pack declares its platform in `portable-pack.json`:

```json
{
  "schemaVersion": 1,
  "minecraftVersion": "1.6.4",
  "loader": { "type": "legacyfabric", "version": "0.18.2" },
  "javaMajorVersion": 17
}
```

`javaMajorVersion` is optional and must name an installed launcher pin (8, 17, 21 or 25). Without it the existing Minecraft-to-Java selection and descriptor hash remain unchanged. With it the selected Java participates in the runtime identity, cache validation and actual JVM compatibility options. Pack-specific options stay in `launcher/jvm-args.txt`.

Legacy Fabric installs from a profile and does not execute a Java installer. Preparation skips Mojang's auxiliary Java and installs only the pinned game Java. A cold preparation fixture in the Release profile rejects requests for Mojang's Java metadata and verifies that a repeat launch reuses the prepared runtime.

The loader provider requests the official `https://meta.legacyfabric.net/v2/versions/loader/<minecraft>/<loader>/profile/json` profile through CmlLib's configurable Fabric installer. It preserves upstream libraries, including `net.legacyfabric:intermediary` and Legacy Fabric's patched LWJGL/native artifacts. Its installed ID is `legacyfabric-loader-<loader>-<minecraft>`; ordinary Fabric profiles in the shared store are separate. The inherited vanilla client is named explicitly using the same Knot game-JAR repair as Fabric and Quilt.

For legacy native entries that only declare a top-level Maven repository, the provider supplies that repository as classifier download metadata. This lets CmlLib resolve the publisher's native archives on an empty cache while keeping the original classifiers and extraction rules.

The library's platform metadata uses `legacyfabric`; the preparation indicator says `Legacy Fabric`. Existing library cards continue to show their Minecraft version and mod count.

Minecraft 1.6.4 uses name-based player files and no authlib. The old UUID/authlib agent must not be applied to that runtime. Player storage, authenticated Steam identity, chosen skins and native network UI are separate compatibility contracts implemented by the legacy module and the launcher's legacy profile services. A successful runtime installation alone does not certify those features.

Publication declares `legacy-fabric-runtime-v1`, `legacy-player-profiles-v1` and `legacy-steam-v1` in a schema-2 remote manifest only after all three integrations have passed their acceptance checks and the launcher supports those capability gates.

Run the focused runtime and component validation with:

```powershell
dotnet test Program.Tests/Program.Tests.csproj --filter "FullyQualifiedName~LegacyFabricRuntimeTests|FullyQualifiedName~LegacySteamComponentCatalogTests|FullyQualifiedName~JavaRuntimeCatalogTests|FullyQualifiedName~SteamComponentCatalogTests" -p:SkipPortableIdentityAdapter=true
```

These tests cover official-profile routing, profile isolation, retained mapping/native coordinates, explicit Java/cache identity, compatible GC logging, source-port provenance and narrow Steam compatibility. Full client/world, repeat launch, native UI, skins, host handover and actual Steam P2P still need recorded runtime checks for the final published revision.
