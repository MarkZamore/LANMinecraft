# Native portable host

`Publish.ps1` publishes framework-dependent application files, then `Build-PortableHost.ps1` wraps them in the outer `LANMinecraft.exe`. The EXE retains the full icon, version and DPI/asInvoker manifest. The .NET/WPF environment, background/game icons and Steam native DLL are separate immutable packages. See [desktop component delivery](../../tools/DESKTOP-COMPONENTS.md) for the dependency protocol, cache and acceptance procedure.

Before CLR, WinHTTP and a native progress/retry/cancel window prepare the pinned dependencies under `Minecraft/Launcher/Bootstrap`. Healthy cache reuse performs no bootstrap HTTP. The host prepares portable temp, loads private `host/fxr/<version>/hostfxr.dll`, and passes separate application and dotnet roots. `hostfxr_initialize_for_dotnet_command_line` and `hostfxr_run_app` preserve the same outer PID and STA thread. No installed .NET is required or used as a fallback.

Resource 101 is the uncompressed application `LMHOST01` packet. External packages use the same bounded header/table/data representation; resource 102 is the `LMDESK01` dependency descriptor. Integers are little endian. `LMHOST01` has a 72-byte header: magic, uint32 version/file count/entry byte length/reserved zero, uint64 table/data lengths, and SHA256 of table plus data. The UTF8 entry precedes file records (uint32 path byte length, UTF8 path, uint64 offset/length, 32-byte SHA). Limits remain 4096 files, 1024 UTF8 bytes per path and 512 MiB per packet. The lock independently pins each external packet and its header/table prefix.

SHA256 is an integrity check, not Authenticode. The existing update protocol verifies the outer EXE. Native preparation checks dependency URLs, sizes, packet/manifest/file hashes and safe paths before entering CLR. Per-component preparation locks and read leases protect files. Missing or corrupt ordinary files require verified download; an intact prepared set runs offline. Security errors do not weaken path checks or fall back to system/temp files. Publication of metadata uses exclusive write, flush/hash and a read guard before activation; interrupted metadata cannot pass its pin.

New ownership leases are siblings of generation directories: ASCII `LMDESK01\n<kind>\n<hash>\n`. Read handles stay open throughout managed execution. Startup cleanup preserves current/previous/staged sets and active leases. Old `Runtime`/`LMHOST01` generations are collected only after successful migration and after their old leases close.

Publishing needs PowerShell 7.2+, .NET SDK and Visual Studio x64 C++ tools with Windows SDK. JDK is needed for separate Java contract tests, not the EXE build. Compiler discovery uses `vswhere`/`VsDevCmd`; `/MT` avoids a separate Visual C++ redistributable. The desktop component release must exist before wrapping the EXE and activating update.json. Test-only loopback sources, font hooks and historical source overrides require an explicit build switch and are excluded from production.

This controls the launcher's own payload and temporary paths. It does not redirect Windows Known Folders, font/input/shell caches, Steam state or graphics-driver caches. Broad `APPDATA`/`USERPROFILE` replacement is deliberately avoided. Native self-extraction remains a shape that antivirus can inspect; the release workflow attempts to scan the actual final artifact without changing security settings. A successful scan or local I/O trace is evidence for that build and machine, not a universal guarantee.

## Antivirus publication gate

The ten pure output-parser regressions are reproducible without starting Defender or reading its configuration:

```powershell
pwsh -NoProfile -File Program/Bootstrap/Test-AntivirusCompletionParser.ps1
```

The helper extracts only the production parser's function AST. It checks exact target attribution, line endings, missing markers, warnings, extra output, detections and unknown locale/format; it never executes the scanner script's top-level code.

Run the same gate locally and in CI after `Publish.ps1` has produced the final outer EXE, before hashing release assets or uploading them:

```powershell
pwsh -NoProfile -File Program/Bootstrap/Test-PortableHostAntivirus.ps1 `
    -ExecutablePath artifacts/LANMinecraft.exe `
    -ArtifactsDirectory artifacts/portable-host-antivirus
```

The script requires active Microsoft Defender in Normal mode, current signatures and access to its Operational event log. [Microsoft documents administrative permissions for MpCmdRun](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus). The script records the current permission context and fails on unavailable evidence; it does not elevate, change settings, add exclusions or update signatures.

It runs the installed scanner with `-Scan -ScanType 3 -File <exact EXE> -DisableRemediation`. The file stays open without write/delete sharing, and SHA256/size must agree before and after scanning. Native exit zero is mandatory. Completion preferably uses matching [scan-start/completion events 1000 and 1001](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus) for the exact target and Scan ID, without cancellation/error.

Some platforms suppress those events for this custom-scan mode. The explicitly calibrated alternative accepts only three native output lines: `Scan starting...`, `Scan finished.`, then `Scanning <exact complete EXE path> found no threats.`, with empty stderr. This English output was observed from the installed scanner on a Russian Windows system using a normal, nonelevated token. It is not a general English/Russian substring search: unknown languages/formats, extra warnings and messages for another file remain Unknown and fail. Completion method and absent-event explanation are recorded in JSON. Timeout, nonzero exit, changed bytes, unavailable protection, correlated cancellation/error or a matching detection also fails.

Each run writes `scan.json`, complete raw stdout/stderr and decoded text into a unique artifacts subdirectory. Only target-matching scan/detection events enter its evidence; unrelated detections do not reject this artifact. `-DisableRemediation` suppresses custom-scan detection history/events, so empty history alone is never proof of a clean scan. It also leaves the file available for investigation if the custom scanner reports a detection; real-time protection remains enabled.

The result applies to the recorded EXE hash and current engine/signatures. If CI rebuilds different bytes, scan those bytes too. A passing scan does not guarantee that another machine or a later protection update will make the same decision.

## Exact CI candidate fallback

A regular push to `main` builds, tests and publishes the release on GitHub. If the GitHub-hosted runner reports unavailable Defender protection at preflight, before any scan is attempted, the workflow can publish after the same version, hash, delta and asset checks. The evidence remains **Unknown**, and the release notes explicitly say that Defender did not scan the executable. No local Defender or manual step is needed for that case. This exception applies only to the hosted main-push workflow and fresh evidence for the exact unchanged executable.

A detection, failed scan or ambiguous scan result still blocks automated publication. When antivirus evidence blocks publication, the workflow retains `portable-host-candidate-<run ID>-<attempt>` for diagnosis if the release assets and original scan record are available. This artifact contains only the release assets, original scan JSON and repository/commit/version/run metadata. The optional manual verifier below can handle eligible failures. Detections and nonzero native scan results cannot be promoted.

From the trusted checkout, verify the approved run and version:

```powershell
pwsh -NoProfile -File Program/Bootstrap/Publish-VerifiedCandidate.ps1 `
    -Repository MarkZamore/LANMinecraft -ExpectedCommit <40-character-commit> `
    -ExpectedRelease 444 -RunId <GitHub-run-ID> -RunAttempt 1
```

The default performs verification/scanning without changing a release. Add explicit `-Publish` when publication is authorized. Keep `main` at the approved commit until publication completes; a changed head or newer/conflicting latest release is rejected. Actions concurrency does not serialize a separate local publisher, so coordinate this publication window.

The gate verifies authenticated run/repository/main-push/workflow identity and successful test/build/delta/content/upload steps. It downloads the official immutable artifact ZIP and checks GitHub's SHA256 digest, bounded safe paths, metadata, outer PE version/commit, and every EXE/manifest/patch hash and size. Optional `-CandidateDirectory` compares an existing extracted download byte-for-byte with that official artifact. Earlier local QA bytes or antivirus evidence cannot substitute for CI bytes.

If the official ZIP has already been obtained, pass `-ArtifactArchivePath artifacts/agent-runs/<owned-task>/candidate.zip` to avoid another download. It must be an ordinary file inside this checkout. The same authenticated GitHub metadata, exact archive size and authoritative ZIP SHA256 are still required. The supplied archive is held read-only through verification/extraction and is never deleted by the helper; its owner retains cleanup responsibility.

Actual active-Defender scanning precedes the shared publisher. Verified release files stay locked during upload, `update.json` goes last, and proof/system logs are never public release assets. The gate runs no tests/builds and changes no protection settings or privileges.

Owned staging lives in `artifacts/agent-runs/verify-candidate-*` with `RUN.json`. ZIPs and failed/published payloads are removed in `finally`, retaining compact proof. Verified unpublished bytes remain for explicit review for at most 24 hours; the next verification for the same task removes previous completed payload staging before creating another copy.
