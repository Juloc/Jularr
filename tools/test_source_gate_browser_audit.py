"""Focused tests for the optional real-browser gate audit.

No external sites are contacted. Chromium smoke coverage may be enabled locally
with JULARR_AUDIT_BROWSER=/path/to/chromium.
"""
import os
import unittest
from unittest.mock import Mock

from source_gate_browser_audit import (MAX_BODY, READ_BROWSER_RESPONSE, classify,
                                       same_origin, visible_content)
from source_gate_audit import allowed_base, target_url


class BrowserGateAuditTests(unittest.TestCase):
    def test_classification_requires_actual_content(self):
        self.assertEqual("EXPOSED", classify(403, True, True))
        self.assertEqual("BLOCKED", classify(403, False, True))
        self.assertEqual("BLOCKED", classify(429, False, False))
        self.assertEqual("SUSPECT", classify(206, False, False, partial=True))
        self.assertEqual("INCONCLUSIVE", classify(200, False, False))
        self.assertEqual("INCONCLUSIVE", classify(None, False, False, error="TimeoutError"))

    def test_origin_constraints(self):
        origin = allowed_base("https://owned.example.test")
        self.assertTrue(same_origin(origin, target_url(origin, "/protected")))
        self.assertFalse(same_origin(origin, "https://different.example.test/protected"))
        with self.assertRaises(ValueError):
            target_url(origin, "//different.example.test/protected")
        with self.assertRaises(ValueError):
            allowed_base("http://different.example.test")

    def test_visible_proof_is_not_inferred_from_status(self):
        page = Mock()
        page.locator.return_value.inner_text.return_value = "A protected sample BOOK_PROOF_82902"
        self.assertEqual(
            {"exposed": True, "challenge": False},
            visible_content(page, b"BOOK_PROOF_82902")
        )
        page.locator.return_value.inner_text.return_value = "Just a moment. Checking your browser"
        self.assertEqual(
            {"exposed": False, "challenge": True},
            visible_content(page, b"BOOK_PROOF_82902")
        )

    @unittest.skipUnless(os.getenv("JULARR_AUDIT_BROWSER"), "Optional local Chromium smoke test")
    def test_chromium_executes_real_javascript_without_network(self):
        from playwright.sync_api import sync_playwright

        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(
                executable_path=os.environ["JULARR_AUDIT_BROWSER"], headless=True)
            try:
                page = browser.new_page()
                page.set_content("<html><body>OWNED_BROWSER_PROOF</body></html>")
                self.assertTrue(visible_content(page, b"OWNED_BROWSER_PROOF")["exposed"])
                import base64
                proof = b"OWNED_BYTES_98765"
                source = "data:text/plain;base64," + base64.b64encode(proof).decode("ascii")
                fetched = page.evaluate(READ_BROWSER_RESPONSE, {
                    "url": source, "proofHex": proof.hex(),
                    "limit": MAX_BODY, "timeoutMs": 2000
                })
                self.assertTrue(fetched["exposed"], fetched)
                self.assertEqual(200, fetched["status"])
            finally:
                browser.close()


if __name__ == "__main__":
    unittest.main()
