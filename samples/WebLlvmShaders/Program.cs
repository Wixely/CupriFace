using CupriFace.Samples.Shaders;
using CupriFace.Web;

// samples/Shaders on the NativeAOT-LLVM host. The same ShadersApp the desktop window and the Mono
// sample run — the shaders are @shader blocks in its stylesheet, so nothing here knows they exist.
//
// The host draws on the GPU when the browser has one and on the CPU when it does not, and the line
// at the bottom of the page says which. ?renderer=cpu shows what the GPU is saving: the same
// animated shaders, rasterised in wasm.
WebHost.Run(new ShadersApp());
