// CupriFace raw-WASM host — the entire "JS glue" (DESIGN.md §9.1): boot the .NET runtime,
// hand the engine a <canvas> 2D context, blit its pixels each frame, and forward pointer,
// wheel and keyboard input. No Blazor, no UI framework — just a canvas and input plumbing.
//
// This is the JS half of CupriFace.Web and ships inside that package, next to the C# half whose
// [JSImport] names it binds below. An app gets it at _content/CupriFace.Web.Mono/main.js and writes no
// JS at all. Keeping the halves in one package is the point: every name in `setModuleImports`
// below is a contract with Interop.cs, and a copied-out main.js drifts from it silently.
//
// Served from _content/CupriFace.Web.Mono/, so the runtime that the APP publishes is two levels up.
import { dotnet } from '../../_framework/dotnet.js'

const canvas = document.getElementById('cupri');
const ctx = canvas.getContext('2d');

// The canvas is opaque to assistive tech. Over it sits a transparent DOM tree mirroring the
// engine's semantics tree — the Flutter-web "semantics overlay" model. Each node is placed at its
// control's bounds (which is what gives a screen reader hit-testing, a focus ring in the right
// place and touch exploration on a phone), carries the engine's data-path (so a `click` on it —
// how a screen reader activates a control — reaches AccessibilityActivate) and tabindex="-1" (so
// DOM focus can follow the engine's and be announced, the way UIA's focus-changed event is).
// pointer-events:none keeps the browser's own hit-testing off it: a real pointer always reaches the
// canvas, while an AT's activation dispatches `click` straight to the node, which still fires.
canvas.setAttribute('aria-hidden', 'true');
const a11y = document.createElement('div');
a11y.id = 'cupri-a11y';
a11y.setAttribute('aria-live', 'polite');
a11y.style.cssText = 'position:absolute;left:0;top:0;width:0;height:0;overflow:hidden;pointer-events:none;';
document.body.appendChild(a11y);
const a11yCss = document.createElement('style');
// Transparent text rather than opacity:0 or font-size:0, which some ATs treat as hidden. No
// outline: the engine paints the focus ring itself, at the same box. Only the container clips —
// a node that clipped its children would report a popup positioned outside its parent as
// off screen, and some ATs skip those.
a11yCss.textContent = '#cupri-a11y div{position:absolute;margin:0;padding:0;white-space:nowrap;color:transparent;outline:none;}'
                    + '#cupri-a11y>div{left:0;top:0;width:100%;height:100%;}'
    // A leaf text field is mirrored as a REAL <input>/<textarea> over the painted field, so the
    // browser's own editor, IME, clipboard and password manager work on the thing they were built
    // for. It must be completely invisible: the ENGINE paints the text, the caret and the selection,
    // and a second set painted by the browser on top of them is what this hides. (Measured in a
    // browser: colour, caret-color, -webkit-text-fill-color and ::selection all have to be
    // transparent — any one of them left out shows through over the canvas.)
                    + '#cupri-a11y input,#cupri-a11y textarea{position:absolute;margin:0;padding:0;border:0;'
                    + 'background:transparent;color:transparent;caret-color:transparent;'
                    + '-webkit-text-fill-color:transparent;outline:none;resize:none;overflow:hidden;'
                    + 'box-sizing:border-box;font:inherit;}'
                    + '#cupri-a11y input::selection,#cupri-a11y textarea::selection{background:transparent;color:transparent;}';
document.head.appendChild(a11yCss);

/// A mirror node that is a real editing surface, as opposed to a described one.
const editable = el => !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA');
const editingNow = () => { const a = document.activeElement; return editable(a) && a.closest('#cupri-a11y') ? a : null; };
const placeA11y = () => {
    const r = canvas.getBoundingClientRect();
    a11y.style.left = Math.round(r.left + window.scrollX) + 'px';
    a11y.style.top = Math.round(r.top + window.scrollY) + 'px';
    a11y.style.width = Math.round(r.width) + 'px';
    a11y.style.height = Math.round(r.height) + 'px';
};

// Republish by PATCHING the live tree against the new fragment, keyed by data-path, rather than
// replacing innerHTML: a replace tears DOM focus off the node holding it on every settled frame,
// and a screen reader announces the same control again each time. (The NativeAOT host's main.js
// carries the same code; the two are twins and change together.)
let a11yHtml = '';
const sameNode = (o, n) => o.nodeType === n.nodeType &&
    (o.nodeType !== 1 || (o.getAttribute('data-path') === n.getAttribute('data-path')
                          && o.getAttribute('role') === n.getAttribute('role')
                          && o.tagName === n.tagName));   // a field that becomes multiline changes element
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
// BROWSER is authoritative and the two already agree (the engine's buffer is what the page pushed
// into it), so nothing is written and the caret never moves. When the engine rewrites a value —
// clamping a number, reformatting, committing a picked suggestion — they differ, the engine wins,
// and the caret goes back to where IT says it is (data-sel) rather than jumping to the end.
function reconcileEditors() {
    for (const el of a11y.querySelectorAll('input,textarea')) {
        // A password input is never written FROM the engine: the plaintext is deliberately absent
        // from the mirror. Whatever a password manager filled stays as the manager left it.
        if (el.type === 'password') continue;
        const want = el.tagName === 'INPUT' ? (el.getAttribute('value') || '') : el.textContent;
        if (el.value === want) continue;
        const focused = document.activeElement === el;
        el.value = want;
        if (focused) restoreSel(el);
    }
}
const restoreSel = el => {
    const raw = (el.getAttribute('data-sel') || '').split(',');
    const a = Number(raw[0]), b = Number(raw[1]);
    if (Number.isFinite(a) && Number.isFinite(b)) { try { el.setSelectionRange(a, b); } catch { /* not selectable */ } }
};

// DOM focus follows the engine's. Where the field has a real input, that is what takes focus — it
// is the editing surface, and the browser's IME, clipboard and undo all key off DOM focus. The
// hidden textarea keeps the rest: a described-only text field (a combobox CONTAINER has no input of
// its own) and a masked one, whose value is never published into the DOM, so there is nothing here
// to type into and the engine goes on owning the keystrokes.
const textRole = r => r === 'textbox' || r === 'searchbox' || r === 'combobox' || r === 'spinbutton';
let a11yFocusPath = null;   // what the engine last said holds focus — never re-announced
function syncA11yFocus() {
    const f = a11y.querySelector('[data-focused]');
    const path = f ? f.getAttribute('data-path') : null;
    if (path === a11yFocusPath) return;
    a11yFocusPath = path;
    if (!f || (textRole(f.getAttribute('role')) && !editable(f)) || f.type === 'password') { focusKbd(); return; }
    f.focus({ preventScroll: true });
    if (editable(f)) restoreSel(f);
}
function syncA11y(html) {
    if (html === a11yHtml) return;
    a11yHtml = html;
    const t = document.createElement('template');
    t.innerHTML = html;
    patchA11y(a11y, t.content, true);
    reconcileEditors();
    syncA11yFocus();
}
// Actions back to the engine, by data-path — bound once the runtime is live (below).
let a11yAct = null;
const a11yPathOf = e => { const p = e.target && e.target.closest && e.target.closest('[data-path]'); return p ? p.getAttribute('data-path') : null; };
a11y.addEventListener('click', e => {
    const path = a11yPathOf(e);
    if (path && a11yAct) { e.preventDefault(); a11yAct.activate(path); }
});
a11y.addEventListener('focusin', e => {
    // Focus arriving FROM the AT (its virtual cursor, or a user tabbing in focus mode) — as
    // opposed to the one syncA11yFocus just placed, which the engine already knows about.
    const path = a11yPathOf(e);
    if (path && a11yAct && path !== a11yFocusPath) { a11yFocusPath = path; a11yAct.focus(path); }
});
a11y.addEventListener('keydown', e => {
    // Enter/Space on a node holding DOM focus activates THAT node, and stops the window handler
    // below from also sending Enter to the engine — focus is synced, so that is the same control,
    // and the press would land twice.
    if (e.key !== 'Enter' && e.key !== ' ') return;
    if (editable(e.target)) return;          // in a text field both keys are typing, not activation
    const path = a11yPathOf(e);
    if (!path || !a11yAct) return;
    e.preventDefault(); e.stopPropagation();
    a11yAct.activate(path);
});

// ---- what a real editing element reports back ------------------------------------------------
// Delegated, because the overlay's elements are replaced as the tree changes; `input` and the
// composition events all bubble.

a11y.addEventListener('input', e => {
    const el = e.target;
    if (!editable(el) || !a11yAct) return;
    // A composition in flight is the ENGINE's preedit (it paints the underline), so the
    // composition events below carry it and these intermediate values are ignored.
    if (e.isComposing) return;
    if (document.activeElement === el) a11yAct.setEditText(el.value, el.selectionStart, el.selectionEnd);
    // A value arriving for a field nobody is editing is a FILL — a password manager completing a
    // form, which does not focus anything first. It goes through the binding, by path.
    else if (el.getAttribute('data-path')) a11yAct.setText(el.getAttribute('data-path'), el.value);
});

// The engine renders the preedit itself (underlined, at the caret), so composition keeps the seam
// it has always used rather than arriving as a run of value pushes.
a11y.addEventListener('compositionstart', e => { if (editable(e.target) && a11yAct) a11yAct.composition(''); });
a11y.addEventListener('compositionupdate', e => { if (editable(e.target) && a11yAct) a11yAct.composition(e.data || ''); });
a11y.addEventListener('compositionend', e => {
    if (!editable(e.target) || !a11yAct) return;
    a11yAct.commitComposition(e.data || '');
    // The browser's value is the truth once the IME is done with it; the engine's committed preedit
    // should equal it, and this makes certain of it.
    a11yAct.setEditText(e.target.value, e.target.selectionStart, e.target.selectionEnd);
});

// Caret and selection, wherever they came from: arrow keys, a drag, select-all, an IME moving the
// cursor. selectionchange is the only event that covers all of them.
document.addEventListener('selectionchange', () => {
    const el = editingNow();
    if (el && a11yAct) a11yAct.setEditSelection(el.selectionStart, el.selectionEnd);
});

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
    const { setModuleImports, getAssemblyExports, runMain } = await dotnet
        .withDiagnosticTracing(false)
        .create();
    logBoot('created');

    // C# → JS: `rgba` is a MemoryView over the engine's bitmap in WASM memory. `.slice()` reads
    // it into a Uint8Array in a single WASM→JS copy (no managed allocation on the .NET side —
    // bitmap.Bytes would allocate + copy 2.7 MB every frame). We reuse one ImageData per size.
    let img = null;
    const underlays = new Map(); // id -> underlaid element: a <video> the browser decodes,
                             // or a <canvas> a surface (3D viewport) draws into
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
        underlays.set(id, v);
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
        // Tab icon, from CupriApp.Icon. The page carries no favicon of its own, so create the link
        // element on first use rather than assuming index.html declared one.
        favicon: uri => {
            let link = document.querySelector("link[rel='icon']");
            if (!link) { link = document.createElement('link'); link.rel = 'icon'; document.head.appendChild(link); }
            link.href = uri;
        },
        // Context-menu clipboard (async browser clipboard). Paste reads then feeds the engine.
        clipboardWrite: text => navigator.clipboard.writeText(text).catch(() => {}),
        clipboardPaste: () => navigator.clipboard.readText().then(t => { if (t) I.KeyChar(t); }).catch(() => {}),
        // The ARIA overlay: the semantics tree, patched into the live DOM (see syncA11y above).
        a11y: syncA11y,
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

        // A canvas underlay: the same lane a video takes, but the app draws into it rather than the
        // browser. Created by the HOST so element order (beneath the engine canvas) and the
        // pointer-events rule stay the host's business rather than every 3D app's.
        underlayOpenCanvas: (id, key) => {
            canvas.style.position = "relative"; canvas.style.zIndex = "1";   // above all underlays
            const c = document.createElement("canvas");
            c.id = "cupri-underlay-" + key;   // how the app finds its own canvas
            c.style.cssText = "position:absolute;z-index:0;pointer-events:none;display:none;";
            document.body.insertBefore(c, canvas);
            underlays.set(id, c);
        },
        underlayClose: (id) => {
            const el = underlays.get(id);
            if (el) { el.remove(); underlays.delete(id); }
        },
        // Embedded/file/data: sources arrive as BYTES (resolved through the same pipeline images
        // use) and play from a Blob URL — an app's embedded clip works identically on the web.
        videoOpenBytes: (id, bytes) => {
            const url = URL.createObjectURL(new Blob([bytes.slice()], { type: 'video/webm' }));
            videoOpen(id, url);
            underlays.get(id).dataset.blobUrl = url;   // revoked on close
        },
        videoClose: id => {
            const v = underlays.get(id);
            if (v) {
                v.pause(); v.remove(); underlays.delete(id);
                if (v.dataset.blobUrl) URL.revokeObjectURL(v.dataset.blobUrl);
            }
        },
        // play() rejection (no gesture, unmuted) is expected — the 'pause'-state truth above
        // keeps the engine's controls honest, so the rejection needs no handling here.
        videoPlay: id => { underlays.get(id)?.play().catch(() => {}); },
        videoPause: id => { underlays.get(id)?.pause(); },
        videoMuted: (id, m) => { const v = underlays.get(id); if (v) v.muted = m; },
        videoVolume: (id, vol) => { const v = underlays.get(id); if (v) v.volume = vol; },
        videoLoop: (id, l) => { const v = underlays.get(id); if (v) v.loop = l; },
        videoSeek: (id, t) => { const v = underlays.get(id); if (v) v.currentTime = t; },
        // Position/size/clip in canvas pixels (backing store == CSS px here). clip-path recreates
        // the engine's scroll/overflow clipping, which a DOM element would otherwise ignore; the
        // matrix mirrors any engine transform on the chain (hover lift, transform transition) —
        // the painted hole moves through those, so the element must move identically.
        underlayRect: (id, x, y, w, h, cT, cR, cB, cL, visible, fit, ta, tb, tc, td, te, tf) => {
            const v = underlays.get(id); if (!v) return;
            if (!visible) { v.style.display = 'none'; return; }
            const r = canvas.getBoundingClientRect();
            v.style.display = '';
            v.style.left = (r.left + window.scrollX + x) + 'px';
            v.style.top = (r.top + window.scrollY + y) + 'px';
            v.style.width = w + 'px';
            v.style.height = h + 'px';
            // A <canvas> has a DRAWING BUFFER separate from its CSS box, defaulting to 300x150. A
            // <video> has none, so the video path never needed this and the seam inherited the gap:
            // the underlay was CSS-sized correctly while the app rendered into 300x150, stretched.
            // Guarded because ASSIGNING width/height RESETS the buffer — unconditionally would clear
            // the canvas every frame, after the app had drawn into it.
            if (v.tagName === 'CANVAS') {
                const bw = Math.max(1, Math.round(w)), bh = Math.max(1, Math.round(h));
                if (v.width !== bw || v.height !== bh) { v.width = bw; v.height = bh; }
            }
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

    logBoot('exports...');
    // The host's exports, not the app's: the [JSExport] surface lives in CupriFace.Web, so every
    // app boots the same host rather than owning a copy of it. (This used to read the app's
    // `config.mainAssemblyName`, back when the app WAS the host.)
    const exports = await getAssemblyExports('CupriFace.Web.Mono');
    const I = exports.CupriFace.Web.Interop;
    // Exposed for automation, as the WebLlvm host does: a browser test drives the same exports the
    // page does, rather than a parallel path that could pass while the real one is broken.
    // `isCoarse` is the UNIFORM contract both web hosts publish, so one gate can drive either
    // without knowing whether it is talking to JSExports or to Emscripten's module.
    // The overlay's way back into the engine — the same three entry points every native bridge
    // posts through. Published for automation too, so a browser test can drive them directly.
    a11yAct = {
        activate: path => I.A11yActivate(path),
        focus: path => I.A11yFocus(path),
        setValue: (path, value) => I.A11ySetValue(path, value),
        setEditText: (text, s, e) => I.SetEditText(text, s, e),
        setEditSelection: (s, e) => I.SetEditSelection(s, e),
        setText: (path, text) => I.A11ySetText(path, text),
        composition: text => I.SetComposition(text),
        commitComposition: text => I.CommitComposition(text),
    };
    globalThis.__cupri = Object.assign(globalThis.__cupri || {}, {
        I,
        isCoarse: () => I.IsCoarsePointer(),
        a11yAct,
    });
    logBoot('exports ok');

    // The browser can end fullscreen on its own (Esc goes to the BROWSER, not our key handler) —
    // tell the engine so an element-fullscreened video returns to its place in the layout.
    document.addEventListener('fullscreenchange', () => I.HostFullscreen(!!document.fullscreenElement));

    // Size the canvas backing store to its CSS box (the full window), and keep it in sync on
    // resize so Hybrid-Zoom scaling reflows to the viewport. Tick notices the size change and
    // repaints (render-on-demand).
    const sizeCanvas = () => { canvas.width = canvas.clientWidth || 940; canvas.height = canvas.clientHeight || 720; placeA11y(); };
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
        // Not while a real editing element has focus: the engine decides where focus goes next and
        // the mirror's next publish moves it there. Yanking it to the hidden textarea first closes
        // a phone's keyboard for the frame it takes to land on the field that was just tapped.
        if (!editingNow()) focusKbd();
        profile(touch(e));
        const [x, y] = at(e);
        // Capture, so a finger that slides off the canvas mid-drag still reports — otherwise a
        // scroll that leaves the element strands the gesture with no up, and the next tap
        // inherits it.
        try { canvas.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
        if (touch(e)) { I.TouchDown(e.pointerId, x, y, e.timeStamp); e.preventDefault(); }
        // LEFT button only. This used to dispatch a click for any button, so on the web a
        // right-click activated whatever was under it and THEN opened the menu — right-clicking a
        // button pressed it. The desktop host has always sent right-click to DispatchContextMenu
        // alone, so this was also a silent divergence: an app aimed by the accidental click worked
        // in a browser and did nothing on the desktop (#85). The menu's own target now arrives
        // through OnContext on every host.
        else if (e.button === 0) I.PointerDown(x, y, e.detail || 1);
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

        // A real editing element owns the keys that EDIT. The browser's editor, IME, clipboard and
        // undo are the whole point of putting one there, and forwarding those keystrokes as well
        // would apply each of them twice. What stays with the engine is everything that is not
        // editing — moving focus, dismissing, and the keys that mean something only IT knows.
        const editingEl = editingNow();
        if (editingEl) {
            if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? EK.ShiftTab : EK.Tab, mods); e.preventDefault(); return; }
            if (e.key === 'Escape') { I.EditKeyPress(EK.Escape, mods); e.preventDefault(); return; }
            // Enter is ALWAYS the engine's, in a textarea too: "Enter sends, Shift+Enter starts a
            // new line", a tag field committing a chip, a single-line field committing and blurring
            // — all of that lives in the engine, and a newline it decides to insert comes back
            // through the value it republishes. Letting the browser insert one instead would have
            // quietly turned the Showcase's submit-on-enter composer into a plain textarea.
            if (e.key === 'Enter') { I.EditKeyPress(EK.Enter, mods); e.preventDefault(); return; }
            // A combobox's list is navigated with the arrows, and the engine owns the highlight.
            if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && editingEl.parentElement?.closest('[role="combobox"]'))
            { I.EditKeyPress(EK[e.key], mods); e.preventDefault(); return; }
            // Backspace in an EMPTY tag entry takes back the last chip — an engine idiom the
            // browser has no equivalent for, and a no-op for any other empty field.
            if (e.key === 'Backspace' && editingEl.value === '' && !ctrl) { I.EditKeyPress(EK.Backspace, mods); return; }
            if (ctrl) {
                const k = e.key.toLowerCase();
                // Clipboard, undo/redo and select-all act natively ON THIS INPUT; the value and
                // selection they produce come back through `input` and selectionchange.
                if (k === 'c' || k === 'x' || k === 'v' || k === 'z' || k === 'y' || k === 'a') return;
                if (k.length === 1 && I.KeyChord(k, mods)) { e.preventDefault(); return; }
            }
            return;   // printable text, arrows, Backspace, Delete, Home/End: the browser's
        }

        if (ctrl) {
            const k = e.key.toLowerCase();
            // Let the native copy/cut/paste event fire — on the textarea, which is where the
            // listeners are: DOM focus may be sitting on an overlay node the engine focused.
            if (k === 'c' || k === 'x' || k === 'v') { focusKbd(); return; }
            if (k === 'a') { I.EditKeyPress(EK.SelectAll, 0); e.preventDefault(); return; }         // select all
            if (k === 'z') { if (e.shiftKey) I.Redo(); else I.Undo(); e.preventDefault(); return; }   // Ctrl+Shift+Z = redo
            if (k === 'y') { I.Redo(); e.preventDefault(); return; }
            // Any other Ctrl/Cmd + letter → an app shortcut (e.g. Ctrl+K). preventDefault only if the engine
            // took it, so unbound chords (Ctrl+F/P/…) still reach the browser.
            if (k.length === 1 && I.KeyChord(k, mods)) { e.preventDefault(); return; }
        }
        // Shift is carried by the KEY here (ShiftTab), not by mods — but the other modifiers
        // still have to travel, exactly as they do for every other named key on the line below.
        // Passing a literal 0 made OnShortcut(Ctrl, "Tab", …) a registration that could fire on
        // desktop and Android and never in a browser (#96).
        if (e.key === 'Tab') { I.EditKeyPress(e.shiftKey ? EK.ShiftTab : EK.Tab, mods); e.preventDefault(); return; }
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
