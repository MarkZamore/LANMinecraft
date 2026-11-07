# First native game window

This standalone probe does not launch Minecraft, Steam or personal worlds.
It creates one small native LWJGL window, checks its caption and both Windows
icon handles immediately after creation, changes the requested title and checks
again. The modern case also checks that an intentionally hidden window stays
hidden and an initially unfocused window does not take focus or change future
focus-on-show behavior. Synthetic JVM tests separately assert that the icon and title are set
before the first ShowWindow, and cover failed creation, icon failure and opt-out.

Build the launcher normally first with an actual JDK. Inputs come from a prepared
Minecraft library cache; the tool does not download or alter them. It records
SHA256 before and after, bounds child processes to 45 seconds and removes its
classes, native DLL copies and isolated Java profile in finally. Small receipts
and logs remain under artifacts/agent-runs/initial-window-probe/<version>.

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
dotnet build Program -c Debug
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<Minecraft/Launcher/Shared/libraries>' -Version 3.4.1
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<libraries>' -Version 3.3.3
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<libraries>' -Version 3.3.1
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<libraries>' -Version 3.2.2
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<libraries>' -Version '2.9.4+legacyfabric.17' -JavaExecutable '<Java 17/bin/java.exe>' -JavaMajorVersion 17
.\tools\InitialWindowProbe\Run-Probe.ps1 -LibraryRoot '<libraries>' -Version 2.9.0 -JavaExecutable '<Java 8/bin/java.exe>' -JavaMajorVersion 8
```

The probe uses the launcher's bundled game icon archive by default. Pass
`-IconArchive <prepared icons.jar or client.jar>` to check another verified
runtime's own icons. LWJGL 3.4.1's optional unsafe classifier is recognized.

Windows maintains pinned shortcuts and taskbar grouping separately; these checks
observe the game's own title and small/large HICONs. They are not a gameplay or
GPU-driver acceptance test. Native Rust clients create their title/icon through
their own winit attributes before creating a window; they do not load this Java agent.
GLFW ignores the visibility hint for exclusive fullscreen windows: their title
is supplied at creation and icons are installed immediately on return, before
the first game frame. The launcher does not change their monitor, mode or refresh rate.
