import { readdirSync } from 'fs';
const fw = process.argv[2];
const dj = readdirSync(fw).find(f => /^dotnet(\.[a-z0-9]+)?\.js$/.test(f));
console.log('using', dj);
const { dotnet } = await import('file://' + fw + '/' + dj);
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
  exports.Interop.Init();
  console.log('STEP Init ok');
  const t0 = Date.now();
  exports.Interop.Tick(940, 720, 0);
  console.log('STEP Tick ok in ' + (Date.now()-t0) + 'ms, painted=' + painted);
  const t1 = Date.now();
  for (let i=0;i<5;i++){ exports.Interop.PointerMove(100+i, 100); exports.Interop.Tick(940,720, i+1); }
  console.log('PERF 5x(move+tick) = ' + ((Date.now()-t1)/5).toFixed(0) + 'ms avg');
  process.exit(0);
} catch (e) {
  console.log('FAIL: ' + (e && e.stack || e));
  process.exit(1);
}
