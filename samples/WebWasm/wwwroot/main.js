// CupriFace raw-WASM host — the entire "JS glue" (DESIGN.md §9.1): boot the .NET runtime,
// hand the engine a <canvas> 2D context, blit its pixels each frame, and forward pointer,
// wheel and keyboard input. No Blazor, no UI framework — just a canvas and input plumbing.

import { dotnet } from './_framework/dotnet.js'

const canvas = document.getElementById('cupri');
const ctx = canvas.getContext('2d');

// The canvas is opaque to assistive tech; mirror the engine's semantics tree into an off-screen but
// screen-reader-visible element next to it. (Visually hidden via the clip pattern — NOT display:none
// or aria-hidden, which would hide it from screen readers too.)
canvas.setAttribute('aria-hidden', 'true');
const a11y = document.createElement('div');
a11y.id = 'cupri-a11y';
a11y.setAttribute('aria-live', 'polite');
a11y.style.cssText = 'position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;';
document.body.appendChild(a11y);

// Hidden focused textarea that owns keyboard focus and receives NATIVE copy/cut/paste events — so
// clipboard works with no permission prompt and without navigator.clipboard.readText (which prompts
// on paste and wedges headless automation). It keeps a selected placeholder so copy/cut always fire.
const kbd = document.createElement('textarea');
kbd.id = 'cupri-kbd';
kbd.setAttribute('aria-hidden', 'true');
kbd.autocapitalize = 'off'; kbd.autocomplete = 'off'; kbd.spellcheck = false;
kbd.style.cssText = 'position:absolute;top:0;left:0;width:1px;height:1px;opacity:0;border:0;padding:0;resize:none;overflow:hidden;';
kbd.value = ' '; // non-empty so cut/copy events fire even with no textarea selection
document.body.appendChild(kbd);
const keepSelected = () => { kbd.value = ' '; kbd.setSelectionRange(0, kbd.value.length); };
const focusKbd = () => { kbd.focus({ preventScroll: true }); keepSelected(); };

// Boot log mirrored into the DOM (hidden) so headless --dump-dom diagnostics can read boot
// progress and errors even when the canvas never paints. Harmless in normal use.
const bootLog = document.createElement('pre');
bootLog.id = 'bootlog'; bootLog.style.display = 'none';
document.body.appendChild(bootLog);
function logBoot(s) { bootLog.textContent += s + '\n'; }
window.addEventListener('error', e => logBoot('WINDOW-ERROR: ' + (e.error && e.error.stack || e.message)));
window.addEventListener('unhandledrejection', e => logBoot('UNHANDLED-REJECTION: ' + (e.reason && e.reason.stack || e.reason)));
// Mirror console output (runtime asserts + diagnostic tracing) into the boot log.
for (const lvl of ['error', 'warn', 'info', 'log', 'debug']) {
    const orig = console[lvl].bind(console);
    console[lvl] = (...a) => { try { logBoot(lvl.toUpperCase() + ': ' + a.map(x => x && x.stack || String(x)).join(' ')); } catch {} orig(...a); };
}

// Any boot/render failure is drawn onto the canvas so it's visible without dev tools.
function showError(where, err) {
    const msg = (err && (err.stack || err.message)) || String(err);
    console.error('[CupriFace] ' + where, err);
    logBoot('ERROR(' + where + '): ' + msg);
    ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#b00020'; ctx.font = '14px monospace';
    ctx.fillText('CupriFace failed to start (' + where + '):', 16, 28);
    msg.split('\n').slice(0, 24).forEach((line, i) => ctx.fillText(line.slice(0, 110), 16, 52 + i * 18));
}

try {
    logBoot('create...');
    const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet
        .withDiagnosticTracing(false)
        .create();
    logBoot('created');

    // C# → JS: `rgba` is a MemoryView over the engine's bitmap in WASM memory. `.slice()` reads
    // it into a Uint8Array in a single WASM→JS copy (no managed allocation on the .NET side —
    // bitmap.Bytes would allocate + copy 2.7 MB every frame). We reuse one ImageData per size.
    let img = null;
    const videos = new Map(); // id → underlaid <video> element (browser-decoded video)
    const videoOpen = (id, src) => {
        canvas.style.position = 'relative'; canvas.style.zIndex = '1'; // above all underlays
        const v = document.createElement('video');
        v.src = src;
        v.playsInline = true;          // iOS: never hijack into the native fullscreen player
        v.preload = 'auto';
        v.style.cssText = 'position:absolute;z-index:0;pointer-events:none;display:none;';
        v.addEventListener('loadedmetadata', () => I.VideoMeta(id, v.duration || 0, v.videoWidth, v.videoHeight));
        v.addEventListener('loadeddata', () => I.VideoReady(id));
        // The browser's play/pause truth (autoplay rejections included) drives the controls.
        v.addEventListener('play', () => I.VideoPlayState(id, true));
        v.addEventListener('pause', () => I.VideoPlayState(id, false));
        v.addEventListener('timeupdate', () => I.VideoTime(id, v.currentTime || 0));
        v.addEventListener('ended', () => I.VideoEnded(id));
        document.body.insertBefore(v, canvas);
        videos.set(id, v);
    };
    window.__paints = 0; // diagnostic: count actual canvas paints (a paint = one full render)
    setModuleImports('cupri', {
        // (dx,dy,dw,dh) is the damage rect — only that region changed, so only it is blitted.
        present: (rgba, w, h, dx, dy, dw, dh) => {
            if (!img || img.width !== w || img.height !== h) img = ctx.createImageData(w, h);
            img.data.set(rgba.slice());
            ctx.putImageData(img, 0, 0, dx, dy, dw, dh);
            window.__paints++;
        },
        // Cursor: the engine tells us which cursor to show under the pointer (links, text fields,
        // resize boundaries, …). Only called when it changes.
        cursor: name => { if (canvas.style.cursor !== name) canvas.style.cursor = name; },
        // External link (http/mailto/…): open in a new tab. Internal hrefs route inside the app;
        // #anchors are scrolled by the engine.
        navigate: href => { window.open(href, '_blank', 'noopener'); },
        // Context-menu clipboard (async browser clipboard). Paste reads then feeds the engine.
        clipboardWrite: text => navigator.clipboard.writeText(text).catch(() => {}),
        clipboardPaste: () => navigator.clipboard.readText().then(t => { if (t) I.KeyChar(t); }).catch(() => {}),
        // Off-screen ARIA mirror of the semantics tree (screen-reader accessibility).
        a11y: html => { if (a11y.innerHTML !== html) a11y.innerHTML = html; },
        // Move the hidden textarea to the caret so the IME's candidate window appears AT the
        // field; inputmode picks the right virtual keyboard on touch browsers.
        textInput: (focused, numeric, multiline, x, y) => {
            const r = canvas.getBoundingClientRect();
            kbd.style.left = Math.round(r.left + window.scrollX + x) + 'px';
            kbd.style.top = Math.round(r.top + window.scrollY + y) + 'px';
            kbd.inputMode = focused ? (numeric ? 'numeric' : 'text') : 'none';
        },

        // ---- Video underlay: the BROWSER decodes; the engine punches a transparent hole -----
        // where the element shows and paints its own controls on top. Native controls stay off
        // (they'd be dead under the canvas); the canvas sits above every video (z-index below).
        videoOpen,
        // Embedded/file/data: sources arrive as BYTES (resolved through the same pipeline images
        // use) and play from a Blob URL — an app's embedded clip works identically on the web.
        videoOpenBytes: (id, bytes) => {
            const url = URL.createObjectURL(new Blob([bytes.slice()], { type: 'video/webm' }));
            videoOpen(id, url);
            videos.get(id).dataset.blobUrl = url;   // revoked on close
        },
        videoClose: id => {
            const v = videos.get(id);
            if (v) {
                v.pause(); v.remove(); videos.delete(id);
                if (v.dataset.blobUrl) URL.revokeObjectURL(v.dataset.blobUrl);
            }
        },
        // play() rejection (no gesture, unmuted) is expected — the 'pause'-state truth above
        // keeps the engine's controls honest, so the rejection needs no handling here.
        videoPlay: id => { videos.get(id)?.play().catch(() => {}); },
        videoPause: id => { videos.get(id)?.pause(); },
        videoMuted: (id, m) => { const v = videos.get(id); if (v) v.muted = m; },
        videoVolume: (id, vol) => { const v = videos.get(id); if (v) v.volume = vol; },
        videoLoop: (id, l) => { const v = videos.get(id); if (v) v.loop = l; },
        videoSeek: (id, t) => { const v = videos.get(id); if (v) v.currentTime = t; },
        // Position/size/clip in canvas pixels (backing store == CSS px here). clip-path recreates
        // the engine's scroll/overflow clipping, which a DOM element would otherwise ignore; the
        // matrix mirrors any engine transform on the chain (hover lift, transform transition) —
        // the painted hole moves through those, so the element must move identically.
        videoRect: (id, x, y, w, h, cT, cR, cB, cL, visible, fit, ta, tb, tc, td, te, tf) => {
            const v = videos.get(id); if (!v) return;
            if (!visible) { v.style.display = 'none'; return; }
            const r = canvas.getBoundingClientRect();
            v.style.display = '';
            v.style.left = (r.left + window.scrollX + x) + 'px';
            v.style.top = (r.top + window.scrollY + y) + 'px';
            v.style.width = w + 'px';
            v.style.height = h + 'px';
            v.style.objectFit = fit === 'none' ? 'none' : fit;   // same keyword set as the engine
            v.style.clipPath = (cT || cR || cB || cL) ? `inset(${cT}px ${cR}px ${cB}px ${cL}px)` : '';
            const identity = ta === 1 && tb === 0 && tc === 0 && td === 1 && te === 0 && tf === 0;
            v.style.transformOrigin = '0 0';
            v.style.transform = identity ? '' : `matrix(${ta},${tb},${tc},${td},${te},${tf})`;
        },

        // Fullscreen (0 toggle / 1 enter / 2 exit) on the canvas's container, so the underlaid
        // videos come along. Escape exits natively; the resize event reflows the app.
        windowCommand: cmd => {
            const target = canvas.parentElement || document.documentElement;
            const inFs = !!document.fullscreenElement;
            if (cmd === 2 || (cmd === 0 && inFs)) { document.exitFullscreen?.(); return; }
            if (cmd === 1 || cmd === 0) target.requestFullscreen?.().catch(() => {});
        }
    });

    const config = getConfig();
    logBoot('exports...');
    const exports = await getAssemblyExports(config.mainAssemblyName);
    const I = exports.Interop;
    // Exposed for automation, as the WebLlvm host does: a browser test drives the same exports the
    // page does, rather than a parallel path that could pass while the real one is broken.
    // `isCoarse` is the UNIFORM contract both web hosts publish, so one gate can drive either
    // without knowing whether it is talking to JSExports or to Emscripten's module.
    globalThis.__cupri = Object.assign(globalThis.__cupri || {}, {
        I,
        isCoarse: () => I.IsCoarsePointer(),
    });
    logBoot('exports ok');

    // The browser can end fullscreen on its own (Esc goes to the BROWSER, not our key handler) —
    // tell the engine so an element-fullscreened video returns to its place in the layout.
    document.addEventListener('fullscreenchange', () => I.HostFullscreen(!!document.fullscreenElement));

    // Size the canvas backing store to its CSS box (the full window), and keep it in sync on
    // resize so Hybrid-Zoom scaling reflows to the viewport. Tick notices the size change and
    // repaints (render-on-demand).
    const sizeCanvas = () => { canvas.width = canvas.clientWidth || 940; canvas.height = canvas.clientHeight || 720; };
    sizeCanvas();
    window.addEventListener('resize', sizeCanvas);
    const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

    // Without this the browser claims every gesture for its own scrolling and pinch-zoom, and
    // pointermove stops arriving the moment a finger travels — there is no amount of
    // preventDefault that substitutes for it.
    canvas.style.touchAction = 'none';

    // A finger and a mouse are not the same instrument, and the engine has always known it: the
    // TOUCH path runs the same recognizer the Android host uses (activation on RELEASE, an 8px
    // slop before a press becomes a scroll, momentum, long-press, the rubber band), while a mouse
    // keeps the desktop path it always had. This host used to send fingers down the mouse path,
    // which is why a phone in a browser fired buttons on touch-down and stopped dead instead of
    // coasting.
    const touch = e => e.pointerType === 'touch' || e.pointerType === 'pen';

    // Tell the engine what is actually driving it — reported from the pointer in use, not from
    // the device, because a laptop with a touchscreen is honestly both.
    let coarse = null;
    const profile = isCoarse => {
        if (coarse === isCoarse) return;
        coarse = isCoarse;
        try { I.SetCoarsePointer(isCoarse); } catch { /* before the runtime is live */ }
    };

    // JS → C#: pointer + wheel. Registered now; they only fire after the runtime is running.
    // e.detail carries the click count (1/2/3 = single/double/triple) for word/line selection.
    canvas.addEventListener('pointerdown', e => {
        focusKbd();
        profile(touch(e));
        const [x, y] = at(e);
        // Capture, so a finger that slides off the canvas mid-drag still reports — otherwise a
        // scroll that leaves the element strands the gesture with no up, and the next tap
        // inherits it.
        try { canvas.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
        if (touch(e)) { I.TouchDown(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        else I.PointerDown(x, y, e.detail || 1);
    });
    // Right-click: the engine's context menu, and the browser's suppressed. On touch the same menu
    // arrives from the recognizer's long-press, so the browser's must not also appear.
    canvas.addEventListener('contextmenu', e => {
        e.preventDefault();
        if (coarse) return;
        focusKbd(); const [x, y] = at(e); I.ContextMenu(x, y);
    });
    canvas.addEventListener('pointermove', e => {
        const [x, y] = at(e);
        if (touch(e)) { I.TouchMove(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        else I.PointerMove(x, y);
    });
    canvas.addEventListener('pointerup', e => {
        const [x, y] = at(e);
        if (touch(e)) { I.TouchUp(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        else I.PointerUp(x, y);
    });
    // The browser took the gesture away (a system gesture, the tab hiding). A cancel must never
    // become a click.
    canvas.addEventListener('pointercancel', e => { if (touch(e)) I.TouchCancel(e.pointerId, e.timeStamp); });
    canvas.addEventListener('wheel', e => { profile(false); const [x, y] = at(e); I.Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

    // Keyboard, WINDOW-level (not kbd): named keys → EditKey codes (must match
    // CupriFace.Interaction.EditKey), Shift/Ctrl mods (KeyMods: Shift=1, Ctrl=2); printable chars →
    // KeyChar. Ctrl+C/X/V are NOT handled here — they fall through so the browser fires the native
    // copy/cut/paste events (handled below), which need no clipboard permission.
    // Window-level so app chords (Ctrl+K…) beat the browser's own (address-bar search) even when the
    // hidden textarea lost focus (fresh load, returning to the tab). With kbd focused the same event
    // bubbles here — one listener either way, no double-fire.
    // Fallback ordinals only until Init, when the ENGINE's own map replaces them (EditKeyMap
    // export) — the hand-copied table was a silent-breakage contract.
    let EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13, Tab: 10, ShiftTab: 11, SelectAll: 14 };
    let live = false; // no export calls until the runtime is running (runMain below)
    window.addEventListener('keydown', e => {
        if (!live) return;
        // An IME owns the keyboard while composing: Enter accepts a candidate, Escape closes the
        // window, arrows navigate it. Stealing those keys mid-composition broke CJK entirely
        // (keydown also fires as key "Process"/keyCode 229 during composition).
        if (e.isComposing || e.keyCode === 229) return;
        const ctrl = e.ctrlKey || e.metaKey;                 // Cmd on macOS
        const mods = (e.shiftKey ? 1 : 0) | (ctrl ? 2 : 0);
        if (ctrl) {
            const k = e.key.toLowerCase();
            if (k === 'c' || k === 'x' || k === 'v') return; // let the native copy/cut/paste event fire
            if (k === 'a') { I.EditKeyPress(EK.SelectAll, 0); e.preventDefault(); return; }         // select all
            if (k === 'z') { if (e.shiftKey) I.Redo(); else I.Undo(); e.preventDefault(); return; }   // Ctrl+Shift+Z = redo
            if (k === 'y') { I.Redo(); e.preventDefault(); return; }
            // Any other Ctrl/Cmd + letter → an app shortcut (e.g. Ctrl+K). preventDefault only if the engine
            // took it, so unbound chords (Ctrl+F/P/…) still reach the browser.
            if (k.length === 1 && I.KeyChord(k, mods)) { e.preventDefault(); return; }
        }
        if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? EK.ShiftTab : EK.Tab, 0); e.preventDefault(); return; }
        if (e.key in EK) { I.EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
        if (e.key.length === 1 && !ctrl) { I.KeyChar(e.key); e.preventDefault(); }
    });

    // Native clipboard events on the focused textarea — no navigator.clipboard, no prompt. We supply
    // the engine's selection on copy/cut and feed pasted text to the engine, and preventDefault so the
    // textarea's own placeholder isn't used.
    // IME composition: the browser streams the preedit through composition events on the focused
    // textarea; the engine renders it underlined at the caret and commits on end. keydown is gated
    // on isComposing above, so the IME keeps Enter/Escape/arrows while its candidate window is up.
    kbd.addEventListener('compositionstart', () => { if (live) I.SetComposition(''); });
    kbd.addEventListener('compositionupdate', e => { if (live) I.SetComposition(e.data ?? ''); });
    kbd.addEventListener('compositionend', e => { if (live) I.CommitComposition(e.data ?? ''); keepSelected(); });

    kbd.addEventListener('copy', e => { const t = I.CopySelection(); if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('cut',  e => { const t = I.CutSelection();  if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('paste', e => { const t = e.clipboardData.getData('text/plain'); e.preventDefault(); if (t) I.KeyChar(t); keepSelected(); });

    // Start the runtime with runMain (runs Main and STAYS RESIDENT — unlike dotnet.run(),
    // which exits after Main so later [JSExport] calls would fail) before calling any export.
    logBoot('runMain...');
    await runMain();
    logBoot('runMain ok');
    live = true;
    EK = JSON.parse(I.EditKeyMap());   // the engine's own wire codes replace the fallback table
    focusKbd(); // arm the clipboard path immediately — before the first click
    window.addEventListener('focus', focusKbd); // …and re-arm it when the tab regains focus

    I.Init();
    logBoot('Init ok');

    // An opening guess before anyone has touched anything, so a phone gets coarse styling on the
    // FIRST paint rather than after the first tap. Any real pointer event corrects it.
    try { profile(window.matchMedia('(pointer: coarse)').matches); } catch { /* ancient browser */ }
    focusKbd(); // keyboard + clipboard flow through the hidden textarea, not the canvas

    // Overlay mode: the engine clears transparent and presents straight alpha, so the canvas
    // composites over the page. Pass pointer events THROUGH wherever nothing is drawn — a
    // window-level move listener (fires even when the canvas has pointer-events:none) samples the
    // rendered alpha under the cursor and flips the canvas between catching and passing events.
    if (I.IsTransparent()) {
        canvas.style.background = 'transparent';
        window.addEventListener('pointermove', e => {
            const r = canvas.getBoundingClientRect();
            const x = Math.floor(e.clientX - r.left), y = Math.floor(e.clientY - r.top);
            if (x < 0 || y < 0 || x >= canvas.width || y >= canvas.height) return;
            let alpha = 0;
            try { alpha = ctx.getImageData(x, y, 1, 1).data[3]; } catch {}
            canvas.style.pointerEvents = alpha > 0 ? 'auto' : 'none';
        }, true);
    }

    // Frame loop: Tick decides whether to paint (render-on-demand) — after input, on the app's
    // periodic re-bind, or throttled while something animates. An idle page costs ~nothing.
    let firstTick = true;
    function frame(now) {
        try { I.Tick(canvas.width, canvas.height, now); if (firstTick) { firstTick = false; logBoot('Tick ok'); } }
        catch (err) { showError('Tick', err); return; }
        requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
} catch (err) {
    showError('boot', err);
}
