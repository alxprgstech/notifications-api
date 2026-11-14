"""Synthetic SIP/RTP caller for the isolated Notify phone lab."""
import argparse
import asyncio
import audioop
import json
import re
import secrets
import socket
import struct
import time
import wave
from io import BytesIO

import aiohttp


async def call(samples, host, number, require_ringback=False):
    sip = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rtp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    for sock in (sip, rtp):
        sock.bind(("127.0.0.1", 0))
        sock.setblocking(False)
    loop = asyncio.get_running_loop()
    local = f"127.0.0.1:{sip.getsockname()[1]}"
    remote = f"sip:{number}@{host}"
    local_uri = f"sip:phone-lab@{local}"
    call_id, tag = secrets.token_hex(12), secrets.token_hex(6)
    target = (host, 5060)
    cseq = 1
    to = f"<{remote}>"

    async def signal(method, uri, body=""):
        headers = [
            f"{method} {uri} SIP/2.0", f"Via: SIP/2.0/UDP {local};branch=z9hG4bK{secrets.token_hex(8)};rport",
            f"From: <{local_uri}>;tag={tag}", f"To: {to}", f"Call-ID: {call_id}", f"CSeq: {cseq} {method}",
            f"Contact: <{local_uri}>", "Max-Forwards: 70", f"Content-Length: {len(body)}",
        ]
        if body:
            headers.append("Content-Type: application/sdp")
        await loop.sock_sendto(sip, ("\r\n".join(headers) + "\r\n\r\n" + body).encode(), target)

    body = "\r\n".join(["v=0", "o=phone 1 1 IN IP4 127.0.0.1", "s=Notify test", "c=IN IP4 127.0.0.1",
                          "t=0 0", f"m=audio {rtp.getsockname()[1]} RTP/AVP 0", "a=rtpmap:0 PCMU/8000", ""])
    before = time.monotonic()
    ringing_ms = None
    await signal("INVITE", remote, body)
    try:
        async with asyncio.timeout(15):
            while True:
                packet, _ = await loop.sock_recvfrom(sip, 8192)
                response = packet.decode()
                if not response.startswith("SIP/2.0 "):
                    continue
                status = int(response[8:11])
                if status == 180 and ringing_ms is None:
                    ringing_ms = (time.monotonic() - before) * 1000
                if status >= 200:
                    break
        if status != 200:
            await signal("ACK", remote)
            raise RuntimeError(f"SIP returned {status}")
        to = re.search(r"(?im)^To:\s*(.+)$", response)[1].strip()
        contact = re.search(r"(?im)^Contact:\s*<([^>]+)>", response)[1]
        port = int(re.search(r"(?m)^m=audio (\d+)", response)[1])
        await signal("ACK", contact)
        answer_ms = (time.monotonic() - before) * 1000
        if require_ringback and ringing_ms is None:
            cseq += 1
            await signal("BYE", contact)
            raise RuntimeError("SIP 180 Ringing was not received before 200 OK")

        ready_at = None
        listen_after = None
        async def listen():
            nonlocal ready_at
            while True:
                packet, address = await loop.sock_recvfrom(rtp, 2048)
                if address != (host, port) or len(packet) != 172 or packet[0] != 0x80 or packet[1] & 0x7f:
                    continue
                if audioop.rms(audioop.ulaw2lin(packet[12:], 2), 2) <= 450:
                    continue
                now = time.monotonic()
                if ready_at is None:
                    ready_at = now
                elif listen_after is not None and now >= listen_after:
                    return now

        heard = asyncio.create_task(listen())
        try:
            seq = secrets.randbits(16)
            ts = secrets.randbits(32)
            ssrc = secrets.randbits(32)
            tick = loop.time()
            last_voice = None
            silence = bytes(320)

            async def send_audio(pcm):
                nonlocal seq, ts, tick
                payload = audioop.lin2ulaw(pcm, 2)
                await loop.sock_sendto(rtp, struct.pack("!BBHII", 128, 0, seq, ts, ssrc) + payload, (host, port))
                seq = (seq + 1) & 65535
                ts = (ts + 160) & 4294967295
                tick += .02
                await asyncio.sleep(max(0, tick - loop.time()))

            # Send silence to establish RTP and let the 180 ms readiness tone finish.
            async with asyncio.timeout(10):
                while ready_at is None or time.monotonic() - ready_at < .3:
                    await send_audio(silence)
            last_voice_index = next((index for index in range(len(samples) - 1, -1, -1)
                                     if audioop.rms(samples[index], 2) > 480), None)
            if last_voice_index is None:
                raise RuntimeError("The test utterance contains no audible speech")
            for index, pcm in enumerate(samples):
                if index == last_voice_index:
                    last_voice = time.monotonic()
                    listen_after = last_voice
                await send_audio(pcm)
            for _ in range(2750):
                await send_audio(silence)
                if heard.done():
                    return {"sipAnswerMs": round(answer_ms, 2),
                            "sipRingingMs": None if ringing_ms is None else round(ringing_ms, 2),
                            "sipAnswerToReadyRtpMs": round((ready_at - before) * 1000 - answer_ms, 2),
                            "lastVoiceToFirstNonSilentRtpMs": round((heard.result() - last_voice) * 1000, 2)}
            raise TimeoutError("No non-silent RTP reply")
        finally:
            heard.cancel()
            await asyncio.gather(heard, return_exceptions=True)
            cseq += 1
            await signal("BYE", contact)
    finally:
        sip.close()
        rtp.close()


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--number", default="7820")
    parser.add_argument("--silero", default="http://silero:8080")
    parser.add_argument("--require-ringback", action="store_true")
    args = parser.parse_args()
    async with aiohttp.ClientSession() as http:
        async with http.post(args.silero + "/synthesize", json={"text": "Скажи коротко, сколько будет два плюс два."}) as response:
            response.raise_for_status()
            data = await response.read()
    with wave.open(BytesIO(data)) as wav:
        pcm, _ = audioop.ratecv(wav.readframes(wav.getnframes()), 2, 1, 16000, 8000, None)
    frames = [bytes(320)] * 10 + [pcm[i:i + 320].ljust(320, b"\0") for i in range(0, len(pcm), 320)]
    failed = False
    for _ in range(args.samples):
        try:
            result = await call(frames, "127.0.0.1", args.number, args.require_ringback)
        except (TimeoutError, RuntimeError) as error:
            failed = True
            result = {"error": str(error)}
        print(json.dumps(result), flush=True)
        await asyncio.sleep(1)
    if args.require_ringback and failed:
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
