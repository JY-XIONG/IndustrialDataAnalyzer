"""Local NuGet v3 bridge for Windows hosts whose .NET TLS stack cannot use the proxy.

Run only while restoring, then stop the process. The listener is loopback only.
"""

import gzip
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import urlopen


PORT = 18765


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/v3/index.json":
            body = json.dumps({"version": "3.0.0", "resources": [
                {"@id": f"http://127.0.0.1:{PORT}/v3-flatcontainer/",
                 "@type": "PackageBaseAddress/3.0.0"},
                {"@id": f"http://127.0.0.1:{PORT}/v3/vulnerabilities/index.json",
                 "@type": "VulnerabilityInfo/6.7.0"}
            ]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        try:
            with urlopen("https://api.nuget.org" + self.path, timeout=60) as response:
                body = response.read()
                if response.headers.get("Content-Encoding") == "gzip":
                    body = gzip.decompress(body)
                if self.path == "/v3/vulnerabilities/index.json":
                    entries = json.loads(body)
                    for entry in entries:
                        entry["@id"] = entry["@id"].replace(
                            "https://api.nuget.org/", f"http://127.0.0.1:{PORT}/")
                    body = json.dumps(entries).encode()
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/octet-stream"))
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
        except Exception as exc:
            self.send_error(502, str(exc))


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
