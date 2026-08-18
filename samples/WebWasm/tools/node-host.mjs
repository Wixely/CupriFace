import { readdirSync } from 'fs';
const fw = process.argv[2];
const dj = readdirSync(fw).find(f => /^dotnet(\.[a-z0-9]+)?\.js$/.test(f));
console.log('using', dj);
const { dotnet } = await import('file://' + fw + '/' + dj);

// Assertions: this file is both the smoke test and the web host's touch gate. Node loads the same
// runtime and calls the same exports the page does, so a claim proven here is a claim about the
// real path — no browser required, and it can run anywhere CI can run node.
let failures = 0;
const check = (name, ok, detail = '') => {
  console.log((ok ? 'PASS  ' : 'FAIL  ') + name + (detail ? ' — ' + detail : ''));
  if (!ok) failures++;
};

try {
  const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet
      .withDiagnosticTracing(false)
      .create();
  console.log('STEP create ok');
  let painted = 0;
  setModuleImports('cupri', { present: () => { painted++; } });
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  console.log('STEP exports ok');
  await runMain();
  console.log('STEP runMain ok');
  const I = exports.Interop;
  I.Init();
  console.log('STEP Init ok');
  const t0 = Date.now();
  I.Tick(940, 720, 0);
  console.log('STEP Tick ok in ' + (Date.now()-t0) + 'ms, painted=' + painted);
  const t1 = Date.now();
  for (let i=0;i<5;i++){ I.PointerMove(100+i, 100); I.Tick(940,720, i+1); }
  console.log('PERF 5x(move+tick) = ' + ((Date.now()-t1)/5).toFixed(0) + 'ms avg');

  // ---- touch ---------------------------------------------------------------------------------
  // The web host drove fingers down the MOUSE path until 2026-08: buttons fired on touch-down and
  // lists stopped dead instead of coasting. These hold it to the touch contract the Android host
  // has had all along.
  const W = 940, H = 720;
  let clock = 100;
  const tick = (ms = 16) => { clock += ms; I.Tick(W, H, clock); };
  const frames = () => { const before = painted; tick(); return painted > before; };

  // Capability: what is driving the app reaches the document, and follows the pointer in USE
  // (a laptop with a touchscreen is honestly both).
  I.SetCoarsePointer(true);
  check('a touch reports a coarse pointer', I.IsCoarsePointer() === true);
  I.SetCoarsePointer(false);
  check('a mouse reports a fine pointer', I.IsCoarsePointer() === false);
  I.SetCoarsePointer(true);

  // Activation on RELEASE. A press that is held changes nothing but its own :active feedback;
  // what must NOT happen is the activation itself, which on the mouse path fires at down.
  I.Tick(W, H, clock);
  painted = 0;
  I.TouchDown(1, 60, 300, clock);
  tick(300);                                   // held, still, well short of long-press
  const paintedWhileHeld = painted;
  I.TouchUp(1, 60, 300, clock);
  tick();
  check('a tap activates on release, not on down', painted > paintedWhileHeld,
        'frames while held=' + paintedWhileHeld + ', after release=' + painted);

  // Momentum: a fast drag that ends still moving must keep moving after the finger is gone.
  painted = 0;
  I.TouchDown(2, 600, 600, clock);
  for (let y = 600; y >= 200; y -= 40) { I.TouchMove(2, 600, y, clock); tick(12); }
  I.TouchUp(2, 600, 200, clock);
  const movedDuringDrag = painted > 0;
  painted = 0;
  let coastFrames = 0;
  for (let i = 0; i < 12; i++) { if (frames()) coastFrames++; }
  check('a drag scrolls while the finger moves', movedDuringDrag);
  check('and coasts after the finger lifts', coastFrames > 0, coastFrames + ' frames of momentum');

  // A cancel is not a tap: the browser taking the gesture (system gesture, tab hidden) must never
  // become a click.
  painted = 0;
  I.TouchDown(3, 60, 300, clock);
  tick(120);
  const beforeCancel = painted;
  I.TouchCancel(3, clock);
  tick();
  check('a cancelled press never activates', painted <= beforeCancel + 1);

  console.log(failures === 0 ? 'ALL TOUCH CHECKS PASSED' : failures + ' TOUCH CHECK(S) FAILED');
  process.exit(failures === 0 ? 0 : 1);
} catch (e) {
  console.log('FAIL: ' + (e && e.stack || e));
  process.exit(1);
}
