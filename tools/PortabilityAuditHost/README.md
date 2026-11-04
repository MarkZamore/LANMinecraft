# Disposable launcher host

This test-only executable calls the real launcher APIs without running `App.Run`, Steam, Minecraft, reports or networking. It is not shipped with the launcher.

`--rendering-probe` runs the real WPF application constructor and five preloaded dialogs, then cleans them up. Its JSON retains the rendering-startup contract and adds process ID/path, application root, entry apartment, private runtime/WPF module locations, temporary paths and exact forwarded arguments. `--probe-hold-ms=<0..10000>` after this option holds only the fixture after writing JSON so tests can observe two overlapping managed entries and native file leases.

`--probe-storage` after the rendering option invokes the production atomic writer in portable temporary, personal, library, component-catalog and updater folders. It also invokes the real runtime-cache pruner with four disposable owned generations while the native host still holds its ancestry and active-runtime handles. The JSON reports actual replacement and deletion results.

The ordinary `install` command calls the production updater and reports its actual source/helper process IDs. A bounded `--probe-hold-ms` suffix keeps only this test caller alive long enough to observe the production helper waiting before replacement. A restarted fixture writes `audit-restarted.txt` beside its outer executable, preserving the native runtime's closed inventory.

`NativePortableHostTests` publishes a fresh ordinary self-contained copy of this project in `artifacts/native-host-tests/<guid>`, calls the production native packer and exercises the resulting executable. One payload and one executable are shared within the nonparallel test collection; the updater case wraps that same payload once more with a distinct outer version. Invalid packets are installed and restored through Windows resource-update APIs; production code has no test flags. All generated projects, compiler output, disposable installations and selected probe evidence stay below that artifact directory. After checking its own processes have exited and rejecting reparse descendants, fixture disposal removes only this new GUID's bulky build/payload/install folders. Small logs, JSON and build metadata remain; earlier fixture directories are never visited.

Build the launcher and test project normally, then run the focused cases with:

```powershell
dotnet test Program.Tests/Program.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~NativePortableHostTests
```

The fixture needs the same .NET SDK and Visual Studio x64 C++/Windows SDK tools as publishing. `RenderingStartupTests` also checks this host through the existing launcher runtime configuration. The native cases verify controlled paths and rejected attacks; they do not claim an operating-system-wide zero-write trace.
