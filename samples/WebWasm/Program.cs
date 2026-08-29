using CupriFace.Demo;
using CupriFace.Web;

// The raw-WASM sample, in full. Everything that used to live here — the Init/Tick/Paint lifecycle,
// damage-rect blitting, premultiplied→straight alpha, pointer/touch/wheel/keyboard dispatch, the
// touch recognizer, the ARIA mirror, IME positioning, clipboard, video, fonts — is CupriFace.Web
// now, which is the whole point of #73: the second web app should not have to copy the first one.
//
// This runs the SAME ShowcaseApp the desktop Viewer and the Android host run.
WebHost.Run(new ShowcaseApp());
