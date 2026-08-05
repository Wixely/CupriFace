// CupriFace raw-WASM host — the entire "JS glue" (DESIGN.md §9.1): boot the .NET runtime,
// hand the engine a <canvas> 2D context, blit its pixels each frame, and forward pointer,
// wheel and keyboard input. No Blazor, no UI framework — just a canvas and input plumbing.

import { dotnet } from './_framework/dotnet.js'

const canvas = document.getElementById('cupri');
const ctx = canvas.getContext('2d');

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
        }
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
    canvas.addEventListener('pointerdown', e => { canvas.focus(); const [x, y] = at(e); I.PointerDown(x, y, e.detail || 1); });
    canvas.addEventListener('pointermove', e => { const [x, y] = at(e); I.PointerMove(x, y); });
    canvas.addEventListener('pointerup',   e => { const [x, y] = at(e); I.PointerUp(x, y); });
    canvas.addEventListener('wheel', e => { const [x, y] = at(e); I.Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

    // Keyboard: named keys → EditKey codes (must match CupriFace.Interaction.EditKey), with
    // Shift/Ctrl modifiers (KeyMods: Shift=1, Ctrl=2) for selection + word movement; printable
    // characters → KeyChar. Ctrl+A/C/X/V are text shortcuts (clipboard I/O lives here in JS).
    const EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13 };
    canvas.addEventListener('keydown', e => {
        const ctrl = e.ctrlKey || e.metaKey;                 // Cmd on macOS
        const mods = (e.shiftKey ? 1 : 0) | (ctrl ? 2 : 0);
        if (ctrl) {
            const k = e.key.toLowerCase();
            if (k === 'a') { I.EditKeyPress(14, 0); e.preventDefault(); return; }                                    // select all
            if (k === 'c') { const t = I.CopySelection(); if (t) navigator.clipboard.writeText(t).catch(() => {}); e.preventDefault(); return; }
            if (k === 'x') { const t = I.CutSelection(); if (t) navigator.clipboard.writeText(t).catch(() => {}); e.preventDefault(); return; }
            if (k === 'v') { navigator.clipboard.readText().then(t => { if (t) I.KeyChar(t); }).catch(() => {}); e.preventDefault(); return; }
            if (k === 'z') { if (e.shiftKey) I.Redo(); else I.Undo(); e.preventDefault(); return; } // Ctrl+Shift+Z = redo
            if (k === 'y') { I.Redo(); e.preventDefault(); return; }
        }
        if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? 11 : 10, 0); e.preventDefault(); return; }
        if (e.key in EK) { I.EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
        if (e.key.length === 1 && !ctrl) { I.KeyChar(e.key); e.preventDefault(); }
    });

    // Start the runtime with runMain (runs Main and STAYS RESIDENT — unlike dotnet.run(),
    // which exits after Main so later [JSExport] calls would fail) before calling any export.
    logBoot('runMain...');
    await runMain();
    logBoot('runMain ok');

    I.Init();
    logBoot('Init ok');
    canvas.tabIndex = 0;
    canvas.focus();

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
