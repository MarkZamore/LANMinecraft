# Fullscreen notification bridge

This is a launcher-owned Java agent, not a Minecraft mod. Its JAR is extracted
inside `Minecraft/Launcher/NoticeOverlay`, never into a pack's `mods` directory.
It runs independently of the identity adapter and requires no Minecraft names,
mappings, mod loader APIs, resource pack, or game-specific render event.

The agent instruments the Java wrappers for LWJGL 3
`org.lwjgl.glfw.GLFW.glfwSwapBuffers(long)` and LWJGL 2
`org.lwjgl.opengl.Display.swapBuffers()` and SDL 3
`org.lwjgl.sdl.SDLVideo.SDL_GL_SwapWindow(long)` (the boolean-returning wrapper).
Immediately before presentation it
draws the notice into the game's own default back buffer. That makes the notice
part of the fullscreen frame rather than a desktop window competing with
exclusive presentation. The hook has no keyboard, mouse, focus, activation, or
window-positioning calls. The existing WPF notice remains available outside a
healthy foreground game session.

These presentation APIs are independent of Minecraft versions and Forge,
NeoForge, Fabric, Quilt, and vanilla. The class containing the presentation API
is passed into the bootstrap hook so reflective OpenGL lookups use the game's
actual class loader. LWJGL 2 and 3 can therefore run behind isolated loaders.
Java classes target Java 8. A future renderer that replaces LWJGL/OpenGL, for
example a Vulkan replacement, requires a corresponding presentation backend;
the agent reports readiness rather than claiming that an unhooked renderer is
working.

The OpenGL implementation supports the compatibility GL 2.1/GLSL 1.20 path and
the GL 3.2+/GLSL 1.50 core path. It captures and restores the program, VAO and
array buffer, touched texture/sampler bindings, pixel-unpack buffer and layout,
viewport, scissor, blending, depth/stencil/culling, polygon mode, color mask,
sample coverage/mask, clip distances/origin, sRGB, and draw-framebuffer state.
Indexed blend/color/scissor changes affect only output zero. ARB-only indexed
blending uses the ARB entry points instead of unavailable core function
pointers. An already bound custom game framebuffer is restored after the
default back buffer is drawn. Text is rasterized to an alpha texture only when
content, dimensions or DPI change. The no-notification path does no OpenGL work
after initialization. Desktop focus is never acquired.

## Session protocol

`-Dlanminecraft.notice.directory=<absolute session folder>` selects a unique
launcher session. Every file written by this component, including its extracted
bootstrap hooks, is inside that folder. The game launch also redirects Java
home, temporary files and Windows profile environment into the launcher folder.
The agent does not write registry keys or create an installed application
identity. It does not modify game JARs or pack files.

The launcher atomically replaces UTF-8 `notices.properties` with `count` (0–3)
and zero-based `<n>.title`, `<n>.body`, `<n>.createdUnixMs` fields. Reader limits
are 32 KiB, 160 title UTF-16 units, 320 body UTF-16 units, and three notices.
The producer truncates longer text without splitting a surrogate pair.
Messages expire after seven seconds. Malformed or incomplete replacements keep
the last valid snapshot until its own expiry.

`dpiScale` is the effective DPI of the verified foreground game window divided
by 96. Windows LWJGL 2 always reports a pixel scale of 1; the launcher supplies
the missing window scale and refreshes it every 500 ms while notices are active.
The agent reads it off the render thread and uses the greater of the native API
scale and this value, so GLFW scaling is not multiplied twice. Old snapshots and
invalid values use 1. A background or reused process cannot supply another DPI.

SDL reads its own current window/context, pixel dimensions and display scale.
It uses the same OpenGL state preservation and rendering code as GLFW, with no
Minecraft names or loader rules. Backend `sdl3` is accepted only after a fresh
successful presentation heartbeat. Near-future notice timestamps within two
seconds are retained until the render clock reaches them, so a clock tick of
cross-process skew cannot permanently discard a valid notice.

## Native client frontend contract

Native launches receive `LANMINECRAFT_NOTICE_DIRECTORY` for the same launcher-owned
session, styles, message queue, DPI and lifetime. They require launcher capability
`native-launcher-notices-v1`. Their existing engine frontend displays the queue,
without a separate content mod or replacement of game settings. It writes
`backend=native-ui-v1` and its actual rendering PID in the normal heartbeat.
The launcher verifies the root process start time and that the foreground renderer
belongs to that still-running launcher's native process tree. An unrelated PID,
reused root PID, stale heartbeat, unknown backend or rendering error keeps the
desktop fallback. Restart adoption retains this process-tree distinction.
Monitor choice follows the verified native child window rather than its hidden
preparation bootstrap. Ordinary Java sessions never accept a child's heartbeat.

Optional UTF-8 `style.properties` supplies `bgArgb`, `borderArgb`, `textArgb`,
`mutedArgb` (eight-digit ARGB hex), `fontFamily`, `fontSize`, `width`, `gap`,
`padding`, `radius`, `riseMs`, and `fadeMs`. The launcher derives these values
from its current design tokens. Defaults follow the launcher notice design.

A daemon thread reads snapshots every 250 ms and atomically writes
`heartbeat.properties` every 500 ms. Its `timestampUnixMs` is the time of the
last successful presentation hook; `pid`, `backend`, `renderedCount`,
`transformations`, and `error` identify the actual renderer. No file operations
occur on the game's render thread. A missing context or failed renderer does
not refresh the successful-frame heartbeat. Recoverable failures are retried
after five seconds and a successful frame clears the error.

## Build and verification

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
.\Program\NoticeOverlay\Test-NoticeOverlay.ps1
```

The build checks the vendored ASM 9.8 hashes and compiles runtime classes with
`--release 8`. Its ASM is relocated into `minecraft.portable.notice.internal.asm`,
separately from the identity agent and the loader's public ASM. Strict Fabric
classpath validation therefore sees only the loader's `org.objectweb.asm`.
The build-only Java 17 relocator is not packaged. Only the small hooks/render
JAR is appended to the bootstrap search path. The audit checks Java 8 class versions, package boundaries, real
transformation of all three presentation signatures, rejection of native methods,
Unicode/expiry/bounds, and stable output on an unchanged build. Native OpenGL
smoke tests additionally exercise actual installed LWJGL libraries, fullscreen
buffer pixels, GL-state restoration, and unchanged foreground window.
