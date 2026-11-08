# Native runtime interoperability

This launcher-owned Java agent contains only general Windows/JVM interoperability.
It does not depend on Minecraft names, a mod loader, pack identity, Steam, the
identity adapter, or notification rendering. It adds no game mod and changes no
game/native JAR or DLL, authored settings, or global Java encoding.

## Legacy OpenAL Unicode paths

LWJGL 2 converts its OpenAL DLL path to bytes using Java's default charset and
passes those bytes to the ANSI Windows loader. With the launcher's UTF-8 charset,
non-ASCII paths can therefore fail on a Windows code page such as 1251. The
[upstream Windows loader](https://github.com/Legacy-Fabric/lwjgl/blob/a2e2ecbebd9102e7d4d178ac405b75bf62bc6fa9/lwjgl-platform/src/windows/c/windows/windows_al.c#L51)
and its `GetStringNativeChars` conversion preserve this old ABI mismatch.

For a prepared Windows LWJGL 2 runtime, the launcher supplies the exact extracted
OpenAL DLL and its SHA256. Selection follows the actual CmlLib native directory,
client/platform rules and extraction order; the extracted DLL must match the
entry in the last eligible, verified native JAR. Before the game or its loader initializes, the agent
checks the path and hash, then calls Java `System.load` on that absolute Unicode
path. The Windows module remains loaded for the system class loader's lifetime;
LWJGL's existing ASCII-basename fallback can subsequently find it, including
when LWJGL belongs to a separate loader. The agent itself never loads LWJGL.
UTF-8 remains in effect for game content, configuration and logs.

The contract is:

```text
-javaagent:<launcher-native-interop-agent.jar>
-Dlanminecraft.native.openal=<absolute OpenAL-amd64.dll or OpenAL32.dll>
-Dlanminecraft.native.openal.sha256=<64 hex characters>
```

The launcher selects and validates the current runtime files before invoking this
contract; this is not a search of arbitrary DLLs, a native replacement, or an
allowlist of pack names. A hash mismatch, symbolic link, missing file or loader
failure is logged as `[LauncherNative] OpenAL preload failed` and does not abort
the JVM. No preload is attempted without the explicit path or on another OS.
This workaround addresses DLL loading. Audio device names and other native text
decoded with an incorrect charset are a separate issue.

The agent has no file writes or temporary extraction, registry entries, services,
native bindings, or third-party Java dependencies. Its extracted JAR belongs in
the launcher's controlled runtime cache. Windows and driver-owned caches remain
outside this component's control.

This remains a launcher runtime correction because there is no pack setting for
the old native ANSI loader. Moving it into a pack would require replacing the
upstream binary or adding a content-specific bootstrap; changing the default
charset would affect unrelated text. Neither is needed for the general fix.

## Bounded runtime validation

See [NativeInteropProbe](../../tools/NativeInteropProbe/README.md) for the
reproducible comparison using an installed profile, unchanged native bytes and
the production bootstrap. It exercises real OpenAL with a silent PCM source and
the actual Knot class loader, without invoking Minecraft's main or using a world.
The mandatory release tests cover archive integrity, argument boundaries and
negative bootstrap cases; they do not replace checking audible output on a
player's audio device.

## Build

Use the actual JDK 25.0.2 specified by the repository's build instructions:

```powershell
.\Program\NativeInterop\Build-NativeInterop.ps1 -OutputPath .\Program\Build\NativeInterop\launcher-native-interop-agent.jar
```

Runtime classes target Java 8. The build records its input hashes, preserves an
unchanged JAR, uses a fixed archive timestamp, and removes its stage in `finally`.
Build temporary/profile paths stay inside `Program/Build/NativeInterop`.
