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

## Issue #212: controlled swap-count probe

`--present-count N` runs a separate, minimal OpenGL scene through Silk/GLFW, without
CupriFace rendering, Skia, or animation. It clears the framebuffer to zero, draws a
premultiplied translucent rectangle, reads back a corner and the panel, and presents
exactly N frames. It then pumps events without further drawing or swapping until closed.
This is an investigation tool, not an alternative application host.

```powershell
dotnet build samples/TransparentHud -c Release
powershell -NoProfile -File tools/Test-WindowsAlpha.ps1 -Exe samples/TransparentHud/bin/Release/net10.0/TransparentHud.exe -GlBaseline -PresentCount 1
powershell -NoProfile -File tools/Test-WindowsAlpha.ps1 -Exe samples/TransparentHud/bin/Release/net10.0/TransparentHud.exe -GlBaseline -PresentCount 2
```

The script waits for the requested swap count, reports the loaded GLFW DLL's SHA-256,
checks the screen over a synthetic backdrop, closes normally, and verifies the final
swap count. It skips resize/lifecycle checks in this mode because resizing a frozen
front buffer would invalidate the experiment. No screenshots are saved. The GPU
readback precedes each swap; screen capture follows after the compositor has settled.
This establishes what is visible **after one application swap**, not the contents or
timing of every intermediate DWM frame during window creation.

Interpret `CornersMatch` only alongside `PanelVisible`, `SyntheticBackdrop`, and
`TranslucentCardBlends`. A capture of the backdrop alone also has matching corners and
zero black pixels. An obscured backdrop or indistinguishable panel is inconclusive;
the acceptance modes now reject those captures explicitly. The window search selects
the requested backend's class instead of accepting a GLFW context window while waiting
for a layered presentation window.

### Investigation results (2026-10-05; reviewed 2026-10-05)

On Windows build 28000 with NVIDIA driver 32.0.16.1047, the probe selected the RTX 5090.
The machine has physical NVIDIA displays: `EnumDisplayDevices` reports the primary
desktop on the RTX 5090 and all six attached desktop displays on RTX 5090/3090 adapters.
Neither installed virtual adapter is reported as attached to the desktop. The older
issue's Parsec routing must not be assumed to describe this session; installed drivers
and `Win32_VideoController` mode information alone do not establish active routing.
Topmost captures over the verified RGB (64,128,192) backdrop produced:

| GLFW build | Swaps | GPU corner RGBA | GPU panel RGBA | Screen panel RGB | Black share |
|---|---:|---|---|---|---:|
| Shipped native DLL | 1 and 2 | 0,0,0,0 | 15,17,22,217 | 15,17,22 | 22.35% |
| Locally built 3.4 | 1 | 0,0,0,0 | 15,17,22,217 | 15,17,22 | 22.35% |
| 3.4 + glfw/glfw#2815 patch | 1 | 0,0,0,0 | 15,17,22,217 | 15,17,22 | 22.35% |

All measured topmost corners differed from the backdrop. The black area matches the
synthetic scene's transparent margins: `1 - (462*288)/(510*336) = 22.35%`. Correct
panel blending would be approximately RGB (25,36,51); instead its screen RGB equals
the premultiplied framebuffer RGB. This is consistent with composition against black.
It does not identify which driver/compositor surface loses or ignores the alpha.

The fault therefore needs neither a second application swap nor CupriFace/Skia drawing.
The **actual native** upstream patch did not fix this controlled reproduction.
[glfw/glfw#2815](https://github.com/glfw/glfw/pull/2815) remained open at review time.
The patch was applied unchanged to GLFW tag 3.4, commit
`7b6aead9fb88b3623e3b3725ebb42670cbe4c579`, and built with MSVC x64 in Release mode:

```powershell
cmake -S <glfw-source> -B <build-directory> -G "Visual Studio 18 2026" -A x64 -DBUILD_SHARED_LIBS=ON -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_DOCS=OFF
cmake --build <build-directory> --config Release
```

Use a separate copy of the built sample for each native variant and replace only its
`runtimes/win-x64/native/glfw3.dll`. The script's loaded-module hash confirmed these
specific experimental binaries (hashes are build-specific, not expected release hashes):

- Stock 3.4: `4AE6A36E9DBC0D8314BE028C388AB086D047FBA1D65FC2ADC0B36C499718ADE3`
- Patched 3.4: `101F27E1D3CCAB5B838C46A2746CCFD79D773C25BE8C5DDFF812FF40B19E286F`

Non-topmost captures in this session showed the backdrop without a distinguishable
panel, so they provide no evidence about whether topmost is causal. The layered-GPU
recheck was likewise inconclusive in this session; the earlier acceptance results
above remain historical results, not fresh verification.

This reproduction already occurs on a physical NVIDIA desktop path; an active virtual
display is not established as a prerequisite. Installed virtual-display software could
still influence the system, but this experiment neither proves nor excludes that.

Remaining investigation, owner TBD: isolate the WGL/NVIDIA/Windows presentation path
with the same minimal scene, recording the window's monitor/adapter and comparing a
known-good composition path. A test on a machine without virtual-display software
would distinguish installation effects, but is separate from selecting a physical
display, which this session already uses. No display routing or global driver settings
were changed for these tests.

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
