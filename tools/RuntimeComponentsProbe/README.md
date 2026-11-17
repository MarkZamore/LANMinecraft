# Native component delivery acceptance

This opt-in probe uses the real Minecraft 26.3 Fabric runtime and the installed Better Vanilla author package. It creates an independent mutable game directory and a new `RuntimeComponentsQa` world inside one marked `artifacts/agent-runs` fixture. Personal instance files, settings and worlds are outside its inputs.

The existing installation supplies shared verified Java, Minecraft, libraries and asset caches. Ordinary component preparation writes the shared component cache and prepared selection through production APIs. The copied author package is byte-checked before and after acceptance. The game uses an offline technical player identity.

The mutable game is beneath this fixture's own `Minecraft/Personal/Instances` tree. When the selected Steam transport JAR already exists in the installation's managed cache, the probe verifies the selected catalog hash and copies it independently into the fixture before the normal managed-component check. It never links mutable files to the original package.

The first run prepares components online, starts the native title menu, creates a fresh Creative world with commands, opens the real pause menu, checks hidden World Options, invokes the native Open to LAN action and verifies native reporting after publication. The second run repeats preparation with all launcher HTTP requests rejected, then opens the saved disposable world and repeats the native checks. The host network configuration is unchanged, and this verifies no real Steam guest connection.

The observer performs these actions programmatically through native Java APIs. If this reviewed runtime presents its experimental-world backup confirmation while reopening the exact owned `RuntimeComponentsQa` world, it invokes that screen's native proceed callback. This does not change the pack's warning policy or touch any personal world.

Create the fixture and its ownership `RUN.json` after checking `python tools/workspace_storage.py --check`. Reserve up to 1 GiB for the author copy and generated game files. Build after other launcher builds have finished.

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
tools/RuntimeComponentsProbe/Build-Agent.ps1 -OutputDirectory artifacts/agent-runs/runtime-components-acceptance/agent
dotnet build tools/RuntimeComponentsProbe -p:BuildProjectReferences=false --disable-build-servers -m:1 -nr:false
dotnet tools/RuntimeComponentsProbe/bin/Debug/net10.0-windows/win-x64/RuntimeComponentsProbe.dll `
  . artifacts/agent-runs/runtime-components-acceptance `
  artifacts/agent-runs/runtime-components-acceptance/agent/runtime-components-qa-agent.jar
```

`verification.json`, per-run result JSON and compact native events are the evidence. The Java observer uses reviewed native APIs and performs no class transformation. It stops only its own Minecraft process. A failed or timed-out observer produces a failure report, and the harness cleans its own game copy in `finally`. Console logs are capped at 32 MiB per run. Retain compact evidence after triage and remove raw logs, Java argument files and the compiled observer when the task finishes.

An optional pack name and evidence ID follow the three required arguments. For example, `"Better Vanilla" final` writes `final-online`, `final-offline` and `verification-final.json` in the same fixture. Existing evidence is never overwritten. Remove the previous recoverable game copy before another run; keep only one current game tree and record any retained diagnostic evidence in `RUN.json`.
