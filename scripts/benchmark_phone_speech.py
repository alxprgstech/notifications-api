"""Measure the warm speech HTTP endpoints from their Docker network."""
import argparse
import http.client
import json
import math
import statistics
import time
import wave
from io import BytesIO
from urllib.parse import urlsplit


def post(client, path, body, content_type):
    started = time.perf_counter_ns()
    client.request("POST", path, body, {"Content-Type": content_type})
    response = client.getresponse()
    headers = (time.perf_counter_ns() - started) / 1e6
    result = response.read()
    complete = (time.perf_counter_ns() - started) / 1e6
    if response.status != 200:
        raise RuntimeError(f"{path}: HTTP {response.status}")
    return result, headers, complete


def summary(values):
    ordered = sorted(values)
    return dict(n=len(values), min=round(min(values), 2),
                p50=round(statistics.median(values), 2),
                p95=round(ordered[math.ceil(0.95 * len(values)) - 1], 2),
                max=round(max(values), 2))


def connection(url):
    address = urlsplit(url)
    if address.scheme != "http" or not address.hostname or address.path not in ("", "/"):
        raise ValueError("Expected a local speech service HTTP origin")
    return http.client.HTTPConnection(address.hostname, address.port, timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stt", default="http://stt:8080")
    parser.add_argument("--tts", default="http://silero:8080")
    parser.add_argument("--samples", type=int, default=30)
    args = parser.parse_args()
    stt, tts = connection(args.stt), connection(args.tts)
    prompt = json.dumps({"text": "Проверка телефонного помощника. Назови столицу России."}).encode()
    metrics = {key: [] for key in ("silero_headers_ms", "silero_total_ms", "vosk_700ms_tail_ms", "vosk_120ms_tail_ms")}
    try:
        for index in range(args.samples + 3):
            wav, tts_headers, tts_total = post(tts, "/synthesize", prompt, "application/json")
            tails = (700, 120) if index % 2 == 0 else (120, 700)
            results = {}
            for tail in tails:
                result, _, elapsed = post(stt, "/recognize", with_tail(wav, tail), "audio/wav")
                results[tail] = (json.loads(result)["text"].lower(), elapsed)
            if results[700][0] != results[120][0] or "столицу россии" not in results[120][0]:
                raise RuntimeError("Vosk transcript changed with the shorter tail")
            if index >= 3:
                for values, sample in zip(metrics.values(), (tts_headers, tts_total, results[700][1], results[120][1])):
                    values.append(sample)
    finally:
        stt.close()
        tts.close()
    print(json.dumps({name: summary(values) for name, values in metrics.items()}, indent=2))


def with_tail(wav, tail_ms):
    with wave.open(BytesIO(wav)) as source:
        channels, width, rate = source.getnchannels(), source.getsampwidth(), source.getframerate()
        pcm = source.readframes(source.getnframes())
    output = BytesIO()
    with wave.open(output, "wb") as target:
        target.setparams((channels, width, rate, 0, "NONE", "not compressed"))
        target.writeframes(pcm + bytes(rate * width * channels * tail_ms // 1000))
    return output.getvalue()


if __name__ == "__main__":
    main()
