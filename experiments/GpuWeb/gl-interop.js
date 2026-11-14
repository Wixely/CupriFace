// Linked into dotnet.native.js as an Emscripten JS library (see GpuWeb.csproj).
//
// Emscripten's GL object is module-private, and it is the only thing that can register a WebGL
// context in the table libSkiaSharp's gl* calls go through. This hands it to the page.
var CupriGpuProbe = {
    cupri_probe_expose_gl__deps: ['$GL'],
    cupri_probe_expose_gl: function () {
        globalThis.CupriGL = GL;
    }
};

mergeInto(LibraryManager.library, CupriGpuProbe);
