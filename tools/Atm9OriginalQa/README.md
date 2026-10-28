# Original ATM9 isolated runtime QA

This console harness uses ordinary independent files below a new `artifacts`
directory. It never copies personal worlds, links into a playing instance, edits
the source pack, disables the RAM advisory, or changes author options/configs.
Full source SHA256 records, copied-cache hashes, before/after game snapshots,
console output, loaded Forge mod IDs and native lifecycle observations stay in
the fixture. Raw evidence contains local paths and must not be published.
Java receives its original CmlLib argument tokens through a local argument file
so a deeply nested independent fixture does not exceed Windows' process command
limit. That private argument file is evidence, never a publication payload.

`baseline` launches the copied author content directly, without
`PackInstanceService`, e4steam or the production identity adapter. `integrated`
uses the real `PackInstanceService`, `ManagedComponentService` and
`PortableIdentityAdapterService`, in production order. Their normal instance
preparation is deliberately exercised and captured by the snapshot. This is a
technical offline QA identity; it does not test the launcher's Steam account
identity or claim an actual Steam guest/overlay connection.

After those services, integrated mode invokes the production
`LocalAiProvisioningService.PrepareAsync` and applies its environment bindings
before starting Java. A pack without `launcher/local-ai.json` receives the same
ordinary preparation as before. A descriptor prepares the pinned official
runtime/model and instance configuration in this independent fixture; Verity
inside Java owns backend start and shutdown. The harness starts no backend and
does not keep it alive. `local-ai-preparation.json` records file provisioning
separately from readiness, Russian conversation and game/backend lifetime;
those runtime checks require their own evidence. No microphone is enabled by
the harness. File preparation may perform the same large first-run downloads
as the production Play action.

The Java agent is launch-only test instrumentation, never a Forge mod or a
distribution file. It performs no bytecode transformation. Until a coordinator
writes the ready marker after observing a fully loaded title menu, it does not
enumerate Minecraft classes or reflect into game code. Later native reads,
optional fresh-world creation and normal `Minecraft.stop` execute on the client
executor with the game classloader context. Its fixed symbols are for Minecraft
1.20.1 Forge, whose exact prepared profile/JAR hashes remain in the evidence.

From the repository root, build after other launcher builds have finished:

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
dotnet build .\tools\Atm9OriginalQa\Atm9OriginalQa.csproj -c Debug
.\tools\Atm9OriginalQa\Build-Agent.ps1 -OutputDirectory .\artifacts\atm9-original\qa-agent
$qa = '.\tools\Atm9OriginalQa\bin\Debug\net10.0-windows\win-x64\Atm9OriginalQa.dll'
$fixture = '.\artifacts\atm9-original\qa-baseline'
$agent = '.\artifacts\atm9-original\qa-agent\atm9-original-qa-agent.jar'
dotnet $qa prepare $fixture --source .\artifacts\atm9-original\pack --cache-root . --mode baseline
dotnet $qa launch $fixture --label baseline-01 --agent $agent --heap 10240 --fresh-world ATM9QaFresh
```

When validating a different clean launcher worktree, pass
`-p:LauncherProject=<absolute-path-to-that-Program/Minecraft.csproj>` to the
harness build. After that worktree's full Java/launcher build has completed,
`-p:BuildProjectReferences=false` avoids another shared Program build. Do not
backfill DLLs from another checkout. Integrated launches use the production
`MinecraftProcessService.PlayingHere` helper to supply the dashed local QA UUID,
so normal chunk pacing recognizes the host's own client.

The existing tool also accepts `prepare ... --pack-name "All the Mods 9 Modified"`
to create that separate profile in a new independent fixture. Its source must
be the final frozen pack, never an old Modified installation. The recorded
launcher assembly hash must belong to the selected isolated launcher worktree.

For a focused runtime mixin diagnostic, append the standalone `--mixin-export`
flag to `launch`. It passes the standard Mixin export setting with the filter
`net.minecraft.world.item.BoatItem`; the resulting native transformed class is
under the fixture game's `.mixin.out` directory. The flag introduces no
transformer or payload injection, changes no mixin requirements, and grants no
compatibility PASS. Preserve the class and relevant startup warning for caller
analysis. Export files remain private QA evidence, not pack content.

Skin verification is explicitly opt-in. `--skin-from-install-root <installation>`
reads only the chosen `SkinPath` from that installation's settings; alternatively
`--skin-path <png>` selects an explicit reference. The PNG is copied into private
fixture evidence and the normal `SkinService` runs with `NullPeerTransport`, so
no data is sent to friends. The harness verifies exact bytes, SHA256 and detected
model over the production loopback endpoint, passes the normal skin registry JVM
property, and keeps the service alive until game exit. Source settings and PNG
are never written. `skin-evidence.json` exposes only texture facts and QA flags,
not source settings, private paths or account IDs. HTTP verification is separate
from the coordinator's visual check; another player's skin remains unverified.

The launch command remains alive, recording output until normal game exit.
Use another terminal for control commands. Once the actual client reaches a
healthy title menu, explicitly allow native observation/creation:

```powershell
dotnet $qa ready $fixture --label baseline-01
dotnet $qa audit $fixture --label baseline-01
dotnet $qa stop $fixture --label baseline-01
```

`--fresh-world ATM9QaFresh` requests native creative/normal world creation
using the current registry's NORMAL preset and a native random seed. It refuses
an existing target, active world or loading overlay. A returned creation request
does not establish a joined world: require repeated actual `client_state`
observations with player, level and integrated server, then normal save logs and
an exit result. After normal save/exit, `--world ATM9QaFresh` re-enters that same
disposable world through ordinary QuickPlay. Omit both options to create a world
through the ordinary native UI instead. A fresh evidence label is mandatory for
every launch. The ready marker is required again after each restart.

Create a separate fresh root with `--mode integrated` for the comparison.
`--cache-root` reads and verifies existing launcher caches into independent
copies, retaining their timestamps. It never copies existing player instances or
worlds. Omit it to prepare official runtime downloads independently.

To run the updated client on `tools/PackMigrationQa`'s technical old-installation
copy, use `adopt <its-installation-directory> --cache-root <prepared-QA-root>`.
Adoption requires both the sibling migration and historical-helper reports to
be PASS, no copied personal worlds, an unchanged original snapshot, all target
manifest file sizes/SHA256 to match, and no pending apply. It changes only that
technical copy and adds independent runtime caches. The synthetic sentinel
`saves/PackMigrationQa` is deliberately not a Minecraft world. After verifying
its two exact text files, adoption moves it to `migration-sentinels` outside
`saves`, retaining the migration proof without a false world-scanner error.
Create a distinct reserved `ATM9Qa...` world for the runtime test instead.
`prepare-integration <fixture-root>` prepares the real instance, pinned transport
and production adapter without starting Java, allowing title/preflight inspection
while another smoke run owns the GPU. `launch` repeats those same production
services before starting the client, preserving their normal order.

Compare ERROR/FATAL messages by exact family and matching startup/world time
windows with `python tools/Atm9OriginalQa/compare_logs.py --baseline <baseline-latest.log>
--current <current-latest.log> --output <artifacts/comparison.json>`. The comparer
preserves resource IDs and callers, separates offline QA credential failures and
JEI timing notices, and normalizes only runtime object identities and JEI elapsed
seconds. A baseline signature observed in another stage still requires review;
counts from different windows are not rates. `audit.json` is bounded and lists observations; it never grants visual,
world-compatibility, Steam overlay, discovery or guest-connection PASS. The
production one-click pause-menu action, repeated-click behavior and both Steam
join routes need separately recorded observations. A dedicated server or a
local TCP status reply is not a substitute for those checks.
