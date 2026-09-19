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
| `CupriFace.Web.Mono` | The browser host — `WebHost.Run(new SettingsApp())` on a canvas. Frame loop, pointer/touch/wheel/keyboard, the ARIA overlay screen readers read and operate (with a real `<input>` per text field, so the browser's own editor, IME and password manager work on it), IME composition, clipboard, and browser-decoded video. No Blazor and no JS to write. Named for its runtime: it uses the wasm runtime in the .NET SDK, so it builds anywhere, but the engine runs interpreted (~8x slower than native). A faster NativeAOT-LLVM sibling with the identical `WebHost.Run` API is planned, so the choice is a `PackageReference`. |
| `CupriFace.Web.NativeAot` | The same browser host **compiled ahead of time** — several times faster for interaction-heavy UI, because the engine is not interpreted. Identical `WebHost.Run` API, so the choice is a `PackageReference`. Costs toolchain maturity: it needs the experimental dotnet/runtimelab ILC feed, which your app must add itself. |

The engine has no windowing dependency at all, which is what makes it embeddable: `RenderToPixels`
fills any RGBA buffer — a game texture, an HTML canvas, a server-side image — and the same document
takes pointer and key events with no display attached. That also makes UI genuinely unit-testable.

## Notes

- Requires **.NET 10**. Skia and HarfBuzz natives for Windows, Linux and macOS come in as
  dependencies, so one build runs on any desktop OS.
- **On Android, put `<UseMonoRuntime>false</UseMonoRuntime>` in your app's `.csproj`.** The package
  pins it too, but it cannot do so in time for *restore*, and restore is what downloads the runtime
  packs. NuGet evaluates your project with `ExcludeRestorePackageImports=true` while restoring, so
  the package's `.props` is not imported then: restore fetches **Mono's** packs, the build asks for
  **CoreCLR's**, and on any machine that has not already cached them the build fails with
  `NETSDK1112`. A forced restore does not help — it evaluates the project the same way. Set in the
  project, the property is visible to both, and everything works.

  A machine that has built a CoreCLR Android app before has the packs cached and never sees this,
  which is why it tends to appear first on a clean machine or a CI runner. If it does, the build
  reports **CUPRI0002** naming this fix.

- On Android the runtime is **CoreCLR** — `CupriFace.Android` pins `UseMonoRuntime=false` for
  every consumer from its `buildTransitive/CupriFace.Android.props` (see the note above about
  restore). This is a correctness requirement, not a preference: Mono 10.0.11 miscompiles the engine on Android (forensics in the
  repo, `samples/AndroidProbe/MONO-CRASH.md`). An app that sets `UseMonoRuntime=true` anyway fails
  the build with **CUPRI0001** rather than shipping an APK that dies during startup; set
  `CupriFaceAllowMonoRuntime=true` to build it regardless.

  **Your build will warn `XA1040`: "The CoreCLR runtime on Android is an experimental feature and
  not yet suitable for production use." That is expected, and this package is the cause of it.**
  The warning is Microsoft's and it is accurate about the runtime; what it cannot say is that the
  choice was forced. The trade in full:

  - XA1040 fires for **any** runtime that is not Mono — NativeAOT trips it too. Mono is the only
    runtime it stays quiet about, and Mono is the one that crashes. There is no configuration here
    that is both warning-free and working.
  - It clears when CoreCLR on Android stops being experimental. That is a **different** upstream
    event from Mono's defect being fixed; a Mono fix would only restore Mono as an *option*.
  - Nothing needs doing about it. The build prints a note next to the warning explaining the above;
    `CupriFaceQuietRuntimeNote=true` silences the note (not the warning), and `<NoWarn>XA1040</NoWarn>`
    silences the warning if you would rather not see it every build.
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
