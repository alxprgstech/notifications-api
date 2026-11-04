import hashlib
import hmac
import json
import unittest
import httpx
from alxprgs_notify import NotifyClient, AsyncNotifyClient, NotifyError, verify_webhook


class ClientTests(unittest.TestCase):
    def test_form_payload_and_limits(self):
        captured = {}

        def handler(request):
            captured["body"] = json.loads(request.content)
            captured["key"] = request.headers["Idempotency-Key"]
            return httpx.Response(200, json={"id": "a" * 32, "status": "pending"})

        fields = [{"id": "env", "label": "Env", "type": "single_choice", "required": True, "options": [{"id": "prod", "label": "Prod"}]}]
        with NotifyClient("https://example.com", "test") as client:
            client.http.close()
            client.http = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://example.com")
            client.request_form("T", "B", expires_at="2030-01-01T00:00:00Z", fields=fields, idempotency_key="form-key-1")
            self.assertEqual(captured["body"]["kind"], "form")
            self.assertFalse(captured["body"]["allowCustom"])
            self.assertEqual(captured["body"]["form"]["fields"], fields)
            self.assertEqual(captured["key"], "form-key-1")
            with self.assertRaises(ValueError):
                client.request_form("T", "B", expires_at="2030-01-01T00:00:00Z", fields=[], idempotency_key="form-key-2")

    def test_voice_upload_send_download_and_status(self):
        calls = []

        def handler(request):
            calls.append(request)
            if request.url.path == "/api/v1/audio/uploads":
                self.assertEqual(request.headers["Content-Type"], "application/octet-stream")
                return httpx.Response(200, json={"uploadId": "b" * 32, "durationMs": 1000, "sizeBytes": 44})
            if request.url.path == "/api/v1/notifications":
                body = json.loads(request.content)
                self.assertEqual(body["kind"], "voice")
                self.assertEqual(body["voiceUpload"]["uploadId"], "b" * 32)
                return httpx.Response(200, json={"id": "a" * 32, "status": "active"})
            if request.url.path.startswith("/api/v1/audio/"):
                self.assertEqual(request.headers["X-Notify-Audio-Grant"], "grant")
                return httpx.Response(200, content=b"wav-bytes")
            if request.url.path.startswith("/api/v1/voice-messages/"):
                return httpx.Response(200, json={"id": "c" * 32, "status": "queued"})
            return httpx.Response(404, json={"code": "not_found"})

        with NotifyClient("https://example.com", "test") as client:
            client.http.close()
            client.http = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://example.com")
            client.send_voice("T", "B", wav=b"RIFF" + b"\0" * 40, expires_at="2030-01-01T00:00:00Z", idempotency_key="voice-key")
            self.assertEqual(client.download_audio("b" * 32, grant="grant"), b"wav-bytes")
            self.assertEqual(client.voice_status("c" * 32)["status"], "queued")
            with self.assertRaises(ValueError):
                client.download_audio("../audio")
            self.assertEqual(len(calls), 4)

    def test_signature_raw_and_expiry(self):
        body = b'{"eventId":"abc","type":"notification.answered"}'
        sig = "v1=" + hmac.new(b"test-secret", b"1000." + body, hashlib.sha256).hexdigest()
        self.assertEqual(verify_webhook(body, "1000", sig, "test-secret", now=1000)["eventId"], "abc")
        for data, stamp, now in [(body + b" ", "1000", 1000), (body, "1000", 1301), (body, "1001", 1000)]:
            with self.assertRaises(ValueError):
                verify_webhook(data, stamp, sig, "test-secret", now=now)

    def test_url_and_id_validation(self):
        for url in ["http://example.com", "https://user:secret@example.com", "https://example.com/path"]:
            with self.assertRaises(ValueError):
                NotifyClient(url, "test")
        with NotifyClient("https://example.com", "test") as client:
            with self.assertRaises(ValueError):
                client.get("../sources")

    def test_pending_is_not_approval(self):
        with NotifyClient("https://example.com", "test") as client:
            client.http.close()
            client.http = httpx.Client(transport=httpx.MockTransport(lambda _: httpx.Response(200, json={"id": "a"*32, "status": "pending"})), base_url="https://example.com")
            self.assertEqual(client.wait("a"*32, timeout=0)["status"], "pending")

    def test_errors_do_not_echo_sensitive_body(self):
        with NotifyClient("https://example.com", "test") as client:
            client.http.close()
            client.http = httpx.Client(transport=httpx.MockTransport(lambda _: httpx.Response(403, json={"code": "scope_required", "secret": "never echo"})), base_url="https://example.com")
            with self.assertRaises(NotifyError) as error:
                client.get("a"*32)
            self.assertNotIn("never echo", str(error.exception))


class AsyncTests(unittest.IsolatedAsyncioTestCase):
    async def test_pending(self):
        async with AsyncNotifyClient("https://example.com", "test") as client:
            await client.http.aclose()
            client.http = httpx.AsyncClient(transport=httpx.MockTransport(lambda _: httpx.Response(200, json={"status": "pending"})), base_url="https://example.com")
            self.assertEqual((await client.wait("a"*32, timeout=0))["status"], "pending")

    async def test_form_uses_same_contract(self):
        captured = {}

        def handler(request):
            captured["body"] = json.loads(request.content)
            return httpx.Response(200, json={"id": "a" * 32, "status": "pending"})

        fields = [{"id": "count", "label": "Count", "type": "number", "required": True, "min": 1, "max": 5}]
        async with AsyncNotifyClient("https://example.com", "test") as client:
            await client.http.aclose()
            client.http = httpx.AsyncClient(transport=httpx.MockTransport(handler), base_url="https://example.com")
            await client.request_form("T", "B", expires_at="2030-01-01T00:00:00Z", fields=fields, idempotency_key="async-form")
            self.assertEqual(captured["body"]["kind"], "form")
            self.assertEqual(captured["body"]["form"]["fields"], fields)
            with self.assertRaises(ValueError):
                await client.request_form("T", "B", expires_at="2030-01-01T00:00:00Z", fields=[], idempotency_key="async-form-2")


if __name__ == "__main__":
    unittest.main()
