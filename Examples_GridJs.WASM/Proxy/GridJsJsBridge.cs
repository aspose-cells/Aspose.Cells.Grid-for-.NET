using Microsoft.JSInterop;
using GridJsWasm.Proxy;

namespace GridJsWasm.Proxy;

/// <summary>
/// Static JS interop hop used by OpfsCacheForStream.JsInvoker.
/// </summary>
public static class GridJsJsBridge
{
    private static IJSRuntime? _js;

    public static void SetRuntime(IJSRuntime js) => _js = js;

    // Blazor hands back a boxed JsonElement for arrays/objects and the raw primitive for
    // strings/numbers; OpfsCacheForStream.InvokeBridgeAsync normalizes both shapes.
    public static async Task<object?> InvokeAsync(string identifier, params object[] args)
    {
        var js = _js ?? throw new InvalidOperationException("GridJsJsBridge runtime not set");
        return await js.InvokeAsync<object?>(identifier, args);
    }
}
