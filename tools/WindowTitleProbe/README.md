# Offline window-title verification

The global title policy uses only the pack's directory name. Minecraft and
loader versions remain audit metadata; they are not appended to the title.
Every installed profile uses the common final title producer, including ATM10.
No per-pack opt-in file or template is required, and no author configuration is
read or changed to select the title.

This probe never launches Minecraft, initializes Steam, joins a world, edits a
runtime JAR, or reads personal worlds. The snapshot console reads installed
descriptors and runtime caches and invokes the current production settings and
mapping helpers. It does not call runtime preparation or download anything.

Build the current launcher, adapter and snapshot console sequentially with
other project builds. `--install-root` names the Minecraft data directory
containing `Packs` and `Launcher`. With no `--pack` options, every installed
pack containing a manifest is included; repeated `--pack` options select a
specific subset. Every selected pack must have a matching prepared runtime.

```powershell
dotnet run --project .\tools\WindowTitleProbe -- `
    --install-root '<Minecraft data directory>' `
    --output .\artifacts\WindowTitleSnapshot
```

Use a new output directory under `artifacts`. The console checks that every
production title producer is enabled and its resolved text is exactly the pack
name. It records manifest and runtime-cache hashes and verifies those inputs
remain unchanged during observation. It writes:

- `targets.json`: exact native class/method selected by the production mapper.
- `state-N.json`: current production properties/targets for explicit preflight.
- `snapshot.json`: source hashes, exact platform versions and resolved policy.

The target list also contains one explicitly labelled disabled negative
control, derived from the first real target. This control must remain
byte-identical; it is separate from the enabled production configurations.
It does not claim that ATM10 or another installed profile is disabled.

```powershell
$env:JAVA_HOME = '<JDK installation>'
.\tools\WindowTitleProbe\Run-Probe.ps1 -TargetsJson .\artifacts\WindowTitleSnapshot\targets.json
```

The Java fixture is compiled against the freshly built production adapter and
runs with JVM `-Xverify:all`. Simulated menu, singleplayer, LAN publication,
guest join, participant changes, disconnect and repeated title updates verify
the stable pack name and unchanged connection counters. Negative cases check
method scope, aliases, descriptors and disabled behavior.

The same built adapter also runs as an actual `-javaagent` with the unrelated
identity family disabled. The fixture defines its original class bytes and
requires the registered transformer to change its title producer. The native
pass reads installed class bytes, checks the exact instance producer
`()Ljava/lang/String;`, and proves that only that method changes. The local
report records adapter, input, runtime-JAR and class SHA-256 and the
changed-method count. The disabled control must change zero methods.

Pass the emitted `state-N.json` explicitly to
`Program/IdentityAdapters/Common/Verify-IdentityAdapter.ps1`, with the current
adapter and JDK. These aliases are freshly derived offline; they do not replay
a stale state from a previous game launch. A zero-target preflight is not
claimed as proof of disabled behavior.

`Run-Probe.ps1` accepts optional `-AdapterJar` and `-OutputDirectory`. Defaults
are the current built production adapter and a new ignored directory under
`artifacts/WindowTitleProbe`. The xUnit entry point is
`WindowTitleAdapterTests`; it runs portable synthetic cases without installed
packs or a prebuilt adapter.

This is offline JVM and bytecode proof. It does **not** establish actual window
text during gameplay, visual acceptance, transient runtime title behavior, LAN
functionality, Steam overlay/discovery, or two-participant joins. Those remain
unverified when Minecraft is intentionally not run. Raw local reports stay
under ignored `artifacts` and must not be published.
