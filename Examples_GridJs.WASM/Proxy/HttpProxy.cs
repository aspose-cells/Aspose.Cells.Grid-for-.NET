using Microsoft.JSInterop;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Globalization;
using Aspose.Cells;
using Aspose.Cells.GridJs;

namespace GridJsWasm.Proxy;

public class WasmRequest
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "";
    public string Query { get; set; } = "";
    public Dictionary<string, string> Form { get; set; } = new();
    public string? FileBase64 { get; set; }
    public string FileName { get; set; } = "";
    public string BodyType { get; set; } = "json"; // json | text | bytes (informational)
}

public class WasmResponse
{
    public int Status { get; set; } = 200;
    public string ContentType { get; set; } = "text/plain";
    public string? Value { get; set; }
    public string? BytesBase64 { get; set; }

    /// <summary>
    /// Flat OPFS file name holding the response body. When set, JS reads the value straight
    /// from its own file system instead of receiving it across interop, which is what keeps
    /// big payloads (saved workbooks, image batch zips) off the WASM heap.
    /// </summary>
    public string? StorageKey { get; set; }
}

/// <summary>
/// Browser-side replacement for the GridJs MVC controller: one JS-invokable entry,
/// path-routed to IGridJsService calls, with OPFS hydrate/persist around each operation.
/// </summary>
public class HttpProxy
{
    // static hop so request.js can use DotNet.invokeMethodAsync (static interop)
    public static HttpProxy? Instance;

    private readonly GridJsService _service;
    private readonly IJSRuntime _js;
    private readonly HttpClient _http;

    private const string DefaultUid = "demo_chart_xlsx";
    private static bool _bootstrapped;

    public HttpProxy(GridJsService service, IJSRuntime js, HttpClient http)
    {
        _service = service;
        _js = js;
        _http = http;
        Instance = this;
    }

    [JSInvokable]
    public static Task<WasmResponse> HttpProxyEntry(WasmRequest req)
    {
        if (Instance == null)
        {
            return Task.FromResult(new WasmResponse { Status = 503, Value = "GridJs proxy not initialized" });
        }
        return Instance.HandleAsync(req);
    }

    /// <summary>
    /// One-time start: license from wwwroot, OPFS preload. Called by the editor page.
    /// </summary>
    [JSInvokable]
    public static async Task InitializeAsync()
    {
        var self = Instance;
        if (self == null || _bootstrapped) return;
        _bootstrapped = true;
        try
        {
            var lic = await self._http.GetByteArrayAsync("license/Aspose.Cells.lic");
            var license = new Aspose.Cells.License();
            license.SetLicense(new MemoryStream(lic));
        }
        catch
        {
            // no license in wwwroot -> evaluation mode
        }
    }

    private string F(WasmRequest r, string key) =>
        r.Form.TryGetValue(key, out var v) ? v : "";

    // Diagnostics. One AOT publish buys every later A/B: the switches let JS turn the library's
    // runtime options on and off, and the marks split a request into the phases a browser build
    // cannot be attributed to from the outside.
    private static readonly Stopwatch _clock = new();
    private static readonly List<string> _marks = new();
    private static long _markLap;

    private static void MarkStart()
    {
        _marks.Clear();
        _markLap = 0;
        _clock.Restart();
    }

    private static void Mark(string name)
    {
        long now = _clock.ElapsedMilliseconds;
        _marks.Add(name + "=" + (now - _markLap) + "ms");
        _markLap = now;
    }

    [JSInvokable]
    public static string GetMarks() => string.Join(" ", _marks);

    /// <summary>
    /// Flip runtime options by JSON, e.g. {"jsonCache":true,"jsonDisk":false,"lazy":false,"log":true}.
    /// Returns the effective settings so a measurement can state what it actually ran with.
    /// </summary>
    [JSInvokable]
    public static string SetSwitches(string json)
    {
        var switches = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json)
                       ?? new Dictionary<string, bool>();
        if (switches.TryGetValue("jsonCache", out var cache)) Config.JsonCache.Enable = cache;
        if (switches.TryGetValue("jsonDisk", out var disk)) Config.JsonCache.EnableDisk = disk;
        if (switches.TryGetValue("lazy", out var lazy)) Config.LazyLoading = lazy;
        if (switches.TryGetValue("log", out var log))
        {
            GridJsLogger.EnableConsoleOutput = log;
            GridJsLogger.Level = log ? GridJsLogLevel.Info : GridJsLogLevel.None;
        }
        return Snapshot();
    }

    [JSInvokable]
    public static string ConfigSnapshot() => Snapshot();

    private static string Snapshot() =>
        "JsonCache=" + Config.JsonCache.Enable + " Disk=" + Config.JsonCache.EnableDisk +
        " Lazy=" + Config.LazyLoading + " IsWasm=" + Config.IsWasm +
        " LimitShapes=" + Config.IslimitShapeOrImage + " MaxPic=" + Config.MaxShapeOrImageCount +
        " SameImage=" + Config.SameImageDetecting + " AutoOpt=" + Config.AutoOptimizeForLargeCells;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return d;
    }

    /// <summary>
    /// Runs one virtual request, then flushes everything the GridJs call staged. The flush is
    /// global because nested helpers save derived keys this dispatcher cannot predict (the
    /// image batch zip, the workbook written by Download) -- without it those keys would exist
    /// only in the in-memory stage and never reach OPFS for the JS-side chunked readers.
    /// </summary>
    private async Task<WasmResponse> HandleAsync(WasmRequest req)
    {
        MarkStart();
        var resp = await RouteAsync(req);
        Mark("route");
        await OpfsCacheForStream.Instance.PersistAllAsync();
        await OpfsJsonCache.Instance.PersistDirtyAsync();
        Mark("persist");
        if (resp.Value != null)
        {
            _marks.Add("chars=" + resp.Value.Length);
        }
        return resp;
    }

    private async Task<WasmResponse> RouteAsync(WasmRequest req)
    {
        var cache = OpfsCacheForStream.Instance;
        string path = req.Path.TrimStart('/');
        var q = ParseQuery(req.Query);

        string uid;
        if (q.TryGetValue("uid", out var qUid) && !string.IsNullOrEmpty(qUid)) uid = qUid;
        else if (req.Form.TryGetValue("uid", out var fUid) && !string.IsNullOrEmpty(fUid)) uid = fUid;
        else uid = DefaultUid;

        var parts = path.Split('/');
        string action = parts[^1];

        switch (action.ToLowerInvariant())
        {
            case "detailstreamjson":
            {
                string fname = q.GetValueOrDefault("filename", "chart.xlsx");
                await OpfsJsonCache.Instance.HydrateForUidAsync(uid);
                Mark("jsonHydrate");
                await cache.HydrateAsync(uid);
                Mark("hydrate");
                if (!cache.IsExisted(uid))
                {
                    await ImportAsync(fname, uid);
                    Mark("import");
                    return new WasmResponse { Status = 200, ContentType = "application/json", Value = _lastImportJson };
                }
                using var ms = new MemoryStream();
                _service.DetailStreamJsonWithUid(ms, fname, uid);
                Mark("build");
                await cache.PersistAsync(uid);
                var warm = Text(ms, "application/json");
                Mark("utf8+string");
                return warm;
            }
            case "updatecell":
            {
                SetCulture(F(req, "locale"));
                await cache.HydrateAsync(uid);
                var ret = _service.UpdateCell(F(req, "p"), uid);
                await cache.PersistAsync(uid);
                return Text(ret, "text/plain");
            }
            case "lazystreamjson":
            case "lazyloadingstreamjson":
            {
                SetCulture(F(req, "locale"));
                await OpfsJsonCache.Instance.HydrateForUidAsync(uid);
                Mark("jsonHydrate");
                await cache.HydrateAsync(uid);
                Mark("hydrate");
                using var ms = new MemoryStream();
                _service.LazyLoadingStreamJson(ms, F(req, "name"), uid);
                Mark("build");
                await cache.PersistAsync(uid);
                var lazy = Text(ms, "application/json");
                Mark("utf8+string");
                return lazy;
            }
            case "lazyloadingjson":
            {
                await OpfsJsonCache.Instance.HydrateForUidAsync(uid);
                await cache.HydrateAsync(uid);
                var ret = _service.LazyLoadingJson(F(req, "name"), uid).ToString();
                await cache.PersistAsync(uid);
                return Text(ret, "text/plain");
            }
            case "load":
            {
                await cache.HydrateAsync(uid);
                q.TryGetValue("filename", out var fname);
                var ret = _service.Load(uid, fname ?? F(req, "filename"));
                return Text(ret, "text/plain");
            }
            case "addimage":
            {
                await cache.HydrateAsync(uid);
                req.Form.TryGetValue("control", out var control);
                byte[]? imageBytes = string.IsNullOrEmpty(req.FileBase64) ? null : Convert.FromBase64String(req.FileBase64!);
                var ret = _service.AddImage(F(req, "p"), uid, control, imageBytes);
                await cache.PersistAsync(uid);
                return JsonMvc(ret);
            }
            case "copyimage":
            {
                await cache.HydrateAsync(uid);
                var ret = _service.CopyImage(F(req, "p"), uid);
                await cache.PersistAsync(uid);
                return JsonMvc(ret);
            }
            case "addimagebyurl":
            {
                return JsonMvc(ErrorJson("AddImageByURL is not available in the browser build (CORS)"));
            }
            case "image":
            {
                string fileid = q.GetValueOrDefault("id", "");
                await cache.HydrateAsync(uid + "." + fileid);
                using var s = _service.Image(uid, fileid);
                return Bytes(ReadAll(s), "image/png");
            }
            case "imageurl":
            {
                var ret = _service.ImageUrl("/GridJs2", q.GetValueOrDefault("id", ""), uid);
                return JsonMvc(ret);
            }
            case "ole":
            {
                await cache.HydrateAsync(uid);
                q.TryGetValue("id", out var oid);
                q.TryGetValue("sheet", out var sheet);
                byte[]? bytes = null;
                string? fname = null;
                try
                {
                    bytes = _service.Ole(uid, sheet ?? "", int.Parse(oid ?? "0"), out fname);
                }
                catch (Exception e)
                {
                    return Text(ErrorJson(e.Message), "application/json");
                }
                if (bytes == null || fname == null) return new WasmResponse { Status = 404 };
                return Bytes(bytes, MimeType(fname));
            }
            case "download":
            {
                await cache.HydrateAsync(uid);
                var ret = _service.Download(F(req, "p"), uid, F(req, "file"));
                await cache.PersistAsync(uid);
                var resp = JsonMvc(ret);
                // GetFileUrl points at the virtual /GetFile route; under WASM the same value
                // is an OPFS key, so hand it to JS and let the browser stream it from disk.
                string? produced = StorageUidFromUrl(ret);
                if (produced != null)
                {
                    resp.StorageKey = OpfsCacheForStream.ToStorageName(produced);
                }
                return resp;
            }
            case "getfile":
            {
                string id = q.GetValueOrDefault("id", uid);
                return await StoredAsync(id, MimeType(id));
            }
            case "getzipfile":
            {
                string zf = q.GetValueOrDefault("f", "");
                return await StoredAsync(zf, "application/zip");
            }
            case "list":
            {
                return Text("[]", "application/json");
            }
            case "import":
            {
                string fname = q.GetValueOrDefault("file", "chart.xlsx");
                string json = string.IsNullOrEmpty(req.FileBase64)
                    ? await ImportAsync(fname, uid)
                    : await ImportAsync(Convert.FromBase64String(req.FileBase64!), fname, uid);
                return Text(json, "application/json");
            }
            default:
                return new WasmResponse { Status = 404, Value = "unknown action " + action };
        }
    }

    private string _lastImportJson = "";

    /// <summary>
    /// Force finalizers to run now so we can tell whether an operation created native Skia
    /// objects, instead of guessing from whenever the GC happened to fire.
    /// </summary>
    [JSInvokable]
    public static string ForceGc(string phase)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return "gc done: " + phase;
    }

    public async Task<string> ImportAsync(string fileName, string uid)
    {
        byte[] bytes = await _http.GetByteArrayAsync("data/" + fileName);
        return await ImportAsync(bytes, fileName, uid);
    }

    /// <summary>
    /// Import already-held bytes (e.g. a workbook saved by this same browser build) so the
    /// demo can round-trip Download -> Import without any server-side storage.
    /// </summary>
    public async Task<string> ImportAsync(byte[] bytes, string fileName, string uid)
    {
        using var src = new MemoryStream(bytes);
        return await ImportAsync(src, fileName, uid);
    }

    public async Task<string> ImportAsync(Stream src, string fileName, string uid)
    {
        var cache = OpfsCacheForStream.Instance;
        // A re-uploaded workbook comes back under the same uid and the json cache keys on
        // uid+fileName, so its stored copy would keep answering for the old content.
        OpfsJsonCache.Instance.InvalidateAllForUid(uid);
        var wb = new Workbook(src);
        await cache.HydrateAsync(uid);
        var json = _service.DetailFileJsonWithUid(wb, fileName, uid).ToString();
        await cache.PersistAsync(uid);
        _lastImportJson = json;
        return json;
    }

    /// <summary>
    /// Import a workbook the browser already streamed into OPFS under UploadStorageUid(uid).
    /// Copying chunk by chunk keeps the peak at one materialised file, which new Workbook(Stream)
    /// needs regardless: xlsx is a zip read from its tail, so a forward-only reader cannot back
    /// it. The temp upload is deleted once parsed.
    /// </summary>
    [JSInvokable]
    public static async Task<string> ImportFromStorageAsync(string uid, string fileName)
    {
        var self = Instance;
        if (self == null) return ErrorJson("proxy not initialized");
        string storageUid = UploadStorageUid(uid);
        using var src = new MemoryStream();
        await OpfsCacheForStream.OpfsCopyToAsync(storageUid, src);
        string json = await self.ImportAsync(src, fileName, uid);
        await OpfsCacheForStream.Instance.DeleteAsync(storageUid);
        // This entry point is not inside HandleAsync, so the json cache's own flush would otherwise
        // wait for the next request and leave the invalidated copy readable until then.
        await OpfsJsonCache.Instance.PersistDirtyAsync();
        return json;
    }

    // The file picker stages a picked workbook here before the engine parses it. Flat (no
    // '/'), so the OPFS file name equals this uid.
    public static string UploadStorageUid(string uid) => uid + ".upload";

    private static void SetCulture(string cultureName)
    {
        if (!string.IsNullOrEmpty(cultureName))
        {
            try
            {
                var c = new CultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentCulture = c;
                CultureInfo.DefaultThreadCurrentUICulture = c;
            }
            catch (CultureNotFoundException)
            {
            }
        }
    }

    // MVC JsonResult serializes the string result, matching client double-parse behavior
    private static WasmResponse JsonMvc(string raw) =>
        new() { Status = 200, ContentType = "application/json", Value = JsonConvert.SerializeObject(raw) };

    private static WasmResponse Text(Stream s, string contentType) =>
        new() { Status = 200, ContentType = contentType, Value = System.Text.Encoding.UTF8.GetString(ReadAll(s)) };

    private static WasmResponse Text(string value, string contentType) =>
        new() { Status = 200, ContentType = contentType, Value = value };

    private static WasmResponse Bytes(byte[] bytes, string contentType) =>
        new() { Status = 200, ContentType = contentType, BytesBase64 = Convert.ToBase64String(bytes) };

    /// <summary>
    /// Response body that lives in OPFS rather than in interop: every staged key is flushed to
    /// storage before the request returns, so JS can always open it by name. Used for the
    /// payloads a browser can read directly (saved workbook, image batch zip).
    /// </summary>
    private static async Task<WasmResponse> StoredAsync(string storageUid, string contentType)
    {
        if (await OpfsCacheForStream.SizeInStorageAsync(storageUid) < 0)
        {
            return new WasmResponse { Status = 404, Value = "not in browser storage: " + storageUid };
        }
        return new WasmResponse
        {
            Status = 200,
            ContentType = contentType,
            StorageKey = OpfsCacheForStream.ToStorageName(storageUid),
        };
    }

    /// <summary>
    /// Extract the cache uid from a GetFileUrl-shaped url ("/GridJs2/GetFile?id=uid/name.xlsx").
    /// Returns null for anything that is not such a url, notably a GridJs error string.
    /// </summary>
    private static string? StorageUidFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        int i = url.IndexOf("id=", StringComparison.Ordinal);
        if (i < 0) return null;
        string id = url.Substring(i + 3);
        int amp = id.IndexOf('&');
        if (amp >= 0) id = id.Substring(0, amp);
        return Uri.UnescapeDataString(id);
    }

    private static string ErrorJson(string msg) => "{\"Error\":\"" + msg.Replace("\"", "\\\"") + "\"}";

    private static byte[] ReadAll(Stream? s)
    {
        if (s == null) return Array.Empty<byte>();
        if (s is MemoryStream m) return m.ToArray();
        using var outMs = new MemoryStream();
        s.Position = 0;
        s.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static string MimeType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream",
        };
    }
}
