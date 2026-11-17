# Native desktop startup comparison

This technical host reflects into a specified, unchanged `LANMinecraft.dll`. WPF queues `OnStartup` from its `Application` constructor even without `Application.Run`. Before constructing the real `Minecraft.App`, the probe hooks its own dispatcher and aborts exactly the Send-priority operation whose delegate belongs to `System.Windows.Application`'s constructor. The known callback is identified through .NET10 WPF's internal `DispatcherOperation._method` metadata; an unknown layout fails closed before pumping. Results must confirm exactly one operation was aborted, remains `Aborted` after the first frame, only one window exists, and owned personal settings/log files remain byte-identical. The real App type, constructor and `App.xaml` initialization still execute. The host constructs the real `MainWindow` and removes `Window_Loaded` and `Window_Closing` before showing it. Therefore the measured runs do not acquire/signal the global single-instance guard, start Steam/network/background services, load/save settings, or stop the user's existing launcher. Earlier exploratory runs without constructor-operation suppression are invalid and must be excluded from timing results.

The window has `ShowInTaskbar=false`, `ShowActivated=false`, native `WS_EX_NOACTIVATE`, and descendant keyboard-focus requests suppressed. Its native source is created while still hidden, then placed offscreen after the launcher's placement callback. Each run records any activation/foreground transition and rejects a foreground probe window. Measurements cover native host process creation to WPF `ContentRendered`, offline library seed preparation, programmatic routed nickname input/edit, real library navigation and window resize. The new launcher's actual `DesktopBootstrapCache.CommitSuccessful` runs after the first frame and must pass `HasSuccessfulSet`; its cost is recorded separately. UI timings span an Input-priority dispatcher callback, actual control/layout work and a ContextIdle sentinel after the WPF render queue. They do not measure physical keyboard, compositor/display scanout, full service startup or Steam readiness.

Publish the probe independently of the launcher. Both variants pin .NET/Windows Desktop **10.0.14** and use ReadyToRun; only self-contained delivery differs:

```powershell
.\tools\Measure-DesktopStartup.ps1 -PublishProbe -SelfContained -OutputDirectory artifacts/agent-runs/desktop-startup-perf/baseline-payload
.\tools\Measure-DesktopStartup.ps1 -PublishProbe -OutputDirectory artifacts/agent-runs/desktop-startup-perf/candidate-payload
```

Add each original `LANMinecraft.dll`, its original `.deps.json` and non-framework managed dependencies to its corresponding payload. Keep the baseline's original embedded resources. Do not overwrite the probe's runtimeconfig/deps with the application's config. Package the baseline with its original native host from a detached source worktree at the baseline commit; package the candidate with the new native host. Both native packers must select `DesktopStartupProbe.dll` as entry assembly. The candidate must receive a valid normal native bootstrap component context; the probe never fabricates or bypasses it. Native icon resources must be present as in a real release. Record input binary SHA256 and source commits alongside the results.

All payloads, native executables and outputs belong under `artifacts/agent-runs/desktop-startup-perf`, coordinated as a single **2 GiB** comparison stand with one baseline and one candidate. The helper creates `RUN.json` and checks the global workspace budget before and after work. It refuses to launch an executable outside that stand, or without a matching `host-build.json` proving its SHA256 and `DesktopStartupProbe.dll` entry assembly. Keep native build reports inside the stand after deleting their intermediate packets. Source worktrees remain in `LocalSources`, never in the disposable stand. Remove reproducible payloads/caches after keeping the compact measurement summary and provenance.

```powershell
.\tools\Measure-DesktopStartup.ps1 `
  -BaselineExecutable artifacts/agent-runs/desktop-startup-perf/baseline/LANMinecraft.exe `
  -CandidateExecutable artifacts/agent-runs/desktop-startup-perf/candidate/LANMinecraft.exe `
  -RunDirectory artifacts/agent-runs/desktop-startup-perf/results `
  -FirstLaunchState Ready -WarmRuns 20 -FailOnRegression
```

Without application overrides the probe loads `LANMinecraft.dll` beside its extracted entry assembly. Use `-BaselineApplication`/`-CandidateApplication` only for explicit absolute paths to the actual corresponding application DLLs.

The first observed launch is reported separately. `-FirstLaunchState Cold` means the caller has prepared an empty **owned native bootstrap/application cache**, not a cold Windows filesystem cache; the script never flushes OS caches. Use a separate result subdirectory and `-FirstLaunchOnly` for a cold extraction/download scenario, then measure a ready component/app cache independently. Do not run these measurements alongside test suites/builds or other CPU-intensive work. Warm pairs alternate baseline/candidate then candidate/baseline to balance ordering. Reports contain 20 runs per variant by default, eight UI samples per process, medians, nearest-rank p95 and regression thresholds `max(100 ms,5%)`/`max(200 ms,10%)`.
