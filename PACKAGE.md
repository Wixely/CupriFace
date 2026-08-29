# CupriFace

A native, cross-platform UI runtime that renders **HTML + CSS** to a Skia canvas and binds it to
plain C# objects — **no browser, no JavaScript engine, no XAML**.

```csharp
public sealed class SettingsApp : CupriApp
{
    private readonly Settings _model = new();

    public override string Html => """
        <div class="row">
          <span>Volume</span>
          <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
          <span>{{Volume}}</span>
        </div>
        """;

    public override string Css => ".row { display:flex; align-items:center; gap:12px; }";
    public override object Model => _model;
}

public sealed class Settings { public int Volume { get; set; } = 60; }
```

Dragging the slider writes `Volume` on that object — no `INotifyPropertyChanged`, no converters, no
code-behind. Styling is real CSS: the cascade, class and descendant selectors, flexbox, grid,
variables, `@media`, `@keyframes`, transitions.

## Packages

| Package | What it gives you |
|---|---|
| `CupriFace` | The engine — parse, style, layout, shape text, paint, bind, components. Renders into any Skia canvas or RGBA buffer, so it works headless too. |
| `CupriFace.Shell` | The desktop host — a window (GPU with a software fallback), input, and cursors. `DesktopHost.Run(new SettingsApp())`. |
| `CupriFace.Android` | The Android host — subclass `CupriActivity`, return your `CupriApp`. GL surface, touch gestures (tap/fling/long-press), soft keyboard with real IME composition, and the TalkBack accessibility bridge. Needs the `android` workload. |
| `CupriFace.Web` | The browser host — `WebHost.Run(new SettingsApp())` in a raw WebAssembly app. Canvas, frame loop, pointer/touch/wheel/keyboard, the ARIA mirror screen readers read, IME composition, clipboard, and browser-decoded video. No Blazor and no JS to write. |

The engine has no windowing dependency at all, which is what makes it embeddable: `RenderToPixels`
fills any RGBA buffer — a game texture, an HTML canvas, a server-side image — and the same document
takes pointer and key events with no display attached. That also makes UI genuinely unit-testable.

## Notes

- Requires **.NET 10**. Skia and HarfBuzz natives for Windows, Linux and macOS come in as
  dependencies, so one build runs on any desktop OS.
- On Android the runtime is **CoreCLR** — `CupriFace.Android` pins `UseMonoRuntime=false` for
  every consumer via its buildTransitive targets. This is a correctness requirement, not a
  preference: Mono 10.0.11 miscompiles the engine on Android (forensics in the repo,
  `samples/AndroidProbe/MONO-CRASH.md`).
- **App icons come in two kinds, and CupriFace only owns one of them.** Override `CupriApp.Icon`
  with PNG/JPEG bytes and every host adapts it to its own *runtime* icon: the desktop window and
  taskbar, the browser tab's favicon, the Android recents card. The **launcher** icon is not
  CupriFace's to set — the OS reads it out of the built file before your code exists, so it stays
  an SDK concern: `<ApplicationIcon>app.ico</ApplicationIcon>` for a Windows `.exe`,
  `Resources/mipmap-*/ic_launcher.png` plus `[Application(Icon = "@mipmap/ic_launcher")]` for an
  APK. No runtime API can reach either one.
- CSS support is a real but **documented subset** — the cascade, flexbox, grid, transforms and
  animations are there; the modern long tail is not.
- Pre-1.0: the API is expected to change.

Source, screenshots, the full element reference and comparisons with Avalonia, Electron and MewUI:
**https://github.com/Wixely/CupriFace**
