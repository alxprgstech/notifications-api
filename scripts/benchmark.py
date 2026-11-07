"""Synthetic local benchmark. Never accepts production credentials or an existing DB."""
import argparse
import concurrent.futures
import ctypes
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import statistics
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--dll", default="src/Notify.Server/bin/Release/net10.0/Notify.Server.dll")
parser.add_argument("--count", type=int, default=100)
args = parser.parse_args()
if not 1 <= args.count <= 200:
    parser.error("count must be 1..200 (within the real API rate limit)")
run = ROOT / "artifacts" / ("benchmark-" + secrets.token_hex(6)); run.mkdir(parents=True)
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0)); port = listener.getsockname()[1]
origin = f"http://127.0.0.1:{port}"
env = dict(os.environ, DOTNET_ENVIRONMENT="Development", ASPNETCORE_URLS=origin, Notify__DataPath=str(run / "data"), Notify__PublicUrl=origin)
for name in list(env):
    if name.lower().startswith("notify__") and name not in ("Notify__DataPath", "Notify__PublicUrl"):
        del env[name]
log = open(run / "server.log", "w", encoding="utf-8")
process = subprocess.Popen(["dotnet", str((ROOT / args.dll).resolve())], env=env, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)


def memory():
    if os.name == "nt":
        from ctypes import wintypes as w
        class Counters(ctypes.Structure):
            _fields_ = [("cb", w.DWORD), ("faults", w.DWORD)] + [(n, ctypes.c_size_t) for n in ("peak", "rss", "peakPaged", "paged", "peakNonPaged", "nonPaged", "pagefile", "peakPagefile", "private")]
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = w.HANDLE
        kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
        handle = kernel.OpenProcess(0x410, False, process.pid)
        if not handle:
            raise OSError("Cannot inspect benchmark process memory")
        try:
            data = Counters(); data.cb = ctypes.sizeof(data)
            psapi = ctypes.WinDLL("psapi", use_last_error=True)
            psapi.GetProcessMemoryInfo.argtypes = [w.HANDLE, ctypes.c_void_p, w.DWORD]
            if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(data), data.cb):
                raise OSError("GetProcessMemoryInfo failed")
            kernel.CloseHandle.argtypes = [w.HANDLE]
            return {"rssBytes": data.rss, "peakRssBytes": data.peak, "privateBytes": data.private}
        finally:
            kernel.CloseHandle(handle)
    values = {}
    for line in Path(f"/proc/{process.pid}/status").read_text().splitlines():
        if line.startswith(("VmRSS:", "VmHWM:")):
            values[line.split(":")[0]] = int(line.split()[1]) * 1024
    return {"rssBytes": values["VmRSS"], "peakRssBytes": values["VmHWM"]}


try:
    deadline = time.monotonic() + 30
    while True:
        try:
            urllib.request.urlopen(origin + "/health/live", timeout=2).read(); break
        except Exception:
            if time.monotonic() > deadline or process.poll() is not None:
                raise RuntimeError("Benchmark server failed to start; inspect isolated server.log")
            time.sleep(.2)
    urllib.request.urlopen(origin + "/openapi/v1.json", timeout=10).read()
    token = "nt_" + secrets.token_urlsafe(32); source = secrets.token_hex(16)
    with sqlite3.connect(run / "data/notify.db") as db:
        db.execute("INSERT INTO objects VALUES(?,?,?)", ("source", source, json.dumps(dict(id=source, name="Synthetic benchmark", scopes=["notify:send", "requests:read"], preview="hidden", revoked=False))))
        db.execute("INSERT INTO credentials VALUES(?,?,?,?)", (hashlib.sha256(token.encode()).hexdigest(), "integration", source, int(time.time()*1000)+600000))
    time.sleep(2)
    idle = memory()
    def request(i):
        body = json.dumps(dict(kind="message", title="Synthetic benchmark", body="No external delivery configured")).encode()
        req = urllib.request.Request(origin + "/api/v1/notifications", data=body, headers={"Content-Type":"application/json", "Authorization":"Bearer " + token, "Idempotency-Key":f"benchmark-{i:08}"})
        start = time.perf_counter()
        with urllib.request.urlopen(req, timeout=30) as response:
            if response.status != 200:
                raise RuntimeError("Unexpected API status")
            response.read()
        return (time.perf_counter()-start)*1000
    start = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=10) as pool:
        timings = sorted(pool.map(request, range(args.count)))
    elapsed = time.perf_counter()-start
    peak = memory()
    result = {"platform":os.name,"runtime":".NET 10","database":"SQLite WAL","requests":args.count,"concurrency":10,"elapsedSeconds":elapsed,"p50Ms":statistics.median(timings),"p95Ms":timings[min(len(timings)-1,int(len(timings)*.95))],"idle":idle,"afterLoad":peak,"externalProviders":"not called","containerLimitVerified":False}
    (run/"metrics.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps({"result":result,"artifact":str(run/"metrics.json")},indent=2))
finally:
    process.terminate()
    try: process.wait(timeout=10)
    except subprocess.TimeoutExpired: process.kill(); process.wait()
    log.close()
