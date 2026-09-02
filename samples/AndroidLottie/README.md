# Lottie on Android

The optional `CupriFace.Lottie` package on a phone. `LottieApp.cs` is portable — not one line of it
is Android-specific, and `MainActivity.cs` only names it — so this is the same app the desktop and
web samples run, on a third host.

`Assets/cupri-spinner.json` is the same animation `samples/LottieDemo` uses: **original work,
MIT-licensed with this repository**, hand-authored rather than taken from a gallery.

## Why this sample exists

Skottie's natives ship in all four Android ABIs — that was already known from the archives. But a
resolved symbol is not a rendered frame, and every other Android claim about Lottie was inference.
This is the APK that makes it observable.

Measured on an API 36 x86_64 emulator (`-gpu swiftshader_indirect`):

| state | pixels changing over 400 ms |
|-------|-----------------------------|
| playing | 53,199 |
| paused | **0** |
| resumed | 40,336 |

Paused is exactly zero, and the four rings are still on screen — 12,720 pixels of CupriFace's copper
inside the animation's own bounds. That is the bargain `LottiePlayer.Ticking` promises: nothing
ticks, the host goes idle, and the last frame stays up.

Counting copper across the WHOLE screen would have been a bad check and nearly went in as one: the
Pause button is copper too, and at 181,801 pixels it is 93% of any full-screen count. A number that
large barely moves whether the animation renders or not.

The device also reports what Skottie parsed, under the `cupri` logcat tag:

```
cupri-lottie: skottie parsed 120x120 duration=1.50s
```

That is the same shape `LottieTests` asserts on desktop, read back off the phone.

## No extra natives

The claim that Lottie costs no native code is checkable in the shipped artifact. Every `.so` in the
arm64 APK is either .NET's runtime or a library the engine already needed:

```
libSkiaSharp.so        ← Skottie lives in here, and the engine already loads it
libHarfBuzzSharp.so    ← text shaping, already required
libcoreclr.so, libclrjit.so, libmonodroid.so, libassembly-store.so, libSystem.*.so
```

There is no Skottie `.so`, because there is no such thing to ship.

## Running it

```
dotnet publish samples/AndroidLottie/AndroidLottie.csproj -c Release -r android-arm64 -o out/arm64
adb install -r out/arm64/com.cupriface.lottie-Signed.apk
```

Use `-r android-x64` for an emulator. Tapping **Pause** stops all four animations together: they
share one player, because the surface key is the `src`.
