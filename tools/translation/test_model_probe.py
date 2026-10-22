"""Run with: python3 -m unittest discover -s tools/translation -p 'test_*.py'."""
import json
from pathlib import Path
import tempfile
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
import model_probe


class FakeOllama(BaseHTTPRequestHandler):
    requests = []

    def log_message(self, *args):
        pass

    def send_data(self, payload):
        data = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/api/tags":
            self.send_data({"models": [{"name": "translategemma:4b", "size": 3_300_000_000, "digest": "test"}]})
        elif self.path == "/api/ps":
            self.send_data({"models": [{"name": "translategemma:4b", "size_vram": 0}]})
        else:
            self.send_error(404)

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.requests.append((self.path, payload))
        if self.path == "/api/generate":
            self.send_data({"response": "Rina ging nach Hause.", "eval_count": 15, "eval_duration": 1_000_000_000})
        elif self.path == "/api/pull":
            self.send_data({"status": "success"})
        else:
            self.send_error(404)


class ProbeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeOllama)
        cls.thread = Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.host = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=3)

    def test_url_validation(self):
        for bad in ("file:///etc/passwd", "https://user:password@server",
                    "http://server/path", "http://server:abc", "http://server?foo=1"):
            with self.subTest(url=bad), self.assertRaises(ValueError):
                model_probe.endpoint(bad)
        self.assertEqual(model_probe.endpoint("http://ollama:11434/"), "http://ollama:11434")

    def test_memory_respects_container_limits(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "proc").mkdir()
            (root / "sys/fs/cgroup").mkdir(parents=True)
            (root / "proc/meminfo").write_text("MemAvailable: 16000000 kB\n")
            (root / "sys/fs/cgroup/memory.max").write_text("8589934592")
            (root / "sys/fs/cgroup/memory.current").write_text("4294967296")
            memory = model_probe.memory(root)
            self.assertEqual(memory["available"], 4294967296)
            self.assertEqual(memory["origin"], "cgroup-v2-conservative")
            self.assertTrue(model_probe.fit(memory["available"], "translategemma:12b", True).startswith("insufficient"))
            self.assertTrue(model_probe.fit(memory["available"], "translategemma:4b", False).startswith("unknown"))

    def test_discovery_and_installed_only_benchmark(self):
        self.assertIn("translategemma:4b", model_probe.installed(self.host))
        self.assertEqual(model_probe.loaded(self.host)["translategemma:4b"]["size_vram"], 0)
        metrics = model_probe.benchmark(self.host, "translategemma:4b", model_probe.SAMPLES[:1])
        self.assertEqual(metrics[0]["output_tokens_per_second"], 15)
        self.assertTrue(metrics[0]["nonempty"])
        self.assertEqual(FakeOllama.requests[-1][1]["options"]["num_ctx"], 2048)
        self.assertEqual(FakeOllama.requests[-1][0], "/api/generate")
        with self.assertRaises(ValueError):
            model_probe.benchmark(self.host, "unreviewed:70b", [])

    def test_pull_is_allowlisted_and_explicit(self):
        with self.assertRaises(ValueError):
            model_probe.pull(self.host, "unreviewed:70b")
        model_probe.pull(self.host, "translategemma:4b")
        self.assertEqual(FakeOllama.requests[-1][0], "/api/pull")


if __name__ == "__main__":
    unittest.main()
