#nullable disable
using Aspose.Cells.GridJs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GridJsWasm
{
    /// <summary>
    /// Stream cache for the browser WASM host, backed by the Origin Private File System (OPFS).
    /// Lives in the host project, not in Aspose.Cells.GridJs, so the shipped library assembly
    /// carries no browser-storage or JS-interop code.
    ///
    /// The GridCacheForStream contract is synchronous but OPFS is only reachable through the
    /// asynchronous JS interop bridge, so this implementation works as a staged cache:
    ///   - synchronous SaveStream/LoadStream/IsExisted/GetFileUrl run against an in-memory stage;
    ///   - the bridge hydrates the stage before a read (HydrateAsync) and persists the dirty
    ///     stage entry back to OPFS after a write (PersistAsync/PersistAllAsync).
    /// Program.cs wires the four bridge delegates and installs this instance through
    /// GridJsOptions.CacheImp; without the delegates the class degrades to a pure memory cache.
    /// </summary>
    public class OpfsCacheForStream : GridCacheForStream
    {
        /// <summary>
        /// The instance the host installs as GridJsWorkbook.CacheImp through GridJsOptions.CacheImp.
        /// </summary>
        public static OpfsCacheForStream Instance = new OpfsCacheForStream();

        /// <summary>
        /// Bridge delegate: read the whole file for uid from OPFS, null when it does not exist.
        /// </summary>
        public Func<string, Task<byte[]>> ReadBytesAsync = OpfsReadBytesAsync;

        /// <summary>
        /// Bridge delegate: write bytes to the OPFS file for uid.
        /// </summary>
        public Func<string, byte[], Task> WriteBytesAsync = OpfsWriteBytesAsync;

        /// <summary>
        /// Bridge delegate: delete the OPFS file for uid.
        /// </summary>
        public Func<string, Task> RemoveBytesAsync = OpfsRemoveBytesAsync;

        /// <summary>
        /// Bridge delegate: list all uid keys stored in OPFS.
        /// </summary>
        public Func<Task<List<string>>> ListKeysAsync = OpfsListKeysAsync;

        private readonly Dictionary<string, byte[]> _staged = new Dictionary<string, byte[]>();
        private readonly HashSet<string> _dirty = new HashSet<string>();

        /// <summary>
        /// Host-injected JS interop invoker: (identifier, args) => Task.
        /// For value results the host returns a Task&lt;object&gt; (Task&lt;byte[]&gt; and
        /// Task&lt;List&lt;string&gt;&gt; convert to it covariantly).
        /// This keeps the class free of a JSInterop assembly reference; the WASM host wires this
        /// to IJSRuntime in Program.cs before creating GridJsService.
        /// </summary>
        internal static Func<string, object[], Task> JsInvoker;

        /// <summary>
        /// Wires the JS interop invoker used by the OPFS bridge. The browser host must call this
        /// before creating GridJsService with IsWasm=true; without it the bridge fails with a
        /// clear InvalidOperationException instead of touching OPFS.
        /// </summary>
        /// <param name="invoker">(identifier, args) => Task, typically IJSRuntime.InvokeAsync.</param>
        public static void SetJsBridge(Func<string, object[], Task> invoker)
        {
            JsInvoker = invoker;
        }

        internal static async Task<T> InvokeBridgeAsync<T>(string identifier, params object[] args)
        {
            var invoker = JsInvoker;
            if (invoker == null)
            {
                throw new InvalidOperationException("OpfsCacheForStream JS invoker is not wired by the host.");
            }
            object value = await (Task<object>)invoker(identifier, args);
            if (value is T typed)
            {
                return typed;
            }
            if (value is System.Text.Json.JsonElement element)
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.Null || element.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                {
                    return default;
                }
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            if (value == null)
            {
                return default;
            }
            return (T)System.Convert.ChangeType(value, typeof(T));
        }

        internal static async Task InvokeBridgeVoidAsync(string identifier, params object[] args)
        {
            var invoker = JsInvoker;
            if (invoker == null)
            {
                throw new InvalidOperationException("OpfsCacheForStream JS invoker is not wired by the host.");
            }
            await invoker(identifier, args);
        }

        // OPFS file names must not contain '/'; uids like "uid/picid" are stored with a
        // reversible token so ListKeys can restore the exact stage key.
        private const string SLASH_TOKEN = "__sl__";

        // Multiple of 3 so every chunk is a whole base64 group and can be decoded on its
        // own, and a multiple of 4 in base64 so no chunk carries padding bytes.
        private const int BridgeChunkBytes = 49152;

        internal static string ToStorageName(string uid)
        {
            return uid == null ? null : uid.Replace("/", SLASH_TOKEN);
        }

        internal static string FromStorageName(string name)
        {
            return name == null ? null : name.Replace(SLASH_TOKEN, "/");
        }

        // The bridge hands back base64 text, so a chunk is decoded here rather than through
        // InvokeBridgeAsync<T>, whose ChangeType fallback cannot turn a string into bytes.
        private static async Task<byte[]> OpfsReadChunkAsync(string key, int offset, int length)
        {
            string b64 = await InvokeBridgeAsync<string>("gridJsOpfs_readAt", key, offset, length);
            return string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64);
        }

        /// <summary>
        /// Byte size of a stored value, or -1 when the key is absent. Lets a caller answer
        /// "is this really in the browser's storage" without reading it.
        /// </summary>
        public static async Task<long> SizeInStorageAsync(String uid)
        {
            return await InvokeBridgeAsync<long>("gridJsOpfs_size", ToStorageName(uid));
        }

        // Reads are chunked because a whole-file base64 string costs 1.37x the value in
        // UTF-16 chars on top of the decoded array, and the WASM heap is the constrained one.
        internal static async Task<byte[]> OpfsReadBytesAsync(string uid)
        {
            string key = ToStorageName(uid);
            long size = await SizeInStorageAsync(uid);
            if (size <= 0)
            {
                // Zero stays "absent", matching the whole-file reader this replaced: an empty
                // cached stream must not satisfy IsExisted and then feed new Workbook() an
                // empty buffer.
                return null;
            }
            var data = new byte[size];
            int offset = 0;
            while (offset < data.Length)
            {
                int length = Math.Min(BridgeChunkBytes, data.Length - offset);
                byte[] chunk = await OpfsReadChunkAsync(key, offset, length);
                if (chunk == null || chunk.Length == 0)
                {
                    break;
                }
                Buffer.BlockCopy(chunk, 0, data, offset, chunk.Length);
                offset += chunk.Length;
                if (chunk.Length != length)
                {
                    break;
                }
            }
            if (offset != data.Length)
            {
                Array.Resize(ref data, offset);
            }
            return data;
        }

        internal static async Task OpfsWriteBytesAsync(string uid, byte[] data)
        {
            string key = ToStorageName(uid);
            await InvokeBridgeVoidAsync("gridJsOpfs_writeStart", key);
            try
            {
                for (int offset = 0; offset < data.Length; offset += BridgeChunkBytes)
                {
                    int length = Math.Min(BridgeChunkBytes, data.Length - offset);
                    // the offset/length overload encodes in place, so no chunk array is copied
                    await InvokeBridgeVoidAsync("gridJsOpfs_writeChunk", key, Convert.ToBase64String(data, offset, length));
                }
            }
            catch
            {
                await InvokeBridgeVoidAsync("gridJsOpfs_writeAbort", key);
                throw;
            }
            await InvokeBridgeVoidAsync("gridJsOpfs_writeEnd", key);
        }

        /// <summary>
        /// Copy a stored value into target in bridge-sized chunks, so a stream consumer never
        /// pays for a whole-file base64 string. The stage stays untouched.
        /// </summary>
        internal static async Task OpfsCopyToAsync(string uid, Stream target)
        {
            string key = ToStorageName(uid);
            long size = await SizeInStorageAsync(uid);
            if (size < 0)
            {
                throw new FileNotFoundException("OPFS cache key is not in storage: " + uid, uid);
            }
            long remaining = size;
            int offset = 0;
            while (remaining > 0)
            {
                int length = (int)Math.Min(BridgeChunkBytes, remaining);
                byte[] chunk = await OpfsReadChunkAsync(key, offset, length);
                if (chunk == null || chunk.Length == 0)
                {
                    break;
                }
                await target.WriteAsync(chunk, 0, chunk.Length);
                offset += chunk.Length;
                remaining -= chunk.Length;
            }
        }

        /// <summary>
        /// Stream a value into OPFS chunk by chunk so the writer never holds it whole, for hosts
        /// whose only source is forward-only (a file picker stream). A failed write aborts the
        /// file stream, leaving the previous content rather than a torn value.
        /// </summary>
        public static async Task WriteStreamAsync(String uid, Stream source)
        {
            string key = ToStorageName(uid);
            byte[] buffer = new byte[BridgeChunkBytes];
            await InvokeBridgeVoidAsync("gridJsOpfs_writeStart", key);
            try
            {
                int read;
                while ((read = await ReadUpToFillAsync(source, buffer)) > 0)
                {
                    await InvokeBridgeVoidAsync("gridJsOpfs_writeChunk", key, Convert.ToBase64String(buffer, 0, read));
                }
            }
            catch
            {
                await InvokeBridgeVoidAsync("gridJsOpfs_writeAbort", key);
                throw;
            }
            await InvokeBridgeVoidAsync("gridJsOpfs_writeEnd", key);
        }

        // A short Stream.Read is legal; chunks must be full so their base64 stays group-aligned
        // and only the final chunk can carry padding.
        private static async Task<int> ReadUpToFillAsync(Stream source, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await source.ReadAsync(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        internal static Task OpfsRemoveBytesAsync(string uid)
        {
            return InvokeBridgeVoidAsync("gridJsOpfs_remove", ToStorageName(uid));
        }

        internal static async Task<List<string>> OpfsListKeysAsync()
        {
            var names = await InvokeBridgeAsync<List<string>>("gridJsOpfs_list");
            var keys = new List<string>();
            foreach (var n in names)
            {
                keys.Add(FromStorageName(n));
            }
            return keys;
        }

        /// <summary>
        /// Save the stream to the staged cache with the key uid and mark it dirty.
        /// </summary>
        public override void SaveStream(Stream s, String uid)
        {
            byte[] data;
            if (s is MemoryStream ms)
            {
                data = ms.ToArray();
            }
            else
            {
                using (var buffer = new MemoryStream())
                {
                    s.Position = 0;
                    s.CopyTo(buffer);
                    data = buffer.ToArray();
                }
            }
            _staged[uid] = data;
            _dirty.Add(uid);
        }

        /// <summary>
        /// Return the staged stream for uid. The caller must have hydrated the key first
        /// (HydrateAsync / PersistAsync guarantees the stage is filled for that uid).
        /// </summary>
        public override Stream LoadStream(String uid)
        {
            byte[] data;
            if (_staged.TryGetValue(uid, out data))
            {
                return new MemoryStream(data);
            }
            throw new FileNotFoundException("OPFS cache key is not hydrated: " + uid, uid);
        }

        public override bool IsExisted(String uid)
        {
            return _staged.ContainsKey(uid);
        }

        /// <summary>
        /// Return the virtual download URL. The WASM dispatcher maps this path to a
        /// ReadBytesAsync call instead of issuing a real HTTP request.
        /// </summary>
        public override String GetFileUrl(String uid)
        {
            return Config.BaseRouteName + "/GetFile?id=" + uid;
        }

        /// <summary>
        /// Drop a key from both the stage and OPFS.
        /// </summary>
        public void Delete(String uid)
        {
            _staged.Remove(uid);
            _dirty.Remove(uid);
        }

        /// <summary>
        /// Pull a key from OPFS into the stage unless a dirty copy already waits there.
        /// </summary>
        public async Task HydrateAsync(String uid)
        {
            if (_dirty.Contains(uid) || ReadBytesAsync == null)
            {
                return;
            }
            byte[] data = await ReadBytesAsync(uid);
            if (data != null)
            {
                _staged[uid] = data;
            }
        }

        /// <summary>
        /// Flush the staged copy of uid to OPFS when it is dirty.
        /// </summary>
        public async Task PersistAsync(String uid)
        {
            if (WriteBytesAsync == null)
            {
                return;
            }
            byte[] data;
            if (_dirty.Contains(uid) && _staged.TryGetValue(uid, out data))
            {
                await WriteBytesAsync(uid, data);
                _dirty.Remove(uid);
            }
        }

        /// <summary>
        /// Flush every dirty stage entry to OPFS.
        /// </summary>
        public async Task PersistAllAsync()
        {
            if (WriteBytesAsync == null)
            {
                return;
            }
            foreach (string uid in new List<string>(_dirty))
            {
                await PersistAsync(uid);
            }
        }

        /// <summary>
        /// Delete a key from the stage and OPFS through the bridge.
        /// </summary>
        public async Task DeleteAsync(String uid)
        {
            Delete(uid);
            if (RemoveBytesAsync != null)
            {
                await RemoveBytesAsync(uid);
            }
        }

        /// <summary>
        /// Load the uid key list from OPFS through the bridge.
        /// </summary>
        public async Task<List<string>> ListKeysFromStorageAsync()
        {
            if (ListKeysAsync == null)
            {
                return new List<string>();
            }
            return await ListKeysAsync();
        }
    }
}
