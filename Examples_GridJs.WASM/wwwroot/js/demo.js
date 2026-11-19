// Plain-JS host page for the GridJs WASM demo. Blazor only launches the .NET runtime
// (index.html loads _framework/blazor.webassembly.js with autostart="false"), so editing this
// file needs no republish - tools/serve.py sends wwwroot with no-store.
const GRID = '#gridjs-demo';
const SAMPLE_FILE = /^[A-Za-z0-9._-]+\.xlsx$/;

const statusEl = () => document.getElementById('status');

function setStatus(text) {
  const el = statusEl();
  if (el) {
    el.textContent = text;
  }
}

// The uid is also the OPFS file name, so it has to stay flat and short.
function uidFor(fileName) {
  const base = fileName.replace(/\.[^.]*$/, '').replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 48);
  return 'upload_' + base;
}

// ?file= picks a shipped sample. The name reaches both a query string and an OPFS key, so it
// is restricted to a flat, path-free .xlsx name; anything else falls back to the default load.
async function loadSample() {
  const requested = new URLSearchParams(location.search).get('file');
  if (requested && SAMPLE_FILE.test(requested)) {
    const uid = 'demo_' + requested.replace(/\.xlsx$/, '').slice(0, 48);
    setStatus('loading ' + requested + ' ...');
    const r = await window.gridJsWasmLoadFile(GRID, requested, uid);
    setStatus(requested + ' loaded: ' + r.sheetCount + ' sheet(s)');
    return;
  }
  setStatus('loading SampleExcel.xlsx ...');
  const r = await window.gridJsWasmInit(GRID);
  setStatus('sample loaded: ' + r.sheetCount + ' sheet(s)');
}

// Stage -> import -> mount. upload_<name> is reused for the same file name, so re-uploading
// replaces that workbook's cached copy instead of leaving another one behind in OPFS.
async function uploadAndMount(file) {
  const uid = uidFor(file.name);
  const label = document.getElementById('upload-label');
  const input = document.getElementById('upload-input');
  label.classList.add('disabled');
  try {
    setStatus('staging ' + file.name + ' ...');
    await window.gridJsOpfs_writeFile(uid + '.upload', file, (done, total) => {
      if (done % 524288 < 49152 || done === total) {
        setStatus('staging ' + file.name + ': ' + Math.round(done / 1048576) + ' / ' +
          Math.round(total / 1048576) + ' MB');
      }
    });

    setStatus('parsing ' + file.name + ' in the browser ...');
    const r = await window.gridJsWasmUpload(GRID, uid, file.name);
    setStatus(file.name + ' loaded: ' + r.sheetCount + ' sheet(s)');
  } catch (e) {
    setStatus('upload failed: ' + (e && e.message ? e.message : e));
    throw e;
  } finally {
    label.classList.remove('disabled');
    input.value = '';
  }
}

// Blazor.start() resolves once the runtime is up; index.html's autostart="false" is what makes
// that await meaningful, since nothing renders and no root component signals readiness for us.
async function boot() {
  await Blazor.start();
  document.getElementById('upload-input').addEventListener('change', (ev) => {
    const file = ev.target.files && ev.target.files[0];
    if (file) {
      uploadAndMount(file).catch(() => {});
    }
  });
  try {
    await loadSample();
  } catch (e) {
    setStatus('sample load failed: ' + (e && e.message ? e.message : e) + ' - upload a workbook');
  }
}

boot();
