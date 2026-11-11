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

For Forge/NeoForge the probe needs exactly one versioned `client-*-srg.jar`.
For Fabric it uses the vanilla version JAR from the shared runtime and the
Fabric profile ID from the pack descriptor. An isolated pack and instance can
be provided as the optional fourth and fifth arguments while using the shared
runtime read-only. The built DLL must match the source revision being diagnosed. A freshly
generated configuration does not itself prove that its bytecode checks pass:
run `PortableIdentityPreflight` with those properties, targets and support
sources against the actual adapter and game JARs. A cached title-only state is
not an equivalent check of the unavailable discovery hooks.
