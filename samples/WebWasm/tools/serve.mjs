// Tiny static file server for a *published* WebWasm build (the WASM SDK dev server only serves
// `dotnet run` output, not `dotnet publish`). Sends correct MIME types — critically
// `application/wasm` — so the browser can stream-instantiate the modules.
//
//   node tools/serve.mjs <wwwroot-dir> [port]
//
// Used by the "Web (AOT) — browser" VS Code launch config (see .vscode).
import { createServer } from 'http';
import { readFile } from 'fs';
import { join, extname, normalize } from 'path';

const root = process.argv[2];
const port = Number(process.argv[3]) || 5199;
const mime = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
    '.json': 'application/json', '.css': 'text/css', '.wasm': 'application/wasm',
    '.dat': 'application/octet-stream', '.blat': 'application/octet-stream',
    '.pdb': 'application/octet-stream', '.symbols': 'text/plain', '.ico': 'image/x-icon',
};

createServer((req, res) => {
    let p = decodeURIComponent(req.url.split('?')[0]);
    if (p === '/') p = '/index.html';
    const fp = normalize(join(root, p));
    if (!fp.startsWith(normalize(root))) { res.writeHead(403); res.end(); return; }
    readFile(fp, (err, data) => {
        if (err) { res.writeHead(404); res.end('404 ' + p); return; }
        res.writeHead(200, { 'Content-Type': mime[extname(fp)] || 'application/octet-stream' });
        res.end(data);
    });
}).listen(port, '127.0.0.1', () => console.log(`Serving ${root} on http://127.0.0.1:${port}`));
