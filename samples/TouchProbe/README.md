# Touch probe — #143

Taps do nothing in a desktop build. Every proposed fix assumes SDL receives `SDL_FINGER*` events,
and **nobody has tested that.** The Deck has working GL, so the app takes the GLFW window, and GLFW
has no touch API at all — so "nothing reaches the engine" is equally consistent with gamescope never
delivering touch to SDL either. Those two possibilities need completely different fixes.

This binary tells them apart in one run.

## Run it

```
./TouchProbe                 # report every event, and route it into a CupriFace document
./TouchProbe --raw           # report only, dispatch nothing
./TouchProbe --mouse-hint    # ask SDL to synthesise mouse events from touch, then report both
```

**Run it in Desktop Mode first, then in Game Mode (gamescope).** The answer can differ, and the
gamescope one is the one that matters.

Tap the three coloured boxes. Everything is printed to the terminal.

## The first line is most of the answer

```
[probe] touch devices    : 0   <<< SDL SEES NO TOUCHSCREEN
```

This is printed before you touch anything, from `SDL_GetNumTouchDevices`.

- **Non-zero** → SDL sees the touchscreen. Handling `SDL_FINGER*` in `SdlSoftwareWindow`
  (**option 1** in the issue) will work, and needs no new dependency — `Silk.NET.SDL 2.22.0`
  already exposes `Fingerdown`/`Fingermotion`/`Fingerup`.
- **Zero** → SDL cannot see it, and option 1 was never going to help however it was written. That
  points at **option 2** (XInput2 on X11, `wl_touch` on Wayland), or at a gamescope setting.

## Then tap, and read three things

| Line | Means |
|---|---|
| `[touch] Down  touchId=… fingerId=… norm=(0.412,0.533) px=(371,320)` | fingers **are** arriving. Note `norm` — SDL gives 0..1, not pixels, which is one of the two easy ways to get the fix wrong |
| `[mouse] … which=4294967295  <<< SYNTHESISED FROM TOUCH` | this platform already turns taps into mouse events. If you see these *and* `[touch]` lines, a naive fix would deliver every tap **twice** |
| `[route] pointer=… -> HIT an element` | the tap reached the right box. `hit nothing` with a `[touch]` line above it means the coordinate mapping is wrong, not the delivery |

A **magenta dot** marks the last press. If the dot is not under your finger, the coordinates are
being mis-mapped — which on screen looks identical to a tap that missed.

The summary at exit is the part to paste back:

```
[probe] finger events 42, mouse events 8 (of which 8 synthesised from touch), element hits 6
```

## What this is not

It is not a fix, and it deliberately does **not** use `DesktopHost` — a probe routed through the
host being tested could have its answer shaped by that host. It is a raw SDL window and event loop
with one CupriFace document rendered into it, so nothing in the engine can mask the result.

Mouse clicks are routed into the document as well as fingers. That is on purpose: if this platform
delivers taps *only* as synthesised mouse events, the boxes still respond and the run says so,
rather than reading as "the tap missed".
