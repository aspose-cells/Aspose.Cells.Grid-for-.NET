// OPFS bridge for the GridJs WASM stream cache (OpfsCacheForStream).
// Values cross interop as base64 strings; C# encodes '/' in uids to "__sl__",
// so keys here are always flat file names.
const DIR = 'gridjs-cache';

let _root = null;

async function root() {
  if (_root) return _root;
  const base = await navigator.storage.getDirectory();
  _root = await base.getDirectoryHandle(DIR, { create: true });
  return _root;
}

function bytesToBase64(bytes) {
  let bin = '';
  const CHUNK = 0x8000;
  for (let i = 0; i < bytes.length; i += CHUNK) {
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
  }
  return btoa(bin);
}

function base64ToBytes(b64) {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i += 1) {
    bytes[i] = bin.charCodeAt(i);
  }
  return bytes;
}

window.gridJsOpfs_remove = async function (key) {
  try {
    await (await root()).removeEntry(key);
  } catch (e) {
    // a missing file is fine
  }
};

window.gridJsOpfs_list = async function () {
  const keys = [];
  const dir = await root();
  for await (const [name] of dir.entries()) {
    keys.push(name);
  }
  return keys;
};

// Every value moves through these chunked primitives, not as one base64 string: a whole-value
// transfer holds size bytes plus 1.37*size UTF-16 chars on each side at once, which is what
// blows up the WASM heap on a big workbook. C# sizes its result once and fills it per chunk.

// byte size of the value, or -1 when the key is absent
window.gridJsOpfs_size = async function (key) {
  try {
    const fh = await (await root()).getFileHandle(key, { create: false });
    return (await fh.getFile()).size;
  } catch (e) {
    return -1;
  }
};

// base64 of bytes [offset, offset+length). C# always passes a length that is a multiple
// of 3 except for the final chunk, so each call is an independently decodable base64 string.
window.gridJsOpfs_readAt = async function (key, offset, length) {
  const file = await (await (await root()).getFileHandle(key, { create: false })).getFile();
  const buf = await file.slice(offset, offset + length).arrayBuffer();
  return bytesToBase64(new Uint8Array(buf));
};

const _writers = new Map();

window.gridJsOpfs_writeStart = async function (key) {
  await window.gridJsOpfs_writeAbort(key);
  const fh = await (await root()).getFileHandle(key, { create: true });
  _writers.set(key, await fh.createWritable());
};

window.gridJsOpfs_writeChunk = async function (key, b64) {
  const w = _writers.get(key);
  if (!w) throw new Error('no open OPFS writer for ' + key);
  await w.write(base64ToBytes(b64));
};

window.gridJsOpfs_writeEnd = async function (key) {
  const w = _writers.get(key);
  if (!w) return;
  _writers.delete(key);
  await w.close();
};

// drop an incomplete write so the previous content (or nothing) stays, never a torn file
window.gridJsOpfs_writeAbort = async function (key) {
  const w = _writers.get(key);
  if (!w) return;
  _writers.delete(key);
  try {
    await w.abort();
  } catch (e) {
    // already closed
  }
};

// blob: URL for the value; lets JS download or decode straight from OPFS so the bytes
// never cross interop. The caller owns revokeObjectURL.
window.gridJsOpfs_getUrl = async function (key) {
  const file = await (await (await root()).getFileHandle(key, { create: false })).getFile();
  return URL.createObjectURL(file);
};

// Stage a picked File into OPFS without ever holding it whole: each slice becomes one base64
// chunk, the same shape the C# writer uses, so a several-hundred-MB download/upload stays flat.
window.gridJsOpfs_writeFile = async function (key, file, onProgress) {
  const CHUNK = 49152; // multiple of 3, so every chunk is a standalone base64 group
  await window.gridJsOpfs_writeStart(key);
  try {
    for (let offset = 0; offset < file.size; offset += CHUNK) {
      const buf = await file.slice(offset, Math.min(offset + CHUNK, file.size)).arrayBuffer();
      await window.gridJsOpfs_writeChunk(key, bytesToBase64(new Uint8Array(buf)));
      if (onProgress) {
        onProgress(Math.min(offset + CHUNK, file.size), file.size);
      }
    }
  } catch (e) {
    await window.gridJsOpfs_writeAbort(key);
    throw e;
  }
  await window.gridJsOpfs_writeEnd(key);
};
