"""Bridge one Notify call to the configured Asterisk PJSIP endpoint."""
import asyncio
import audioop
import json
import os
import re
import secrets
import struct
import time
from contextlib import suppress
from urllib.parse import urlsplit

import aiohttp

FRAME = struct.Struct("!III")
RTP = struct.Struct("!BBHII")
ID = re.compile(r"[0-9a-f]{32}")


class RtpPeer(asyncio.DatagramProtocol):
    def __init__(self):
        self.incoming = asyncio.Queue(50)
        self.remote = None
        self.last_packet = None
        self.sequence = secrets.randbits(16)
        self.timestamp = secrets.randbits(32)
        self.ssrc = secrets.randbits(32)
        self.decode_state = None
        self.encode_state = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if addr != self.remote or len(data) < 12 or data[0] != 0x80 or data[1] & 0x7f:
            return
        _, _, seq, _, ssrc = RTP.unpack_from(data)
        if self.last_packet and ssrc == self.last_packet[0]:
            distance = (seq - self.last_packet[1]) & 0xffff
            if distance == 0 or distance > 0x7fff:
                return
        self.last_packet = (ssrc, seq)
        if len(data) == 172:
            pcm, self.decode_state = audioop.ratecv(audioop.ulaw2lin(data[12:], 2), 2, 1, 8000, 16000, self.decode_state)
            if not self.incoming.full():
                self.incoming.put_nowait(pcm.ljust(640, b"\0"))

    def send(self, pcm):
        samples, self.encode_state = audioop.ratecv(pcm, 2, 1, 16000, 8000, self.encode_state)
        header = RTP.pack(0x80, 0, self.sequence, self.timestamp, self.ssrc)
        self.transport.sendto(header + audioop.lin2ulaw(samples, 2), self.remote)
        self.sequence = (self.sequence + 1) & 0xffff
        self.timestamp = (self.timestamp + 160) & 0xffffffff


class Ari:
    def __init__(self, session, port, user, password, app, assistant=None):
        self.session = session
        self.origin = f"http://127.0.0.1:{port}/ari/"
        self.auth = aiohttp.BasicAuth(user, password)
        self.app = app
        self.events = None
        self.ready = asyncio.Event()
        self.assistant = assistant
        self.incoming = asyncio.Queue(8)

    async def request(self, method, path, **params):
        async with self.session.request(method, self.origin + path, params=params, auth=self.auth,
                                        allow_redirects=False) as response:
            if method == "DELETE" and response.status == 404:
                return None
            response.raise_for_status()
            return await response.json() if response.content_type == "application/json" else None

    async def run(self):
        async with self.session.ws_connect(self.origin + "events", params={"app": self.app},
                                           auth=self.auth, heartbeat=10) as events:
            self.ready.set()
            async for message in events:
                if message.type != aiohttp.WSMsgType.TEXT:
                    break
                event = message.json()
                if event.get("type") == "StasisStart" and event.get("args", [])[:1] == ["phone-assistant"]:
                    channel = event.get("channel", {})
                    channel_id = channel.get("id", "")
                    if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", channel_id):
                        continue
                    if self.trusted_incoming(event) and not self.incoming.full():
                        self.incoming.put_nowait(channel_id)
                    else:
                        with suppress(aiohttp.ClientError, TimeoutError):
                            await self.request("DELETE", "channels/" + channel_id)
                elif self.events is not None:
                    try:
                        self.events.put_nowait(event)
                    except asyncio.QueueFull:
                        return

    def trusted_incoming(self, event):
        if self.assistant is None:
            return False
        extension, context, endpoint = self.assistant
        channel = event["channel"]
        return (event["args"] == ["phone-assistant", extension]
                and channel.get("state") != "Up"
                and channel.get("dialplan", {}).get("context") == context
                and channel.get("dialplan", {}).get("exten") == extension
                and re.fullmatch(re.escape(endpoint) + r"-[0-9a-fA-F]+", channel.get("name", "")) is not None)


async def media(socket, rtp, playback, call_id=None):
    started = time.monotonic()
    timing = call_id is not None and os.environ.get("NOTIFY_PHONE_TIMING") == "1"
    if timing:
        print(json.dumps({"callId": call_id, "stage": "media_clock_origin", "monotonicNs": int(started * 1e9)}), flush=True)

    async def upload():
        seq = 0
        while True:
            try:
                pcm = await asyncio.wait_for(rtp.incoming.get(), timeout=0.020)
            except TimeoutError:
                pcm = bytes(640)
            if not playback:
                await socket.send_bytes(FRAME.pack(seq, int((time.monotonic() - started) * 1000), 0) + pcm)
                seq += 1

    generation = 0
    reply = None
    last_voice = {}
    sender = asyncio.create_task(upload())
    try:
        async for message in socket:
            if sender.done():
                sender.result()
            if message.type == aiohttp.WSMsgType.TEXT:
                event = message.json()
                if event.get("type") == "interrupt":
                    generation = max(generation, event["generation"])
                elif timing and event.get("type") == "assistant_timing":
                    if len(last_voice) >= 16:
                        last_voice.pop(next(iter(last_voice)))
                    last_voice[event["turnId"]] = event["lastVoiceMediaMs"]
                elif timing and event.get("status") == "speaking" and event.get("userTurnId"):
                    reply = event["userTurnId"], event["generation"]
            elif message.type == aiohttp.WSMsgType.BINARY and len(message.data) == 652:
                _, _, frame_generation = FRAME.unpack_from(message.data)
                if frame_generation >= generation:
                    rtp.send(message.data[12:])
                    if reply is not None and reply[1] == frame_generation:
                        turn_id, _ = reply
                        tick = time.monotonic()
                        voice_ms = last_voice.pop(turn_id, None)
                        print(json.dumps({"callId": call_id, "turnId": turn_id, "stage": "first_rtp_sent",
                                          "monotonicNs": int(tick * 1e9),
                                          "lastVoiceToRtpMs": None if voice_ms is None else (tick - started) * 1000 - voice_ms}),
                              flush=True)
                        reply = None
    finally:
        sender.cancel()
        with suppress(asyncio.CancelledError):
            await sender


async def wait_for_answer(events, channel_id, deadline):
    while True:
        remaining = max(0, deadline - time.time())
        event = await asyncio.wait_for(events.get(), remaining)
        if event.get("type") == "Dial" and event.get("peer", {}).get("id") == channel_id:
            reason = {"BUSY": "busy", "NOANSWER": "missed", "CANCEL": "rejected",
                      "CHANUNAVAIL": "asterisk_unavailable", "CONGESTION": "asterisk_unavailable"}
            if event.get("dialstatus") in reason:
                return reason[event["dialstatus"]]
        if event.get("channel", {}).get("id") == channel_id:
            if event["type"] == "StasisStart" and event["channel"]["state"] == "Up":
                return None
            if event["type"] == "ChannelDestroyed":
                return {17: "busy", 18: "missed", 19: "missed", 21: "rejected"}.get(event.get("cause"), "disconnected")


async def wait_for_hangup(events, channel_id):
    while True:
        event = await events.get()
        if event.get("channel", {}).get("id") == channel_id and event["type"] in ("StasisEnd", "ChannelDestroyed"):
            return


async def run_call(ari, session, origin, headers, control, invite, endpoint, inbound=None):
    call_id, invite_id = invite["callId"], invite["inviteId"]
    if not ID.fullmatch(call_id) or not ID.fullmatch(invite_id):
        raise ValueError("invalid call identifier")
    prefix = "notify-" + call_id
    phone_id, media_id, bridge_id = inbound or prefix + "-phone", prefix + "-media", prefix
    reason = "asterisk_unavailable"
    transport = None
    events = asyncio.Queue(128)
    ari.events = events
    try:
        if inbound is None:
            deadline = invite["expiresAt"] / 1000
            if deadline <= time.time():
                reason = "missed"
                return
            await ari.request("POST", "channels/" + phone_id, endpoint=endpoint, app=ari.app,
                              callerId="Notify", timeout=max(1, int(deadline - time.time())))
            reason = await wait_for_answer(events, phone_id, deadline)
            if reason:
                return
        reason = "disconnected"
        transport, rtp = await asyncio.get_running_loop().create_datagram_endpoint(
            RtpPeer, local_addr=("127.0.0.1", 0))
        port = transport.get_extra_info("sockname")[1]
        await ari.request("POST", "channels/externalMedia", channelId=media_id, app=ari.app,
                          external_host=f"127.0.0.1:{port}", format="ulaw")
        local = await ari.request("GET", f"channels/{media_id}/variable", variable="UNICASTRTP_LOCAL_PORT")
        rtp.remote = ("127.0.0.1", int(local["value"]))
        await ari.request("POST", "bridges/" + bridge_id, type="mixing,proxy_media")
        await ari.request("POST", f"bridges/{bridge_id}/addChannel", channel=f"{phone_id},{media_id}")
        async with session.ws_connect(f"{origin}/api/v1/telephony/calls/{call_id}/audio", headers=headers,
                                      params={"inviteId": invite_id}, heartbeat=10) as socket:
            if inbound is not None:
                await ari.request("POST", f"channels/{phone_id}/answer")
            tasks = [asyncio.create_task(wait_for_hangup(events, phone_id)),
                     asyncio.create_task(media(socket, rtp, invite.get("mode") == "playback", call_id if inbound else None))]
            try:
                completed, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                for task in completed:
                    task.result()
                reason = "ended"
            finally:
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
    except TimeoutError:
        reason = "missed" if inbound is None and time.time() >= invite["expiresAt"] / 1000 else "asterisk_unavailable"
    except (aiohttp.ClientError, ValueError, KeyError):
        reason = "asterisk_unavailable"
    finally:
        ari.events = None
        if transport is not None:
            transport.close()
        await asyncio.gather(
            ari.request("DELETE", "channels/" + phone_id),
            ari.request("DELETE", "channels/" + media_id),
            ari.request("DELETE", "bridges/" + bridge_id), return_exceptions=True)
        if not control.closed:
            await control.send_json({"type": "ended", "callId": call_id, "reason": reason})


async def session_loop(session, ari, origin, headers, endpoint):
    ari_events = asyncio.create_task(ari.run())
    call = None
    call_id = None
    pending = None
    try:
        ready = asyncio.create_task(ari.ready.wait())
        done, _ = await asyncio.wait([ready, ari_events], return_when=asyncio.FIRST_COMPLETED)
        if ari_events in done:
            ready.cancel()
            ari_events.result()
            return
        async with session.ws_connect(origin + "/api/v1/telephony/connect", headers=headers, heartbeat=10) as control:
            async def incoming_call(channel, request_id, ack):
                try:
                    await control.send_json({"type": "incoming", "requestId": request_id, "extension": ari.assistant[0]})
                    invite = await asyncio.wait_for(ack, 8)
                    if invite.get("ok") is True:
                        await run_call(ari, session, origin, headers, control, invite, endpoint, inbound=channel)
                except (aiohttp.ClientError, TimeoutError, ValueError, KeyError):
                    pass
                finally:
                    with suppress(aiohttp.ClientError, TimeoutError):
                        await ari.request("DELETE", "channels/" + channel)

            async def read_incoming():
                nonlocal call, call_id, pending
                while True:
                    channel = await ari.incoming.get()
                    if call is not None and not call.done():
                        with suppress(aiohttp.ClientError, TimeoutError):
                            await ari.request("DELETE", "channels/" + channel, reason="busy")
                        continue
                    call_id = None
                    request_id = secrets.token_hex(16)
                    ack = asyncio.get_running_loop().create_future()
                    pending = (request_id, ack)
                    call = asyncio.create_task(incoming_call(channel, request_id, ack))

            async def heartbeat():
                while not ari_events.done():
                    await control.send_json({"type": "heartbeat"})
                    await asyncio.sleep(5)
                await control.close()

            pulse = asyncio.create_task(heartbeat())
            incoming = asyncio.create_task(read_incoming())
            try:
                async for msg in control:
                    if msg.type != aiohttp.WSMsgType.TEXT:
                        break
                    frame = msg.json()
                    if frame["type"] == "invite":
                        if call is not None and not call.done():
                            await control.send_json({"type": "ended", "callId": frame["callId"], "reason": "busy"})
                            continue
                        call_id = frame["callId"]
                        call = asyncio.create_task(run_call(ari, session, origin, headers, control, frame, endpoint))
                    elif frame["type"] == "incoming_ack" and pending and frame.get("requestId") == pending[0]:
                        _, ack = pending
                        pending = None
                        if not ack.done():
                            call_id = frame.get("callId")
                            ack.set_result(frame)
                    elif frame["type"] == "end" and frame["callId"] == call_id and call is not None:
                        if not call.done():
                            call.cancel()
                        await asyncio.gather(call, return_exceptions=True)
            finally:
                pulse.cancel()
                incoming.cancel()
                await asyncio.gather(pulse, incoming, return_exceptions=True)
    finally:
        if call is not None:
            call.cancel()
            await asyncio.gather(call, return_exceptions=True)
        ari_events.cancel()
        await asyncio.gather(ari_events, return_exceptions=True)
        while not ari.incoming.empty():
            with suppress(aiohttp.ClientError, TimeoutError):
                await ari.request("DELETE", "channels/" + ari.incoming.get_nowait())


async def main():
    origin = os.environ["NOTIFY_URL"].rstrip("/")
    parts = urlsplit(origin)
    endpoint = os.environ["ASTERISK_ENDPOINT"]
    if (parts.scheme != "https" or not parts.hostname or parts.username or parts.path
            or parts.query or parts.fragment or not re.fullmatch(r"PJSIP/[A-Za-z0-9_.-]+", endpoint)):
        raise ValueError("invalid Notify origin or PJSIP endpoint")
    headers = {"Authorization": "Bearer " + os.environ["NOTIFY_BRIDGE_TOKEN"]}
    extension = os.environ.get("ASTERISK_ASSISTANT_EXTENSION", "")
    context = os.environ.get("ASTERISK_ASSISTANT_CONTEXT", "")
    assistant = None
    if extension or context:
        if not re.fullmatch(r"[0-9]{2,8}", extension) or not re.fullmatch(r"[A-Za-z0-9_.-]+", context):
            raise ValueError("invalid phone assistant dialplan configuration")
        assistant = (extension, context, endpoint)
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=5)) as session:
        while True:
            try:
                ari = Ari(session, int(os.environ.get("ARI_PORT", "8088")),
                          os.environ["ARI_USER"], os.environ["ARI_PASSWORD"], "alxprgs-notify", assistant)
                await session_loop(session, ari, origin, headers, endpoint)
            except (aiohttp.ClientError, TimeoutError, OSError, ValueError, KeyError, asyncio.QueueFull) as error:
                print("Notify bridge reconnect:", type(error).__name__, flush=True)
            await asyncio.sleep(3)


if __name__ == "__main__":
    asyncio.run(main())
