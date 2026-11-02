# Catalog startup acceptance probe

This console measures the production staged catalog API against an existing
isolated fixture. It does not launch the application, Minecraft, Steam or a
world. HTTP responses come only from the fixture's saved index, manifests and
library bundles; no request reaches the internet.

The probe measures the constructor plus `Sources`/`GetDisplayName`, index
readiness, the selected pack's checked game plan, and complete background
presentation preparation. It temporarily gates unrelated manifest responses
and records that the selected pack remains ready before full library readiness.
With a single-source fixture that gate cannot be exercised; the report says so.

## Input and output

`--fixture-root` must name a directory below this checkout's `artifacts`, with:

```text
Minecraft/
  Launcher/Library/catalog.json
  Launcher/Library/<source-key SHA256>.json
  Packs/<pack relativePath>/<directories and zero-length replica files>
```

Supply a previously prepared, independently copied cache fixture. Do not point
the probe at an installed launcher or move personal data into the fixture.
The pack tree contains only directory names and zero-length files representing
manifest paths. A missing tree measures checks against absent paths; an existing
empty-file replica is needed to represent filesystem/reparse-check overhead.
The console never reads pack-file contents and rejects nonempty replica files.
It never reads settings or worlds. Source-marker/config contents are not part
of this fixture; use the staged catalog tests for custom-source/offline cases.

The input is read-only. Each invocation creates a unique run directory below
`artifacts/CatalogStartupProbe/runs`, copies top-level cache JSON, and reconstructs
the empty-file tree. All production cache writes and logs go into that run.
No files or previous results are deleted or moved. Reparse points are rejected.
Input limits are 256 MiB of cache JSON, 1,000 cache files and 200,000 empty files
or directories. The per-file limits match the index and library cache bounds.

Relative CLI paths are resolved against the repository root. `--output-root`
may select a different directory below `artifacts`; the unique child is always
created automatically. An output root inside the input fixture is rejected.
`report.json` contains timings, assembly SHA256, selected
manifest path counts, initial/selected/gated/final readiness, request counts,
warning count and initial list labels. `probe.log` contains fixture diagnostics.
Reports and copied caches are local evidence and should not be committed.

## Build and run

Use .NET 10 on Windows. Run builds sequentially with launcher checks. The default
project reference skips Java adapter construction. Tool build outputs also stay
under `artifacts/CatalogStartupProbe`.

```powershell
dotnet build .\tools\CatalogStartupProbe -c Debug
& .\artifacts\CatalogStartupProbe\bin\Debug\net10.0-windows\win-x64\CatalogStartupProbe.exe `
    --fixture-root .\artifacts\catalog-fixture `
    --pack '<published relativePath>'
```

To use an already built managed launcher DLL without rebuilding `Program`:

```powershell
dotnet build .\tools\CatalogStartupProbe -c Debug `
    '-p:LauncherAssemblyPath=<absolute path to LANMinecraft.dll>'
```

Use a normal managed build output with its dependencies beside the DLL, rather
than a single-file EXE. The explicit-DLL build copies those dependencies into the
tool output. The report records the DLL actually loaded.

Each invocation is one fresh process. After building once, collect five samples:

```powershell
1..5 | ForEach-Object {
    & .\artifacts\CatalogStartupProbe\bin\Debug\net10.0-windows\win-x64\CatalogStartupProbe.exe `
        --fixture-root .\artifacts\catalog-fixture `
        --pack '<published relativePath>'
    if ($LASTEXITCODE -ne 0) { throw "Catalog startup probe failed: $LASTEXITCODE" }
}
```

The default selected path is the launcher's default pack; `--pack` should be
explicit for repeatable comparisons. Include the heaviest active manifest by
current plus legacy path count when it differs from the normal selection.
`--list-goal-ms` defaults to 250; `--timeout-seconds` defaults to 180.

Exit code 0 means the list goal and readiness/frozen-manifest assertions passed;
1 means a completed measurement failed them; 2 means invalid input or an operation
error. Warnings are reported separately and need review. The fixture-copy time
is excluded. Compare median and range from fresh processes on the same fixture,
with other builds stopped. OS caches are not flushed, so label results as fresh
CLR with warm OS cache. These timings exclude internet latency and do not prove
Minecraft runtime compatibility, installed JAR integrity or world safety.
