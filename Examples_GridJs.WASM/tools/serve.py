"""Serve the published Blazor WASM demo with the MIME types the runtime requires.

Requires Python 3 and a publish output, not a build output:
    python tools/serve.py --root bin/Release/net8.0/publish/wwwroot --port 5301
"""
import argparse
import mimetypes
import os
import sys
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler

mimetypes.add_type("application/wasm", ".wasm")
mimetypes.add_type("application/json", ".json")


class Handler(SimpleHTTPRequestHandler):
    extensions_map = {
        **mimetypes.types_map,
        ".wasm": "application/wasm",
        ".dll": "application/octet-stream",
        ".pdb": "application/octet-stream",
        ".json": "application/json",
    }

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def log_message(self, fmt, *args):
        # flushed: request logging is how this server is verified, and a redirected stdout
        # would otherwise hold the lines back until the process exits.
        print("%s - %s" % (self.address_string(), fmt % args), flush=True)


class Server(ThreadingHTTPServer):
    # Windows lets a second bind succeed on an in-use port, so a stale server over an old
    # wwwroot would silently coexist with a fresh one and split the requests between them.
    allow_reuse_address = False


parser = argparse.ArgumentParser()
parser.add_argument("--root", required=True)
parser.add_argument("--port", type=int, default=5301)
args = parser.parse_args()

root = os.path.abspath(args.root)
if not os.path.isdir(root):
    sys.exit("%s is not a directory - run 'dotnet publish -c Release' first" % root)
os.chdir(root)

# threaded: the AOT dotnet.native.wasm is ~72 MB, and a serial server would block the ~120
# assembly requests that follow it.
try:
    server = Server(("127.0.0.1", args.port), Handler)
except OSError as exc:
    sys.exit("cannot listen on port %d: %s" % (args.port, exc))
print("serving %s at http://localhost:%d" % (root, args.port))
server.serve_forever()
