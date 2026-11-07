import asyncio
import json
import struct
import unittest

import websockets

from alxprgs_notify import AgentConnector, CallAudio, CallbackRuntime, CALL_PROTOCOL, PROTOCOL, NotifyCallError


class FakeSocket:
    def __init__(self, messages=None):
        self.sent = []
        self.messages = list(messages or [])

    async def send(self, data):
        self.sent.append(data)

    async def close(self):
        return None

    def __aiter__(self):
        return self

    async def __anext__(self):
        if not self.messages:
            raise StopAsyncIteration
        return self.messages.pop(0)


class AgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_call_audio_generation_header_and_stream(self):
        socket = FakeSocket()
        audio = CallAudio(socket)
        await audio.send(b"\x01" * 640)
        audio.set_generation(7)
        await audio.send(b"\x02" * 640)
        self.assertEqual(len(socket.sent), 2)
        first = struct.unpack(">III", socket.sent[0][:12]); second = struct.unpack(">III", socket.sent[1][:12])
        self.assertEqual(first[0], 0); self.assertEqual(second[0], 1); self.assertEqual(first[2], 0); self.assertEqual(second[2], 7)
        with self.assertRaises(ValueError):
            await audio.send(b"short")
        with self.assertRaises(ValueError):
            audio.set_generation(-1)
        stream_socket = FakeSocket([b"\0" * 12 + b"\x03" * 640, json.dumps({"type": "interrupt", "generation": 2})])
        events = [(kind, payload) async for kind, payload in CallAudio(stream_socket).stream()]
        self.assertEqual(events[0][0], "audio")
        self.assertEqual(len(events[0][1]), 640)
        self.assertEqual(events[0][1][:4], b"\x03" * 4)
        self.assertEqual(events[1], ("event", {"type": "interrupt", "generation": 2}))

    async def test_agent_initiated_call_dial_ack_and_accept(self):
        call_id = "d" * 32
        accepted = asyncio.Event()
        control_events = []

        class Handler:
            async def on_invite(self, connector, call):
                return None
            async def on_interrupt(self, call_id, generation):
                return None
            async def on_ended(self, call_id, reason):
                return None
            async def on_accepted(self, connector, call):
                self.call = call
                accepted.set()

        handler = Handler()

        async def server(socket):
            hello_frame = json.loads(await socket.recv()); self.assertIn(CALL_PROTOCOL, hello_frame["callProtocols"])
            await socket.send(json.dumps({"eventId": "h" * 32, "protocolVersion": PROTOCOL, "type": "hello_ack", "heartbeatSeconds": 30, "offlineSeconds": 90, "callProtocol": CALL_PROTOCOL, "supportedCallProtocols": [CALL_PROTOCOL]}))
            raw = json.loads(await socket.recv()); control_events.append(raw)
            await socket.send(json.dumps({"eventId": "a" * 32, "protocolVersion": CALL_PROTOCOL, "type": "call.dial_ack", "payload": {"requestEventId": raw["eventId"], "ok": True, "callId": call_id, "inviteId": "i" * 32, "status": "ringing", "expiresAt": 9999999999999}}))
            await socket.send(json.dumps({"eventId": "b" * 32, "protocolVersion": CALL_PROTOCOL, "type": "call.accepted", "payload": {"callId": call_id, "deviceId": "p" * 32, "deviceName": "Pixel"}}))
            raw = json.loads(await socket.recv()); control_events.append(raw)
            await socket.send(json.dumps({"eventId": "c" * 32, "protocolVersion": CALL_PROTOCOL, "type": "call.dial_ack", "payload": {"requestEventId": raw["eventId"], "ok": False, "code": "busy"}}))
            await asyncio.sleep(0.2)

        async def noop(_command):
            return None

        async with websockets.serve(server, "127.0.0.1", 0) as srv:
            port = srv.sockets[0].getsockname()[1]
            connector = AgentConnector(f"http://127.0.0.1:{port}", "nt_test", agent_id="agent-1", name="Call agent", capabilities=["realtime_audio"], runtime=CallbackRuntime(noop), call_handler=handler)
            task = asyncio.create_task(connector.run())
            try:
                for _ in range(100):
                    if connector._control is not None: break
                    await asyncio.sleep(0.02)
                ack = await connector.dial_call(session_id="task-1", security_level="elevated", title="Callback", icon_id="a" * 64)
                self.assertEqual(ack["callId"], call_id); self.assertEqual(ack["status"], "ringing")
                event = await connector.wait_call(call_id, timeout=5)
                self.assertEqual(event["deviceName"], "Pixel")
                await asyncio.wait_for(accepted.wait(), timeout=5)
                with self.assertRaises(NotifyCallError) as busy:
                    await connector.dial_call()
                self.assertEqual(busy.exception.code, "busy")
            finally:
                await connector.stop(); task.cancel(); await asyncio.gather(task, return_exceptions=True)
        self.assertEqual(control_events[0]["type"], "call.dial"); self.assertEqual(control_events[0]["payload"]["sessionId"], "task-1"); self.assertEqual(control_events[0]["payload"]["securityLevel"], "elevated")
        self.assertEqual(control_events[0]["payload"]["iconId"], "a" * 64)
        self.assertEqual(control_events[1]["type"], "call.dial")

    async def test_old_agent_without_call_protocol_keeps_non_voice_commands(self):
        handled = asyncio.Event(); command_id = "o" * 32

        class Handler:
            async def on_invite(self, connector, call): return None
            async def on_interrupt(self, call_id, generation): return None
            async def on_ended(self, call_id, reason): return None

        async def runtime(command):
            self.assertEqual(command["type"], "task")
            handled.set()

        async def server(socket):
            await socket.recv()
            await socket.send(json.dumps({"eventId": "h" * 32, "protocolVersion": PROTOCOL, "type": "hello_ack", "heartbeatSeconds": 30, "offlineSeconds": 90}))
            await socket.send(json.dumps({"eventId": command_id, "protocolVersion": PROTOCOL, "type": "command", "command": "task", "createdAt": 1, "expiresAt": 9999999999999, "payload": {"taskId": "t1"}}))
            await socket.recv()  # ack
            await asyncio.sleep(0.2)

        async with websockets.serve(server, "127.0.0.1", 0) as srv:
            port = srv.sockets[0].getsockname()[1]
            connector = AgentConnector(f"http://127.0.0.1:{port}", "nt_test", agent_id="agent-old", name="Old agent", capabilities=["text_tasks", "realtime_audio"], runtime=CallbackRuntime(runtime), call_handler=Handler())
            task = asyncio.create_task(connector.run())
            try:
                await asyncio.wait_for(handled.wait(), timeout=5)
                self.assertIsNone(connector.call_protocol)
                with self.assertRaises(NotifyCallError) as rejected:
                    await connector.dial_call()
                self.assertEqual(rejected.exception.code, "call_protocol_unsupported")
            finally:
                await connector.stop(); task.cancel(); await asyncio.gather(task, return_exceptions=True)

    async def test_call_invite_accept_and_end(self):
        call_id = "c" * 32
        accepted = asyncio.Event()
        ended = asyncio.Event()

        class Handler:
            async def on_invite(self, connector, call):
                self.call = call
                await connector.accept_call(call["callId"])
                accepted.set()

            async def on_interrupt(self, call_id, generation):
                return None

            async def on_ended(self, ended_call_id, reason):
                self.reason = reason
                ended.set()

        handler = Handler()
        control_events = []

        async def server(socket):
            hello_frame = json.loads(await socket.recv()); self.assertIn(CALL_PROTOCOL, hello_frame["callProtocols"])
            await socket.send(json.dumps({"eventId": "h" * 32, "protocolVersion": PROTOCOL, "type": "hello_ack", "heartbeatSeconds": 30, "offlineSeconds": 90, "callProtocol": CALL_PROTOCOL, "supportedCallProtocols": [CALL_PROTOCOL]}))
            await socket.send(json.dumps({"eventId": "i" * 32, "protocolVersion": PROTOCOL, "type": "call.invite", "payload": {"callId": call_id, "sessionId": "task-1", "securityLevel": "standard"}}))
            control_events.append(json.loads(await socket.recv()))
            await socket.send(json.dumps({"eventId": "e" * 32, "protocolVersion": PROTOCOL, "type": "call.ended", "payload": {"callId": call_id, "reason": "disconnected"}}))
            await asyncio.sleep(0.2)

        async def noop(_command):
            return None

        async with websockets.serve(server, "127.0.0.1", 0) as srv:
            port = srv.sockets[0].getsockname()[1]
            connector = AgentConnector(f"http://127.0.0.1:{port}", "nt_test", agent_id="agent-1", name="Call agent", capabilities=["realtime_audio"], runtime=CallbackRuntime(noop), call_handler=handler)
            task = asyncio.create_task(connector.run())
            try:
                await asyncio.wait_for(accepted.wait(), timeout=5)
                await asyncio.wait_for(ended.wait(), timeout=5)
            finally:
                await connector.stop(); task.cancel(); await asyncio.gather(task, return_exceptions=True)
        self.assertEqual(control_events[0]["type"], "call.accept")
        self.assertEqual(control_events[0]["protocolVersion"], CALL_PROTOCOL)
        self.assertEqual(control_events[0]["payload"], {"callId": call_id})
        self.assertEqual(handler.call["sessionId"], "task-1")
        self.assertEqual(handler.reason, "disconnected")

    async def test_hello_heartbeat_duplicate_command_is_acked_once_and_handled_once(self):
        connections = []
        commands = []
        acks = []
        auth = []
        all_acks = asyncio.Event()
        heartbeat = asyncio.Event()
        command_id = "a" * 32

        async def process_request(connection, request):
            auth.append(request.headers.get("Authorization"))
            return None

        async def handler(socket):
            connections.append(json.loads(await socket.recv()))
            await socket.send(json.dumps({"eventId": "s" * 32, "protocolVersion": PROTOCOL, "type": "hello_ack", "heartbeatSeconds": 1, "offlineSeconds": 3}))
            command = {"eventId": command_id, "protocolVersion": PROTOCOL, "type": "command", "command": "task", "createdAt": 1, "expiresAt": 9999999999999, "payload": {"taskId": "t1"}}
            await socket.send(json.dumps(command))
            await socket.send(json.dumps(command))
            async for raw in socket:
                message = json.loads(raw)
                if message.get("type") == "heartbeat":
                    heartbeat.set()
                if message.get("type") == "ack":
                    acks.append(message)
                    if len(acks) == 2:
                        all_acks.set()

        async def capture(command):
            commands.append(command)

        async with websockets.serve(handler, "127.0.0.1", 0, process_request=process_request) as server:
            port = server.sockets[0].getsockname()[1]
            connector = AgentConnector(f"http://127.0.0.1:{port}", "nt_test_token", agent_id="agent-1", name="Test agent", capabilities=["text_tasks"], sessions=[{"id": "task-1", "name": "Build"}], runtime=CallbackRuntime(capture))
            task = asyncio.create_task(connector.run())
            try:
                await asyncio.wait_for(all_acks.wait(), timeout=6)
                await asyncio.wait_for(heartbeat.wait(), timeout=4)
            finally:
                await connector.stop()
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
        self.assertEqual(auth, ["Bearer nt_test_token"])
        self.assertEqual(connections[0]["type"], "hello")
        self.assertEqual(connections[0]["protocolVersion"], PROTOCOL)
        self.assertEqual(connections[0]["agentId"], "agent-1")
        self.assertEqual(connections[0]["capabilities"], ["text_tasks"])
        self.assertEqual(connections[0]["sessions"], [{"id": "task-1", "name": "Build"}])
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0]["payload"], {"taskId": "t1"})
        self.assertEqual([ack["commandEventId"] for ack in acks], [command_id, command_id])
        self.assertTrue(all(ack["status"] == "done" for ack in acks))

    async def test_connector_reconnects_after_network_close(self):
        connections = 0
        reconnected = asyncio.Event()

        async def handler(socket):
            nonlocal connections
            connections += 1
            await socket.recv()
            await socket.send(json.dumps({"eventId": "s" * 32, "protocolVersion": PROTOCOL, "type": "hello_ack", "heartbeatSeconds": 30, "offlineSeconds": 90}))
            if connections == 1:
                await socket.close()
            else:
                reconnected.set()
                await asyncio.sleep(5)

        async def noop(_command):
            return None

        async with websockets.serve(handler, "127.0.0.1", 0) as server:
            port = server.sockets[0].getsockname()[1]
            connector = AgentConnector(f"http://127.0.0.1:{port}", "nt_test_token", agent_id="agent-1", name="Test", capabilities=["text_tasks"], runtime=CallbackRuntime(noop), heartbeat_seconds=30)
            task = asyncio.create_task(connector.run())
            try:
                await asyncio.wait_for(reconnected.wait(), timeout=8)
            finally:
                await connector.stop()
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
        self.assertGreaterEqual(connections, 2)

    def test_external_plain_http_and_bad_heartbeat_are_rejected(self):
        runtime = CallbackRuntime(lambda _: asyncio.sleep(0))
        with self.assertRaises(ValueError):
            AgentConnector("http://example.com", "token", agent_id="a", name="A", capabilities=[], runtime=runtime)
        with self.assertRaises(ValueError):
            AgentConnector("https://example.com", "token", agent_id="a", name="A", capabilities=[], runtime=runtime, heartbeat_seconds=0)
