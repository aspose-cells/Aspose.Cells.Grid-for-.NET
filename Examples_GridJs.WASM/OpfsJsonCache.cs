#nullable disable
using Aspose.Cells.GridJs;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace GridJsWasm
{
    /// <summary>
    /// JSON cache for the browser WASM host, persisted in OPFS.
    ///
    /// IGridJsJsonCache is synchronous, but OPFS is only reachable through asynchronous interop,
    /// so this keeps the staged shape OpfsCacheForStream already proved: the library's
    /// GetCacheKey/TryGetJson/SetJson/Invalidate calls run against an in-memory stage, and the
    /// dispatcher hydrates the stage before a request and flushes it after. Nothing here ever
    /// blocks on interop, which would deadlock the single WASM thread.
    ///
    /// Registering this through Config.JsonCache.Implementation also keeps the library's own
    /// DefaultGridJsJsonCache out of existence: it names disk files with MD5 and writes them with
    /// System.IO, neither of which a browser build has.
    /// </summary>
    public class OpfsJsonCache : IGridJsJsonCache
    {
        public static OpfsJsonCache Instance = new OpfsJsonCache();

        /// <summary>
        /// Keys are also the OPFS file names (the bridge tokenises '/' itself), and this prefix is
        /// what keeps them apart from the uid-keyed workbook files in the same flat directory.
        /// </summary>
        private const string KEY_PREFIX = "__json__.";

        private readonly Dictionary<string, string> _stage = new Dictionary<string, string>();
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private readonly HashSet<string> _removed = new HashSet<string>();
        private readonly HashSet<string> _tombstoneUids = new HashSet<string>();
        private bool _clearStorage;

        /// <summary>
        /// Insertion order, used only to evict clean entries when the stage outgrows its cap. A
        /// whole-book JSON for an 11 MB workbook is ~3.9M chars, so an unbounded stage turns every
        /// file switch into heap pressure.
        /// </summary>
        private readonly List<string> _order = new List<string>();

        private const int MaxStageEntries = 6;

        public string GetCacheKey(string uid, string fileName)
        {
            // uid first and delimiter-terminated: HydrateForUidAsync and InvalidateAllForUid match
            // on the prefix, and the '|' keeps uid "a" from matching uid "ab". fileName may itself
            // carry the per-sheet suffix the library appends for lazy loading ("book_Sheet1"),
            // which needs no special case here.
            return KEY_PREFIX + uid + "|" + fileName;
        }

        public string TryGetJson(string cacheKey)
        {
            string json;
            return _stage.TryGetValue(cacheKey, out json) ? json : null;
        }

        public void SetJson(string cacheKey, string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }
            if (!_stage.ContainsKey(cacheKey))
            {
                _order.Add(cacheKey);
            }
            _stage[cacheKey] = json;
            _removed.Remove(cacheKey);
            _dirty.Add(cacheKey);
            EvictIfNeeded();
        }

        public void Invalidate(string cacheKey)
        {
            if (_stage.Remove(cacheKey))
            {
                _order.Remove(cacheKey);
            }
            _dirty.Remove(cacheKey);
            // removing a key that was never stored is fine: gridJsOpfs_remove swallows a miss.
            _removed.Add(cacheKey);
        }

        public void InvalidateAllForUid(string uid)
        {
            string prefix = PrefixFor(uid);
            var matched = new List<string>();
            foreach (string key in _stage.Keys)
            {
                if (key.StartsWith(prefix))
                {
                    matched.Add(key);
                }
            }
            foreach (string key in matched)
            {
                Invalidate(key);
            }
            // The stage only holds what this page session touched, so dropping the stage would
            // leave an edited workbook's older json file in OPFS. The tombstone makes the flush
            // delete every stored key for the uid, hydrated or not.
            _tombstoneUids.Add(uid);
        }

        public void ClearAll()
        {
            foreach (string key in new List<string>(_stage.Keys))
            {
                Invalidate(key);
            }
            _clearStorage = true;
        }

        /// <summary>
        /// Read every stored JSON entry for this uid into the stage. Called by the dispatcher
        /// before the GridJs call, because TryGetJson cannot await.
        /// </summary>
        public async Task HydrateForUidAsync(string uid)
        {
            var bridge = OpfsCacheForStream.Instance;
            if (bridge.ReadBytesAsync == null || bridge.ListKeysAsync == null)
            {
                return;
            }
            string prefix = PrefixFor(uid);
            foreach (string name in await bridge.ListKeysAsync())
            {
                if (!name.StartsWith(prefix) || _stage.ContainsKey(name) || _dirty.Contains(name))
                {
                    continue;
                }
                byte[] data = await bridge.ReadBytesAsync(name);
                if (data != null && data.Length > 0)
                {
                    _stage[name] = Encoding.UTF8.GetString(data);
                    _order.Add(name);
                    EvictIfNeeded();
                }
            }
        }

        /// <summary>
        /// Flush stage writes and stage deletions to OPFS. Called by the dispatcher after the GridJs
        /// call, next to the stream cache's own PersistAllAsync.
        /// </summary>
        public async Task PersistDirtyAsync()
        {
            var bridge = OpfsCacheForStream.Instance;
            if (bridge.RemoveBytesAsync != null && (bridge.ListKeysAsync != null) &&
                (_tombstoneUids.Count > 0 || _clearStorage))
            {
                var storedNames = await bridge.ListKeysAsync();
                foreach (string name in storedNames)
                {
                    if (!name.StartsWith(KEY_PREFIX))
                    {
                        continue;
                    }
                    if (_clearStorage || _tombstoneUids.Contains(UidOfStoredKey(name)))
                    {
                        await bridge.RemoveBytesAsync(name);
                    }
                }
                _tombstoneUids.Clear();
                _clearStorage = false;
            }
            foreach (string key in new List<string>(_removed))
            {
                if (bridge.RemoveBytesAsync != null)
                {
                    await bridge.RemoveBytesAsync(key);
                }
                _removed.Remove(key);
            }
            if (bridge.WriteBytesAsync == null)
            {
                return;
            }
            foreach (string key in new List<string>(_dirty))
            {
                string json;
                if (_stage.TryGetValue(key, out json))
                {
                    await bridge.WriteBytesAsync(key, Encoding.UTF8.GetBytes(json));
                }
                _dirty.Remove(key);
            }
        }

        private static string UidOfStoredKey(string name)
        {
            int start = KEY_PREFIX.Length;
            int bar = name.IndexOf('|', start);
            return bar < 0 ? name.Substring(start) : name.Substring(start, bar - start);
        }

        private static string PrefixFor(string uid)
        {
            return KEY_PREFIX + uid + "|";
        }

        private void EvictIfNeeded()
        {
            while (_order.Count > MaxStageEntries)
            {
                string oldest = null;
                foreach (string key in _order)
                {
                    if (!_dirty.Contains(key) && !_removed.Contains(key))
                    {
                        oldest = key;
                        break;
                    }
                }
                if (oldest == null)
                {
                    return;
                }
                _stage.Remove(oldest);
                _order.Remove(oldest);
            }
        }
    }
}
