# Browser WASM vs. server-side GridJs: a measured comparison

This document records a head-to-head measurement of the two ways to run Aspose.Cells.GridJs with
the x-spreadsheet client:

| | Build under test | Where the engine runs |
| --- | --- | --- |
| **WASM** | this repository, `GridJsWasm` published for `net8.0` with AOT | inside the browser tab |
| **SERVER** | the original server-side demo (`gridjs-demo-.netcore`, `netcoreapp3.1`, Kestrel on `http://localhost:24262`) | in an ASP.NET Core process on the same machine |

Both were driven by the same automation, against byte-identical workbooks and an identical grid
client, on one machine, with neither side caching anything the other did not also skip.

**Short version.** The browser build is correct on every workbook it was given, and it needs no
server process — but it is 2.9-5.4x slower to first render, uses 10-13x more JavaScript heap for
the same workbook, and every write path (cell edit, shape insert) costs 2.2-14.7x more wall time,
because the work that the server used to absorb now happens in the tab.

---

## 1. What is being compared

| | WASM | SERVER |
| --- | --- | --- |
| Process | none — everything is in the tab | `dotnet.exe` hosting ASP.NET Core 3.1 |
| Engine | Aspose.Cells 26.11.0, Aspose.Cells.GridJs 26.11.0, compiled AOT to wasm | same package versions, JIT on .NET Core 3.1 |
| Drawing | SkiaSharp 4.154.0 + `SkiaSharp.NativeAssets.WebAssembly` 4.154.0 linked into the image | System.Drawing on the server |
| Runtime | Blazor WebAssembly 8.0.34, `RunAOTCompilation=true`, `PublishTrimmed=true` | CLR 3.1 |
| Grid client | `xspreadsheet.js`, 10,636,387 bytes, MD5 `414bd199804c959fca5d69ccbf7b6ac3` | the same file, same MD5 |
| Transport | `DotNet.invokeMethodAsync` interop; logical `/GridJs2/*` routes resolved in wasm, no HTTP for data | XHR to `/GridJs2/*` over loopback |
| Workbook source | `wwwroot/data/` served as static files, cached in OPFS | `Models/TestConfig.cs` → `ListDir` on disk |
| Loading mode | full load (`Lazy=false`) | full load (`options.LazyLoading` commented out in `Startup.cs:79`) |
| License | **evaluation mode** — no `.lic` ships here, `InitializeAsync` falls back silently | **licensed** — `Startup.cs:191` calls `SetLicense` with an existing file |
| Engine switches at run time | `JsonCache=False Disk=True Lazy=False IsWasm=True LimitShapes=True MaxPic=100 SameImage=True AutoOpt=True` (read back from `ConfigSnapshot`) | library defaults with lazy loading off |

The client bundle matters more than it looks: the wasm build is useless without a grid client that
routes `/GridJs2/*` through interop (`setWasmMode(true)`). To stop the CDN from being a variable,
the measurement page loads the **local** copy of the bundle, and that copy is verified byte-identical
to the one the SERVER build serves. Both sides therefore ran the same JavaScript.

The licensing asymmetry is unavoidable — the SERVER demo is configured with a license file, and this
repository deliberately ships without one. Evaluation mode caps some rendering and adds notices; it
does not change how many cells are parsed or how long parsing takes, so the timing and memory numbers
stand, but §6 flags the one place where it did show up in the results.

## 2. Workbooks

Five workbooks, staged so that both sides read the same bytes. S1 and S2 are the samples that ship
with this demo; S3-S5 are large customer-grade files kept outside the repository.

| id | file name as loaded | size on disk | MD5 (first 12) | worksheets stored | grid rows x cols | sheets rendered |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | `githubdemo_SampleExcel.xlsx` | 12,747 B | `757cd1ad40f3` | 4 (Sheet1-4) | 13 x 16 | 4 |
| S2 | `githubdemo_chart.xlsx` | 27,567 B | `80eddc4ec9e5` | 2 (Sheet1-2) | 39 x 24 | 2, 5 shapes |
| S3 | `SampleExcel555.xlsx` | 1.45 MB | `40af326381df` | 8 | 128 x 45 | 8, 15 shapes |
| S4 | `deal_59.xlsx` | 2.63 MB | `11605f2862f6` | 42 | 42 x 18 | 29 |
| S5 | `claim_farmer_list_2020.xlsx` | 5.81 MB | `f240c7e877d0` | 2 | 42,029 x 21 | see §6 |

S4 declares 42 worksheets and both engines render 29 of them, so that truncation is a shared
library behaviour and not a difference between the two builds. Compressed size is not the
interesting number: what both engines have to produce is the same workbook rendered as
x-spreadsheet JSON, and the tables below report that payload in characters.

## 3. How it was measured

Automation: Node 24 driving headless Chrome 120.0.6099.71 over the Chrome DevTools Protocol.

- `--headless=new`, `--enable-precise-memory-info`, 1440x900, and **a brand-new
  `--user-data-dir` per run**, so OPFS is empty at the start of every measurement. This is
  important: the wasm cache key is derived from the file name, so a reused profile would silently
  turn cold loads into warm ones. The runs behind the tables put that profile on the same volume as
  the workbooks (`--profile DIR`, default is the system temp directory); the choice of volume turns
  out to matter for the first-load number, see §4.1.
- Hooks are installed with `Page.addScriptToEvaluateOnNewDocument`, i.e. before any page script
  runs, so both builds are observed through the same events rather than through build-specific
  instrumentation.
- One Chrome process tree per run, walked from the launched PID (`mem.ps1`), so the memory numbers
  belong to that run only and not to the browser the analyst was already using.
- The server side is sampled the same way, by resolving the process that owns the listening port
  (`srv.ps1`), before and after each load.
- Each run: navigate → poll until the grid reports the sheet loaded → settle 2-3 s → sample →
  optionally edit a cell → optionally insert a shape → dump JSON.

Phase names used in the tables:

| phase | recorded when |
| --- | --- |
| `engineUp` | WASM only: `__bootStats.pageStart` — Blazor runtime and AOT image in place, before the workbook is requested |
| `jsonReady` | WASM `afterFirstJson` / SERVER `dataResponseEnd`: the workbook JSON is complete in the tab |
| `gridBuilt` | x-spreadsheet has constructed the grid and returned from `loadData` |
| `ready` | wall-clock from navigation until the grid itself reports the sheet loaded — the number users feel |
| `transport` | SERVER only: the XHR from request sent to last byte received |
| `heap` | `performance.memory.usedJSHeapSize` after load |
| `chrome WS` | summed Working Set of the run's Chrome process tree |

Conditions held equal: same machine (Intel Core i7-10870H @ 2.20 GHz, 32 GB, Windows 10 19045),
same Chrome build and window size, same client bundle, fresh uid and fresh profile per run, full
load on both sides, both servers on loopback, `Cache-Control: no-store` from the static server so
nothing is served from the HTTP cache. The server was warmed with an extra load before the matrix
so that the first data point is not JIT warm-up (the very first cell edit measured 2,089 ms before
warming and 12 ms after — that warm-up cost belongs to the server process, not to the comparison).

## 4. Results: time

### 4.1 Load, in milliseconds

| | ready | engineUp | jsonReady | gridBuilt | payload chars | client-side parse |
| --- | --- | --- | --- | --- | --- | --- |
| **S1** WASM | 11,895 | 8,775 | 9,907 | 10,131 | 27,074 | 8 |
| **S1** SERVER | 2,224 | - | 1,186 | 1,175 | 27,197 | 23 |
| **S2** WASM | 12,309 | 8,801 | 10,331 | 10,509 | not recorded | 6 |
| **S2** SERVER | 4,234 | - | 2,648 | 2,639 | 12,243 | 41 |
| **S3** WASM | 18,455 | 8,728 | 15,831 | 16,137 | not recorded | 18 |
| **S3** SERVER | 5,059 | - | 2,833 | 2,818 | 9,214,181 | 111 |
| **S4** WASM | 17,534 | 8,632 | 14,852 | 15,085 | not recorded | 31 |
| **S4** SERVER | 4,071 | - | 2,480 | 2,441 | 2,780,249 | 135 |
| **S5** WASM | 25,578 | 8,806 | 23,859 | 24,446 | 34,975,720 | 18 |
| **S5** SERVER | 5,581 | - | 4,539 | 4,506 | 34,977,940 | 96 |

`client-side parse` is the time x-spreadsheet spends turning JSON into grid state. It is the same
code on both sides and it is never the bottleneck; on the wasm side it is 6-31 ms because the JSON
is already in memory, and on the server side it is 23-135 ms because the same bytes arrive over XHR
a little later.

Ratios, WASM relative to SERVER:

| | S1 | S2 | S3 | S4 | S5 |
| --- | --- | --- | --- | --- | --- |
| time to ready | 5.3x | 2.9x | 3.6x | 4.3x | 4.6x |
| workbook work only, WASM (`jsonReady` minus engine up) | 0.78 s | 1.14 s | 6.75 s | 5.87 s | 14.7 s |
| workbook work only, SERVER (transport) | 0.29 s | 0.42 s | 1.81 s | 1.41 s | 3.6 s |

One column needs a warning before it is read. On the wasm side `jsonReady` includes `jsonHydrate`,
the first touch of OPFS in a fresh profile, and that step is not stable: three runs of S1 on the same
machine gave 31 ms, 1,363 ms and 4,699 ms, i.e. `ready` of 11,890 / 12,893 / 15,848 ms for an
identical workbook and identical memory (heap 352.7-359.9 MB in all three). The matrix above was
taken with the profile on the same volume as the workbooks, which is the fast case; a profile created
on the system volume pays the worst of it. The SERVER build has no equivalent step. Everything else
in the tables reproduced to within a few percent.

Two things fall out of this:

1. **A fixed ~8.7 s is paid once per tab, before any workbook is touched.** `engineUp` is 8,632-8,806
   ms across all five workbooks and both delivery encodings (see §7). That is the AOT image being
   downloaded and instantiated — 112 individual `.wasm` module requests and 103 MB of uncompressed
   `_framework` payload per cold load.
2. **Per-workbook work in the browser costs 2.7-4.2x what the same library call costs on the server.**
   The gap is roughly proportional to the JSON that has to be produced: 6.7 s for S3's 9.2 MB against
   1.8 s on the server; 14.7 s for S5's 35 MB against 3.6 s. Workbooks with many shapes are worse per
   byte than their size suggests — S2 is 12 KB and still costs 1.1 s in the tab, because rasterizing
   five charts in Skia on wasm is the actual work.

### 4.2 Editing and drawing

| | cell `=sin(1)`, WASM | cell `=sin(1)`, SERVER | shape insert, WASM | shape insert, SERVER |
| --- | --- | --- | --- | --- |
| S1 | 176 ms | 12 ms | 194 ms | 25 ms |
| S2 | 392 ms | 105 ms | 98 ms | 44 ms |
| S3 | 2,526 ms | 784 ms | 1,053 ms | 406 ms |
| S4 | 2,557 ms | 994 ms | 1,014 ms | 345 ms |
| S5 | 4,232 ms | 1,780 ms | 4,038 ms | 1,339 ms |

Both sides answered every edit with the same result: the cell came back as
`0.841471` with `f: "=SIN(1)"`, and the inserted rectangle came back with a
`/GridJs2/GetFile?id=<uid>.s.1.<tick>` url that the client then drew, i.e. the write paths are
wired end to end in the browser build and not stubbed out.

The shape of these numbers is the most important part of the whole report. A cell write is not a
local edit in this architecture: the whole workbook is re-emitted by the engine in the tab, so the
cost scales with workbook size. On S5 a single keystroke costs 4.2 s. On the server build the same
write costs 1.8 s, because a licensed .NET process on real hardware does the same work about
2.4x faster — and the user's machine does not do it at all.

### 4.3 Coming back to a workbook you already opened

Re-requesting the same workbook under the same uid, in the same tab, without reloading the page:

| | first load | second request | JS heap after |
| --- | --- | --- | --- |
| WASM, S5 | 25,178 ms | 7,022 ms | 1,884 → 2,270 MB |
| SERVER, S5 | 5,633 ms | 2,659 ms | 141 → 235 MB |

The wasm side does get a benefit from its OPFS copy: the second request re-emits from the resident
session in 6.4-7.0 s instead of 13.6-14.7 s. It is still slower than the server's *first* load.

## 5. Results: memory and CPU

### 5.1 In the browser

| | WASM heap MB | SERVER heap MB | WASM chrome WS MB | SERVER chrome WS MB | WASM chrome CPU s | SERVER chrome CPU s |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | 352.7 | 30.9 | 862.6 | 543.1 | 11.94 | 6.22 |
| S2 | 377.5 | 36.6 | 926.8 | 577.3 | 12.81 | 7.41 |
| S3 | 734.2 | 66.4 | 1,238.9 | 589.4 | 20.98 | 7.92 |
| S4 | 532.3 | 45.3 | 1,046.3 | 572.6 | 18.45 | 7.16 |
| S5 | 1,884.5 | 141.5 | 2,314.0 | 663.6 | 27.72 | 7.17 |

JavaScript heap is 10-13x higher on the wasm side for every workbook. That is the expected shape:
the SERVER build holds only the rendered grid model in JS, while the WASM build holds the grid model
**plus** the .NET runtime, the AOT image, the Aspose.Cells object model, the Skia buffers and the
whole-workbook JSON, all inside the same tab.

The gap in Chrome's Working Set (1.6-3.5x) is much smaller than the heap gap because the browser
process tree on the server build is not thin either: canvas memory for the rendered grid is already
several hundred MB.

S5 is the number to plan around. 1.88 GB of JS heap for one workbook, in a single tab. Chrome's
renderer will not grow indefinitely, and a second large workbook in the same tab pushes it further
(§4.3: 2.27 GB after a re-request). Treat "the largest workbook your users open, plus headroom" as
the sizing question, and see the `LimitShapes` / `MaxPic=100` switches in `ConfigSnapshot`, which
exist to keep this bounded.

CPU moves the other way in one respect: the WASM build burns 1.7-3.9x more browser CPU and essentially
none elsewhere, while the SERVER build burns ~7 s of browser CPU and hands the rest to the server.

### 5.2 The process that disappears

For the SERVER build, the ASP.NET Core process across the five runs:

| | Working Set after | delta for this load | CPU seconds for this load |
| --- | --- | --- | --- |
| S1 | 1,296.2 MB | +2.3 MB | 0.11 s |
| S2 | 1,307.2 MB | +8.5 MB | 4.33 s |
| S3 | 1,825.0 MB | +9.5 MB | 1.36 s |
| S4 | 1,862.6 MB | +24.9 MB | 1.25 s |
| S5 | 2,440.7 MB | +231.8 MB | 4.64 s |

Working Set climbs from 1.29 GB to 2.44 GB over five different workbooks and does not come back
down: the server keeps a copy and a session per uid. CPU deltas include background work inside the
2-second sampling window, so read them as "single-digit seconds per workbook", which is the point.

For the WASM build, the "server" is a static file server: 21.2 MB Working Set, unchanged across the
matrix, 0.28-0.39 CPU seconds per run — it only ever streams bytes. That is the win this
architecture buys, and it is the reason to accept §4 and §5.1: workbook capacity stops being a
function of how many processes you can afford to run.

## 6. Correctness: did both builds render the same thing?

| | rows x cols | sheets WASM | sheets SERVER | shapes WASM | shapes SERVER | page errors |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | 13 x 16 | 4 | 4 | 0 | 0 | 0 |
| S2 | 39 x 24 | 2 | 2 | 5 | 5 | 0 |
| S3 | 128 x 45 | 8 | 8 | 15 | 15 | 0 |
| S4 | 42 x 18 | 29 | 29 | 0 | 0 | 0 |
| S5 | 42,029 x 21 | **1** | **2** | 0 | 0 | 0 |

Dimensions, sheet names and shape counts match everywhere except one cell above.

**The S5 difference is real, and it is in the engine, not in the client.** `S5` is a workbook that
was once saved by an unlicensed Aspose build, so it carries a stored worksheet literally named
`Evaluation Warning` next to `Sheet1`; both sheets are visible in `xl/workbook.xml` and neither is
hidden. The server build renders 2 sheets; the wasm build renders 1.

The proof that the client is not at fault: the JSON payload itself differs by exactly the size of
that sheet. The wasm build emitted 34,975,720 characters against the server's 34,977,940 (delta
2,220), and the loader's reported sheet count from inside the page was 1. Two code paths were
tested, because the wasm proxy has a cold-import branch (`DetailFileJsonWithUid` on a
`new Workbook(stream)`) and a warm branch (`DetailStreamJsonWithUid`): **both** return 1 sheet
(13.6 s cold, 6.4 s warm). Small workbooks are unaffected — the same build emits 4 sheets for S1
and 2 for S2 — so this is a library behaviour specific to this workbook, and it is worth reporting
to Aspose with the file.

Practically: the missing sheet is an evaluation artifact, not user data, and its loss makes the
browser build's output *cleaner*. But it is a divergence, and a divergence at 42,029 rows is exactly
the kind you do not want to discover in production, so it is recorded here rather than smoothed over.

The cell-write and shape-insert round trips produced byte-equivalent semantics on both builds
(same recalculated text, same formula, same shape descriptor with a per-uid image url), so the
interaction contract holds.

## 7. Where this measurement is weak

Read these before quoting any number.

- **Repeat runs agree on the expensive part, not on the cheap part.** Each figure is one run. For
  the large workbooks, load times repeated within about 1% (S5 `ready`: 25,776 / 25,462 / 25,578 ms)
  and memory within 0.2% (S5 heap 1,884.5 / 1,884.6 / 1,884.8 MB). For the small ones the first
  OPFS touch dominates the spread: S1 `ready` came out 11,890 / 12,893 / 15,848 ms depending on
  where the run's Chrome profile was created (§4.1). Quote the large-workbook figures with
  confidence and the small-workbook ones with a range.
- **Headless Chrome.** `--headless=new` does not paint to a screen, which removes compositing work
  from both sides equally but makes the absolute render timings optimistic.
- **Loopback, and a dev static server.** `tools/serve.py` sends `Cache-Control: no-store` and does
  **not** attach `Content-Encoding: br` to the pre-compressed assets it publishes, so every load
  transferred the full 103 MB of `_framework` payload. A production server sends the 26.5 MB of `.br` files
  instead, and this matters a lot over a real network. On loopback it barely mattered at all: the
  same measurement through a brotli-aware server gave `engineUp` 8,616 ms against 8,728-8,806 ms
  without it — the boot cost is module instantiation, not transfer. The download saving is real for
  anyone not on localhost; the boot cost in these tables is what a CPU of this class pays to bring
  up the engine either way.
- **Evaluation vs. licensed.** See §1. It did not shift the timings; it is a candidate explanation
  for the S5 sheet divergence, which is why that divergence is reported as observed rather than
  explained.
- **One machine.** A weaker CPU or a browser with a lower memory ceiling changes the absolute
  numbers, and on a machine this size S5 was already 1.88 GB of heap.
- **Full load, no lazy loading, JSON cache off on both sides.** These are worst-case cold-path
  numbers. Enabling `JsonCache` on the wasm side would move repeat loads off the engine entirely,
  and the server build has the same lazy mode available that was disabled for this test.

## 8. Reproduce it

The harness is in [`tools/bench/`](tools/bench) and needs Node 24+, Chrome, PowerShell and Python 3.
It writes results to `tools/bench/out/` and a throwaway Chrome profile per run under the temp
directory. Override the browser location with `--chrome PATH` or the `CHROME` variable.

```bash
# 1. build and serve the browser demo
dotnet workload install wasm-tools
dotnet publish -c Release
python tools/serve.py --root bin/Release/net8.0/publish/wwwroot --port 5301

# 2. serve the same grid client to the browser build, so the JS is identical on both sides
#    (copies the client into <wwwroot>/xspread/ and generates <wwwroot>/bench.html)
python tools/bench/stage_client.py --from <server-demo>/wwwroot/xspread --url http://localhost:24262

# 3. run the server-side demo (netcoreapp3.1, natively - do not roll forward, see below)
dotnet bin/Debug/netcoreapp3.1/gridjs-demo-local.netcore.dll --urls http://localhost:24262

# 4. one measurement, cold profile, no cache reuse
node tools/bench/cdp-bench.mjs --engine wasm \
     --url 'http://localhost:5301/bench.html?file=SampleExcel555.xlsx' --label s3-wasm --cell --shape
node tools/bench/cdp-bench.mjs --engine netcore \
     --url 'http://localhost:24262/xspread/uidload.html?file=SampleExcel555.xlsx&uid=__UID__' \
     --label s3-net --server-port 24262 --cell --shape

# 5. the tables
python tools/bench/table.py

# two one-off probes used in this report
node tools/bench/sheets.mjs <url> wasm|netcore <file>     # json sheet count vs mounted sheets (§6)
node tools/bench/reload.mjs <url> wasm|netcore <file>     # second request, same uid (§4.3)
python tools/bench/servebr.py --root bin/Release/net8.0/publish/wwwroot --port 5302  # §7
```

`tools/bench/` contents:

| file | role |
| --- | --- |
| `cdp-bench.mjs` | the driver: launches one isolated headless Chrome, navigates, waits for ready, samples memory, runs the cell/shape tests, writes JSON + screenshots |
| `hooks.mjs` | the document-start hooks both builds are measured through (grid construction, XHR/fetch/interop timing, `__h.setCell` / `__h.addShape` / `__h.stats`) |
| `mem.ps1` | Chrome process-tree Working Set / Private / CPU, walked from the launched PID |
| `srv.ps1`, `proc.ps1` | the same for the server process, by listening port or by image name |
| `table.py` | turns `out/*.json` into the tables |
| `stage_client.py` | local bundle + generated `bench.html` |
| `sheets.mjs`, `reload.mjs` | the parity and second-visit probes |
| `servebr.py` | static server that does attach `Content-Encoding: br`, used for the §7 boot comparison |

Notes that cost an hour each to discover:

- Run the netcoreapp3.1 demo on its own runtime. Launching it with
  `DOTNET_ROLL_FORWARD=LatestMajor` fails inside `Aspose.Cells.GridJs.Util.SimpleHelper..cctor`
  with `FileNotFoundException: System.Drawing.Common, Version=4.0.0.2`.
- `uid` in the server demo URL may be any random `.xlsx`-shaped string; it selects a fresh cached
  copy of the workbook, which is what makes the load cold. The wasm demo derives its uid from the
  file name, so cold loads there come from a fresh Chrome profile instead — `cdp-bench.mjs` makes
  one per run by default.
- `bench.html` is the published `index.html` with the CDN script tags pointed at the local bundle,
  and it keeps the `#upload-label` / `#upload-input` elements because `js/demo.js` binds them.
- Renderer command lines do not repeat `--user-data-dir`, so `mem.ps1` walks the process tree from
  the launched PID rather than matching on the command line.
- The workbooks S3-S5 are not in this repository. Copy them into the wasm demo's
  `wwwroot/data/` (served statically) and into whatever directory the server demo's `ListDir`
  points at, and confirm the MD5s match before comparing anything.

## 9. What to take from this

- The browser build **works**: identical renders on 4 of 5 workbooks, identical cell recalculation,
  identical shape round trip, zero page errors, and one workbook whose evaluation-warning worksheet
  it drops that the server build shows.
- It is **not a faster way to open a spreadsheet**. It is slower per tab (2.9-5.4x to first render,
  plus ~8.7 s of engine start before the first workbook), much hungrier in the tab (10-13x heap),
  and every write re-does whole-workbook work in the user's browser.
- What it removes is the **server**. 21 MB of static file serving and 0.3 CPU seconds replace a
  1.3-2.4 GB ASP.NET process, and workbook capacity stops being a capacity-planning question at all.
- Choose it for intranet, offline and trusted-client scenarios where a server is the expensive part
  - and where you can put a license in the client, which per the README is a distribution problem,
  not a deployment detail. Choose the server build where many workbooks, large workbooks or
  interactive editing speed matter, and where the browser should stay thin.
