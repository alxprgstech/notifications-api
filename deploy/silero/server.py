"""Private HTTP endpoint for the fixed Silero voice used by Notify."""
from array import array
from hashlib import file_digest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import json
import os
import subprocess
import sys
from threading import Lock
import wave

import torch

MODEL_PATH = "/model/v5_6_ru.pt"
MODEL_SHA256 = "04cc9b1d08ce38bcaa9d02c488d80d3e6806d17f7eaa7c95982f36a3cbf50b2e"
MAX_TEXT = 1000
MAX_DURATION = 300


def synthesize(model, text):
    with torch.inference_mode():
        samples = model.apply_tts(text=text, speaker="baya", sample_rate=24000)
    if not 0 < samples.numel() <= 24000 * MAX_DURATION:
        raise ValueError("audio_duration")
    pcm = array("h", (samples.clamp(-1, 1) * 32767).round().to(torch.int16).tolist())
    if sys.byteorder != "little":
        pcm.byteswap()
    result = subprocess.run(
        ["ffmpeg", "-nostdin", "-loglevel", "error", "-threads", "1", "-filter_threads", "1",
         "-f", "s16le", "-ar", "24000", "-ac", "1", "-i", "pipe:0",
         "-f", "s16le", "-ar", "16000", "-ac", "1", "pipe:1"],
        input=pcm.tobytes(), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        timeout=10, check=True,
    )
    with BytesIO() as output:
        with wave.open(output, "wb") as wav:
            wav.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            wav.writeframes(result.stdout)
        return output.getvalue()


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        self.request.settimeout(5)
        super().setup()

    def log_message(self, *_args):
        pass

    def do_GET(self):
        if self.path != "/health/ready":
            return self.reply(404, b"not_found")
        return self.reply(200, b"ready")

    def do_POST(self):
        if self.path != "/synthesize":
            return self.reply(404, b"not_found")
        if not self.server.synthesizer_lock.acquire(blocking=False):
            return self.reply(503, b"silero_busy")
        try:
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 16384 or self.headers.get_content_type() != "application/json":
                    return self.reply(400, b"invalid_request")
                payload = json.loads(self.rfile.read(length))
                text = payload.get("text") if isinstance(payload, dict) else None
                if not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT:
                    return self.reply(400, b"invalid_text")
            except (ValueError, UnicodeError):
                return self.reply(400, b"invalid_request")
            try:
                audio = synthesize(self.server.model, text)
            except Exception as error:
                print(f"Silero synthesis failed: {type(error).__name__}", file=sys.stderr, flush=True)
                return self.reply(422, b"synthesis_failed")
            return self.reply(200, audio, "audio/wav")
        finally:
            self.server.synthesizer_lock.release()

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
    torch.set_num_threads(int(os.environ.get("SILERO_TORCH_THREADS", "1")))
    torch.set_num_interop_threads(1)
    with open(MODEL_PATH, "rb") as model_file:
        if file_digest(model_file, "sha256").hexdigest() != MODEL_SHA256:
            raise SystemExit("Silero model checksum mismatch")
        model_file.seek(0)
        model = torch.package.PackageImporter(model_file).load_pickle("tts_models", "model")
    model.to("cpu")
    with ThreadingHTTPServer(("0.0.0.0", 8080), Handler) as server:
        server.model = model
        server.synthesizer_lock = Lock()
        print("Silero v5.6 ready", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
