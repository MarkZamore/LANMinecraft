# Native rendering diagnostic

Run after building `tools/UiPreloadProbe`:

```powershell
dotnet run --project tools/NativeRenderSample -- . artifacts/memory-dialog-438/native-render

# Re-resolve the existing sample using only its local symbol cache; no new fixture.
dotnet run --project tools/NativeRenderSample -- --resolve . artifacts/memory-dialog-438/native-render
```

This Windows x64 helper launches only the checkout's `UiPreloadProbe.exe`, verifies
its actual executable path, and samples only threads belonging to that child.
The fixture uses synthetic overview cards, SoftwareOnly, the existing full-present
switch and themed transparent client. It does not initialize Steam, send reports,
or launch Minecraft. There is no arbitrary PID/executable option.

After four seconds of warmup it ranks threads by `GetThreadTimes`, then briefly
suspends up to three recently active threads every 10 ms for 18 seconds, reads the
AMD64 instruction pointer, and resumes in `finally`. No module enumeration,
symbol lookup or network operation happens while a thread is suspended. A child
that exceeds 50 seconds is terminated; no existing application is stopped.

These are wall-time instruction snapshots of CPU-active threads, including
possible waits. They identify native rendering functions; they are neither ETW
CPU samples nor valid FPS acceptance data. Sampling perturbs the fixture. Run
separately from performance comparisons and other tests/builds.

Only after the child has exited does DbgHelp resolve WPF raster symbols from
Microsoft's server; other modules retain module-level names. Its explicit symbol
cache, raw snapshots, thread CPU totals, fixture
output and aggregate function counts remain in the requested `artifacts` folder.
The Windows SDK x64 debugger DLLs are used when installed; they are not copied
or committed. No administrator rights, system trace or registry change is used.

API references: [GetThreadContext](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadcontext),
[GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes),
[SymFromAddr](https://learn.microsoft.com/en-us/windows/win32/api/dbghelp/nf-dbghelp-symfromaddr).
