// CupriFace NativeAOT-LLVM host glue — the same job as WebWasm/wwwroot/main.js, minus the Mono
// runtime: exports are plain wasm functions on Module (Module._Init, Module._Tick, …) and the
// engine's imports live in imports.js (linked into dotnet.native.js at build time).

import { dotnet } from "./dotnet.js";

const canvas = document.getElementById("cupri");
const ctx = canvas.getContext("2d");

canvas.setAttribute("aria-hidden", "true");
const a11y = document.createElement("div");
a11y.id = "cupri-a11y";
a11y.setAttribute("aria-live", "polite");
a11y.style.cssText = "position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;";
document.body.appendChild(a11y);

// Hidden focused textarea owning keyboard focus + native clipboard events (same scheme as WebWasm).
const kbd = document.createElement("textarea");
kbd.id = "cupri-kbd";
kbd.setAttribute("aria-hidden", "true");
kbd.autocapitalize = "off"; kbd.autocomplete = "off"; kbd.spellcheck = false;
kbd.style.cssText = "position:absolute;top:0;left:0;width:1px;height:1px;opacity:0;border:0;padding:0;resize:none;overflow:hidden;";
kbd.value = " ";
document.body.appendChild(kbd);
const keepSelected = () => { kbd.value = " "; kbd.setSelectionRange(0, kbd.value.length); };
const focusKbd = () => { kbd.focus({ preventScroll: true }); keepSelected(); };

const bootLog = document.createElement("pre");
bootLog.id = "bootlog"; bootLog.style.display = "none";
document.body.appendChild(bootLog);
function logBoot(s) { bootLog.textContent += s + "\n"; }
window.addEventListener("error", e => logBoot("WINDOW-ERROR: " + (e.error && e.error.stack || e.message)));
window.addEventListener("unhandledrejection", e => logBoot("UNHANDLED-REJECTION: " + (e.reason && e.reason.stack || e.reason)));
for (const lvl of ["debug", "log", "trace", "warn", "info", "error"]) {
    const orig = console[lvl].bind(console);
    console[lvl] = (...a) => { try { logBoot(lvl.toUpperCase() + ": " + a.map(x => x && x.stack || String(x)).join(" ")); } catch {} orig(...a); };
}

function showError(where, err) {
    const msg = (err && (err.stack || err.message)) || String(err);
    console.error("[CupriFace] " + where, err);
    ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#b00020"; ctx.font = "14px monospace";
    ctx.fillText("CupriFace (LLVM) failed (" + where + "):", 16, 28);
    msg.split("\n").slice(0, 24).forEach((line, i) => ctx.fillText(line.slice(0, 110), 16, 52 + i * 18));
}

try {
    logBoot("create...");
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    logBoot("created");
    await runtime.runMain("WebLlvm", []);
    logBoot("runMain ok");
    const M = runtime.Module;

    // Bridge for imports.js (which runs inside the Emscripten module scope).
    globalThis.__cupri = {
        canvas, ctx, a11y, paints: 0,
        // JS → C# strings: write UTF-16 into the engine-owned buffer, then invoke the consumer.
        sendText: (s, entry) => {
            const ptr = M._TextBuffer(s.length + 1);
            M.stringToUTF16(s, ptr, (s.length + 1) * 2);
            M["_" + entry](s.length);
        },
    };
    window.__paints = () => globalThis.__cupri.paints; // diagnostics parity with the Mono host

    const sizeCanvas = () => { canvas.width = canvas.clientWidth || 940; canvas.height = canvas.clientHeight || 720; };
    sizeCanvas();
    window.addEventListener("resize", sizeCanvas);
    const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

    canvas.addEventListener("pointerdown", e => { focusKbd(); const [x, y] = at(e); M._PointerDown(x, y, e.detail || 1); });
    canvas.addEventListener("contextmenu", e => { focusKbd(); const [x, y] = at(e); M._ContextMenu(x, y); e.preventDefault(); });
    canvas.addEventListener("pointermove", e => { const [x, y] = at(e); M._PointerMove(x, y); });
    canvas.addEventListener("pointerup", e => { const [x, y] = at(e); M._PointerUp(x, y); });
    canvas.addEventListener("wheel", e => { const [x, y] = at(e); M._Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

    const EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13 };
    // WINDOW-level, not kbd: app chords (Ctrl+K…) must beat the browser's own (address-bar search)
    // even when the hidden textarea lost focus (fresh load, returning to the tab via its title bar).
    // When kbd IS focused the same event just bubbles here — one listener either way, no double-fire.
    let live = false; // no export calls until the engine is initialised
    window.addEventListener("keydown", e => {
        if (!live) return;
        const ctrl = e.ctrlKey || e.metaKey;
        const mods = (e.shiftKey ? 1 : 0) | (ctrl ? 2 : 0);
        if (ctrl) {
            const k = e.key.toLowerCase();
            if (k === "c" || k === "x" || k === "v") return; // native clipboard events below
            if (k === "a") { M._EditKeyPress(14, 0); e.preventDefault(); return; }
            if (k === "z") { if (e.shiftKey) M._Redo(); else M._Undo(); e.preventDefault(); return; }
            if (k === "y") { M._Redo(); e.preventDefault(); return; }
            if (k.length === 1 && M._KeyChord(k.charCodeAt(0), mods)) { e.preventDefault(); return; }
        }
        if (e.key === "Tab") { M._EditKeyPress(e.shiftKey ? 11 : 10, 0); e.preventDefault(); return; }
        if (e.key in EK) { M._EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
        if (e.key.length === 1 && !ctrl) { globalThis.__cupri.sendText(e.key, "KeyChar"); e.preventDefault(); }
    });

    kbd.addEventListener("copy", e => { const p = M._CopySelection(); if (p) { e.clipboardData.setData("text/plain", M.UTF16ToString(p)); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener("cut", e => { const p = M._CutSelection(); if (p) { e.clipboardData.setData("text/plain", M.UTF16ToString(p)); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener("paste", e => { const t = e.clipboardData.getData("text/plain"); e.preventDefault(); if (t) globalThis.__cupri.sendText(t, "KeyChar"); keepSelected(); });

    M._Init();
    logBoot("Init ok");
    live = true;
    focusKbd();
    window.addEventListener("focus", focusKbd); // clipboard events still ride the kbd element — re-arm it

    let firstTick = true;
    function frame(now) {
        try { M._Tick(canvas.width, canvas.height, now); if (firstTick) { firstTick = false; logBoot("Tick ok"); } }
        catch (err) { showError("Tick", err); return; }
        requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
} catch (err) {
    showError("boot", err);
}
