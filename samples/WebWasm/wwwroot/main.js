// CupriFace raw-WASM host — the entire "JS glue" (DESIGN.md §9.1): boot the .NET runtime,
// hand the engine a <canvas> 2D context, blit its pixels each frame, and forward pointer,
// wheel and keyboard input. No Blazor, no UI framework — just a canvas and input plumbing.

import { dotnet } from './_framework/dotnet.js'

const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet
    .withDiagnosticTracing(false)
    .create();

const canvas = document.getElementById('cupri');
const ctx = canvas.getContext('2d');

// C# → JS: copy the RGBA pixels the engine rendered into the 2D canvas.
setModuleImports('cupri', {
    present: (rgba, w, h) => {
        const clamped = rgba instanceof Uint8ClampedArray ? rgba : new Uint8ClampedArray(rgba);
        ctx.putImageData(new ImageData(clamped, w, h), 0, 0);
    }
});

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
const I = exports.Interop;

canvas.width = canvas.clientWidth;
canvas.height = canvas.clientHeight;

const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

// JS → C#: pointer + wheel (same hit-test/dispatch as desktop).
canvas.addEventListener('pointerdown', e => { canvas.focus(); const [x, y] = at(e); I.PointerDown(x, y); });
canvas.addEventListener('pointermove', e => { const [x, y] = at(e); I.PointerMove(x, y); });
canvas.addEventListener('pointerup',   e => { const [x, y] = at(e); I.PointerUp(x, y); });
canvas.addEventListener('wheel', e => { const [x, y] = at(e); I.Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

// Keyboard: named keys → EditKey codes (must match CupriFace.Interaction.EditKey); printable
// characters → KeyChar. Tab/arrows/Escape are prevented so they drive the app, not the browser.
const EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13 };
canvas.addEventListener('keydown', e => {
    if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? 11 : 10); e.preventDefault(); return; }
    if (e.key in EK) { I.EditKeyPress(EK[e.key]); e.preventDefault(); return; }
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey) { I.KeyChar(e.key); e.preventDefault(); }
});

I.Init();
canvas.tabIndex = 0;
canvas.focus();

// Continuous render loop: drives @keyframes (spinner) and the live Diagnostics readout, and
// reflects any input from the previous frame.
function frame() { I.RenderFrame(canvas.width, canvas.height); requestAnimationFrame(frame); }
requestAnimationFrame(frame);

// Keep the runtime resident so exported methods stay callable.
await runMain();
