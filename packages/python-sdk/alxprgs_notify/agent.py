"""Outbound agent connector. The agent connects to Notify; no inbound port is opened."""
from __future__ import annotations

import asyncio
import json
import logging
import struct
import time
import uuid
from collections.abc import AsyncIterator, Awaitable, Callable, Sequence
from contextlib import asynccontextmanager
from typing import Any, Protocol
from urllib.parse import urlparse

import websockets

from .client import _url

PROTOCOL = "alxprgs.notify.agent.v1"
CALL_PROTOCOL = "alxprgs.notify.call.v2"
CALL_AUDIO_BYTES = 640
CALL_HEADER_BYTES = 12
CALL_FRAME_BYTES = CALL_HEADER_BYTES + CALL_AUDIO_BYTES
logger = logging.getLogger("alxprgs_notify.agent")


class NotifyCallError(RuntimeError):
    def __init__(self, code: str):
        self.code = code
        super().__init__(f"Call rejected: {code}")


class CallHandler(Protocol):
    async def on_invite(self, connector: AgentConnector, call: dict[str, Any]) -> None:
        """Accept/reject with connector.accept_call/reject_call and optionally open_call_audio."""

    async def on_interrupt(self, call_id: str, generation: int) -> None:
        """Agent-side VAD detected user speech; cancel current synthesis/response."""

    async def on_ended(self, call_id: str, reason: str) -> None:
        """Media/control connection ended; stop audio and never reconnect silently."""


class AgentRuntime(Protocol):
    async def handle(self, command: dict[str, Any]) -> None:
        """Handle one durable command. Raise to leave it unacknowledged for a later retry."""


class Transcriber(Protocol):
    async def transcribe(self, audio: bytes, *, sample_rate: int = 16000) -> str:
        """Provider-owned speech-to-text; Notify never runs models on the shared server."""


class Synthesizer(Protocol):
    async def synthesize(self, text: str, *, sample_rate: int = 16000) -> bytes:
        """Provider-owned text-to-speech returning PCM16LE mono 16 kHz bytes."""


class CallbackRuntime:
    """Explicit reference adapter; the callback is passed by code, never imported from the network."""

    def __init__(self, handler: Callable[[dict[str, Any]], Awaitable[None]]):
        self._handler = handler

    async def handle(self, command: dict[str, Any]) -> None:
        await self._handler(command)


class AgentConnector:
    def __init__(
        self,
        base_url: str,
        token: str,
        *,
        agent_id: str,
        name: str,
        capabilities: Sequence[str],
        sessions: Sequence[dict[str, str]] = (),
        runtime: AgentRuntime,
        call_handler: CallHandler | None = None,
        heartbeat_seconds: int = 30,
        max_seen_commands: int = 1000,
    ):
        if not token or len(token) > 512:
            raise ValueError("Invalid agent token")
        if not agent_id or len(agent_id) > 64:
            raise ValueError("Invalid agent id")
        if not 1 <= heartbeat_seconds <= 60:
            raise ValueError("heartbeat_seconds must be 1..60")
        self._base = _url(base_url)
        parsed = urlparse(self._base)
        self._url = ("wss://" if parsed.scheme == "https" else "ws://") + parsed.netloc + "/api/v1/agents/connect"
        self._token, self._agent_id, self._name = token, agent_id, name
        self._capabilities = list(capabilities)
        self._sessions = list(sessions)
        self._runtime = runtime
        self._call_handler = call_handler
        self._heartbeat_seconds = heartbeat_seconds
        self._stop = asyncio.Event()
        self._socket = None
        self._control = None
        self._call_tasks: set[asyncio.Task] = set()
        self._dial_waiters: dict[str, asyncio.Future] = {}
        self._call_waiters: dict[str, list[asyncio.Future]] = {}
        self._last_calls: dict[str, dict[str, Any]] = {}
        self._seen: list[str] = []
        self._max_seen = max_seen_commands

    async def run(self) -> None:
        """Connect and reconnect until stop(); durable commands survive reconnects on the server."""
        backoff = 1
        while not self._stop.is_set():
            try:
                await self._session()
                backoff = 1
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # network/TLS/protocol; keep the agent process alive
                logger.info("agent connection ended: %s", type(exc).__name__)
            if self._stop.is_set():
                break
            try:
                await asyncio.wait_for(self._stop.wait(), timeout=backoff)
            except asyncio.TimeoutError:
                pass
            backoff = min(30, backoff * 2)

    async def stop(self) -> None:
        self._stop.set()
        socket = self._socket
        if socket is not None:
            try:
                await socket.close()
            except Exception:
                pass

    def set_sessions(self, sessions: Sequence[dict[str, str]]) -> None:
        self._sessions = list(sessions)

    async def _session(self) -> None:
        async with websockets.connect(
            self._url,
            additional_headers={"Authorization": f"Bearer {self._token}"},
            ping_interval=20,
            ping_timeout=20,
            max_size=64 * 1024,
            close_timeout=5,
        ) as socket:
            await socket.send(json.dumps({
                "type": "hello", "protocolVersion": PROTOCOL, "eventId": _event(),
                "agentId": self._agent_id, "name": self._name, "capabilities": self._capabilities,
                "sessions": self._sessions, "client": "alxprgs-notify-sdk",
            }))
            hello = json.loads(await asyncio.wait_for(socket.recv(), timeout=10))
            if hello.get("type") != "hello_ack":
                raise RuntimeError("Notify did not acknowledge the agent hello")
            self._heartbeat_seconds = int(hello.get("heartbeatSeconds", self._heartbeat_seconds))
            self._socket = socket
            self._control = socket
            heartbeat = asyncio.create_task(self._heartbeat(socket))
            try:
                async for raw in socket:
                    await self._handle(json.loads(raw), socket)
            finally:
                heartbeat.cancel()
                await asyncio.gather(heartbeat, return_exceptions=True)
                self._socket = None
                self._control = None
                tasks = list(self._call_tasks); self._call_tasks.clear()
                for task in tasks: task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)

    async def _heartbeat(self, socket) -> None:
        while True:
            await asyncio.sleep(self._heartbeat_seconds)
            await socket.send(json.dumps({
                "type": "heartbeat", "protocolVersion": PROTOCOL, "eventId": _event(), "sessions": self._sessions,
            }))

    async def _handle(self, event: dict[str, Any], socket) -> None:
        kind = event.get("type")
        if kind == "command":
            await self._handle_command(event, socket)
            return
        if kind == "call.dial_ack":
            payload = event.get("payload") or {}
            request_id = payload.get("requestEventId")
            waiter = self._dial_waiters.get(request_id)
            if waiter is not None and not waiter.done():
                waiter.set_result(payload)
        elif kind == "call.accepted":
            payload = event.get("payload") or {}
            call_id = payload.get("callId")
            if isinstance(call_id, str):
                self._resolve_call(call_id, payload)
                handler = getattr(self._call_handler, "on_accepted", None)
                if handler is not None:
                    task = asyncio.create_task(self._invoke_call(handler(self, payload)))
                    self._call_tasks.add(task); task.add_done_callback(self._call_tasks.discard)
        elif kind in ("call.rejected", "call.missed"):
            payload = event.get("payload") or {}
            call_id = payload.get("callId") or payload.get("call_id")
            if isinstance(call_id, str):
                payload = dict(payload); payload.setdefault("reason", kind.split(".", 1)[1])
                self._resolve_call(call_id, payload)
                handler = getattr(self._call_handler, "on_rejected", None)
                if handler is not None:
                    task = asyncio.create_task(self._invoke_call(handler(call_id, str(payload["reason"]))))
                    self._call_tasks.add(task); task.add_done_callback(self._call_tasks.discard)
        elif kind == "call.invite":
            payload = event.get("payload") or {}
            call_id = payload.get("callId")
            if not isinstance(call_id, str) or len(call_id) != 32:
                return
            if self._call_handler is None:
                await self.reject_call(call_id)
                return
            task = asyncio.create_task(self._invoke_call(self._call_handler.on_invite(self, payload)))
            self._call_tasks.add(task); task.add_done_callback(self._call_tasks.discard)
        elif kind == "call.ended":
            payload = event.get("payload") or {}
            call_id, reason = payload.get("callId"), payload.get("reason")
            if isinstance(call_id, str):
                self._resolve_call(call_id, payload)
            if self._call_handler is not None and isinstance(call_id, str):
                task = asyncio.create_task(self._invoke_call(self._call_handler.on_ended(call_id, str(reason or "ended"))))
                self._call_tasks.add(task); task.add_done_callback(self._call_tasks.discard)

    def _resolve_call(self, call_id: str, payload: dict[str, Any]) -> None:
        self._last_calls[call_id] = payload
        for waiter in self._call_waiters.pop(call_id, []):
            if not waiter.done():
                waiter.set_result(payload)

    async def _handle_command(self, event: dict[str, Any], socket) -> None:
        event_id = event.get("eventId")
        if not isinstance(event_id, str) or len(event_id) != 32:
            return
        if event_id not in self._seen:
            command = {
                "type": event.get("command"), "eventId": event_id, "correlationId": event.get("correlationId"),
                "sessionId": event.get("sessionId"), "createdAt": event.get("createdAt"),
                "expiresAt": event.get("expiresAt"), "payload": event.get("payload"),
            }
            try:
                await self._runtime.handle(command)
            except Exception as exc:
                logger.warning("agent command %s failed: %s", event_id, type(exc).__name__)
                return
            self._seen.append(event_id)
            if len(self._seen) > self._max_seen:
                self._seen.pop(0)
        await socket.send(json.dumps({
            "type": "ack", "protocolVersion": PROTOCOL, "eventId": _event(),
            "commandEventId": event_id, "status": "done",
        }))

    async def _invoke_call(self, coroutine) -> None:
        try:
            await coroutine
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.warning("agent call handler failed: %s", type(exc).__name__)

    async def _send_call(self, kind: str, payload: dict[str, Any], event_id: str | None = None) -> None:
        control = self._control
        if control is None:
            raise RuntimeError("agent is not connected")
        await control.send(json.dumps({"type": kind, "protocolVersion": CALL_PROTOCOL, "eventId": event_id or _event(), "payload": payload}))

    async def dial_call(self, session_id: str | None = None, security_level: str = "standard", title: str | None = None, timeout: float = 10) -> dict[str, Any]:
        """Ask the server to ring the owner. Does not open a microphone."""
        if security_level not in ("standard", "elevated"):
            raise ValueError("security_level must be standard or elevated")
        if session_id is not None and (not isinstance(session_id, str) or len(session_id) > 64):
            raise ValueError("Invalid session id")
        if title is not None and len(title) > 160:
            raise ValueError("Title is too long")
        if self._control is None:
            raise RuntimeError("agent is not connected")
        request_id = _event(); loop = asyncio.get_running_loop(); waiter = loop.create_future(); self._dial_waiters[request_id] = waiter
        try:
            payload: dict[str, Any] = {"securityLevel": security_level}
            if session_id is not None: payload["sessionId"] = session_id
            if title is not None: payload["title"] = title
            await self._send_call("call.dial", payload, event_id=request_id)
            result = await asyncio.wait_for(waiter, timeout)
        finally:
            self._dial_waiters.pop(request_id, None)
        if not result.get("ok"):
            raise NotifyCallError(str(result.get("code") or "call_failed"))
        return result

    async def wait_call(self, call_id: str, timeout: float = 60) -> dict[str, Any]:
        """Wait for the first acceptance/end event for an agent-initiated call."""
        if len(call_id) != 32:
            raise ValueError("Invalid call id")
        if call_id in self._last_calls:
            return self._last_calls[call_id]
        loop = asyncio.get_running_loop(); waiter = loop.create_future(); self._call_waiters.setdefault(call_id, []).append(waiter)
        try:
            return await asyncio.wait_for(waiter, timeout)
        finally:
            waiters = self._call_waiters.get(call_id)
            if waiters is not None:
                if waiter in waiters: waiters.remove(waiter)
                if not waiters: self._call_waiters.pop(call_id, None)

    async def accept_call(self, call_id: str) -> None:
        await self._send_call("call.accept", {"callId": call_id})

    async def reject_call(self, call_id: str) -> None:
        await self._send_call("call.reject", {"callId": call_id})

    async def end_call(self, call_id: str) -> None:
        await self._send_call("call.end", {"callId": call_id})

    async def interrupt(self, call_id: str, generation: int) -> None:
        if generation < 0:
            raise ValueError("generation must be non-negative")
        await self._send_call("call.interrupt", {"callId": call_id, "generation": generation})

    @asynccontextmanager
    async def call_audio(self, call_id: str):
        if len(call_id) != 32:
            raise ValueError("Invalid call id")
        parsed = urlparse(self._base)
        socket = await websockets.connect(
            ("wss://" if parsed.scheme == "https" else "ws://") + parsed.netloc + f"/api/v1/agents/calls/{call_id}/audio",
            additional_headers={"Authorization": f"Bearer {self._token}"},
            ping_interval=20,
            ping_timeout=20,
            max_size=64 * 1024,
            close_timeout=5,
        )
        audio = CallAudio(socket)
        try:
            yield audio
        finally:
            await audio.close()


class CallAudio:
    """PCM16LE mono 16 kHz media: 640-byte frames with a uint32 seq/timestamp/generation header."""

    def __init__(self, socket):
        self._socket = socket
        self._sequence = 0
        self.generation = 0

    def set_generation(self, generation: int) -> None:
        if generation < 0 or generation > 0xFFFFFFFF:
            raise ValueError("generation must fit uint32")
        self.generation = generation

    async def send(self, pcm: bytes) -> None:
        if len(pcm) != CALL_AUDIO_BYTES:
            raise ValueError("Call audio frame must be 640 bytes")
        timestamp = int(time.monotonic() * 1000) & 0xFFFFFFFF
        await self._socket.send(struct.pack(">III", self._sequence, timestamp, self.generation) + pcm)
        self._sequence = (self._sequence + 1) & 0xFFFFFFFF

    async def stream(self) -> AsyncIterator[tuple[str, Any]]:
        async for message in self._socket:
            if isinstance(message, bytes):
                if len(message) != CALL_FRAME_BYTES:
                    raise ValueError("Invalid call audio frame")
                yield "audio", message[CALL_HEADER_BYTES:]
            else:
                yield "event", json.loads(message)

    async def close(self) -> None:
        try:
            await self._socket.close()
        except Exception:
            pass


def _event() -> str:
    return uuid.uuid4().hex
