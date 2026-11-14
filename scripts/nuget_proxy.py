"""Local NuGet v3 bridge for Windows hosts whose .NET TLS stack cannot use the proxy.

Run only while restoring, then stop the process. The listener is loopback only.
"""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import urlopen


PORT = 18765


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/v3/index.json":
            body = ('{"version":"3.0.0","resources":[{"@id":'
                    '"http://127.0.0.1:18765/v3-flatcontainer/",'
                    '"@type":"PackageBaseAddress/3.0.0"}]}').encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        try:
            with urlopen("https://api.nuget.org" + self.path, timeout=60) as response:
                body = response.read()
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/octet-stream"))
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
        except Exception as exc:
            self.send_error(502, str(exc))


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
