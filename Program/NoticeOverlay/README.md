# Fullscreen notification bridge

This is a launcher-owned Java agent, not a Minecraft mod. Its JAR is extracted
inside `Minecraft/Launcher/NoticeOverlay`, never into a pack's `mods` directory.
It runs independently of the identity adapter and requires no Minecraft names,
mappings, mod loader APIs, resource pack, or game-specific render event.

The agent instruments the Java wrappers for LWJGL 3
`org.lwjgl.glfw.GLFW.glfwSwapBuffers(long)` and LWJGL 2
`org.lwjgl.opengl.Display.swapBuffers()`. Immediately before presentation it
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
are 32 KiB, 160 title characters, 320 body characters, and three notices.
Messages expire after seven seconds. Malformed or incomplete replacements keep
the last valid snapshot until its own expiry.

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

The build checks the vendored ASM 9.8 hashes, compiles with `--release 8`, and
packages ASM only on the agent/system class path. Only the small hooks/render
JAR is appended to the bootstrap search path, avoiding interference with mod
loaders' ASM. The audit checks Java 8 class versions, package boundaries, real
transformation of both presentation signatures, rejection of native methods,
Unicode/expiry/bounds, and stable output on an unchanged build. Native OpenGL
smoke tests additionally exercise actual installed LWJGL libraries, fullscreen
buffer pixels, GL-state restoration, and unchanged foreground window.
