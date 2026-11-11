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
    def __init__(self, session, port, user, password, app):
        self.session = session
        self.origin = f"http://127.0.0.1:{port}/ari/"
        self.auth = aiohttp.BasicAuth(user, password)
        self.app = app
        self.events = None
        self.ready = asyncio.Event()

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
                if self.events is not None:
                    event = message.json()
                    self.events.put_nowait(event)


async def media(socket, rtp, playback):
    async def upload():
        seq = 0
        started = time.monotonic()
        while True:
            try:
                pcm = await asyncio.wait_for(rtp.incoming.get(), timeout=0.020)
            except TimeoutError:
                pcm = bytes(640)
            if not playback:
                await socket.send_bytes(FRAME.pack(seq, int((time.monotonic() - started) * 1000), 0) + pcm)
                seq += 1

    generation = 0
    sender = asyncio.create_task(upload())
    try:
        async for message in socket:
            if sender.done():
                sender.result()
            if message.type == aiohttp.WSMsgType.TEXT:
                event = message.json()
                if event.get("type") == "interrupt":
                    generation = max(generation, event["generation"])
            elif message.type == aiohttp.WSMsgType.BINARY and len(message.data) == 652:
                _, _, frame_generation = FRAME.unpack_from(message.data)
                if frame_generation >= generation:
                    rtp.send(message.data[12:])
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


async def run_call(ari, session, origin, headers, control, invite, endpoint):
    call_id, invite_id = invite["callId"], invite["inviteId"]
    if not ID.fullmatch(call_id) or not ID.fullmatch(invite_id):
        raise ValueError("invalid call identifier")
    prefix = "notify-" + call_id
    phone_id, media_id, bridge_id = prefix + "-phone", prefix + "-media", prefix
    reason = "asterisk_unavailable"
    transport = None
    events = asyncio.Queue(128)
    ari.events = events
    try:
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
            tasks = [asyncio.create_task(media(socket, rtp, invite.get("mode") == "playback")),
                     asyncio.create_task(wait_for_hangup(events, phone_id))]
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
        reason = "missed" if time.time() >= invite["expiresAt"] / 1000 else "asterisk_unavailable"
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
    try:
        ready = asyncio.create_task(ari.ready.wait())
        done, _ = await asyncio.wait([ready, ari_events], return_when=asyncio.FIRST_COMPLETED)
        if ari_events in done:
            ready.cancel()
            ari_events.result()
            return
        async with session.ws_connect(origin + "/api/v1/telephony/connect", headers=headers, heartbeat=10) as control:
            async def heartbeat():
                while not ari_events.done():
                    await control.send_json({"type": "heartbeat"})
                    await asyncio.sleep(5)
                await control.close()

            pulse = asyncio.create_task(heartbeat())
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
                    elif frame["type"] == "end" and frame["callId"] == call_id and call is not None:
                        if not call.done():
                            call.cancel()
                        await asyncio.gather(call, return_exceptions=True)
            finally:
                pulse.cancel()
                await asyncio.gather(pulse, return_exceptions=True)
    finally:
        if call is not None:
            call.cancel()
            await asyncio.gather(call, return_exceptions=True)
        ari_events.cancel()
        await asyncio.gather(ari_events, return_exceptions=True)


async def main():
    origin = os.environ["NOTIFY_URL"].rstrip("/")
    parts = urlsplit(origin)
    endpoint = os.environ["ASTERISK_ENDPOINT"]
    if (parts.scheme != "https" or not parts.hostname or parts.username or parts.path
            or parts.query or parts.fragment or not re.fullmatch(r"PJSIP/[A-Za-z0-9_.-]+", endpoint)):
        raise ValueError("invalid Notify origin or PJSIP endpoint")
    headers = {"Authorization": "Bearer " + os.environ["NOTIFY_BRIDGE_TOKEN"]}
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=5)) as session:
        while True:
            try:
                ari = Ari(session, int(os.environ.get("ARI_PORT", "8088")),
                          os.environ["ARI_USER"], os.environ["ARI_PASSWORD"], "alxprgs-notify")
                await session_loop(session, ari, origin, headers, endpoint)
            except (aiohttp.ClientError, TimeoutError, OSError, ValueError, KeyError, asyncio.QueueFull) as error:
                print("Notify bridge reconnect:", type(error).__name__, flush=True)
            await asyncio.sleep(3)


if __name__ == "__main__":
    asyncio.run(main())
