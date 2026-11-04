# Native portable host

`Publish.ps1` publishes the ordinary self-contained WPF payload to an ignored artifacts directory, then `Build-PortableHost.ps1` wraps it in one outer `LANMinecraft.exe`. The SDK's unused apphost is omitted. The payload is uncompressed and contains its private `hostfxr`, `hostpolicy`, CLR, WPF and application files. The outer executable carries the launcher icon, release version and original DPI/asInvoker manifest.

Before loading any CLR code, the Win32 host validates its packet and the complete private runtime cache under `Minecraft/Launcher/Runtime/<payload SHA256>`. It prepares `Minecraft/Launcher/Temp`, sets process-only `TEMP` and `TMP`, and loads its own `hostfxr.dll` by absolute path. The supported `hostfxr_initialize_for_dotnet_command_line` and `hostfxr_run_app` APIs run the managed application in the same process and STA thread. `Environment.ProcessPath`, application root, updater PID/target, executable rename and taskbar identity continue to refer to the outer executable. No installed .NET runtime is required.

The cache packet is a raw RCDATA resource with ID 101. All integers are little endian. Its 72-byte header is: ASCII `LMHOST01`, uint32 version 1, uint32 file count, uint32 entry-path byte length, uint32 reserved zero, uint64 table byte length, uint64 data byte length, SHA256 of table plus data. The table starts with the UTF8 entry DLL path, then one record per file: uint32 path byte length, UTF8 relative path, uint64 data offset, uint64 file length, 32-byte SHA256. Raw file data follows the table. Limits are 4096 files, 1024 UTF8 bytes per path and 512 MiB for the complete packet.

The SHA256 table is an integrity check, not an Authenticode signature. Normal update manifest hashes still verify the final downloaded outer executable against its HTTPS manifest in the existing release protocol. Publisher records and private compiler output stay under artifacts. Production cache validation rejects unsafe paths, reparse points, unlisted files and corrupt payloads before loading runtime DLLs. A preparation lock and a shared generation lease protect extraction; a damaged ordinary cache file is securely removed and recreated from verified embedded bytes. The host loads nothing until the entire generation has passed verification, so interrupted partial files are repaired at the next start. Runtime failures do not fall back to an external temporary directory.

Generation ownership is recorded beside the directory in `Runtime/<SHA256>.lease`: exactly 74 ASCII bytes, `LMHOST01\n<SHA256>\n`, without a BOM. A read handle allowing only other readers remains open throughout managed execution. Cleanup can exclusively acquire an inactive generation's lease before inspecting and removing its files; active and unknown generations are preserved.

Publishing needs PowerShell 7.2 or newer, the existing .NET SDK/JDK and Visual Studio x64 C++ tools with a Windows SDK. Compiler discovery uses `vswhere`; `VsDevCmd` supplies the matching compiler/SDK environment. The host uses `/MT`, so users do not need a separate Visual C++ redistributable. CI must verify this prerequisite before publishing. Hashes, deltas and release assets are generated after wrapping the final EXE.

This controls the launcher's own payload and temporary paths. It does not redirect Windows Known Folders, font/input/shell caches, Steam state or graphics-driver caches. Broad `APPDATA`/`USERPROFILE` replacement is deliberately avoided. Native self-extraction remains a shape that antivirus can inspect; scan the actual final artifact without changing security settings before publication. A successful scan or local I/O trace is evidence for that build and machine, not a universal guarantee.

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

Unavailable hosted Defender remains **Unknown**. CI creates/verifies deltas, retains `portable-host-candidate-<run ID>-<attempt>` as an Actions artifact, then fails with `latest` unchanged. The artifact contains only release assets, original scan JSON and repository/commit/version/run metadata. Detections and nonzero native scan results cannot be locally promoted.

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
