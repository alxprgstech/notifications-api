from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import time
from typing import Any, Literal, TypedDict
from urllib.parse import urlparse

import httpx


class Notification(TypedDict):
    id: str
    sourceId: str
    content: dict[str, Any]
    createdAt: int
    status: str
    answer: dict[str, Any] | None
    answeredAt: int | None


class NotifyError(Exception):
    def __init__(self, status: int, code: str):
        self.status, self.code = status, code
        super().__init__(f"Notify HTTP {status}: {code}")


def _url(base_url: str) -> str:
    url = urlparse(base_url)
    if url.username or url.password or url.query or url.fragment or url.path not in ("", "/"):
        raise ValueError("Notify URL must be an origin without credentials")
    if url.scheme != "https" and not (url.scheme == "http" and url.hostname in ("localhost", "127.0.0.1", "::1")):
        raise ValueError("HTTPS required except loopback development")
    return base_url.rstrip("/")


def _result(response: httpx.Response) -> Any:
    if not response.is_success:
        raise NotifyError(response.status_code, _error_code(response))
    return response.json() if response.content else None


def _error_code(response: httpx.Response) -> str:
    try:
        return response.json().get("code", "request_failed")
    except (ValueError, AttributeError):
        return "request_failed"


class NotifyClient:
    def __init__(self, base_url: str, token: str, *, timeout: float = 35):
        self.http = httpx.Client(base_url=_url(base_url), headers={"Authorization": f"Bearer {token}"}, timeout=timeout, follow_redirects=False)

    def close(self) -> None:
        self.http.close()

    def __enter__(self) -> NotifyClient:
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()

    def create(self, notification: dict[str, Any], *, idempotency_key: str) -> Notification:
        return _result(self.http.post("/api/v1/notifications", json=notification, headers={"Idempotency-Key": idempotency_key}))

    def send(self, title: str, body: str, *, idempotency_key: str, icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        return self.create({"kind": "message", "title": title, "body": body, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    def upload_icon(self, data: bytes) -> str:
        if not 0 < len(data) <= 262144:
            raise ValueError("Icon must be 1..262144 bytes")
        return _result(self.http.post("/api/v1/icons", content=data, headers={"Content-Type": "application/octet-stream"}))["id"]

    def request(self, title: str, body: str, *, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", allow_custom: bool = True, icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        return self.create({"kind": "confirmation", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "allowCustom": allow_custom, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    def request_form(self, title: str, body: str, *, expires_at: str, fields: list[dict[str, Any]], idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not 1 <= len(fields) <= 20 or not all(isinstance(field, dict) for field in fields):
            raise ValueError("Form must contain 1..20 field objects")
        return self.create({"kind": "form", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "allowCustom": False, "form": {"fields": fields}, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    def upload_audio(self, wav: bytes) -> dict[str, Any]:
        if not 44 <= len(wav) <= 10 * 1024 * 1024:
            raise ValueError("WAV must be 44 bytes..10 MiB")
        return _result(self.http.post("/api/v1/audio/uploads", content=wav, headers={"Content-Type": "application/octet-stream"}))

    def send_voice(self, title: str, body: str, *, wav: bytes, transcript: str, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not isinstance(transcript, str) or not transcript.strip() or len(transcript) > 16000:
            raise ValueError("Explicit WAV requires a non-empty transcript")
        upload = self.upload_audio(wav)
        return self.create({"kind": "voice", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "voiceUpload": {"uploadId": upload["uploadId"]}, "voiceTranscript": transcript, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    def send_voice_text(self, title: str, body: str, *, text: str, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not isinstance(text, str) or not text.strip() or len(text) > 1000:
            raise ValueError("Voice text must be 1..1000 characters")
        return self.create({"kind": "voice", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "voiceText": text, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    def announce_call(self, title: str, text: str, *, idempotency_key: str, target: str = "android") -> dict[str, Any]:
        return _result(self.http.post("/api/v1/calls/announce", json={"title": title, "text": text, "idempotencyKey": idempotency_key, **({"target": target} if target != "android" else {})}))

    def call_status(self, call_id: str) -> dict[str, Any]:
        return _result(self.http.get(f"/api/v1/calls/{_id(call_id)}"))

    def start_conversation(self, title: str, *, idempotency_key: str, target: str = "android", session_id: str | None = None) -> dict[str, Any]:
        payload = {"title": title, "idempotencyKey": idempotency_key, "target": target, "sessionId": session_id}
        return _result(self.http.post("/api/v1/calls/conversation", json=payload))

    def speak_call(self, call_id: str, text: str, *, idempotency_key: str) -> dict[str, Any]:
        return _result(self.http.post(f"/api/v1/calls/{_id(call_id)}/say", json={"text": text, "idempotencyKey": idempotency_key}))

    def call_transcript(self, call_id: str) -> dict[str, Any]:
        return _result(self.http.get(f"/api/v1/calls/{_id(call_id)}/transcript"))

    def download_audio(self, audio_id: str, *, grant: str | None = None) -> bytes:
        headers = {"X-Notify-Audio-Grant": grant} if grant else {}
        response = self.http.get(f"/api/v1/audio/{_audio_id(audio_id)}", headers=headers)
        if not response.is_success:
            raise NotifyError(response.status_code, _error_code(response))
        return response.content

    def voice_status(self, voice_message_id: str) -> dict[str, Any]:
        return _result(self.http.get(f"/api/v1/voice-messages/{_audio_id(voice_message_id)}"))

    def get(self, notification_id: str) -> Notification:
        return _result(self.http.get(f"/api/v1/notifications/{_id(notification_id)}"))

    def list(self, *, before: int | None = None, before_id: str = "~", limit: int = 50) -> dict[str, Any]:
        params: dict[str, Any] = {"beforeId": before_id, "limit": limit}
        if before is not None:
            params["before"] = before
        return _result(self.http.get("/api/v1/notifications", params=params))

    def cancel(self, notification_id: str) -> Notification:
        return _result(self.http.post(f"/api/v1/notifications/{_id(notification_id)}/cancel"))

    def wait(self, notification_id: str, *, timeout: float = 300) -> Notification:
        end = time.monotonic() + timeout
        while True:
            seconds = min(25, max(0, int(end - time.monotonic())))
            n = _result(self.http.get(f"/api/v1/notifications/{_id(notification_id)}/wait", params={"seconds": seconds}))
            if n["status"] != "pending" or time.monotonic() >= end:
                return n
            if seconds == 0:
                time.sleep(min(.2, max(0, end - time.monotonic())))


class AsyncNotifyClient:
    def __init__(self, base_url: str, token: str, *, timeout: float = 35):
        self.http = httpx.AsyncClient(base_url=_url(base_url), headers={"Authorization": f"Bearer {token}"}, timeout=timeout, follow_redirects=False)

    async def __aenter__(self) -> AsyncNotifyClient:
        return self

    async def __aexit__(self, *_: Any) -> None:
        await self.http.aclose()

    async def create(self, notification: dict[str, Any], *, idempotency_key: str) -> Notification:
        return _result(await self.http.post("/api/v1/notifications", json=notification, headers={"Idempotency-Key": idempotency_key}))

    async def send(self, title: str, body: str, *, idempotency_key: str, icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        return await self.create({"kind": "message", "title": title, "body": body, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    async def upload_icon(self, data: bytes) -> str:
        if not 0 < len(data) <= 262144:
            raise ValueError("Icon must be 1..262144 bytes")
        return _result(await self.http.post("/api/v1/icons", content=data, headers={"Content-Type": "application/octet-stream"}))["id"]

    async def request(self, title: str, body: str, *, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", allow_custom: bool = True, icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        return await self.create({"kind": "confirmation", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "allowCustom": allow_custom, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    async def request_form(self, title: str, body: str, *, expires_at: str, fields: list[dict[str, Any]], idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not 1 <= len(fields) <= 20 or not all(isinstance(field, dict) for field in fields):
            raise ValueError("Form must contain 1..20 field objects")
        return await self.create({"kind": "form", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "allowCustom": False, "form": {"fields": fields}, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    async def upload_audio(self, wav: bytes) -> dict[str, Any]:
        if not 44 <= len(wav) <= 10 * 1024 * 1024:
            raise ValueError("WAV must be 44 bytes..10 MiB")
        return _result(await self.http.post("/api/v1/audio/uploads", content=wav, headers={"Content-Type": "application/octet-stream"}))

    async def send_voice(self, title: str, body: str, *, wav: bytes, transcript: str, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not isinstance(transcript, str) or not transcript.strip() or len(transcript) > 16000:
            raise ValueError("Explicit WAV requires a non-empty transcript")
        upload = await self.upload_audio(wav)
        return await self.create({"kind": "voice", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "voiceUpload": {"uploadId": upload["uploadId"]}, "voiceTranscript": transcript, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    async def send_voice_text(self, title: str, body: str, *, text: str, expires_at: str, idempotency_key: str, security_level: Literal["standard", "elevated"] = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> Notification:
        if not isinstance(text, str) or not text.strip() or len(text) > 1000:
            raise ValueError("Voice text must be 1..1000 characters")
        return await self.create({"kind": "voice", "title": title, "body": body, "expiresAt": expires_at, "securityLevel": security_level, "voiceText": text, "iconId": icon_id, "correlationId": correlation_id}, idempotency_key=idempotency_key)

    async def announce_call(self, title: str, text: str, *, idempotency_key: str, target: str = "android") -> dict[str, Any]:
        return _result(await self.http.post("/api/v1/calls/announce", json={"title": title, "text": text, "idempotencyKey": idempotency_key, **({"target": target} if target != "android" else {})}))

    async def call_status(self, call_id: str) -> dict[str, Any]:
        return _result(await self.http.get(f"/api/v1/calls/{_id(call_id)}"))

    async def start_conversation(self, title: str, *, idempotency_key: str, target: str = "android", session_id: str | None = None) -> dict[str, Any]:
        payload = {"title": title, "idempotencyKey": idempotency_key, "target": target, "sessionId": session_id}
        return _result(await self.http.post("/api/v1/calls/conversation", json=payload))

    async def speak_call(self, call_id: str, text: str, *, idempotency_key: str) -> dict[str, Any]:
        return _result(await self.http.post(f"/api/v1/calls/{_id(call_id)}/say", json={"text": text, "idempotencyKey": idempotency_key}))

    async def call_transcript(self, call_id: str) -> dict[str, Any]:
        return _result(await self.http.get(f"/api/v1/calls/{_id(call_id)}/transcript"))

    async def download_audio(self, audio_id: str, *, grant: str | None = None) -> bytes:
        headers = {"X-Notify-Audio-Grant": grant} if grant else {}
        response = await self.http.get(f"/api/v1/audio/{_audio_id(audio_id)}", headers=headers)
        if not response.is_success:
            raise NotifyError(response.status_code, _error_code(response))
        return response.content

    async def voice_status(self, voice_message_id: str) -> dict[str, Any]:
        return _result(await self.http.get(f"/api/v1/voice-messages/{_audio_id(voice_message_id)}"))

    async def get(self, notification_id: str) -> Notification:
        return _result(await self.http.get(f"/api/v1/notifications/{_id(notification_id)}"))

    async def list(self, *, before: int | None = None, before_id: str = "~", limit: int = 50) -> dict[str, Any]:
        params: dict[str, Any] = {"beforeId": before_id, "limit": limit}
        if before is not None:
            params["before"] = before
        return _result(await self.http.get("/api/v1/notifications", params=params))

    async def cancel(self, notification_id: str) -> Notification:
        return _result(await self.http.post(f"/api/v1/notifications/{_id(notification_id)}/cancel"))

    async def wait(self, notification_id: str, *, timeout: float = 300) -> Notification:
        end = time.monotonic() + timeout
        while True:
            seconds = min(25, max(0, int(end - time.monotonic())))
            n = _result(await self.http.get(f"/api/v1/notifications/{_id(notification_id)}/wait", params={"seconds": seconds}))
            if n["status"] != "pending" or time.monotonic() >= end:
                return n
            if seconds == 0:
                await asyncio.sleep(min(.2, max(0, end - time.monotonic())))


def _id(value: str) -> str:
    if len(value) != 32 or any(c not in "0123456789abcdef" for c in value):
        raise ValueError("Invalid notification ID")
    return value


def _audio_id(value: str) -> str:
    if len(value) != 32 or any(c not in "0123456789abcdef" for c in value):
        raise ValueError("Invalid audio/voice message ID")
    return value


def verify_webhook(body: bytes, timestamp: str, signature: str, secret: str, *, now: int | None = None, tolerance: int = 300) -> dict[str, Any]:
    """Verify raw bytes before decoding; caller must deduplicate the signed eventId."""
    if not secret or len(signature) != 67 or not signature.startswith("v1=") or any(c not in "0123456789abcdef" for c in signature[3:]):
        raise ValueError("Invalid webhook signature format")
    if not timestamp.isascii() or not timestamp.isdecimal() or len(timestamp) > 12:
        raise ValueError("Invalid webhook timestamp")
    if abs((int(time.time()) if now is None else now) - int(timestamp)) > tolerance:
        raise ValueError("Expired webhook")
    expected = "v1=" + hmac.new(secret.encode(), timestamp.encode() + b"." + body, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(expected, signature):
        raise ValueError("Invalid webhook signature")
    event = json.loads(body)
    if not isinstance(event, dict) or not isinstance(event.get("eventId"), str):
        raise ValueError("Invalid webhook event")
    return event
