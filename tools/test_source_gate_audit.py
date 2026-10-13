"""Controlled local HTTP checks for the real-source Jularr gate auditor."""

import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

from source_gate_audit import allowed_base, run_audit, target_url


class LocalGate(BaseHTTPRequestHandler):
    def do_HEAD(self):
        self.respond()

    def do_GET(self):
        self.respond()

    def respond(self):
        path = urlsplit(self.path).path
        if path == "/blocked":
            self.send_response(403)
            self.send_header("Content-Type", "text/html")
            self.send_header("cf-mitigated", "challenge")
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(b"<html><title>Just a moment</title></html>")
        elif path in ("/leaked", "/leaked/"):
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(b"<html>OWNED_SECRET_PROTECTED_TEXT</html>")
        elif path == "/download":
            self.send_response(206 if self.headers.get("Range") else 200)
            self.send_header("Content-Type", "application/pdf")
            if self.headers.get("Range"):
                self.send_header("Content-Range", "bytes 0-12/1300")
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(b"%PDF-OWNED-DATA")
        elif path == "/redirect":
            self.send_response(302)
            self.send_header("Location", "https://other.example.org/private")
            self.end_headers()
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *args):
        pass


class GateAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), LocalGate)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=3)

    def test_same_origin_and_https(self):
        self.assertEqual(self.base, allowed_base(self.base))
        with self.assertRaises(ValueError):
            allowed_base("http://thirdparty.example.org")
        with self.assertRaises(ValueError):
            target_url(self.base, "//other.example.org/steal")
        with self.assertRaises(ValueError):
            target_url(self.base, "/a/../b")
        with self.assertRaises(ValueError):
            target_url(self.base, "/a#frag")

    def test_real_requests_are_blocked_by_challenge(self):
        report = run_audit(self.base, ["/blocked"], b"OWNED_SECRET_PROTECTED_TEXT", None, delay=0, timeout=2)
        self.assertEqual("BLOCKED", report["conclusion"])
        self.assertTrue(all(item["result"] in ("BLOCKED", "NOT_FOUND") for item in report["results"]))

    def test_unprotected_response_proves_exposure(self):
        report = run_audit(self.base, ["/leaked"], b"OWNED_SECRET_PROTECTED_TEXT", None, delay=0, timeout=2)
        self.assertEqual("EXPOSED", report["conclusion"])
        self.assertEqual(1, report["requests"])
        self.assertNotIn("OWNED_SECRET_PROTECTED_TEXT", json.dumps(report))

    def test_direct_file_response_proves_exposure(self):
        report = run_audit(self.base, ["/download"], bytes.fromhex("25504446"), None, delay=0, timeout=2)
        self.assertEqual("EXPOSED", report["conclusion"])

    def test_redirect_does_not_leave_origin(self):
        report = run_audit(self.base, ["/redirect"], b"OWNED_SECRET_PROTECTED_TEXT", None, delay=0, timeout=2)
        self.assertEqual("INCONCLUSIVE", report["conclusion"])
        self.assertTrue(all(item["result"] in ("REDIRECT", "NOT_FOUND") for item in report["results"]))


if __name__ == "__main__":
    unittest.main()
