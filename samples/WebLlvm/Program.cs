using CupriFace.Demo;
using CupriFace.Web;

// The NativeAOT-LLVM sample, in full. Everything that used to live here — the Init/Tick/Paint
// lifecycle, the whole C-ABI export surface, damage-rect blitting, input, the touch recognizer,
// the ARIA mirror, IME placement, clipboard, video and fonts — is CupriFace.Web.NativeAot now.
//
// The line below is identical to samples/WebWasm's. That is the point: the two hosts differ in
// runtime, not in API, so an app moves between them by changing a reference (#78).
WebHost.Run(new ShowcaseApp());
