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
    window.__paints = 0; // diagnostic: count actual canvas paints (a paint = one full render)
    setModuleImports('cupri', {
        present: (rgba, w, h) => {
            if (!img || img.width !== w || img.height !== h) img = ctx.createImageData(w, h);
            img.data.set(rgba.slice());
            ctx.putImageData(img, 0, 0);
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
        a11y: html => { if (a11y.innerHTML !== html) a11y.innerHTML = html; }
    });

    const config = getConfig();
    logBoot('exports...');
    const exports = await getAssemblyExports(config.mainAssemblyName);
    const I = exports.Interop;
    logBoot('exports ok');

    // Size the canvas backing store to its CSS box (the full window), and keep it in sync on
    // resize so Hybrid-Zoom scaling reflows to the viewport. Tick notices the size change and
    // repaints (render-on-demand).
    const sizeCanvas = () => { canvas.width = canvas.clientWidth || 940; canvas.height = canvas.clientHeight || 720; };
    sizeCanvas();
    window.addEventListener('resize', sizeCanvas);
    const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

    // JS → C#: pointer + wheel (same hit-test/dispatch as desktop). Registered now; they only
    // fire after the runtime is running, below.
    // e.detail carries the click count (1/2/3 = single/double/triple) for word/line selection.
    canvas.addEventListener('pointerdown', e => { focusKbd(); const [x, y] = at(e); I.PointerDown(x, y, e.detail || 1); });
    // Right-click: show the engine's context menu, suppress the browser's default menu.
    canvas.addEventListener('contextmenu', e => { focusKbd(); const [x, y] = at(e); I.ContextMenu(x, y); e.preventDefault(); });
    canvas.addEventListener('pointermove', e => { const [x, y] = at(e); I.PointerMove(x, y); });
    canvas.addEventListener('pointerup',   e => { const [x, y] = at(e); I.PointerUp(x, y); });
    canvas.addEventListener('wheel', e => { const [x, y] = at(e); I.Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

    // Keyboard on the hidden textarea: named keys → EditKey codes (must match
    // CupriFace.Interaction.EditKey), Shift/Ctrl mods (KeyMods: Shift=1, Ctrl=2); printable chars →
    // KeyChar. Ctrl+C/X/V are NOT handled here — they fall through so the browser fires the native
    // copy/cut/paste events (handled below), which need no clipboard permission.
    const EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13 };
    kbd.addEventListener('keydown', e => {
        const ctrl = e.ctrlKey || e.metaKey;                 // Cmd on macOS
        const mods = (e.shiftKey ? 1 : 0) | (ctrl ? 2 : 0);
        if (ctrl) {
            const k = e.key.toLowerCase();
            if (k === 'c' || k === 'x' || k === 'v') return; // let the native copy/cut/paste event fire
            if (k === 'a') { I.EditKeyPress(14, 0); e.preventDefault(); return; }                     // select all
            if (k === 'z') { if (e.shiftKey) I.Redo(); else I.Undo(); e.preventDefault(); return; }   // Ctrl+Shift+Z = redo
            if (k === 'y') { I.Redo(); e.preventDefault(); return; }
            // Any other Ctrl/Cmd + letter → an app shortcut (e.g. Ctrl+K). preventDefault only if the engine
            // took it, so unbound chords (Ctrl+F/P/…) still reach the browser.
            if (k.length === 1 && I.KeyChord(k, mods)) { e.preventDefault(); return; }
        }
        if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? 11 : 10, 0); e.preventDefault(); return; }
        if (e.key in EK) { I.EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
        if (e.key.length === 1 && !ctrl) { I.KeyChar(e.key); e.preventDefault(); }
    });

    // Native clipboard events on the focused textarea — no navigator.clipboard, no prompt. We supply
    // the engine's selection on copy/cut and feed pasted text to the engine, and preventDefault so the
    // textarea's own placeholder isn't used.
    kbd.addEventListener('copy', e => { const t = I.CopySelection(); if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('cut',  e => { const t = I.CutSelection();  if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('paste', e => { const t = e.clipboardData.getData('text/plain'); e.preventDefault(); if (t) I.KeyChar(t); keepSelected(); });

    // Start the runtime with runMain (runs Main and STAYS RESIDENT — unlike dotnet.run(),
    // which exits after Main so later [JSExport] calls would fail) before calling any export.
    logBoot('runMain...');
    await runMain();
    logBoot('runMain ok');

    I.Init();
    logBoot('Init ok');
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
