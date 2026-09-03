using CupriFace.Demo;
using CupriFace.Samples.WebLlvm;
using CupriFace.Web;

// The NativeAOT-LLVM sample, in full. Everything that used to live here — the Init/Tick/Paint
// lifecycle, the whole C-ABI export surface, damage-rect blitting, input, the touch recognizer,
// the ARIA mirror, IME placement, clipboard, video and fonts — is CupriFace.Web.NativeAot now.
//
// The APP argument below is identical to samples/WebWasm's. That is the point: the two hosts differ
// in runtime, not in API, so an app moves between them by changing a reference (#78).
//
// The second argument is this host's own composition root, exactly where video attaches on desktop
// and for the same reason: ShowcaseApp is shared with every host and must not reference a renderer.
// Only THIS sample opts in — WebWasm's line is still the bare one, so it shows the poster, which is
// the engine's ordinary behaviour for a surface with no frames. Here the viewport takes the
// host-composited lane: a WebGL canvas under a hole the engine punches.
WebHost.Run(new ShowcaseApp(), doc => Web3dSurface.TryAttach(doc, Console.WriteLine));
