// Emscripten JS library for the NativeAOT-LLVM host: the C# side DllImports these (module "js",
// bound at link time via DirectPInvoke). main.js publishes the DOM objects on globalThis.__cupri.
// Strings arrive as UTF-16 pointers (managed strings are null-terminated in memory).
mergeInto(LibraryManager.library, {
    // Blit the damage rect of the engine's staging bitmap. TRUE zero-copy: the pixels live in wasm
    // linear memory; we wrap HEAPU8 at the pointer in a view (fresh each call — memory can grow).
    js_present: (rgba, w, h, dx, dy, dw, dh) => {
        const g = globalThis.__cupri;
        const view = new Uint8ClampedArray(HEAPU8.buffer, rgba, w * h * 4);
        g.ctx.putImageData(new ImageData(view, w, h), 0, 0, dx, dy, dw, dh);
        g.paints++;
    },
    js_cursor: (p, len) => {
        const name = UTF16ToString(p);
        const c = globalThis.__cupri.canvas;
        if (c.style.cursor !== name) c.style.cursor = name;
    },
    js_navigate: (p, len) => { window.open(UTF16ToString(p), "_blank", "noopener"); },
    js_clip_write: (p, len) => { navigator.clipboard.writeText(UTF16ToString(p)).catch(() => {}); },
    js_clip_paste: () => {
        navigator.clipboard.readText()
            .then(t => { if (t) globalThis.__cupri.sendText(t, "PasteText"); })
            .catch(() => {});
    },
    js_a11y: (p, len) => {
        const html = UTF16ToString(p);
        const el = globalThis.__cupri.a11y;
        if (el.innerHTML !== html) el.innerHTML = html;
    },
});
