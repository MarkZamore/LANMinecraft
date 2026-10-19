# Process Monitor audit

The observer is adapted from `Autonomy&Performance/Tests/TracePortability.ps1` and
`AnalyzePortability.ps1`. It elevates **only the observer**. Start the Release EXE
or the functional harness separately from the normal user session after `ready.json`.
Existing Process Monitor instances are never interrupted.

All private PML, CSV, command lines, local reports and disposable installations belong
under the ignored `Artifacts` directory. Keep each capture outside its installation.
Use the same scenario matrix and initial installation state for baseline and recheck.
Build before starting the capture; do not include the build in an application audit.

```powershell
$session = Join-Path $PWD 'Program.Tests\PortabilityAudit\Artifacts\baseline-window-capture'
.\Program.Tests\PortabilityAudit\TracePortability.ps1 `
  -ProcmonPath 'C:\path\Procmon64.exe' -ConfigPath 'C:\path\all-events.pmc' `
  -ApplicationPath 'C:\isolated-install\LANMinecraft.exe' -Phase baseline `
  -SessionDirectory $session -RuntimeSeconds 90
$app = Start-Process 'C:\isolated-install\LANMinecraft.exe' -PassThru
.\Program.Tests\PortabilityAudit\AuditSession.ps1 Register $session -ProcessId $app.Id
.\Program.Tests\PortabilityAudit\AuditSession.ps1 Mark $session -Scenario first-start -Status begin
# Exercise the scenario, then record pass/fail/skip with a concrete reason.
.\Program.Tests\PortabilityAudit\AuditSession.ps1 Mark $session -Scenario first-start -Status pass
.\Program.Tests\PortabilityAudit\AuditSession.ps1 Stop $session
# Wait for finished.json; observer-error.txt indicates a failed capture/conversion.
.\Program.Tests\PortabilityAudit\AnalyzePortability.ps1 -SessionDirectory $session
```

`application.json` records the actual executable hash, command line, parent PID and
start time of every registered root. Register each additional launcher instance or
test host. Descendants are reconstructed from Process Start/Create/Exit; unrelated
processes reusing an exited PID are excluded. Newly launched roots must start after
capture readiness. An already running root can be registered while alive; its
verified start time must precede capture start, and the report explicitly excludes
that root's startup and prior Steam activity. `summary.json` lists missing root
starts, pre-existing roots, actual first/last CSV event times and scenario markers.

Completed `capture.csv`, PML segments and their original hashes are immutable.
`-ConvertOnly` is recovery for incomplete conversion; it refuses completed captures.
`Summarize-Portability.ps1 -RefreshAnalysis` verifies original hashes before rebuilding
derived reports. Per-GUI-root metrics keep a cold launch, warm launch and subsequent
interactive run distinct when they share a bounded capture.

The read-only compiled CSV prefilter supports quoted commas, escaped quotes and
multiline fields, and reduces processing of unrelated system rows. The analyzer
requires PowerShell 7; the elevated observer remains compatible with Windows
PowerShell 5.1. Process Start details can contain the complete process environment:
keep these private and print only selected PID/time fields when sharing results.

Confirmed `CreateFile` creations require `OpenResult: Created/Overwritten/Superseded`.
Opening an existing file, requesting write access, or opening an existing registry
key is not classified as a mutation. Registry handle tags, named pipe IPC and NTFS
metadata are reported separately. System services outside the process tree require
separate attribution and must not be represented as launcher writes.

The PMC must disable destructive filters and the ring buffer. `/NoFilter` plus a
backing PML preserves unfiltered events. Successful conversion and file hashes are
recorded, but **do not prove zero kernel event loss**. Record actual Procmon dropped
event statistics using `AuditSession.ps1 Statistics -DroppedEvents 0 -Detail ...`
with the screenshot/export evidence path. Without that evidence, report the count
as unknown. Never invent a zero. Captures stop after the bounded runtime even if
the caller exits. A positive dropped-event count requires a shorter repeat capture.

Run `Verify-Analyzer.ps1` to check false-positive classification and PID reuse using
synthetic fixtures. Raw traces contain private system activity and must stay local.

After verified analysis, `Classify-RemainingWrites.ps1 -SessionDirectory ...` creates
`classified-remaining-writes.csv`: process/parent IDs and lifetimes, exact paths,
operations, purpose, retention and confidence. It contains no environment or command
lines. Unproven Windows Shell causes remain explicitly unconfirmed; functional fixture
paths are not automatically treated as necessary production data.

`Summarize-Portability.ps1` produces the readable `AUDIT-RESULTS.md` and a small
`audit-public-summary.json` without raw process details. The full `audit-index.json`
contains private process environment from original traces and must remain local.
Optional `audit-status.json` and `audit-findings.json` add reviewed scenario/build
boundaries and findings; they must never describe an unfinished native/game case as passed.
