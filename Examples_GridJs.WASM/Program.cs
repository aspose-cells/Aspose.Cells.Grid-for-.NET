using Aspose.Cells.GridJs;
using GridJsWasm;
using GridJsWasm.Proxy;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

// The page is plain HTML+JS (wwwroot/index.html + js/demo.js); Blazor is only the .NET WASM
// launcher here, so nothing is rendered and the UI talks to the [JSInvokable] entry points below.
var builder = WebAssemblyHostBuilder.CreateDefault(args);

var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
builder.Services.AddScoped(_ => http);
// The OPFS json cache is registered but left switched off, so a request runs the rebuild by
// default and the same published bundle can turn caching on from JS (SetSwitches jsonCache).
builder.Services.AddSingleton(sp =>
{
    // the same GridJsOptions constructor the server build exposes. IsWasm skips the timeout
    // interruption thread; CacheImp installs the browser's OPFS cache because the library's
    // LocalFileCache needs a disk the browser does not have.
    var options = new GridJsOptions
    {
        BaseRouteName = "/GridJs2",
        IsCollaborative = false,
        IsWasm = true,
        CacheImp = OpfsCacheForStream.Instance,
    };
    // The service constructor copies these options onto the static Config.JsonCache field by
    // field, so an implementation assigned to Config before this point is overwritten by it.
    options.JsonCache.Implementation = OpfsJsonCache.Instance;
    return new GridJsService(options);
});

var host = builder.Build();

var jsRuntime = host.Services.GetRequiredService<IJSRuntime>();
GridJsJsBridge.SetRuntime(jsRuntime);
OpfsCacheForStream.SetJsBridge((identifier, args) => GridJsJsBridge.InvokeAsync(identifier, args));

var proxy = new HttpProxy(
    host.Services.GetRequiredService<GridJsService>(),
    jsRuntime,
    http);
_ = proxy; // HttpProxy.Instance captured in constructor

await host.RunAsync();
