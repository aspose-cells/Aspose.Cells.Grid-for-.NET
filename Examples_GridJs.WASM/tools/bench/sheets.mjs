// Sheet-level parity probe: what the engine emitted vs what the grid mounted.
import { spawn } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { buildHooks } from './hooks.mjs';

const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const [URL_, ENGINE, FILE] = process.argv.slice(2);
const PORT = Number(process.argv[5] || 9390);
const PROFILE = join(tmpdir(), `gridbench-p-sheets-${Date.now()}`);

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
      setTimeout(() => { if (this.pending.has(id)) { this.pending.delete(id); j(new Error(method + ' timeout')); } }, 300000);
    });
  }
  async eval(e, ap = false) {
    const r = await this.send('Runtime.evaluate', { expression: e, returnByValue: true, awaitPromise: ap });
    if (r.exceptionDetails) return { ERR: (r.exceptionDetails.exception?.description || r.exceptionDetails.text || '').split('\n').slice(0, 3).join(' | ') };
    return r.result.value;
  }
}

const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--no-first-run', '--no-default-browser-check', '--window-size=1440,900', 'about:blank'], { stdio: 'ignore' });
let pageWs = '';
for (let i = 0; i < 60 && !pageWs; i++) {
  await sleep(300);
  try {
    const l = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
    const p = l.find((t) => t.type === 'page');
    if (p) pageWs = p.webSocketDebuggerUrl;
  } catch (e) {}
}
const ws = new WebSocket(pageWs);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
const c = new CDP(ws);
await c.send('Page.enable'); await c.send('Runtime.enable');
await c.send('Page.addScriptToEvaluateOnNewDocument', { source: buildHooks(60000) });
await c.send('Page.navigate', { url: URL_ });
for (let i = 0; i < 240; i++) {
  await sleep(1000);
  if (await c.eval('window.__h ? window.__h.ready() : false') === true) break;
}
const mounted = await c.eval(`(function(){var x=window.__getXs();return {n:x.datas.length, names:x.datas.map(function(d){return d.name}),
  rows:x.datas.map(function(d){return d.rows && d.rows.len}), hidden:x.datas.map(function(d){return d.hidden}),
  firstLoadJsonSheets:window.__jsonSheets, heapMB:+(performance.memory.usedJSHeapSize/1048576).toFixed(1)};})()`);
let emitted = null;
if (ENGINE === 'wasm') {
  // Re-request the workbook already imported in this page, under the same uid, so the proxy
  // takes its warm branch (service-side json) instead of the cold import branch.
  emitted = await c.eval('window.__h.reloadSame()', true);
} else {
  const t = await c.eval(`fetch('/GridJs2/DetailStreamJsonWithUid?filename=${encodeURIComponent(FILE)}&uid=probe_${Date.now()}.xlsx&locale=en')
    .then(function(r){return r.text()}).then(function(s){ var j = JSON.parse(s); return {jsonSheets:(j.data||[]).length, names:j.data.map(function(d){return d.name})}; })`, true);
  emitted = t;
}
console.log(JSON.stringify({ url: URL_, engine: ENGINE, mounted, emitted }, null, 1));
chrome.kill('SIGKILL');
process.exit(0);
