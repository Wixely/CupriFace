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

The engine has no windowing dependency at all, which is what makes it embeddable: `RenderToPixels`
fills any RGBA buffer — a game texture, an HTML canvas, a server-side image — and the same document
takes pointer and key events with no display attached. That also makes UI genuinely unit-testable.

## Notes

- Requires **.NET 10**. Skia and HarfBuzz natives for Windows, Linux and macOS come in as
  dependencies, so one build runs on any desktop OS.
- CSS support is a real but **documented subset** — the cascade, flexbox, grid, transforms and
  animations are there; the modern long tail is not.
- Pre-1.0: the API is expected to change.

Source, screenshots, the full element reference and comparisons with Avalonia, Electron and MewUI:
**https://github.com/Wixely/CupriFace**
