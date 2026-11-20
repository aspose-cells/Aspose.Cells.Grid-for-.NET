"""Stage the grid client locally on the wasm side, so both builds run identical JavaScript.

The published demo pulls x-spreadsheet from unpkg. For a comparison against the server-side demo
that has to stop: the bundle is the one place where a difference would be attributed to the wrong
build. This copies the server demo's own `xspread/` files into the published `wwwroot/` and writes
`bench.html` - `index.html` with the three CDN references pointed at the local copy - so both pages
execute the same bytes. The MD5 of `xspreadsheet.js` is printed for both sides.

python tools/bench/stage_client.py --from <server demo>/wwwroot/xspread ^
        [--root bin/Release/net8.0/publish/wwwroot]

`bench.html` keeps the `#upload-label` / `#upload-input` elements, because js/demo.js binds them and
the page throws without them.
"""
import argparse
import hashlib
import os
import shutil

SKIP_EXT = {".map", ".bak", ".back"}
KEEP = ("xspreadsheet.js", "xspreadsheet.css", "jszip.min.js", ".svg", ".gif", ".jpg", ".png",
        ".css", ".js")


def wanted(name):
    stem, ext = os.path.splitext(name)
    if ext in SKIP_EXT:
        return False
    if "副本" in name or name.endswith(".html"):
        return False
    return ext.lower() in KEEP or name in ("xspreadsheet.js", "xspreadsheet.css", "jszip.min.js")


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--from", dest="src", required=True,
                    help="xspread directory served by the server-side demo")
    ap.add_argument("--root", default=os.path.join("bin", "Release", "net8.0", "publish", "wwwroot"),
                    help="published wwwroot of this demo")
    ap.add_argument("--url", default="",
                    help="base url of the running server-side demo, to verify the bundle it serves")
    a = ap.parse_args()

    dest = os.path.join(a.root, "xspread")
    os.makedirs(dest, exist_ok=True)
    n = 0
    for dp, _dirs, files in os.walk(a.src):
        rel = os.path.relpath(dp, a.src)
        out = dest if rel == "." else os.path.join(dest, rel)
        os.makedirs(out, exist_ok=True)
        for f in sorted(files):
            if not wanted(f):
                continue
            shutil.copy2(os.path.join(dp, f), os.path.join(out, f))
            n += 1
    print("copied %d files into %s" % (n, dest))

    index = os.path.join(a.root, "index.html")
    with open(index, encoding="utf-8") as f:
        html = f.read()
    for cdn, local in (
        ("https://unpkg.com/gridjs-spreadsheet/xspreadsheet.css", "/xspread/xspreadsheet.css"),
        ("https://unpkg.com/gridjs-spreadsheet/xspreadsheet.js", "/xspread/xspreadsheet.js"),
        ("https://unpkg.com/jszip@3.10.1/dist/jszip.min.js", "/xspread/jszip.min.js"),
    ):
        if cdn not in html:
            raise SystemExit("expected CDN reference not found in %s: %s" % (index, cdn))
        html = html.replace(cdn, local)
    bench = os.path.join(a.root, "bench.html")
    with open(bench, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    print("wrote %s (local bundle, everything else unchanged)" % bench)

    here = os.path.join(dest, "xspreadsheet.js")
    print("xspreadsheet.js  staged =%s %d bytes" % (md5(here), os.path.getsize(here)))
    if a.url:
        import urllib.request
        with urllib.request.urlopen(a.url.rstrip("/") + "/xspread/xspreadsheet.js") as r:
            body = r.read()
        print("xspreadsheet.js  served  =%s %d bytes" % (hashlib.md5(body).hexdigest(), len(body)))
        if hashlib.md5(body).hexdigest() != md5(here):
            raise SystemExit("the two bundles differ - compare the wrong JavaScript otherwise")


if __name__ == "__main__":
    main()
