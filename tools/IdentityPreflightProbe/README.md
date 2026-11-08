# Read-only identity mapping probe

This console tool invokes the production mapping builder from an already built
launcher DLL. It prints the fresh properties, native targets and support-class
sources, plus the full-identity refusal which would otherwise be hidden behind
the ordinary skins-only fallback. It does not start Minecraft, install anything,
edit adapter state or modify player data. Its JSON contains local paths and is
private diagnostic output, not a public receipt.

From the repository root, with .NET 10 and the Windows desktop runtime:

```powershell
dotnet run --project tools/IdentityPreflightProbe -- <path-to-LANMinecraft.dll> <application-root> <pack-directory-name>
```

The current probe describes Forge/NeoForge layouts with exactly one versioned
`client-*-srg.jar`. Other runtime layouts are intentionally refused rather than
guessed. The built DLL must match the source revision being diagnosed. A freshly
generated configuration does not itself prove that its bytecode checks pass:
run `PortableIdentityPreflight` with those properties, targets and support
sources against the actual adapter and game JARs. A cached title-only state is
not an equivalent check of the unavailable discovery hooks.
