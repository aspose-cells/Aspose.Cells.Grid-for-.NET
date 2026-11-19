async function bootstrapEngine() {
  await DotNet.invokeMethodAsync('GridJsWasm', 'InitializeAsync');
  if (!window.x_spreadsheet) {
    throw new Error('x-spreadsheet bundle not loaded');
  }
  window.x_spreadsheet.setWasmMode(true);
}

// The one mount path shared by every "load this workbook into this selector" entry point.
function mountFromJson(selector, jsondata) {
  const prev = window.__gridJsInstances && window.__gridJsInstances[selector];
  if (prev && typeof prev.destroy === 'function') {
    try {
      prev.destroy();
    } catch (e) {
      // a half-built instance must not block the new one
    }
  }

  const uniqueid = jsondata.uniqueid;
  const fileName = jsondata.filename;
  const xs = window.x_spreadsheet(selector, {
    updateMode: 'server',
    updateUrl: '/GridJs2/UpdateCell',
    showToolbar: true,
    mode: 'edit',
    local: 'en',
    showContextmenu: true,
  }).loadData(jsondata.data);

  if (!jsondata.showtabs) {
    xs.bottombar.hide();
  }

  xs.setUniqueId(uniqueid);
  xs.setFileName(fileName);
  // imageUrl must be the *imageurl* endpoint, not *image*: fillImageSrc first getText()s
  // a URL from it, then getArrayBuffer()s that URL for the sheet's batch zip.
  xs.setImageInfo('/GridJs2/imageurl', '/GridJs2/AddImage', '/GridJs2/AddImageByURL', '/GridJs2/CopyImage', 5678);
  xs.setFileDownloadInfo('/GridJs2/Download');
  xs.setOleDownloadInfo('/GridJs2/Ole');

  // loadData() replaces xs.datas, leaving sheet.data on the empty DataProxy the
  // constructor created. Without this rebind the grid shows nothing and every manual
  // edit fails the WORKBOOK_SESSION_EXPIRED guard ("Workbook changed during editing").
  xs.setActiveSheetByName(jsondata.actname).setActiveCell(jsondata.actrow, jsondata.actcol);

  // save: POST /GridJs2/Download through the interop seam, then let the browser open the
  // produced file straight out of OPFS (no HTTP server, and no copy of the workbook on the
  // WASM heap).
  xs.__wasmSave = async function () {
    if (!xs.buffer.isFinish()) {
      alert('updating is in progress, please try later');
      return;
    }
    const payload = {
      sheetname: xs.sheet.data.name,
      actrow: xs.sheet.data.selector.ri,
      actcol: xs.sheet.data.selector.ci,
      datas: xs.getUpdateDatas(),
    };
    const d = await DotNet.invokeMethodAsync('GridJsWasm', 'HttpProxyEntry', {
      method: 'POST',
      path: '/GridJs2/Download',
      query: '',
      form: { p: payload ? JSON.stringify(payload) : '', uid: uniqueid, file: fileName },
      bodyType: 'json',
    });
    // GridJs returns the url as a JSON string literal; a plain path is left as-is.
    let ret = d.value;
    if (typeof ret === 'string' && /^["{[]/.test(ret.trim())) {
      try {
        ret = JSON.parse(ret);
      } catch (e) {
        // keep the raw value
      }
    }
    if (ret && (ret.err || ret.Error)) {
      alert((ret && (ret.err || ret.Error)) || 'save failed');
      return;
    }
    // No storage key means GetFileUrl did not come back, so the save itself failed and ret is
    // GridJs's error text. Reading the bytes route would hand the user an empty file instead.
    if (!d.storageKey) {
      alert('save failed: ' + String(ret).slice(0, 200));
      return;
    }
    const a = document.createElement('a');
    a.href = await window.gridJsOpfs_getUrl(d.storageKey);
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
  };

  window.__gridJsInstances = window.__gridJsInstances || {};
  window.__gridJsInstances[selector] = xs;
  window.xs = xs;
  return xs;
}

// Parses wwwroot/data/<fileName> under the given uid and mounts it. A fresh uid means a cold
// load with no OPFS cache hit.
window.gridJsWasmLoadFile = async function (selector, fileName, uid) {
  window.__bootStats = { pageStart: Math.round(performance.now()) };
  const mark = (k) => { window.__bootStats[k] = Math.round(performance.now()); };

  await bootstrapEngine();
  mark('afterInitialize');

  const res = await DotNet.invokeMethodAsync('GridJsWasm', 'HttpProxyEntry', {
    method: 'GET',
    path: '/GridJs2/DetailStreamJson',
    query: 'filename=' + encodeURIComponent(fileName) + '&uid=' + encodeURIComponent(uid),
    form: {},
    bodyType: 'json',
  });
  mark('afterFirstJson');
  if (res.status !== 200) {
    throw new Error('load failed: ' + res.status + ' ' + (res.value || ''));
  }
  const jsondata = JSON.parse(res.value);
  if (jsondata.Error) {
    throw new Error(jsondata.Error);
  }
  jsondata.uniqueid = jsondata.uniqueid || uid;

  const xs = mountFromJson(selector, jsondata);
  mark('afterMount');
  return {
    uniqueid: jsondata.uniqueid,
    sheetCount: (jsondata.data || []).length,
    chars: res.value.length,
    marks: window.__bootStats,
  };
};

// Boot sequence for the GridJs WASM demo page:
// 1) init .NET side (license + OPFS preload) 2) switch request.js to interop 3) load sheet json 4) mount x-spreadsheet
window.gridJsWasmInit = (selector) => window.gridJsWasmLoadFile(selector, 'SampleExcel.xlsx', 'demo_sample_xlsx');

// Upload path: the page has already staged the picked file in OPFS in chunks (gridJsOpfs_writeFile),
// so this only licenses the engine, asks it to parse that staged file, and mounts the result.
// Nothing here holds the file: the bytes cross interop 48KB at a time in both directions.
window.gridJsWasmUpload = async function (selector, uid, fileName) {
  window.__bootStats = { pageStart: Math.round(performance.now()) };
  const mark = (k) => { window.__bootStats[k] = Math.round(performance.now()); };

  await bootstrapEngine();
  mark('afterInitialize');

  const json = await DotNet.invokeMethodAsync('GridJsWasm', 'ImportFromStorageAsync', uid, fileName);
  mark('afterImport');
  const jsondata = JSON.parse(json);
  if (jsondata.Error) {
    throw new Error(jsondata.Error);
  }

  const xs = mountFromJson(selector, jsondata);
  mark('afterMount');
  return { uniqueid: jsondata.uniqueid, sheetCount: (jsondata.data || []).length };
};

window.gridJsWasmSave = () => {
  const xs = window.xs;
  return xs && xs.__wasmSave ? xs.__wasmSave() : undefined;
};
