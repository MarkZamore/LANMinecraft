# Shared Forge runtime QA

This opt-in tool uses the real launcher preparation APIs and native Minecraft 1.20.1 Forge lifecycle. It accepts any declared pack path and loader patch; it has no catalog whitelist. Other Minecraft/loader versions need their own reviewed native acceptance tooling. Universal launcher adapter/loader tests remain in `Program.Tests`.

Pack identity, fixture choices and historical cleanup declarations live in the pack repository's `tools/qa/launcher-runtime-contract.json`. New fixtures require `--contract`, freeze its bytes and bind SHA256. Later commands use that frozen copy; a different contract is rejected. Distributed pack settings are unchanged.

A local custom pack needs neither a GitHub repository nor a catalog ID. A neutral contract for an already declared Forge 1.20.1 source is:

```json
{
  "schemaVersion": 1,
  "pack": { "relativePath": "My custom pack", "source": null },
  "runtime": { "minecraftVersion": "1.20.1", "loader": { "type": "forge", "version": "47.4.0" } },
  "qa": { "heapMiB": 6144, "playerName": "LocalQa", "worldPrefix": "LocalQa" },
  "retiredHelpers": []
}
```

`pack.id` is an optional local QA label. `source` and `retiredHelpers` may also be omitted. The source descriptor must match the declared runtime. The tool copies source files independently, records SHA256 and never copies personal worlds or edits source content. These metadata checks cannot establish arbitrary mod compatibility: actual client/world and relevant mod behavior need native evidence. A manual pack without a reference manifest has no external expected file hashes.

Build only after other launcher builds have finished:

```powershell
dotnet build tools/ForgeRuntimeQa -p:BuildProjectReferences=false
tools/ForgeRuntimeQa/Build-Agent.ps1 -OutputDirectory artifacts/agent-runs/my-pack/agent
$qa = 'tools/ForgeRuntimeQa/bin/Debug/net10.0-windows/win-x64/ForgeRuntimeQa.dll'
dotnet $qa prepare artifacts/agent-runs/my-pack/fixture --source <pack-source> --contract <pack-source>/tools/qa/launcher-runtime-contract.json --mode baseline
dotnet $qa launch artifacts/agent-runs/my-pack/fixture --label baseline-01 --agent artifacts/agent-runs/my-pack/agent/forge-runtime-qa-agent.jar --fresh-world LocalQaFresh
```

`--cache-root` optionally copies verified runtime caches. `-p:LauncherProject=<absolute-project>` selects an isolated launcher checkout. Keep one current fixture, check the shared storage budget before preparation and retain `RUN.json` ownership/deadline. Clean owned bulky fixtures after their evidence chain completes; retain compact reports. The size estimate is an allowance, not permission to exceed the global budget.

The native Java agent makes no class transformations and performs no game reflection before explicit `ready` after the title menu is observed. `ready`, `audit` and `stop` take the fixture path and `--label`. Only the contract's reserved world prefix is allowed, and an existing world cannot be overwritten. Reentry uses `--world <reserved-world>` and a new evidence label.

`baseline` runs copied author content; `integrated` uses normal instance, managed Steam component and identity preparation. This offline technical identity establishes neither a real Steam guest connection nor two-player PASS. Selected-skin HTTP checks remain opt-in; private inputs stay unchanged. The log comparer preserves error IDs and does not label every ERROR inherited. AI provisioning follows the pack recipe; Java owns backend lifecycle. Pack-owned acceptance notes decide which observations apply. This refactor claims no new runtime PASS.
