// The GPU web probe's page half. ?gpu=0|1 picks the lane, ?app=shaders|shaders-crt|showcase the
// page. Results land in the <pre> and in window.__results, for a browser test to read.
import { dotnet } from './_framework/dotnet.js'

const q = new URLSearchParams(location.search);
const gpu = q.get('gpu') !== '0';
const app = q.get('app') || 'shaders';
const canvas = document.getElementById('c');
const out = document.getElementById('out');
const W = canvas.width, H = canvas.height;
const log = s => { out.textContent += '\n' + s; };
// An animation frame — or 250 ms, whichever comes first. Firefox was seen to stop delivering
// animation frames for seconds at a time once single frames ran to ~700 ms (the CPU lane), which
// starved the loop without changing what a frame costs. The timeout keeps the measurement going;
// it cannot make a lane look faster than its own frame time.
const frameOf = () => new Promise(r => {
    let done = false;
    const go = () => { if (!done) { done = true; r(performance.now()); } };
    requestAnimationFrame(go);
    setTimeout(go, 250);
});
const stats = a => {
    const s = [...a].sort((x, y) => x - y);
    const mean = s.reduce((x, y) => x + y, 0) / s.length;
    return { mean: +mean.toFixed(2), median: +s[s.length >> 1].toFixed(2), worst: +s[s.length - 1].toFixed(2), n: s.length };
};

try {
    const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet.create();

    // The CPU lane's present, exactly as the web hosts do it: one copy out of wasm memory, then
    // putImageData narrowed to the damage rect.
    let ctx2d = null, img = null;
    setModuleImports('probe', {
        present: (rgba, w, h, dx, dy, dw, dh) => {
            if (!img) img = ctx2d.createImageData(w, h);
            img.data.set(rgba.slice());
            ctx2d.putImageData(img, 0, 0, dx, dy, dw, dh);
        },
    });

    await runMain();
    const I = (await getAssemblyExports(getConfig().mainAssemblyName)).Probe;

    let g = null, renderer = 'n/a';
    if (gpu) {
        I.ExposeGl();
        const GL = globalThis.CupriGL;
        // preserveDrawingBuffer: the engine repaints only what changed, which is only valid on a
        // target that still holds last frame's pixels. stencil: Skia clips with it.
        const handle = GL.createContext(canvas, {
            alpha: 1, depth: 0, stencil: 8, antialias: 0, premultipliedAlpha: 1,
            preserveDrawingBuffer: 1, majorVersion: 2, minorVersion: 0,
            enableExtensionsByDefault: 1, explicitSwapControl: 0, renderViaOffscreenBackBuffer: 0,
        });
        if (!handle) throw new Error('no WebGL2 context');
        GL.makeContextCurrent(handle);
        g = GL.getContext(handle).GLctx;
        const dbg = g.getExtension('WEBGL_debug_renderer_info');
        renderer = (dbg ? g.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : g.getParameter(g.RENDERER))
                 + ' / ' + g.getParameter(g.VERSION);
    } else {
        ctx2d = canvas.getContext('2d');
    }

    const how = I.Init(app, gpu, W, H);
    out.textContent = `app=${app}  lane=${how}\nrenderer: ${renderer}`;
    if (how.startsWith('FAILED')) throw new Error(how);

    // Timed frames of one mode, synchronised. Stops at `count` frames or `budgetMs`, whichever
    // is first, so the slow lane cannot run for minutes.
    const timed = async (mode, count, budgetMs) => {
        const ms = [], split = [0, 0, 0, 0];
        const started = performance.now();
        while (ms.length < count && performance.now() - started < budgetMs) {
            const now = await frameOf();
            const t0 = performance.now();
            I.Frame(now / 1000, mode, true);   // true: wait for the GPU (see Probe.Frame)
            ms.push(performance.now() - t0);
            if (mode === 0) I.Timings().split(',').slice(0, 4).forEach((v, i) => split[i] += +v);
        }
        const r = stats(ms);
        if (mode === 0) r.engineSplit = {
            layout: +(split[0] / ms.length).toFixed(2), paintList: +(split[1] / ms.length).toFixed(2),
            diff: +(split[2] / ms.length).toFixed(2), raster: +(split[3] / ms.length).toFixed(2),
        };
        return r;
    };

    // Free-running: what a user would see. No readback, one frame per animation frame. Only
    // meaningful when the browser is delivering animation frames — see frameOf.
    const freeRun = async (mode, seconds) => {
        const started = performance.now();
        let n = 0;
        while (performance.now() - started < seconds * 1000) { const now = await frameOf(); I.Frame(now / 1000, mode, false); n++; }
        return +(n / ((performance.now() - started) / 1000)).toFixed(1);
    };

    for (let i = 0; i < 3; i++) { I.Frame((await frameOf()) / 1000, 1, true); }   // warm-up

    const results = { app, gpu, lane: how, renderer, width: W, height: H };
    results.animated = await timed(0, 60, 12000);      // what the host does per frame
    log('animated ' + JSON.stringify(results.animated));
    results.fullRepaint = await timed(1, 40, 12000);   // every pixel of the page
    log('fullRepaint ' + JSON.stringify(results.fullRepaint));
    results.noDraw = await timed(2, 40, 12000);        // the same walk with no pixels
    log('noDraw ' + JSON.stringify(results.noDraw));
    results.fps = await freeRun(0, 4);

    window.__results = results;
    log(JSON.stringify(results, null, 2));
    log('DONE');
} catch (err) {
    window.__results = { error: String(err && err.stack || err) };
    log('ERROR: ' + (err && err.stack || err));
}
