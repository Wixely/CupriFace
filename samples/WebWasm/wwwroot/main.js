// CupriFace raw-WASM host — the entire "JS glue" (DESIGN.md §9.1): boot the .NET runtime,
// hand the engine a <canvas> 2D context, blit its pixels, and forward clicks. No Blazor.

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

function sizeToWindow() {
    const w = Math.max(1, canvas.clientWidth | 0);
    const h = Math.max(1, canvas.clientHeight | 0);
    canvas.width = w;
    canvas.height = h;
    return { w, h };
}

function render() {
    const { w, h } = sizeToWindow();
    exports.Interop.RenderFrame(w, h);
}

// JS → C#: forward clicks in canvas pixel coordinates.
canvas.addEventListener('click', e => {
    const r = canvas.getBoundingClientRect();
    exports.Interop.Click(e.clientX - r.left, e.clientY - r.top, canvas.width, canvas.height);
});
window.addEventListener('resize', render);

exports.Interop.Init();
render();

// Keep the runtime resident so exported methods stay callable.
await runMain();
