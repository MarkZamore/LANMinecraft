# No-game deployment preparation

This standalone .NET 10 Windows console tool loads a reviewed launcher assembly
through reflection. It has no project reference to the launcher and does not
rebuild it. Run only after the launcher and Minecraft have stopped. It never
calls `MinecraftProcessService` or starts a Minecraft client/server main class.
Production runtime preparation can run the official loader/Java installers.

## Build and inspect

From the repository root:

```powershell
dotnet build tools/DeploymentPrep/DeploymentPrep.csproj -c Release
$tool = 'tools/DeploymentPrep/bin/Release/net10.0-windows/win-x64/DeploymentPrep.dll'
$product = '<reviewed distribution>/LANMinecraft.dll'
$portable = '<existing portable installation root>'
$sha = (Get-FileHash -LiteralPath $product -Algorithm SHA256).Hash.ToLowerInvariant()
dotnet $tool --assembly $product --assembly-sha $sha --root $portable --report 'artifacts/deployment/plan.json' --dry-run --mode all
```

Keep the distribution's other assemblies and `.deps.json` beside its DLL.
Use an independent copy when another process is building that distribution.
Read the local JSON plan before applying. The assembly hash pins the reviewed
input; an assembly without the 26.2-to-26.3 migration or owner-hosted transport
URLs is refused. Dry-run loads no production settings/logger and performs no
installation mutation. Only its explicitly requested report directory is written.

## Apply

After reviewing the plan, use a new report filename:

```powershell
dotnet $tool --assembly $product --assembly-sha $sha --root $portable --report 'artifacts/deployment/apply.json' --apply --mode all
```

`--mode all` performs these production operations in order:

1. `SettingsService.Load` and `PackRenameMigration.Apply` migrate the existing
   profile/settings and standard TerraFirma alias. They do not change worlds'
   gameplay data.
2. `PackCatalogService.RefreshOnceAsync` and
   `PortablePackSyncService.SyncAsync` sync only Minecraft 26.3. Offline fallback
   cannot satisfy this operation. Its descriptor must select Minecraft 26.3 and
   NeoForge 26.3.0.99.
3. `PackRuntimeService.PrepareAsync` installs the official runtime, followed by
   `PackInstanceService.PrepareAsync` for this profile only. No prepared runtime
   is copied by hand. Production's distance cap may update this profile's
   `options.txt`; its other option contents remain protected.
4. `ManagedComponentService.EnsureSteamTransportModAsync` prepares transport for
   each existing supported profile using its actual manifest and existing
   `PackInstanceContext`. Other profiles are not mirrored or runtime-installed.
   Their compatible builds share the global component cache and download from
   immutable `MarkZamore/e4steam` release URLs with exact size/SHA verification.

`--mode minecraft26` runs the profile upgrade plus its transport preparation.
`--mode transport-all` only prepares transport for existing supported profiles;
it does not load/migrate settings, sync packs, or prepare runtime/instances.

## Guards and reports

The plan checks canonical and instance mod folders. A transport is recognized
only by a catalog filename, size and SHA256 from the active/historical pins.
Unknown or corrupted `e4steam*.jar` and `e4mc*.jar` cause a refusal before
installation changes. Unknown ordinary JARs remain protected; production may
preserve a conflicting instance copy in `Personal/PackConflicts`, whose exact
bytes are verified. `Steam_Rails` is an ordinary mod and is not a transport match.

World files, player data, identity/skin registries, selected skin, saved player
name and other personal options are protected. Hashes stream file contents;
logs do not print world contents. Saves mappings must point to the same profile
under `Minecraft/Worlds` and remain identical after canonical rename. World
metadata may only receive the known build-name/path rename. Nested world links,
unexpected deployment links, locked worlds, a live launcher or Minecraft
process, conflicting old/new folders, and unsupported legacy JSON identity
keys cause refusal. Existing registries hold the persisted identity; production
`LocalIdentityId`/`LocalIdentityName` properties are `[JsonIgnore]`.

The tool checks processes between stages, but cannot prevent a user from
starting the launcher concurrently; keep it closed throughout. The extra tool
lock prevents two copies of this harness from running together. Reports include
canonical names, selected pins/URLs, before/after hashes, shared-cache paths,
per-profile install/download results, and aggregate counts. Settings are copied
to the report's `.before` directory. This is not a full installation backup or
transactional rollback. A failed operation reports its exact completed stages;
the normal production sync remains responsible for atomic pack delivery.

Reports and production logs can contain local paths. Keep them in ignored
`artifacts`; do not publish raw reports or private logs.

## Reproducible isolated rehearsal

The fixture factory copies only actual baseline descriptors and verified old
author transport/EMI JARs. It creates synthetic world/player/skin sentinels and
independent files, never a personal-world backup. It requires a **new** root
inside the original workspace's `artifacts`; no existing directory is removed.
The original root is read only. Production profile sync and transport APIs run
against a fixture-only HTTP handler using already verified payloads. Any other
request is refused.

After upgrading the original root, pass `-BaselineRoot` pointing to a retained
independent old fixture/input snapshot. It supplies only old descriptors and
exact author transport JARs; its world or other personal files are never copied.
`OriginalRoot` still supplies the allowed workspace `artifacts` output boundary.

```powershell
./tools/DeploymentPrep/Test-Rehearsal.ps1 `
  -Assembly $product `
  -OriginalRoot '<workspace root>' `
  -FixtureParent '<workspace root>/artifacts/deployment-rehearsal-new' `
  -Candidates '<verified candidate JAR directory>' `
  -FixtureDist '<Minecraft 26.3 pack source>/dist'
```

The suite checks dry-run byte/link immutability; seven-profile apply; idempotent
repeat with zero transport downloads/installs; and refusal without fixture
mutation for unknown canonical transport, corrupted instance transport, a held
world lock, and a wrong-profile saves link. Each expected refusal is a passing
safety test, not a successful installation.

`--rehearsal --skip-runtime --fixture-dist ... --fixture-components ...` is
accepted only with the generated `.deployment-rehearsal` marker. Fixtures skip
the genuine official installer and explicitly report it as **NOT RUN**. The
rehearsal does not prove gameplay, Mixin, client/server, world migration, skin
rendering or two-client compatibility. These checks remain unperformed under
the user's no-game acceptance.
