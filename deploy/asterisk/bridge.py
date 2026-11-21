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
        self.pending = bytearray()
        self.received = self.sent = self.dropped = 0
        self.sequence = secrets.randbits(16)
        self.timestamp = secrets.randbits(32)
        self.ssrc = secrets.randbits(32)
        self.decode_state = None
        self.encode_state = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if addr != self.remote:
            return
        if len(data) < RTP.size or data[0] >> 6 != 2 or data[1] & 0x7f:
            self.dropped += 1
            return
        offset = RTP.size + 4 * (data[0] & 0x0f)
        end = len(data)
        if data[0] & 0x20:
            padding = data[-1]
            if not padding:
                self.dropped += 1
                return
            end -= padding
        if data[0] & 0x10:
            if offset + 4 > end:
                self.dropped += 1
                return
            offset += 4 + 4 * int.from_bytes(data[offset + 2:offset + 4], "big")
        if not 0 < end - offset <= 960:
            self.dropped += 1
            return
        _, _, seq, _, ssrc = RTP.unpack_from(data)
        if self.last_packet and ssrc == self.last_packet[0]:
            distance = (seq - self.last_packet[1]) & 0xffff
            if distance == 0 or distance > 0x7fff:
                self.dropped += 1
                return
            self.dropped += distance - 1
        if self.last_packet and (ssrc != self.last_packet[0] or distance != 1):
            self.pending.clear()
            self.decode_state = None
        self.last_packet = (ssrc, seq)
        self.received += 1
        self.pending.extend(data[offset:end])
        while len(self.pending) >= 160:
            pcm, self.decode_state = audioop.ratecv(audioop.ulaw2lin(self.pending[:160], 2), 2, 1, 8000, 16000, self.decode_state)
            del self.pending[:160]
            if not self.incoming.full():
                self.incoming.put_nowait(pcm.ljust(640, b"\0"))
            else:
                self.dropped += 1

    def send(self, pcm):
        samples, self.encode_state = audioop.ratecv(pcm, 2, 1, 16000, 8000, self.encode_state)
        header = RTP.pack(0x80, 0, self.sequence, self.timestamp, self.ssrc)
        self.transport.sendto(header + audioop.lin2ulaw(samples, 2), self.remote)
        self.sent += 1
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
        self.assistant_routes = assistant or {}
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
                if event.get("type") == "StasisStart" and isinstance(event.get("args"), list) and event["args"][:1] == ["phone-assistant"]:
                    channel = event.get("channel", {})
                    channel_id = channel.get("id", "")
                    if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", channel_id):
                        continue
                    if self.trusted_incoming(event) and not self.incoming.full():
                        self.incoming.put_nowait((channel_id, event["args"][1]))
                    else:
                        print("Phone assistant SIP invite rejected by bridge routing policy", flush=True)
                        with suppress(aiohttp.ClientError, TimeoutError):
                            await self.request("DELETE", "channels/" + channel_id)
                elif self.events is not None:
                    try:
                        self.events.put_nowait(event)
                    except asyncio.QueueFull:
                        return

    def trusted_incoming(self, event):
        args = event.get("args", [])
        if (not isinstance(args, list) or len(args) != 2 or args[0] != "phone-assistant"
                or not isinstance(args[1], str) or args[1] not in self.assistant_routes):
            return False
        extension = args[1]
        context, endpoint = self.assistant_routes[extension]
        channel = event["channel"]
        return (channel.get("state") != "Up"
                and channel.get("dialplan", {}).get("context") == context
                and channel.get("dialplan", {}).get("exten") == extension
                and re.fullmatch(re.escape(endpoint) + r"-[0-9a-fA-F]+", channel.get("name", "")) is not None)


async def media(socket, rtp, playback, call_id=None, trace=None):
    started = time.monotonic()
    timing = call_id is not None and os.environ.get("NOTIFY_PHONE_TIMING") == "1"
    if timing:
        print(json.dumps({"callId": call_id, "stage": "media_clock_origin", "monotonicNs": int(started * 1e9)}), flush=True)

    async def upload():
        seq = 0
        has_rtp = False
        while True:
            try:
                pcm = await asyncio.wait_for(rtp.incoming.get(), timeout=0.020)
            except TimeoutError:
                pcm = bytes(640)
            else:
                if not has_rtp:
                    has_rtp = True
                    if trace is not None:
                        await trace("rtp_first_in")
            if not playback:
                await socket.send_bytes(FRAME.pack(seq, int((time.monotonic() - started) * 1000), 0) + pcm)
                seq += 1

    generation = 0
    reply = None
    playback_turn = None
    playback_started = None
    playback_last_sent = None
    playback_packets = 0
    last_voice = {}

    async def finish_playback(status):
        nonlocal playback_turn, playback_started, playback_last_sent, playback_packets
        current = playback_turn
        began = playback_started
        last = playback_last_sent
        count = playback_packets
        playback_turn = playback_started = playback_last_sent = None
        playback_packets = 0
        if trace is not None and current is not None:
            await trace("rtp_playback", status=status, turnId=current[0], replyId=current[1],
                        durationMs=None if began is None else (last - began) * 1000, sent=count)

    async def download():
        nonlocal generation, reply, playback_turn, playback_started, playback_last_sent, playback_packets
        async for message in socket:
            if message.type == aiohttp.WSMsgType.ERROR:
                raise socket.exception() or ConnectionError("media WebSocket failed")
            if message.type == aiohttp.WSMsgType.TEXT:
                event = message.json()
                if event.get("type") == "interrupt":
                    generation = max(generation, event["generation"])
                    if playback_turn is not None and playback_turn[2] < generation:
                        await finish_playback("interrupted")
                elif timing and event.get("type") == "assistant_timing":
                    if len(last_voice) >= 16:
                        last_voice.pop(next(iter(last_voice)))
                    last_voice[event["turnId"]] = event["lastVoiceMediaMs"]
                elif event.get("type") == "conversation" and event.get("status") == "speaking":
                    next_generation = event["generation"]
                    if next_generation >= generation:
                        await finish_playback("interrupted")
                        playback_turn = event.get("userTurnId"), event["turnId"], next_generation, event.get("expectedFrames")
                        if timing and playback_turn[0]:
                            reply = playback_turn[0], next_generation
                elif (event.get("type") == "conversation" and event.get("status") == "idle"
                      and playback_turn is not None and event.get("generation") == playback_turn[2]):
                    await finish_playback("completed" if playback_turn[3] == playback_packets else "cancelled")
            elif message.type == aiohttp.WSMsgType.BINARY and len(message.data) == 652:
                _, _, frame_generation = FRAME.unpack_from(message.data)
                if frame_generation >= generation:
                    rtp.send(message.data[12:])
                    if playback_turn is not None and playback_turn[2] == frame_generation:
                        playback_packets += 1
                        playback_last_sent = time.monotonic()
                        if playback_started is None:
                            playback_started = playback_last_sent
                            if trace is not None:
                                await trace("rtp_first_out", turnId=playback_turn[0], replyId=playback_turn[1])
                        if playback_packets == playback_turn[3]:
                            await finish_playback("completed")
                    elif rtp.sent == 1 and trace is not None:
                        await trace("rtp_first_out")
                    if reply is not None and reply[1] == frame_generation:
                        turn_id, _ = reply
                        tick = time.monotonic()
                        voice_ms = last_voice.pop(turn_id, None)
                        print(json.dumps({"callId": call_id, "turnId": turn_id, "stage": "first_rtp_sent",
                                          "monotonicNs": int(tick * 1e9),
                                          "lastVoiceToRtpMs": None if voice_ms is None else (tick - started) * 1000 - voice_ms}),
                              flush=True)
                        reply = None
    tasks = [asyncio.create_task(download())]
    if not playback:
        tasks.append(asyncio.create_task(upload()))
    try:
        completed, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in completed:
            task.result()
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await finish_playback("cancelled")


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
    rtp = None
    dial_started = time.monotonic()

    async def trace(stage, **fields):
        if invite.get("trace") is True and not control.closed:
            with suppress(aiohttp.ClientError, TimeoutError, ConnectionError):
                await asyncio.wait_for(control.send_json({"type": "trace", "callId": call_id, "stage": stage, **fields}), 1)

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
            await trace("sip_ringing")
            reason = await wait_for_answer(events, phone_id, deadline)
            if reason:
                return
            await trace("sip_answered", durationMs=(time.monotonic() - dial_started) * 1000)
        else:
            await trace("sip_ringing")
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
        if inbound is not None:
            await ari.request("POST", f"channels/{phone_id}/answer")
            await trace("sip_answered")
        await trace("rtp_ready")
        async with session.ws_connect(f"{origin}/api/v1/telephony/calls/{call_id}/audio", headers=headers,
                                      params={"inviteId": invite_id}, heartbeat=10) as socket:
            tasks = [asyncio.create_task(wait_for_hangup(events, phone_id)),
                     asyncio.create_task(media(socket, rtp, invite.get("mode") == "playback", call_id if inbound else None, trace))]
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
    except (aiohttp.ClientError, ConnectionError, ValueError, KeyError):
        reason = "asterisk_unavailable"
    finally:
        ari.events = None
        if rtp is not None:
            await trace("rtp_frames", sent=rtp.sent, received=rtp.received, dropped=rtp.dropped)
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
            async def incoming_call(channel, extension, request_id, ack):
                try:
                    # Send SIP alerting before waiting for the agent. No media is sent until answer.
                    await ari.request("POST", f"channels/{channel}/ring")
                    await control.send_json({"type": "incoming", "requestId": request_id, "extension": extension})
                    invite = await asyncio.wait_for(ack, 8)
                    if invite.get("ok") is True:
                        await run_call(ari, session, origin, headers, control, invite, endpoint, inbound=channel)
                    else:
                        code = invite.get("code")
                        if not isinstance(code, str) or not re.fullmatch(r"[a-z_]{1,40}", code):
                            code = "unknown"
                        print(f"Phone assistant SIP request {request_id} rejected: {code}", flush=True)
                except (aiohttp.ClientError, TimeoutError, ValueError, KeyError) as error:
                    print(f"Phone assistant SIP request {request_id} failed: {type(error).__name__}", flush=True)
                finally:
                    with suppress(aiohttp.ClientError, TimeoutError):
                        await ari.request("DELETE", "channels/" + channel)

            async def read_incoming():
                nonlocal call, call_id, pending
                while True:
                    channel, extension = await ari.incoming.get()
                    if call is not None and not call.done():
                        with suppress(aiohttp.ClientError, TimeoutError):
                            await ari.request("DELETE", "channels/" + channel, reason="busy")
                        continue
                    call_id = None
                    request_id = secrets.token_hex(16)
                    ack = asyncio.get_running_loop().create_future()
                    pending = (request_id, ack)
                    call = asyncio.create_task(incoming_call(channel, extension, request_id, ack))

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
            channel, _ = ari.incoming.get_nowait()
            with suppress(aiohttp.ClientError, TimeoutError):
                await ari.request("DELETE", "channels/" + channel)


def assistant_routes(legacy_extension, additional_extensions, context, endpoint):
    extensions = additional_extensions.split(",") if additional_extensions else []
    if legacy_extension:
        extensions.append(legacy_extension)
    if not extensions and not context:
        return {}
    if (not extensions or len(set(extensions)) != len(extensions)
            or not all(re.fullmatch(r"[0-9]{2,8}", number) for number in extensions)
            or not re.fullmatch(r"[A-Za-z0-9_.-]+", context)):
        raise ValueError("invalid phone assistant dialplan configuration")
    return {extension: (context, endpoint) for extension in extensions}


async def main():
    origin = os.environ["NOTIFY_URL"].rstrip("/")
    parts = urlsplit(origin)
    endpoint = os.environ["ASTERISK_ENDPOINT"]
    if (parts.scheme != "https" or not parts.hostname or parts.username or parts.path
            or parts.query or parts.fragment or not re.fullmatch(r"PJSIP/[A-Za-z0-9_.-]+", endpoint)):
        raise ValueError("invalid Notify origin or PJSIP endpoint")
    headers = {"Authorization": "Bearer " + os.environ["NOTIFY_BRIDGE_TOKEN"]}
    assistant = assistant_routes(os.environ.get("ASTERISK_ASSISTANT_EXTENSION", ""),
                                 os.environ.get("ASTERISK_ASSISTANT_ADDITIONAL_EXTENSIONS", ""),
                                 os.environ.get("ASTERISK_ASSISTANT_CONTEXT", ""), endpoint)
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
