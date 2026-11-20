export function buildHooks(TEST_TIMEOUT) {
return `
window.__log = [];
window.__marks = {};
window.__xs = null;
window.__errs = [];
var L = window.__log, M = window.__marks;
var mark = function (k) { if (M[k] === undefined) M[k] = Math.round(performance.now()); };
addEventListener('DOMContentLoaded', function () { mark('domContentLoaded'); }, false);
addEventListener('load', function () { mark('windowLoad'); }, false);
addEventListener('error', function (e) { window.__errs.push(String(e.message)); }, true);
addEventListener('unhandledrejection', function (e) { window.__errs.push('rejection: ' + e.reason); }, false);

// The wasm demo assigns window.xs; the server demo's uidload.html holds the instance in a
// top-level "let xs", which is a lexical global and not a window property. Resolve either.
window.__getXs = function () {
  if (window.__xs) return window.__xs;
  try { if (typeof xs !== 'undefined' && xs && xs.sheet) return xs; } catch (e) {}
  return null;
};
window.__jsonSheets = null;
var _wld = null;
Object.defineProperty(window, 'gridJsWasmLoadFile', { configurable: true,
  get: function () { return _wld; },
  set: function (v) {
    _wld = async function () {
      var r = await v.apply(null, arguments);
      if (r && typeof r.sheetCount !== 'undefined') window.__jsonSheets = { count: r.sheetCount, ms: r.ms };
      return r;
    };
  } });

var _wf = null;
Object.defineProperty(window, 'x_spreadsheet', { configurable: true,
  get: function () { return _wf; },
  set: function (v) {
    _wf = function () {
      var i = v.apply(null, arguments);
      window.__xs = i; mark('gridConstructed');
      try { i.on('sheet-loaded', function () { mark('sheetLoaded'); }); } catch (e) {}
      var ld = i.loadData;
      if (typeof ld === 'function') i.loadData = function (d) {
        var t = performance.now();
        var r = ld.apply(this, arguments);
        M.loadDataMs = Math.round(performance.now() - t);
        mark('loadDataReturned');
        return r;
      };
      return i;
    };
    try { Object.keys(v).forEach(function (k) { _wf[k] = v[k]; }); } catch (e) {}
  } });

function isData(u) { return String(u).indexOf('/GridJs2/Detail') >= 0; }

var XO = XMLHttpRequest.prototype.open, XSend = XMLHttpRequest.prototype.send;
XMLHttpRequest.prototype.open = function (m, u) {
  this.__e = { via: 'xhr', m: m, u: String(u), start: performance.now() };
  if (isData(this.__e.u)) mark('dataRequestStart');
  return XO.apply(this, arguments);
};
XMLHttpRequest.prototype.send = function (b) {
  var e = this.__e;
  if (e) {
    e.req = b == null ? '' : String(b).slice(0, 300);
    this.addEventListener('loadend', function () {
      e.end = performance.now(); e.status = this.status;
      e.chars = (this.responseText || '').length; e.resp = String(this.responseText).slice(0, 300);
      try { e.wire = Number(this.getResponseHeader('content-length')) || 0; } catch (x) {}
      if (isData(e.u)) mark('dataResponseEnd');
      L.push(e);
    });
  }
  return XSend.apply(this, arguments);
};

var OF = window.fetch;
window.fetch = function (i, init) {
  var u = typeof i === 'string' ? i : (i && i.url) || '';
  var e = { via: 'fetch', m: (init && init.method) || 'GET', u: String(u), start: performance.now() };
  if (init && init.body) e.req = String(init.body).slice(0, 300);
  if (isData(u)) mark('dataRequestStart');
  return OF.apply(this, arguments).then(function (r) {
    e.end = performance.now(); e.status = r.status;
    try { e.wire = Number(r.headers.get('content-length')) || 0; } catch (x) {}
    r.clone().text().then(function (t) {
      e.chars = t.length; e.resp = t.slice(0, 300);
      if (isData(u)) mark('dataResponseEnd');
      L.push(e);
    }, function () { L.push(e); });
    return r;
  }, function (err) { e.error = String(err); L.push(e); throw err; });
};

// Blazor defines window.DotNet its own way, so interop is hooked once the runtime is up.
// __patchDotNet is idempotent and the driver calls it right after the page is ready.
window.__patchDotNet = function () {
  if (!window.DotNet || !window.DotNet.invokeMethodAsync) return 'no DotNet';
  if (window.DotNet.__benchPatched) return 'already patched';
  var o = window.DotNet.invokeMethodAsync.bind(window.DotNet);
  window.DotNet.invokeMethodAsync = function (asm, method) {
    var rest = Array.prototype.slice.call(arguments, 2);
    var path = (rest[0] && rest[0].path) || '';
    var e = { via: 'interop', m: String(method), u: path, start: performance.now() };
    if (rest[0] && rest[0].form) e.req = JSON.stringify(rest[0].form).slice(0, 300);
    if (isData(path)) mark('dataRequestStart');
    return o.apply(null, [asm, method].concat(rest)).then(function (r) {
      e.end = performance.now();
      e.chars = typeof r === 'string' ? r.length : ((r && typeof r.value === 'string') ? r.value.length : 0);
      e.resp = String(typeof r === 'string' ? r : (r && r.value)).slice(0, 300);
      if (isData(path)) mark('dataResponseEnd');
      L.push(e);
      return r;
    }, function (err) { e.error = String(err); L.push(e); throw err; });
  };
  window.DotNet.__benchPatched = true;
  return 'patched';
};

window.__h = {
  ready: function () { var x = window.__getXs(); return !!(x && x.isLoaded && x.isLoaded()); },
  diag: function () {
    var x = window.__getXs();
    return { xsType: typeof x, hooked: !!window.__xs, isLoaded: x && x.isLoaded ? x.isLoaded() : null,
             rows: x && x.sheet && x.sheet.data ? x.sheet.data.rows.len : null,
             canvases: document.querySelectorAll('canvas').length,
             marks: window.__marks, logLen: window.__log.length, errs: window.__errs };
  },
  find: function (from, test) {
    for (var i = L.length - 1; i >= from; i--) if (test(L[i])) return L[i];
    for (var j = from; j < L.length; j++) if (test(L[j]) && L[j].end) return L[j];
    return null;
  },
  stats: function () {
    var x = window.__getXs();
    var d = x && x.sheet && x.sheet.data;
    var data = window.__h.find(0, function (e) { return isData(e.u); });
    return {
      marks: M,
      dataReq: data && data.end ? { via: data.via, url: data.u, ms: Math.round(data.end - data.start),
        status: data.status, chars: data.chars, wire: data.wire, at: Math.round(data.start) } : null,
      writes: L.filter(function (e) { return String(e.u).indexOf('UpdateCell') >= 0 || String(e.u).indexOf('AddImage') >= 0; })
        .map(function (e) { return { u: e.u, via: e.via, ms: e.end ? Math.round(e.end - e.start) : null, status: e.status, chars: e.chars, req: e.req, resp: e.resp }; }),
      bootStats: window.__bootStats || null,
      errs: window.__errs.slice(0, 5),
      sheets: x && x.datas ? x.datas.length : -1,
      sheetNames: x && x.datas ? x.datas.map(function (d) { return d.name; }) : null,
      jsonSheets: window.__jsonSheets,
      activeSheet: d ? d.name : null,
      rows: d ? d.rows.len : -1,
      cols: d ? d.cols.len : -1,
      shapes: d ? (d.shapes || []).length : -1,
      images: d ? (d.images || []).length : -1,
      styles: d && d.styles ? d.styles.length : -1,
      canvases: document.querySelectorAll('canvas').length,
      uid: x ? x.uniqueid : null,
      heapMB: performance.memory ? +(performance.memory.usedJSHeapSize / 1048576).toFixed(1) : -1,
      heapTotalMB: performance.memory ? +(performance.memory.totalJSHeapSize / 1048576).toFixed(1) : -1,
    };
  },
  // setCellText(...,'finished') is where the editor's Enter commit lands, and it is the call that
  // produces the UpdateCell round trip on both builds. editor.change(...) does not: it only
  // touches local state, so it would measure nothing.
  setCell: function (ri, ci, text) {
    var x = window.__getXs();
    x.setActiveCell(ri, ci);
    var n0 = L.length;
    var t = { ri: ri, ci: ci, text: text, start: performance.now(), before: JSON.stringify(x.sheet.data.getCell(ri, ci) || null) };
    x.sheet.data.setCellText(ri, ci, text, 'finished');
    return new Promise(function (res) {
      var dl = performance.now() + ${TEST_TIMEOUT};
      (function check() {
        var c = x.sheet.data.getCell(ri, ci) || {};
        var req = window.__h.find(n0, function (e) { return String(e.u).indexOf('UpdateCell') >= 0 && e.end; });
        if (req && c.text && c.text !== text) {
          t.reqMs = Math.round(req.end - req.start);
          t.via = req.via; t.status = req.status; t.resp = String(req.resp).slice(0, 200);
          t.totalMs = Math.round(req.end - t.start);
          t.after = JSON.stringify(c);
          res(t);
        } else if (performance.now() > dl) {
          t.error = 'timeout'; t.after = JSON.stringify(c);
          t.tail = L.slice(n0).map(function (e) { return e.via + ' ' + e.u + ' ' + e.status; });
          res(t);
        } else setTimeout(check, 20);
      })();
    });
  },
  addShape: function (name, w, h) {
    var x = window.__getXs();
    var arr0 = x.sheet.data.shapes || [];
    var seen = {};
    arr0.forEach(function (s) { seen[(s.id || '') + '|' + (s.tick || '')] = 1; });
    var n0 = L.length;
    var t = { name: name, w: w, h: h, before: arr0.length, start: performance.now() };
    x.sheet.canvas.imageOperationList['paste'].uploadImg(x.sheet, null, { name: name, left: 0, top: 0, w: w, h: h });
    return new Promise(function (res) {
      var dl = performance.now() + ${TEST_TIMEOUT};
      (function check() {
        var arr = x.sheet.data.shapes || [], fresh = null;
        for (var i = 0; i < arr.length; i++) {
          var k = (arr[i].id || '') + '|' + (arr[i].tick || '');
          if (arr[i].type === name && !seen[k]) { fresh = arr[i]; seen[k] = 1; break; }
        }
        if (fresh) {
          var plain = {};
          Object.keys(fresh).forEach(function (kk) { var v = fresh[kk]; if (v === null || typeof v !== 'object') plain[kk] = v; });
          var req = window.__h.find(n0, function (e) { return String(e.u).indexOf('AddImage') >= 0 && e.end; });
          t.shape = plain;
          t.reqMs = req ? Math.round(req.end - req.start) : null;
          t.via = req ? req.via : null;
          t.status = req ? req.status : null;
          t.resp = req ? String(req.resp).slice(0, 200) : null;
          t.totalMs = Math.round(performance.now() - t.start);
          t.shapesAfter = arr.length;
          res(t);
        } else if (performance.now() > dl) {
          t.error = 'timeout';
          t.tail = L.slice(n0).map(function (e) { return e.via + ' ' + e.u + ' ' + e.status; });
          res(t);
        } else setTimeout(check, 20);
      })();
    });
  },
  // same page, engine already booted, fresh uid: the marginal cost of one more workbook
  reloadSame: function () {
    var x = window.__getXs();
    var f = new URLSearchParams(location.search).get('file');
    var t0 = performance.now();
    return window.gridJsWasmLoadFile('#gridjs-demo', f, x.uniqueid).then(function (r) {
      return { file: f, uid: 'same-as-first-load', ms: Math.round(performance.now() - t0), sheets: r.sheetCount };
    });
  },
  loadMore: function (file, uid) {
    var t0 = performance.now();
    return window.gridJsWasmLoadFile('#gridjs-demo', file, uid).then(function (r) {
      return { file: file, uid: uid, ms: Math.round(performance.now() - t0), sheets: r.sheetCount,
               chars: r.chars, bootStats: r.marks, rows: window.__getXs().sheet.data.rows.len };
    });
  },
  engineMarks: function () {
    if (!window.DotNet || !window.DotNet.invokeMethodAsync) return Promise.resolve('no engine');
    return window.DotNet.invokeMethodAsync('GridJsWasm', 'GetMarks');
  },
  config: function () {
    if (!window.DotNet || !window.DotNet.invokeMethodAsync) return Promise.resolve('no engine');
    return window.DotNet.invokeMethodAsync('GridJsWasm', 'ConfigSnapshot');
  },
};
`;
}
