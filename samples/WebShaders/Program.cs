using CupriFace.Samples.Shaders;
using CupriFace.Web;

// samples/Shaders, in the browser. The same ShadersApp the desktop window runs — the shaders are
// @shader blocks in its stylesheet, so nothing here knows they exist.
//
// The web host rasterises on the CPU, so this is also where a shader's cost is easiest to see:
// flip the CRT switch and the whole page is shaded in software on every frame.
WebHost.Run(new ShadersApp());
