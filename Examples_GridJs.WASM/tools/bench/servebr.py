"""Brotli-aware static server for the published wasm demo.

Serves `<asset>.br` with `Content-Encoding: br` when the compressed sibling exists, which is what
a production server does and what tools/serve.py does not. Used only to measure how much of the
engine-start time is the uncompressed AOT payload.

python tools/bench/servebr.py --root bin/Release/net8.0/publish/wwwroot --port 5302
"""
import argparse
import mimetypes
import os
import re
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

mimetypes.add_type("application/wasm", ".wasm")
mimetypes.add_type("application/octet-stream", ".dat")

ENCODABLE = re.compile(r"\.(wasm|js|css|json|dat|html)$")


class Handler(SimpleHTTPRequestHandler):
    def send_head(self):
        path = self.translate_path(self.path)
        if ENCODABLE.search(path) and os.path.isfile(path + ".br"):
            ctype = mimetypes.guess_type(path)[0] or "application/octet-stream"
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Encoding", "br")
            self.send_header("Content-Length", str(os.path.getsize(path + ".br")))
            self.end_headers()
            return open(path + ".br", "rb")
        return super().send_head()

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()


ap = argparse.ArgumentParser()
ap.add_argument("--root", required=True)
ap.add_argument("--port", type=int, default=5302)
a = ap.parse_args()
os.chdir(a.root)
print("serving %s at http://localhost:%d (brotli-aware)" % (a.root, a.port), flush=True)
ThreadingHTTPServer(("127.0.0.1", a.port), Handler).serve_forever()
