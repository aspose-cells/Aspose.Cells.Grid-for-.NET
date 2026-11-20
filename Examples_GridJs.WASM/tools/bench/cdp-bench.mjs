// CDP measurement driver: the browser WASM build vs the server-side .NET Core build.
//
// One isolated headless Chrome per run, with its own user-data-dir and its process tree walked from
// the launched pid, so the memory numbers belong to this run only. Timing hooks are installed
// before any page script so both builds are measured through the same observables: the workbook
// json arriving, x-spreadsheet being constructed, and the grid reporting the sheet loaded.
//
// node tools/bench/cdp-bench.mjs --url URL --label NAME [--engine wasm|netcore] [--cell] [--shape]
//                          [--seq f1,f2] [--profile DIR] [--settle MS] [--timeout MS]
//                          [--server-port PORT] [--server-name EXE] [--chrome PATH] [--out FILE]

import { spawn, execSync } from 'node:child_process';
import { buildHooks } from './hooks.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

function arg(n, d) {
  const i = process.argv.indexOf('--' + n);
  if (i < 0) return d;
  const v = process.argv[i + 1];
  return v === undefined || v.startsWith('--') ? true : v;
}
const CHROME = String(arg('chrome', process.env.CHROME ||
  'C:/Program Files/Google/Chrome/Application/chrome.exe'));
const HERE = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = join(HERE, 'out');

const URL_ = arg('url');
const LABEL = arg('label', 'run');
const ENGINE = arg('engine', 'netcore');
const SETTLE = Number(arg('settle', 3000));
const TIMEOUT = Number(arg('timeout', 300000));
const DO_CELL = arg('cell') === true;
const DO_SHAPE = arg('shape') === true;
const SEQ = String(arg('seq', '') === true ? '' : arg('seq', '')).split(',').filter(Boolean);
const PORT = Number(arg('port', 9350));
const TEST_TIMEOUT = Number(arg('test-timeout', 150000));
const PROFILE = arg('profile', join(tmpdir(), `gridbench-p-${LABEL}-${Date.now()}`));
const OUT = arg('out', join(OUT_DIR, `${LABEL}.json`));
const EXE = arg('server-name', '');
const SRV_PORT = String(arg('server-port', '') === true ? '' : arg('server-port', ''));

const randUid = () => ('xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
  const r = (Math.random() * 16) | 0;
  return (c === 'x' ? r : (r & 3) | 8).toString(16);
})) + '.xlsx';

function ps(script, args) {
  try {
    const out = execSync(`powershell -NoProfile -ExecutionPolicy Bypass -File "${join(HERE, script)}" ${args}`,
      { encoding: 'utf8', shell: 'cmd.exe' }).trim();
    const f = out.split('|');
    return { wsMB: Number(f[0]), privMB: Number(f[1]), procs: Number(f[2]), cpuSec: Number(f[3]) };
  } catch (e) { return null; }
}
// renderer command lines do not repeat --user-data-dir, so the tree is walked from the pid
const chromeTree = () => ps('mem.ps1', `-rootPid ${chrome.pid}`);
const serverTree = () => (SRV_PORT ? ps('srv.ps1', `-port ${SRV_PORT}`) : (EXE ? ps('proc.ps1', `-name ${EXE}`) : null));


class CDP {
  constructor(ws) {
    this.ws = ws; this.id = 0; this.pending = new Map();
    ws.onmessage = (m) => {
      const x = JSON.parse(m.data);
      if (x.id && this.pending.has(x.id)) {
        const p = this.pending.get(x.id); this.pending.delete(x.id);
        x.error ? p.j(new Error(JSON.stringify(x.error))) : p.r(x.result);
      }
    };
  }
  send(method, params = {}) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((r, j) => {
      this.pending.set(id, { r, j });
      setTimeout(() => { if (this.pending.has(id)) { this.pending.delete(id); j(new Error(method + ' timeout')); } }, 240000);
    });
  }
  async eval(e, ap = false) {
    const r = await this.send('Runtime.evaluate', { expression: e, returnByValue: true, awaitPromise: ap });
    if (r.exceptionDetails) return { ERR: (r.exceptionDetails.exception?.description || r.exceptionDetails.text || '').split('\n').slice(0, 2).join(' | ') };
    return r.result.value;
  }
}

mkdirSync(OUT_DIR, { recursive: true });
const RUN_T0 = Date.now();
const uid = randUid();
const url = String(URL_).replace('__UID__', uid);
console.error(`[${LABEL}] engine=${ENGINE} uid=${uid}`);
console.error(`[${LABEL}] url=${url}`);
mkdirSync(PROFILE, { recursive: true });

const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--enable-precise-memory-info', '--no-first-run', '--no-default-browser-check',
  '--disable-background-timer-throttling', '--disable-component-update', '--window-size=1440,900',
  'about:blank',
], { stdio: 'ignore' });

let pageWs = '';
for (let i = 0; i < 120 && !pageWs; i++) {
  await sleep(400);
  try {
    const l = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
    const p = l.find((t) => t.type === 'page');
    if (p) pageWs = p.webSocketDebuggerUrl;
  } catch (e) { /* chrome still starting */ }
}
if (!pageWs) throw new Error('no CDP page target');
const ws = new WebSocket(pageWs);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
const cdp = new CDP(ws);

await cdp.send('Page.enable');
await cdp.send('Runtime.enable');
await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: buildHooks(TEST_TIMEOUT) });

const result = { label: LABEL, engine: ENGINE, url, uid, profile: PROFILE };
const log = (m) => console.error(`[${LABEL}] +${((Date.now() - RUN_T0) / 1000).toFixed(1)}s ${m}`);
result.memChromeBase = chromeTree();
result.serverBase = serverTree();
log(`chrome pid=${chrome.pid} baseMem=${JSON.stringify(result.memChromeBase)}`);
const t0 = Date.now();
await cdp.send('Page.navigate', { url });
log('navigated');

let ready = false, spins = 0;
while (Date.now() - t0 < TIMEOUT) {
  await sleep(1000);
  const r = await cdp.eval('window.__h ? window.__h.ready() : false');
  if (r === true) { ready = true; break; }
  if (++spins % 5 === 0) {
    const d = await cdp.eval('window.__h ? window.__h.diag() : "no hooks"');
    log(`waiting ready r=${JSON.stringify(r)} diag=${JSON.stringify(d).slice(0, 400)}`);
  }
}
result.ready = ready;
result.wallReadyMs = Date.now() - t0;
console.error(`[${LABEL}] ready=${ready} in ${result.wallReadyMs} ms`);
await cdp.eval('window.__patchDotNet ? window.__patchDotNet() : "no hook"');
await sleep(SETTLE);

result.memChromeAfterLoad = chromeTree();
result.serverAfterLoad = serverTree();
result.page = await cdp.eval('window.__h ? window.__h.stats() : null');
if (ENGINE === 'wasm') {
  result.engineMarksAfterLoad = await cdp.eval('window.__h.engineMarks()', true);
  result.config = await cdp.eval('window.__h.config()', true);
}
const shot = async (tag) => {
  try {
    const s = await cdp.send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(OUT_DIR, `${LABEL}-${tag}.png`), Buffer.from(s.data, 'base64'));
  } catch (e) { console.error(`screenshot ${tag}: ${e.message}`); }
};
await shot('load');

if (ready && DO_CELL) {
  const s0 = serverTree();
  result.cell = await cdp.eval('window.__h.setCell(1, 1, "=sin(1)")', true);
  await sleep(2000);
  result.memChromeAfterCell = chromeTree();
  const s1 = serverTree();
  result.serverAfterCell = s1;
  if (s0 && s1) result.serverCpuCellMs = Math.round((s1.cpuSec - s0.cpuSec) * 1000);
  console.error(`[${LABEL}] cell -> ${JSON.stringify(result.cell).slice(0, 260)}`);
  await shot('cell');
}
if (ready && DO_SHAPE) {
  const s0 = serverTree();
  result.shape = await cdp.eval('window.__h.addShape("Rectangle", 140, 70)', true);
  await sleep(2000);
  result.memChromeAfterShape = chromeTree();
  const s1 = serverTree();
  result.serverAfterShape = s1;
  if (s0 && s1) result.serverCpuShapeMs = Math.round((s1.cpuSec - s0.cpuSec) * 1000);
  console.error(`[${LABEL}] shape -> ${JSON.stringify(result.shape).slice(0, 260)}`);
  await shot('shape');
}
for (const f of SEQ) {
  const u = randUid();
  const r = await cdp.eval(`window.__h.loadMore(${JSON.stringify(f)}, ${JSON.stringify(u)})`, true);
  if (ENGINE === 'wasm') r.engineMarks = await cdp.eval('window.__h.engineMarks()', true);
  result.warmLoads = result.warmLoads || [];
  result.warmLoads.push(r);
  await sleep(2500);
  result.memAfterWarm = chromeTree();
  console.error(`[${LABEL}] warm ${f} -> ${JSON.stringify(r).slice(0, 240)}`);
}
result.memChromeFinal = chromeTree();
result.serverFinal = serverTree();
writeFileSync(OUT, JSON.stringify(result, null, 2));
console.log(JSON.stringify({
  label: LABEL, ready, wallReadyMs: result.wallReadyMs, marks: result.page && result.page.marks,
  dataReq: result.page && result.page.dataReq, heapMB: result.page && result.page.heapMB,
  memChromeAfterLoadMB: result.memChromeAfterLoad && result.memChromeAfterLoad.wsMB,
  serverAfterLoadMB: result.serverAfterLoad && result.serverAfterLoad.wsMB,
  cellMs: result.cell && result.cell.totalMs, shapeMs: result.shape && result.shape.totalMs,
}, null, 1));
try { execSync(`taskkill /F /T /PID ${chrome.pid}`, { shell: 'cmd.exe', stdio: 'ignore' }); } catch (e) { chrome.kill('SIGKILL'); }
process.exit(0);
