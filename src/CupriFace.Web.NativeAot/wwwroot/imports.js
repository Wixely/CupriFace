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
    // Tab icon from CupriApp.Icon. index.html declares no <link rel="icon">, so create one.
    js_favicon: (p, len) => {
        let link = document.querySelector("link[rel='icon']");
        if (!link) { link = document.createElement("link"); link.rel = "icon"; document.head.appendChild(link); }
        link.href = UTF16ToString(p);
    },
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
    // Put the hidden textarea where the caret is, so an IME's candidate window opens AT the field
    // instead of at the page's top-left, and tell a touch keyboard which layout to offer. The
    // coordinates arrive in canvas pixels; the textarea is positioned in the page, so the canvas's
    // own offset goes back on. Mirrors the Mono host's `textInput` import exactly.
    js_text_input: (focused, numeric, multiline, x, y) => {
        const kbd = globalThis.__cupri.kbd;
        if (!kbd) return;
        const r = globalThis.__cupri.canvas.getBoundingClientRect();
        kbd.style.left = Math.round(r.left + window.scrollX + x) + "px";
        kbd.style.top = Math.round(r.top + window.scrollY + y) + "px";
        kbd.inputMode = focused ? (numeric ? "numeric" : "text") : "none";
    },

    // ---- Video underlay: the BROWSER decodes; the engine punches a transparent hole where the
    // element shows and paints its own controls on top (same design as the Mono host's main.js).
    // Element creation/eventing lives in main.js (globalThis.__cupri.videoOpen) — this side only
    // moves bytes and forwards transport calls.
    js_video_open: (id, p, len) => { globalThis.__cupri.videoOpen(id, UTF16ToString(p)); },
    js_video_open_bytes: (id, data, len) => {
        // Embedded/file/data: sources arrive as BYTES in wasm memory → Blob URL (revoked on close).
        const bytes = HEAPU8.slice(data, data + len);
        const url = URL.createObjectURL(new Blob([bytes], { type: "video/webm" }));
        const v = globalThis.__cupri.videoOpen(id, url);
        v.dataset.blobUrl = url;
    },
    js_video_close: (id) => {
        const g = globalThis.__cupri;
        const v = g.videos.get(id);
        if (v) {
            v.pause(); v.remove(); g.videos.delete(id);
            if (v.dataset.blobUrl) URL.revokeObjectURL(v.dataset.blobUrl);
        }
    },
    // play() rejection (no gesture, unmuted) is expected — the pause-state event keeps the
    // engine's controls honest, so the rejection needs no handling here.
    js_video_play: (id) => { globalThis.__cupri.videos.get(id)?.play().catch(() => {}); },
    js_video_pause: (id) => { globalThis.__cupri.videos.get(id)?.pause(); },
    js_video_muted: (id, m) => { const v = globalThis.__cupri.videos.get(id); if (v) v.muted = !!m; },
    js_video_volume: (id, vol) => { const v = globalThis.__cupri.videos.get(id); if (v) v.volume = vol; },
    js_video_loop: (id, l) => { const v = globalThis.__cupri.videos.get(id); if (v) v.loop = !!l; },
    js_video_seek: (id, t) => { const v = globalThis.__cupri.videos.get(id); if (v) v.currentTime = t; },
    // Position/size/clip in canvas pixels. clip-path recreates the engine's scroll/overflow
    // clipping, which a DOM element would otherwise ignore.
    js_video_rect: (id, x, y, w, h, cT, cR, cB, cL, visible, fitP, fitLen, ta, tb, tc, td, te, tf) => {
        const g = globalThis.__cupri;
        const v = g.videos.get(id); if (!v) return;
        if (!visible) { v.style.display = "none"; return; }
        const r = g.canvas.getBoundingClientRect();
        v.style.display = "";
        v.style.left = (r.left + window.scrollX + x) + "px";
        v.style.top = (r.top + window.scrollY + y) + "px";
        v.style.width = w + "px";
        v.style.height = h + "px";
        const fit = UTF16ToString(fitP);
        v.style.objectFit = fit === "none" ? "none" : fit;   // same keyword set as the engine
        v.style.clipPath = (cT || cR || cB || cL) ? `inset(${cT}px ${cR}px ${cB}px ${cL}px)` : "";
        // Any engine transform on the chain moved the painted hole — mirror it exactly.
        const identity = ta === 1 && tb === 0 && tc === 0 && td === 1 && te === 0 && tf === 0;
        v.style.transformOrigin = "0 0";
        v.style.transform = identity ? "" : `matrix(${ta},${tb},${tc},${td},${te},${tf})`;
    },

    // Fullscreen (0 toggle / 1 enter / 2 exit) on the canvas's container, so the underlaid videos
    // come along. The browser's Escape exits natively; fullscreenchange (main.js) tells the engine.
    js_window_command: (cmd) => {
        const c = globalThis.__cupri.canvas;
        const target = c.parentElement || document.documentElement;
        const inFs = !!document.fullscreenElement;
        if (cmd === 2 || (cmd === 0 && inFs)) { document.exitFullscreen?.(); return; }
        if (cmd === 1 || cmd === 0) target.requestFullscreen?.().catch(() => {});
    },
});
