// Emscripten JS library for the probe's CPU lane: the same blit CupriFace.Web.NativeAot's
// js_present does — a view over wasm memory, putImageData narrowed to the damage rect.
mergeInto(LibraryManager.library, {
    probe_present: (rgba, w, h, dx, dy, dw, dh) => {
        const view = new Uint8ClampedArray(HEAPU8.buffer, rgba, w * h * 4);
        globalThis.__probeCtx.putImageData(new ImageData(view, w, h), 0, 0, dx, dy, dw, dh);
    },
});
