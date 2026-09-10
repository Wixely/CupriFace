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

This mode requires Windows, `Transparent=true`, `Frameless=true`, and usable OpenGL.
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
