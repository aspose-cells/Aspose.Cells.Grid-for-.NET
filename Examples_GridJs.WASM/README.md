# Aspose.Cells.GridJs running entirely in the browser

A spreadsheet editor built on **Aspose.Cells** and **Aspose.Cells.GridJs** that runs with **no
server process**. The Aspose.Cells engine, the GridJs service and the workbook bytes all live
inside an AOT-compiled Blazor WebAssembly app; the grid client is loaded from a CDN and files
persist in the browser's Origin Private File System (OPFS).

中文文档：[README.zh-CN.md](README.zh-CN.md)
· Measured against the server-side edition: [BENCHMARK.md](BENCHMARK.md)

---

## What it does

- Loads an `.xlsx` workbook and renders it as an editable grid
- Edits cells with formulas recalculated by Aspose.Cells in the browser
- Rasterizes charts and pictures with SkiaSharp linked into the wasm image
- Uploads a local workbook, staged into OPFS in 48 KB chunks
- Exports back to `.xlsx` from the File menu, served straight out of OPFS

Once the page has loaded, no request leaves the browser for data.

## Requirements

| Requirement | Notes |
| --- | --- |
| .NET 8 SDK | net8.0 is the **minimum**: SkiaSharp's wasm assets refuse earlier target frameworks. `global.json` pins `8.0.100` with `rollForward: latestMinor` |
| `wasm-tools` workload | `dotnet workload install wasm-tools`. On Windows this writes under the SDK directory, so run it elevated |
| Python 3 | runs `tools/serve.py`. Any static server that sends `Content-Type: application/wasm` works |
| A browser with OPFS | Chrome/Edge 102+, Firefox 111+, Safari 15.2+ |
| Disk and time | a cold AOT publish needs roughly 5 GB free and about 20 minutes |

## Quick start

```bash
dotnet workload install wasm-tools
dotnet publish -c Release
python tools/serve.py --root bin/Release/net8.0/publish/wwwroot --port 5301
```

Open <http://127.0.0.1:5301>. `SampleExcel.xlsx` loads automatically.

- <http://127.0.0.1:5301/?file=chart.xlsx> loads the chart sample instead
- The **upload** button loads any local workbook; re-uploading a file name replaces that
  workbook's cached copy rather than accumulating copies

Do **not** use `dotnet run`. The Blazor dev server hosts the *build* output rather than the
publish output, so it 404s all of `_framework`. The same applies to pointing `tools/serve.py`
at a build directory.

To confirm the grid client carries the wasm seam, run this in the browser console:

```js
typeof window.x_spreadsheet?.setWasmMode   // must be "function"
```

The client is loaded from unpkg, deliberately **unpinned**
(`https://unpkg.com/gridjs-spreadsheet/xspreadsheet.js` and `.css`), so a new npm publish becomes
live for every visitor as soon as the CDN refreshes — note the version you actually resolved.
`gridjs-spreadsheet@26.11.0` carries the seam, and `Aspose.Cells` / `Aspose.Cells.GridJs` 26.11.0
are on nuget.org.

## Read this before you deploy

**License placement is a go/no-go decision, not a detail.**

The demo runs in Aspose **evaluation mode** out of the box: `HttpProxy.InitializeAsync` tries to
fetch `license/Aspose.Cells.lic` and silently falls back when it is absent, which inserts an
evaluation notice worksheet and applies functional limits. If you own a license, drop
`Aspose.Cells.lic` into `wwwroot/license/` — it is a static file, so no republish is needed.
**Never commit it**: `.gitignore` blocks `wwwroot/license/*.lic` and `*.lic.enc`.

Putting the engine in the browser means the license must be in the browser too. `SetLicense` has
to run in the same process as `Workbook`, so a server cannot license on the client's behalf. AOT
compiles **code** (IL → wasm) while a license is **data** (bytes), so AOT does nothing to hide it
and is not a security or DRM boundary. The browser runtime is entirely under the end user's
control — network capture, JS memory, wasm linear-memory dumps, devtools breakpoints, interop
hooks — so any client-side secret is recoverable:

| Where the license lives | How it is obtained |
| --- | --- |
| A `wwwroot` static file | `GET /license/Aspose.Cells.lic` |
| Passed from JS into a `[JSInvokable]` method | Present in JS memory at the call site |
| Embedded as an assembly resource | The whole assembly is downloaded and cannot be trimmed |
| Encrypted at rest, decrypted inside the WASM | The plaintext exists in wasm memory at decrypt time — plus the key, which either ships to the client or sits behind a call any client can make |
| After `SetLicense` has run | In wasm linear memory; dump or breakpoint to read it |

Encrypting the file before shipping it is still worth doing, as cost-raising hygiene rather than
as a boundary: no plaintext license sits in the published `wwwroot`, so the first row above stops
working and crawlers, proxy logs and accidental commits cannot harvest a valid signed file from a
URL. Keep the `.enc` out of the repository too, and if you put the key behind a service,
authenticate and rate-limit it — per-session keys become attributable and revocable, whereas an
open endpoint with a metered license lets strangers run up your billing.

None of that changes the attacker who is already running your page. Under the Aspose EULA a
decryptable license shipped to the browser is still a license you distributed.

**So: this architecture is for intranet, offline or trusted-client scenarios only.** For
public-facing deployment use the server-side edition, where both Aspose.Cells and GridJs stay on
the server, the browser runs only the JS client, and nothing crosses the wire but JSON results.

## How it works

```
browser page (wwwroot/index.html + js/)
   │  DotNet.invokeMethodAsync('GridJsWasm', 'HttpProxyEntry', req)
   ▼
Proxy/HttpProxy.cs          routes the logical /GridJs2/* endpoints, applies the license, tracks OPFS state
   ▼
GridJsService (IsWasm=true) from Aspose.Cells.GridJs
   ▼
Aspose.Cells                drawing through SkiaSharp linked into the wasm image
```

- **There is no HTTP layer.** The `/GridJs2/*` paths above are logical routes resolved from the
  request object handed over interop by `Proxy/HttpProxy.cs`.
- **File bytes never cross interop whole.** `js/gridjs-opfs.js` stages an upload in 49,152-byte
  chunks (a multiple of 3, so each chunk's base64 decodes independently) via `writeStart` /
  `writeChunk` / `writeEnd`, with `writeAbort` on failure so no half file is left behind.
- **OPFS replaces the disk cache.** `OpfsCacheForStream` implements `GridJsOptions.CacheImp` in
  place of the library's `LocalFileCache`, which needs a filesystem the browser does not have.
- **Blazor renders nothing.** `blazor.webassembly.js` is loaded with `autostart="false"` and
  `js/demo.js` awaits `Blazor.start()`; the page is plain HTML and JS.
- **`?file=` is validated** against `^[A-Za-z0-9._-]+\.xlsx$` before use, because the value
  reaches both a query string and an OPFS key; anything else falls back to the default sample.

## Project layout

```
GridJsWasm.csproj            dependencies + AOT. The file name is the assembly name, which js/
                             hardcodes in DotNet.invokeMethodAsync('GridJsWasm', ...)
global.json                  SDK pin: 8.0.100, rollForward latestMinor
Program.cs                   Blazor launcher; GridJsOptions/GridJsService and OPFS registration
Proxy/HttpProxy.cs           route handling, license bootstrap, upload/export, runtime switches
Proxy/GridJsJsBridge.cs      static IJSRuntime holder for code outside DI
OpfsCacheForStream.cs        CacheImp implementation backed by OPFS
OpfsJsonCache.cs             whole-workbook JSON cache in OPFS (off by default)
wwwroot/index.html           the only page; CDN client, autostart="false"
wwwroot/css/demo.css         header bar and grid container layout
wwwroot/js/demo.js           page orchestration: start Blazor, load sample, upload
wwwroot/js/gridjs-opfs.js    OPFS primitives and chunked upload
wwwroot/js/gridjs-wasm-init.js  .NET bootstrap, setWasmMode, mount x-spreadsheet, export
wwwroot/data/                SampleExcel.xlsx (default), chart.xlsx (chart sample)
wwwroot/license/README.txt   where to put your own .lic; the directory ships empty of licenses
tools/serve.py               static server with correct MIME types and no-store
tools/bench/                 CDP harness behind BENCHMARK.md: driver, hooks, process samplers
```

The sample workbooks carry no personal data: the author fields and the saved-location attribute
Excel writes into `xl/workbook.xml` were emptied before inclusion, leaving cells, styles and
formulas as ordinary workbook content.

## Build notes

- **AOT is a tradeoff, not a requirement.** `SkiaSharp.NativeAssets.WebAssembly` 4.154.0 sets
  `WasmBuildNative` and adds the `libSkiaSharp.a` reference on its own, so the native library
  links with or without AOT — both configurations load and render a workbook. AOT stays enabled
  because it is the configuration this demo was measured under and because it compiles IL instead
  of interpreting it; the cost is publish time and a much larger image (`dotnet.native.wasm` is
  about 72 MiB AOT against 6.4 MiB non-AOT). To build without it, set `RunAOTCompilation=false`
  and re-test the interaction paths.
- **Do not run `dotnet build` before `dotnet publish`.** It regenerates every `.bc` and turns an
  incremental publish into a full one.
- **Editing HTML/CSS/JS needs no republish.** `tools/serve.py` sends `Cache-Control: no-store`, so
  a refresh picks changes up. Only C# changes require a publish.
- **If the build dies linking `Aspose.Cells.dll.bc`, re-run it.** The step skips completed outputs,
  so a retry usually succeeds. On a memory-tight machine add `-p:EmccExtraBitcodeLDFlags=-O1`.
- **No prebuilt output is committed**, and GitHub Pages is a poor fit: neither Pages nor
  `tools/serve.py` attaches `Content-Encoding: br` to the pre-compressed `.br` assets, so every
  visitor pays the uncompressed wasm download. Self-host behind a server that does.
- **No CI workflow ships either** — a cold AOT publish is around 20 minutes, which is outside what
  a hosted runner is good for. Build on a machine you have sized yourself.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `NU1101` during restore | 26.11.0 **is** on nuget.org — check your NuGet source configuration |
| `NETSDK1147: workload wasm-tools must be installed` | Install it. Packs present on disk does not mean installed — check `dotnet workload list` |
| `setWasmMode is not a function` | The CDN served a bundle without the wasm seam — see **Quick start** |
| Startup fails on a `.wasm` request | The server is not sending `Content-Type: application/wasm`. Use `tools/serve.py` |
| `GET /license/Aspose.Cells.lic → 404` | **Expected.** No license ships here; the app catches it and runs in evaluation mode |
| `GET /content/img/updating.gif → 404` | **Expected and cosmetic.** A path hard-coded inside the npm bundle; unused in wasm mode |
| `/GridJs2/*` requests in your HTTP log | **Not expected** — none should reach HTTP. The seam is not wired: usually a bundle without it, or `setWasmMode` never called |
| `DllNotFoundException: libSkiaSharp` on a cell edit | The Skia archive never linked. Check the TFM is net8.0+ and that `SkiaSharp.NativeAssets.WebAssembly` is referenced. This is not an AOT switch |

## Limitations

- Large workbooks are parsed in the browser and carry an out-of-memory risk: the wasm arena is
  finite and shared with the runtime.
- First load and cell writes are slower than the server-side edition, and memory use is higher —
  measured in [BENCHMARK.md](BENCHMARK.md) at 2.9-5.4x to first render and 10-13x the JavaScript
  heap for the same workbook.
- Caching is keyed on the **first 48 characters** of the file name, so two names sharing a
  48-character stem share one cache slot.
- Trimming is on with `SuppressTrimAnalysisWarnings=true`, so the survival of interop POCOs and
  generic instantiations is verified by hand rather than by compiler warnings.
- .NET 8 reached end of support in November 2026. It remains the minimum for this stack because of
  SkiaSharp's wasm assets; moving to a supported target framework is a separate follow-up.

