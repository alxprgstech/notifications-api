"""Synthetic RTP and media relay tests. No ARI, network, or physical endpoint."""

import asyncio
import audioop
import unittest
from types import SimpleNamespace

from aiohttp import WSMsgType
from bridge import FRAME, RTP, RtpPeer, media


class RtpTests(unittest.TestCase):
    def test_variable_packet_sizes_extensions_and_padding(self):
        peer = RtpPeer()
        peer.remote = ("127.0.0.1", 10_000)
        voice = audioop.lin2ulaw((2_800).to_bytes(2, "little") * 80, 2)
        extended = RTP.pack(0xb1, 0x80, 65535, 0, 42) + bytes(4) + bytes((0, 1, 0, 1)) + bytes(4)
        peer.datagram_received(extended + voice + bytes((0, 0, 0, 4)), peer.remote)
        self.assertTrue(peer.incoming.empty())
        peer.datagram_received(RTP.pack(0x80, 0, 0, 80, 42) + voice, peer.remote)
        pcm = peer.incoming.get_nowait()
        self.assertEqual(len(pcm), 640)
        self.assertGreater(audioop.rms(pcm, 2), 2_000)
        peer.datagram_received(RTP.pack(0x80, 0, 0, 80, 42) + voice, peer.remote)
        self.assertEqual(peer.received, 2)
        self.assertEqual(peer.dropped, 1)

    def test_rtp_downlink_format(self):
        peer = RtpPeer()
        peer.remote = ("127.0.0.1", 10_000)

        class Transport:
            packets = []

            def sendto(self, payload, address):
                self.packets.append((payload, address))

        transport = Transport()
        peer.connection_made(transport)
        peer.send(bytes(640))
        peer.send(bytes(640))
        first, second = [packet for packet, _ in transport.packets]
        self.assertEqual((len(first), len(second)), (172, 172))
        self.assertEqual((RTP.unpack_from(second)[3] - RTP.unpack_from(first)[3]) & 0xffffffff, 160)
        self.assertEqual(peer.sent, 2)


class MediaTests(unittest.IsolatedAsyncioTestCase):
    async def test_reply_trace_distinguishes_all_packets_from_interrupted_and_partial(self):
        turn_id, reply_id = "a" * 32, "b" * 32
        for packets, closing, expected in ((2, "idle", "completed"), (1, "interrupt", "interrupted"),
                                            (1, "idle", "cancelled")):
            with self.subTest(closing=closing, packets=packets):
                def control(event):
                    return SimpleNamespace(type=WSMsgType.TEXT, json=lambda: event)

                async def socket():
                    yield control(dict(type="conversation", status="speaking", userTurnId=turn_id,
                                       turnId=reply_id, generation=1, expectedFrames=2))
                    for index in range(packets):
                        yield SimpleNamespace(type=WSMsgType.BINARY, data=FRAME.pack(index, index * 20, 1) + bytes(640))
                    yield control(dict(type="interrupt", generation=2) if closing == "interrupt"
                                  else dict(type="conversation", status="idle", generation=1))

                events = []

                async def trace(stage, **fields):
                    events.append((stage, fields))

                peer = RtpPeer()
                peer.remote = ("127.0.0.1", 10000)
                peer.connection_made(SimpleNamespace(sendto=lambda data, addr: None))
                await media(socket(), peer, True, trace=trace)
                self.assertEqual(events[0], ("rtp_first_out", {"turnId": turn_id, "replyId": reply_id}))
                self.assertEqual(len(events), 2)
                stage, result = events[-1]
                self.assertEqual((stage, result["status"], result["sent"]), ("rtp_playback", expected, packets))
                self.assertEqual(result["replyId"], reply_id)

    async def test_upload_failure_terminates_without_waiting_for_downlink(self):
        class ClosedUpload:
            def __aiter__(self):
                return self

            async def __anext__(self):
                await asyncio.Future()

            async def send_bytes(self, frame):
                assert len(frame) == FRAME.size + 640
                raise ConnectionError("synthetic upload failure")

        with self.assertRaises(ConnectionError):
            await asyncio.wait_for(media(ClosedUpload(), RtpPeer(), False), 1)


if __name__ == "__main__":
    unittest.main()
