# CupriFace.Gl

An OpenGL viewport bound to a CupriFace element — on desktop, on Android and in the browser.

```csharp
// markup, anywhere in your app's HTML:
//   <div data-cupri-surface="scene" data-cupri-image="poster.png"></div>

var viewport = GlViewport.Attach(doc, "scene", new MyScene());
```

That is the whole integration. `MyScene` implements `IGlContent` — three methods — and runs unchanged
on every host.

## This is a seam, not a renderer

There is no glTF loader here, no shading model, no scene graph, no camera. Those are yours, and there
are good libraries for them. What no library outside this repository can write is the half *below*
them, because it reaches into each host:

| | |
|---|---|
| **Desktop / Android** | draws on the **host's own GL context** and hands the engine a texture — no readback, no re-upload, no second context or thread |
| **Software window / headless** | draws on a private context you supply, reads back, gives the engine an ordinary image |
| **Browser** | the wasm host rasterises on the CPU and has no context to share, so the engine punches a transparent hole and a real `<canvas>` sits underneath, kept glued to the element's box through scrolling, clipping and transforms |

Picking between those, and being correct in each, is the work. `GlViewport` does it.

## What it handles that is easy to get wrong

- **The element's device resolution.** The target follows the element's laid-out box times the host's
  scale, and follows a resize. Render at a fixed size and a 3× phone upscales a third-resolution
  image into the panel — visibly soft, with nothing in the markup to explain it.
- **State Skia leaves behind.** Before every frame the driver is put into a documented state. The one
  nobody guesses: Skia binds **sampler objects**, and a bound sampler object overrides *every* texture
  parameter on that unit — including wrap mode. Set `GL_REPEAT`, draw with a tiling UV, and get
  clamping instead, so half the model samples one edge texel. Nothing errors; the parameters read back
  exactly as you set them. It cost a full debugging session, and it was invisible on one desktop
  driver and glaring on a phone.
- **The clear.** The viewport clears, and `ClearColor` is yours to choose — because on the browser
  lane the hole erases the element's own CSS background, so a transparent clear renders near-black on
  a desktop and white in a browser from identical markup.
- **Not keeping the host awake.** A viewport ticks only while its element is actually laid out. A
  surface that always claims to be producing frames stops a render-on-demand host ever idling; the
  paint count stays flat, so it looks like nothing at all, and on a phone it is a battery bug.
- **Teardown.** GL objects are deleted on the thread whose context owns them, which is the only place
  it is legal.

## Degrading is the normal path

A machine with no usable GL, a software window with no offscreen factory supplied, a browser that
refused WebGL2 — each leaves the element showing its `data-cupri-image` poster while `State` reports
`Unavailable` and `Diagnostic` says why. Nothing throws, and no host goes down because a viewport
could not start.

```csharp
if (viewport.State == GlViewportState.Unavailable)
    Console.WriteLine(viewport.Diagnostic);   // "this host shares no GPU context, and no…"
```

`Unavailable` means *there is no GL here* — carry on. `Failed` means *GL was here and something in it
broke*, with the driver's own words where there are any.

## Writing the content

```csharp
sealed class MyScene : IGlContent
{
    public bool Initialise(GlContext gl)
    {
        // Build your own entry-point table from gl.GetProcAddress — the package publishes no GL
        // table, so nothing here is frozen into its API and nothing is static on your behalf.
        var source = gl.ShaderHeader + FragmentBody;   // the one host difference you must handle
        …
        return true;                                   // false fails cleanly
    }

    public void Render(GlContext gl, in GlFrame frame)
    {
        // Framebuffer bound, state reset, viewport set, already cleared. Just draw, at
        // frame.Width x frame.Height device pixels, animating off frame.ElapsedSeconds.
    }

    public void Shutdown(GlContext gl) { /* delete your objects; the context is still current */ }
}
```

`gl.ShaderHeader` is `#version 330 core` or `#version 300 es`, decided by asking the driver rather
than guessing from the platform. **WebGL2 is OpenGL ES 3.0**, so a browser and a phone want the same
shader and only the desktop differs.

## The offscreen fallback is opt-in

Making a private GL context needs a windowing library, and this package will not put a desktop
windowing stack into every phone and browser build to serve a fallback most apps do not want. So
supply one if you want the lane:

```csharp
new GlViewportOptions { OffscreenContext = () => new MySilkContext() }   // IGlOffscreenContext
```

`samples/Viewer/Teapot3dSurface.cs` has a working ~50-line implementation over Silk.NET. Without it,
such a host reports `Unavailable` and shows the poster.

## What it asks of your build

On **wasm only**, one line — `<DirectPInvoke Include="emscripten" />` — added for you by the package's
`buildTransitive` props, because WebAssembly resolves no P/Invoke lazily and the failure would
otherwise arrive at runtime inside the frame loop. On desktop and Android the package adds nothing to
your build at all.

## Known limits

- One `IGlContent` per element. Several viewports in one document work; they each get their own
  target and their own context handle.
- Multisampling is offered only on the browser lane, where the context can be asked for it. On the
  painted lanes the package's framebuffer would need a resolve step, which is better done by drawing
  code that knows what it is drawing.
- **A driver is the one thing tests cannot stand in for.** Every rendering defect in this work so far
  passed CI and 800 unit tests and looked correct on one desktop driver; two were found only by a
  person running it on a phone. Log `viewport.Context.Renderer` — it names the hardware, and it is the
  first question worth answering when a report says it looks wrong.
