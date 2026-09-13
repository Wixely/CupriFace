// CupriFace NativeAOT-LLVM host glue — the same job as WebWasm/wwwroot/main.js, minus the Mono
// runtime: exports are plain wasm functions on Module (Module._Init, Module._Tick, …) and the
// engine's imports live in imports.js (linked into dotnet.native.js at build time).

import { dotnet } from "./dotnet.js";

const canvas = document.getElementById("cupri");
const ctx = canvas.getContext("2d");

// The ARIA overlay: a transparent DOM tree over the canvas mirroring the engine's semantics tree,
// each node at its control's bounds, carrying its data-path and tabindex="-1". Same design and
// same code as CupriFace.Web.Mono's main.js, which carries the full account; the two are twins
// and change together.
canvas.setAttribute("aria-hidden", "true");
const a11y = document.createElement("div");
a11y.id = "cupri-a11y";
a11y.setAttribute("aria-live", "polite");
a11y.style.cssText = "position:absolute;left:0;top:0;width:0;height:0;overflow:hidden;pointer-events:none;";
document.body.appendChild(a11y);
const a11yCss = document.createElement("style");
a11yCss.textContent = "#cupri-a11y div{position:absolute;margin:0;padding:0;white-space:nowrap;color:transparent;outline:none;}"
                    + "#cupri-a11y>div{left:0;top:0;width:100%;height:100%;}"
    // A leaf text field is mirrored as a REAL <input>/<textarea>, completely invisible: the engine
    // paints the text, caret and selection, and this hides the second set the browser would paint
    // over them. Every one of these four properties is needed (measured in a browser).
                    + "#cupri-a11y input,#cupri-a11y textarea{position:absolute;margin:0;padding:0;border:0;"
                    + "background:transparent;color:transparent;caret-color:transparent;"
                    + "-webkit-text-fill-color:transparent;outline:none;resize:none;overflow:hidden;"
                    + "box-sizing:border-box;font:inherit;}"
                    + "#cupri-a11y input::selection,#cupri-a11y textarea::selection{background:transparent;color:transparent;}";
document.head.appendChild(a11yCss);

const editable = el => !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA");
const editingNow = () => { const a = document.activeElement; return editable(a) && a.closest("#cupri-a11y") ? a : null; };
const placeA11y = () => {
    const r = canvas.getBoundingClientRect();
    a11y.style.left = Math.round(r.left + window.scrollX) + "px";
    a11y.style.top = Math.round(r.top + window.scrollY) + "px";
    a11y.style.width = Math.round(r.width) + "px";
    a11y.style.height = Math.round(r.height) + "px";
};

// Republish by patching the live tree, keyed by data-path — never by replacing innerHTML, which
// would tear DOM focus off the node holding it on every settled frame.
let a11yHtml = "";
const sameNode = (o, n) => o.nodeType === n.nodeType &&
    (o.nodeType !== 1 || (o.getAttribute("data-path") === n.getAttribute("data-path")
                          && o.getAttribute("role") === n.getAttribute("role")
                          && o.tagName === n.tagName));
function patchA11y(live, next, isRoot) {
    if (!isRoot) {
        for (const a of [...live.attributes]) if (!next.hasAttribute(a.name)) live.removeAttribute(a.name);
        for (const a of next.attributes) if (live.getAttribute(a.name) !== a.value) live.setAttribute(a.name, a.value);
    }
    const olds = [...live.childNodes], news = [...next.childNodes];
    let i = 0;
    for (; i < news.length; i++) {
        const n = news[i], o = olds[i];
        if (o && sameNode(o, n)) { if (n.nodeType === 3) { if (o.data !== n.data) o.data = n.data; } else patchA11y(o, n, false); }
        else if (o) live.replaceChild(n, o);
        else live.appendChild(n);
    }
    for (let j = olds.length - 1; j >= i; j--) live.removeChild(olds[j]);
}
// The engine's text and the DOM's, reconciled after every patch. While a real input holds focus the
// browser is authoritative and the two already agree; when the engine rewrites a value (a clamp, a
// reformat, a picked suggestion) it wins, and the caret goes back where IT says it is.
function reconcileEditors() {
    for (const el of a11y.querySelectorAll("input,textarea")) {
        if (el.type === "password") continue;      // never written from the engine: see AriaHtml
        const want = el.tagName === "INPUT" ? (el.getAttribute("value") || "") : el.textContent;
        if (el.value === want) continue;
        const focused = document.activeElement === el;
        el.value = want;
        if (focused) restoreSel(el);
    }
}
const restoreSel = el => {
    const raw = (el.getAttribute("data-sel") || "").split(",");
    const a = Number(raw[0]), b = Number(raw[1]);
    if (Number.isFinite(a) && Number.isFinite(b)) { try { el.setSelectionRange(a, b); } catch { /* not selectable */ } }
};

// DOM focus follows the engine's. A field with a real input focuses that (it is the editing
// surface); a described-only text field and a masked one keep the hidden textarea, which is where
// the engine's own editing, IME and clipboard path lives.
const textRole = r => r === "textbox" || r === "searchbox" || r === "combobox" || r === "spinbutton";
let a11yFocusPath = null;
function syncA11yFocus() {
    const f = a11y.querySelector("[data-focused]");
    const path = f ? f.getAttribute("data-path") : null;
    if (path === a11yFocusPath) return;
    a11yFocusPath = path;
    if (!f || (textRole(f.getAttribute("role")) && !editable(f)) || f.type === "password") { focusKbd(); return; }
    f.focus({ preventScroll: true });
    if (editable(f)) restoreSel(f);
}
function syncA11y(html) {
    if (html === a11yHtml) return;
    a11yHtml = html;
    const t = document.createElement("template");
    t.innerHTML = html;
    patchA11y(a11y, t.content, true);
    reconcileEditors();
    syncA11yFocus();
}
let a11yAct = null;   // bound once the engine is live (below)
const a11yPathOf = e => { const p = e.target && e.target.closest && e.target.closest("[data-path]"); return p ? p.getAttribute("data-path") : null; };
a11y.addEventListener("click", e => {
    const path = a11yPathOf(e);
    if (path && a11yAct) { e.preventDefault(); a11yAct.activate(path); }
});
a11y.addEventListener("focusin", e => {
    const path = a11yPathOf(e);
    if (path && a11yAct && path !== a11yFocusPath) { a11yFocusPath = path; a11yAct.focus(path); }
});
a11y.addEventListener("keydown", e => {
    if (e.key !== "Enter" && e.key !== " ") return;
    if (editable(e.target)) return;          // in a text field both keys are typing, not activation
    const path = a11yPathOf(e);
    if (!path || !a11yAct) return;
    e.preventDefault(); e.stopPropagation();
    a11yAct.activate(path);
});

// ---- what a real editing element reports back (see the Mono host's main.js for the full account) --

a11y.addEventListener("input", e => {
    const el = e.target;
    if (!editable(el) || !a11yAct) return;
    if (e.isComposing) return;                    // the composition events carry it
    if (document.activeElement === el) a11yAct.setEditText(el.value, el.selectionStart, el.selectionEnd);
    else if (el.getAttribute("data-path")) a11yAct.setText(el.getAttribute("data-path"), el.value);
});

a11y.addEventListener("compositionstart", e => { if (editable(e.target) && a11yAct) a11yAct.composition(""); });
a11y.addEventListener("compositionupdate", e => { if (editable(e.target) && a11yAct) a11yAct.composition(e.data || ""); });
a11y.addEventListener("compositionend", e => {
    if (!editable(e.target) || !a11yAct) return;
    a11yAct.commitComposition(e.data || "");
    a11yAct.setEditText(e.target.value, e.target.selectionStart, e.target.selectionEnd);
});

document.addEventListener("selectionchange", () => {
    const el = editingNow();
    if (el && a11yAct) a11yAct.setEditSelection(el.selectionStart, el.selectionEnd);
});

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
        canvas, ctx, a11y, syncA11y, kbd, paints: 0,
        // JS → C# strings: write UTF-16 into the engine-owned buffer, then invoke the consumer.
        sendText: (s, entry) => {
            const ptr = M._TextBuffer(s.length + 1);
            M.stringToUTF16(s, ptr, (s.length + 1) * 2);
            M["_" + entry](s.length);
        },
        // Underlays: id -> element BELOW the canvas (a <video>, or a <canvas> a 3D surface draws into) (imports.js moves the bytes and
        // forwards transport; the element + its events live here, where the exports are in scope).
        underlays: new Map(),   // id -> underlaid element: <video> or <canvas>
        videoOpen: (id, src) => {
            canvas.style.position = "relative"; canvas.style.zIndex = "1"; // above all underlays
            const v = document.createElement("video");
            v.src = src;
            v.playsInline = true;          // iOS: never hijack into the native fullscreen player
            v.preload = "auto";
            v.style.cssText = "position:absolute;z-index:0;pointer-events:none;display:none;";
            v.addEventListener("loadedmetadata", () => M._VideoMeta(id, v.duration || 0, v.videoWidth, v.videoHeight));
            v.addEventListener("loadeddata", () => M._VideoReady(id));
            // The browser's play/pause truth (autoplay rejections included) drives the controls.
            v.addEventListener("play", () => M._VideoPlayState(id, 1));
            v.addEventListener("pause", () => M._VideoPlayState(id, 0));
            v.addEventListener("timeupdate", () => M._VideoTime(id, v.currentTime || 0));
            v.addEventListener("ended", () => M._VideoEnded(id));
            document.body.insertBefore(v, canvas);
            globalThis.__cupri.underlays.set(id, v);
            return v;
        },
    };
    window.__paints = () => globalThis.__cupri.paints; // diagnostics parity with the Mono host
    // Diagnostics handle for browser tests: the editing exports (copy/cut/paste/undo/redo) are
    // otherwise only reachable through real clipboard keystrokes, which cannot be driven from
    // automation without wedging it. Exposing the module lets a test exercise the same code paths.
    globalThis.__cupri.M = M;
    // The same automation contract the WASM host publishes, so one browser gate drives both.
    globalThis.__cupri.isCoarse = () => !!M._IsCoarsePointer();
    // The overlay's way back into the engine, and the same automation contract the Mono host
    // publishes (__cupri.a11yAct), so one browser gate drives both.
    // Strings cross the C ABI through the engine-owned buffer: write, then call with the length.
    const withText = (s, call) => {
        const ptr = M._TextBuffer(s.length + 1);
        M.stringToUTF16(s, ptr, (s.length + 1) * 2);
        call(s.length);
    };
    a11yAct = {
        activate: path => withText(path, len => M._A11yActivate(len)),
        focus: path => withText(path, len => M._A11yFocus(len)),
        setValue: (path, value) => withText(path, len => M._A11ySetValue(len, value)),
        setEditText: (text, s, e) => withText(text, len => M._SetEditText(len, s, e)),
        setEditSelection: (s, e) => M._SetEditSelection(s, e),
        // Two strings, one buffer: they arrive concatenated and are split by the path's length.
        setText: (path, text) => withText(path + text, () => M._A11ySetText(path.length, path.length + text.length)),
        composition: text => withText(text, len => M._SetComposition(len)),
        commitComposition: text => withText(text, len => M._CommitComposition(len)),
    };
    globalThis.__cupri.a11yAct = a11yAct;

    const sizeCanvas = () => { canvas.width = canvas.clientWidth || 940; canvas.height = canvas.clientHeight || 720; placeA11y(); };
    sizeCanvas();
    window.addEventListener("resize", sizeCanvas);
    const at = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

    // Without this the browser keeps every gesture for its own scrolling and pinch-zoom, and
    // pointermove stops arriving as soon as a finger travels.
    canvas.style.touchAction = "none";

    // Fingers take the RECOGNIZER (tap on release, slop, momentum, long-press); a mouse keeps the
    // desktop path. Same split as the WASM host and the Android host.
    const touch = e => e.pointerType === "touch" || e.pointerType === "pen";
    let coarse = null;
    const profile = isCoarse => {
        if (coarse === isCoarse) return;
        coarse = isCoarse;
        try { M._SetCoarsePointer(isCoarse ? 1 : 0); } catch { /* before the engine is live */ }
    };

    canvas.addEventListener("pointerdown", e => {
        // Not while a real editing element has focus — the engine decides where focus goes next,
        // and yanking it to the hidden textarea first closes a phone's keyboard for a frame.
        if (!editingNow()) focusKbd();
        profile(touch(e));
        const [x, y] = at(e);
        try { canvas.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
        if (touch(e)) { M._TouchDown(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        // LEFT button only — see the same note in CupriFace.Web.Mono's main.js. A right-click used
        // to activate whatever was under it and then open the menu, which the desktop host has
        // never done (#85).
        else if (e.button === 0) M._PointerDown(x, y, e.detail || 1);
    });
    canvas.addEventListener("contextmenu", e => {
        e.preventDefault();
        if (coarse) return;                       // touch gets the menu from long-press instead
        focusKbd(); const [x, y] = at(e); M._ContextMenu(x, y);
    });
    canvas.addEventListener("pointermove", e => {
        const [x, y] = at(e);
        if (touch(e)) { M._TouchMove(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        else M._PointerMove(x, y);
    });
    canvas.addEventListener("pointerup", e => {
        const [x, y] = at(e);
        if (touch(e)) { M._TouchUp(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        else M._PointerUp(x, y);
    });
    canvas.addEventListener("pointercancel", e => { if (touch(e)) M._TouchCancel(e.pointerId, e.timeStamp); });
    canvas.addEventListener("wheel", e => { profile(false); const [x, y] = at(e); M._Wheel(x, y, e.deltaY); e.preventDefault(); }, { passive: false });

    let EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7, ArrowUp: 8, ArrowDown: 9, Escape: 13, Tab: 10, ShiftTab: 11, SelectAll: 14 };
    // WINDOW-level, not kbd: app chords (Ctrl+K…) must beat the browser's own (address-bar search)
    // even when the hidden textarea lost focus (fresh load, returning to the tab via its title bar).
    // When kbd IS focused the same event just bubbles here — one listener either way, no double-fire.
    let live = false; // no export calls until the engine is initialised
    window.addEventListener("keydown", e => {
        if (!live) return;
        // Composing IMEs own Enter/Escape/arrows (candidate navigation); keydown fires with
        // key "Process"/keyCode 229 during composition and must be ignored wholesale.
        if (e.isComposing || e.keyCode === 229) return;
        const ctrl = e.ctrlKey || e.metaKey;
        const mods = (e.shiftKey ? 1 : 0) | (ctrl ? 2 : 0);

        // A real editing element owns the keys that EDIT; the engine keeps the rest. Same split and
        // same reasons as the Mono host's main.js, which carries the full account.
        const editingEl = editingNow();
        if (editingEl) {
            if (e.key === "Tab") { M._EditKeyPress(e.shiftKey ? EK.ShiftTab : EK.Tab, mods); e.preventDefault(); return; }
            if (e.key === "Escape") { M._EditKeyPress(EK.Escape, mods); e.preventDefault(); return; }
            if (e.key === "Enter") { M._EditKeyPress(EK.Enter, mods); e.preventDefault(); return; }
            if ((e.key === "ArrowDown" || e.key === "ArrowUp") && editingEl.parentElement?.closest("[role=\"combobox\"]"))
            { M._EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
            if (e.key === "Backspace" && editingEl.value === "" && !ctrl) { M._EditKeyPress(EK.Backspace, mods); return; }
            if (ctrl) {
                const k = e.key.toLowerCase();
                if (k === "c" || k === "x" || k === "v" || k === "z" || k === "y" || k === "a") return;
                if (k.length === 1 && M._KeyChord(k.charCodeAt(0), mods)) { e.preventDefault(); return; }
            }
            return;   // printable text, arrows, Backspace, Delete, Home/End: the browser's
        }

        if (ctrl) {
            const k = e.key.toLowerCase();
            // Native clipboard events below — on the textarea, which is where the listeners are:
            // DOM focus may be sitting on an overlay node the engine focused.
            if (k === "c" || k === "x" || k === "v") { focusKbd(); return; }
            if (k === "a") { M._EditKeyPress(EK.SelectAll, 0); e.preventDefault(); return; }
            if (k === "z") { if (e.shiftKey) M._Redo(); else M._Undo(); e.preventDefault(); return; }
            if (k === "y") { M._Redo(); e.preventDefault(); return; }
            if (k.length === 1 && M._KeyChord(k.charCodeAt(0), mods)) { e.preventDefault(); return; }
        }
        // Shift rides in the KEY (ShiftTab) rather than in mods; the rest must still travel, as
        // they do for every other named key below. A literal 0 here left Ctrl+Tab unbindable in
        // a browser while it worked on desktop and Android (#96).
        if (e.key === "Tab") { M._EditKeyPress(e.shiftKey ? EK.ShiftTab : EK.Tab, mods); e.preventDefault(); return; }
        if (e.key in EK) { M._EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
        if (e.key.length === 1 && !ctrl) { globalThis.__cupri.sendText(e.key, "KeyChar"); e.preventDefault(); }
    });

    // IME composition, streamed through the engine's composition seam (see WebWasm's main.js for
    // the full story — the semantics here are identical, only the call mechanism differs).
    kbd.addEventListener("compositionstart", () => { if (live) globalThis.__cupri.sendText("", "SetComposition"); });
    kbd.addEventListener("compositionupdate", e => { if (live) globalThis.__cupri.sendText(e.data ?? "", "SetComposition"); });
    kbd.addEventListener("compositionend", e => { if (live) { globalThis.__cupri.sendText(e.data ?? "", "CommitComposition"); keepSelected(); } });

    kbd.addEventListener("copy", e => { const p = M._CopySelection(); if (p) { e.clipboardData.setData("text/plain", M.UTF16ToString(p)); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener("cut", e => { const p = M._CutSelection(); if (p) { e.clipboardData.setData("text/plain", M.UTF16ToString(p)); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener("paste", e => { const t = e.clipboardData.getData("text/plain"); e.preventDefault(); if (t) globalThis.__cupri.sendText(t, "KeyChar"); keepSelected(); });

    M._Init();
    { const p = M._EditKeyMap(); if (p) EK = JSON.parse(M.UTF16ToString(p)); }
    logBoot("Init ok");
    live = true;
    focusKbd();
    // An opening guess so a phone gets coarse styling on the FIRST paint; a real pointer corrects it.
    try { profile(window.matchMedia("(pointer: coarse)").matches); } catch { /* ancient browser */ }
    window.addEventListener("focus", focusKbd); // clipboard events still ride the kbd element — re-arm it
    // The browser can end fullscreen on its own (its Esc never reaches our key handler) — tell
    // the engine so an element-fullscreened video returns to its place in the layout.
    document.addEventListener("fullscreenchange", () => M._HostFullscreen(document.fullscreenElement ? 1 : 0));

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
