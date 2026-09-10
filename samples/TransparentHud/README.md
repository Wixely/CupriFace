# Transparent HUD

Run the normal GPU path:

```powershell
dotnet run --project samples/TransparentHud
```

On Windows, if the transparent margins appear black, select the software per-pixel
alpha path:

```powershell
dotnet run --project samples/TransparentHud -- --software
```

Add `--no-topmost` to test ordinary window ordering. Applications can select the same
fallback with `DesktopHost.Run(app, preferSoftware: true)`. The existing
`CUPRIFACE_SOFTWARE=1` override also selects it.

To retain GPU drawing while bypassing the failing window swap chain:

```powershell
dotnet run --project samples/TransparentHud -- --layered-gpu
```

Applications select this with `DesktopHost.RunWithLayeredGpu(app)`. An invisible
OpenGL context backs a Skia GPU surface. Changed frames are read back to a retained
BGRA bitmap and presented with Windows alpha; expose-only frames reuse that bitmap,
and idle frames do no rendering/readback. The startup log names the GL renderer.
This is GPU drawing **with CPU readback**, not zero-copy GPU composition. It has a
copy/synchronization cost and is not promised to be faster for a small static UI.
No global driver settings are changed.

The layered mode requires Windows, `Transparent=true`, `Frameless=true`, and usable OpenGL.
On other platforms or with other window flags, this entry point reports a diagnostic
and uses the normal desktop renderer instead; the application call site stays portable.
GL failures are reported rather than silently claiming GPU operation. The explicit
`CUPRIFACE_SOFTWARE=1` override selects the CPU path. GPU surface producers use the
same context and draw contract as the normal GL host. `ThreadedRender` is bypassed
in this mode: the GPU context and drawing stay on the UI thread.

The Windows software path supports desktop alpha for **transparent, frameless**
windows. It uses a premultiplied BGRA bitmap and `UpdateLayeredWindow`, preserving
partial alpha and transparent corners. It registers an SDL window class without
`CS_OWNDC`/`CS_CLASSDC`, and never creates an OpenGL swap chain or SDL renderer for
that window. Opaque windows retain the normal SDL renderer.

These are explicit alternatives for issue #139, not an automatic detection of broken
GPU composition. A successful GL initialization or transparent framebuffer does
not prove correct composition by DWM. The default GPU path is unchanged. Software
mode has CPU rasterization/full-window presentation costs, and GPU-only surface
producers need a software implementation. Transparent decorated windows and other
platforms' software transparency are not supported; a diagnostic reports that
limitation. Layered resize presentation waits for SDL's normal event pump, avoiding
reentrant native geometry changes during the resize callback.

## Compositor acceptance test

From the repository root, in an unlocked Windows desktop:

```powershell
dotnet build samples/TransparentHud -c Release
powershell -File tools/Test-WindowsAlpha.ps1 -Exe samples/TransparentHud/bin/Release/net10.0/TransparentHud.exe
powershell -File tools/Test-WindowsAlpha.ps1 -Exe samples/TransparentHud/bin/Release/net10.0/TransparentHud.exe -LayeredGpu
```

The script places the sample over a solid synthetic background. It compares visible
and hidden captures, asserts all four transparent corners match, checks that the
translucent card blends with the background, and rejects pure-black pixels. It
covers topmost/non-topmost startup, demotion, movement, resize, promotion, and
minimize/restore. No screenshots are saved. It closes each spawned sample normally.
Use `-GlBaseline` to report the same measurements for the default GPU path without
asserting alpha success. Leave `CUPRIFACE_SOFTWARE` unset for that baseline.

Verified on 2026-09-10: Windows 11 build 28000, 150% scale. The GPU baseline had
37.39% black pixels; software alpha had 0%, matching corners and correct fractional
blending in all eight lifecycle checks. Off-screen GPU rendering on NVIDIA RTX 5090
also passed all eight checks with 0% black pixels and correct fractional blending.
Windows 10 and mixed-monitor DPI transitions
still require testing for this change. OS version alone has not been isolated as
the cause of the original GPU failure.

## Linux / WSLg portability check

Without installing a Linux SDK, publish on Windows and run the Linux executable
from WSL (replace `/mnt/c/path/to/repo` with the checkout location):

```powershell
dotnet publish samples/TransparentHud -c Release -r linux-x64 --self-contained true -o artifacts/linux-hud
wsl -- /mnt/c/path/to/repo/artifacts/linux-hud/TransparentHud --layered-gpu
```

The diagnostic must say that normal desktop rendering is being used, not throw.
Set `CUPRIFACE_GL_DEBUG=1` in the Linux process environment to identify the actual
renderer. A working GL context does not necessarily mean hardware acceleration:
WSLg may select Mesa llvmpipe. Where the installed Mesa supports it,
`env GALLIUM_DRIVER=d3d12 CUPRIFACE_GL_DEBUG=1 ./TransparentHud --layered-gpu`
selects the WSLg D3D12 driver for that process without changing system settings.

Verified on 2026-09-10 in Ubuntu 24.04 / WSL2 / WSLg: the previous entry point
threw before creating a window; the fallback starts the HUD with both Mesa
llvmpipe and D3D12 (NVIDIA RTX 3090). A periodically refreshed transparent probe
using the same entry point produced 340x224 GL readbacks with corner alpha 0
and panel RGBA (18,20,26,217) on both renderers. These are **render-surface**
checks, not assertions about WSLg's final desktop composition. They do not exercise
Win32 layered presentation or replace native Linux desktop acceptance testing.
