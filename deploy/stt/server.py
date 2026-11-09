"""Private, local Russian STT for Notify.

The model is mounted read-only and every file is checked against model.sha256 before
the Vosk model is loaded. Audio is accepted only as PCM16LE mono 16 kHz WAV, is
processed in memory, and is never written to disk or logged.
"""
from hashlib import sha256
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import json
import os
import re
from threading import Lock
import wave

from vosk import KaldiRecognizer, Model

MODEL_DIR = os.environ.get("STT_MODEL_DIR", "/model")
MANIFEST = os.path.join(os.path.dirname(__file__), "model.sha256")
MAX_BYTES = 10 * 1024 * 1024
MAX_SECONDS = 300
SAMPLE_RATE = 16000
MANIFEST_LINE = re.compile(r"^([0-9a-f]{64})  ([A-Za-z0-9_./-]+)$")


def verify_model(directory: str) -> None:
    expected = {}
    with open(MANIFEST, "r", encoding="ascii") as handle:
        for raw in handle:
            match = MANIFEST_LINE.match(raw.rstrip("\n"))
            if match is None:
                raise SystemExit("invalid model manifest")
            path, digest = match.group(2), match.group(1)
            if path.startswith("/") or ".." in path.split("/"):
                raise SystemExit("unsafe model path")
            expected[path] = digest
    actual = set()
    for root, _dirs, files in os.walk(directory):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, directory).replace(os.sep, "/")
            actual.add(rel)
            digest = expected.get(rel)
            if digest is None:
                raise SystemExit("unexpected model file")
            with open(full, "rb") as handle:
                if sha256(handle.read()).hexdigest() != digest:
                    raise SystemExit("model checksum mismatch")
    if actual != set(expected):
        raise SystemExit("model files missing")


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        self.request.settimeout(35)
        super().setup()

    def log_message(self, *_args):
        pass

    def do_GET(self):
        if self.path != "/health/ready":
            return self.reply(404, b"not_found")
        return self.reply(200, b"ready")

    def do_POST(self):
        if self.path != "/recognize":
            return self.reply(404, b"not_found")
        if not self.server.recognizer_lock.acquire(blocking=False):
            return self.reply(503, b"stt_busy")
        try:
            try:
                length = int(self.headers.get("Content-Length", "0"))
                content_type = self.headers.get_content_type()
                if not 0 < length <= MAX_BYTES or content_type not in ("audio/wav", "audio/x-wav", "application/octet-stream"):
                    return self.reply(400, b"invalid_request")
                body = self.rfile.read(length)
                if len(body) != length:
                    return self.reply(400, b"invalid_request")
                with wave.open(BytesIO(body), "rb") as source:
                    if source.getnchannels() != 1 or source.getsampwidth() != 2 or source.getframerate() != SAMPLE_RATE:
                        return self.reply(400, b"invalid_audio")
                    if source.getnframes() > SAMPLE_RATE * MAX_SECONDS:
                        return self.reply(400, b"audio_too_long")
                    pcm = source.readframes(source.getnframes())
            except (ValueError, EOFError, wave.Error, UnicodeError):
                return self.reply(400, b"invalid_audio")
            if not pcm:
                return self.reply(422, b"empty_audio")
            recognizer = KaldiRecognizer(self.server.model, SAMPLE_RATE)
            recognizer.SetWords(False)
            for offset in range(0, len(pcm), 8000):
                recognizer.AcceptWaveform(pcm[offset:offset + 8000])
            text = json.loads(recognizer.FinalResult()).get("text", "").strip()
            if not text:
                return self.reply(422, b"empty")
            if len(text) > 16000:
                return self.reply(422, b"too_long")
            text = text[:1].upper() + text[1:] + "."
            return self.reply(200, json.dumps({"text": text}, ensure_ascii=False).encode("utf-8"), "application/json")
        finally:
            self.server.recognizer_lock.release()

    def reply(self, status, body, content_type="text/plain"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            pass


def main():
    verify_model(MODEL_DIR)
    model = Model(MODEL_DIR)
    with ThreadingHTTPServer(("0.0.0.0", 8080), Handler) as server:
        server.model = model
        server.recognizer_lock = Lock()
        print("Vosk small-ru ready", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
