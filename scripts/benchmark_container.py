"""Benchmark a disposable Docker instance. Never mounts an existing data volume."""
import argparse
import asyncio
import base64
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import secrets
import sqlite3
import struct
import subprocess
import time
import urllib.error
import urllib.request

import websockets

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--memory", default="128m", choices=["96m", "128m", "192m", "256m", "384m"])
parser.add_argument("--large-history", action="store_true")
parser.add_argument("--call-seconds", type=int, default=600)
args = parser.parse_args()
name = "notify-benchmark-" + secrets.token_hex(6)
run = ROOT / "artifacts" / name; run.mkdir(parents=True)
token = "nt_" + secrets.token_urlsafe(32); source = secrets.token_hex(16)
device_token = "nt_" + secrets.token_urlsafe(32); device = secrets.token_hex(16)
agent = secrets.token_hex(16)
schema = (ROOT / "src/Notify.Server/Database.cs").read_text(encoding="utf-8").split('"""')[1]
with sqlite3.connect(run / "notify.db") as db:
    db.executescript(schema)
    db.execute("INSERT INTO objects VALUES(?,?,?)", ("source", source, json.dumps(dict(id=source,name="Synthetic benchmark",scopes=["notify:send","requests:read","agents:connect"],preview="hidden",revoked=False))))
    db.execute("INSERT INTO credentials VALUES(?,?,?,?)", (hashlib.sha256(token.encode()).hexdigest(),"integration",source,int(time.time()*1000)+7200000))
    public_key=base64.b64encode(bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d03010703420004"+"6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"+"4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5")).decode()
    db.execute("INSERT INTO objects VALUES(?,?,?)", ("device",device,json.dumps(dict(id=device,name="Synthetic benchmark device",publicKey=public_key,revoked=False))))
    db.execute("INSERT INTO credentials VALUES(?,?,?,?)", (hashlib.sha256(device_token.encode()).hexdigest(),"device",device,int(time.time()*1000)+7200000))
    db.execute("INSERT INTO objects VALUES(?,?,?)", ("agent",agent,json.dumps(dict(id=agent,sourceId=source,name="Synthetic call agent",capabilities=["realtime_audio"],createdAt=int(time.time()*1000),lastSeenAt=None,revoked=False))))
    if args.large_history:
        for i in range(1000):
            nid=secrets.token_hex(16);created=int(time.time()*1000)
            value=dict(id=nid,sourceId=source,content=dict(kind="message",title="Large synthetic history",body="Т"*16000,securityLevel="standard",allowCustom=True),createdAt=created,status="active")
            db.execute("INSERT INTO notifications VALUES(?,?,?,?,?,?)",(nid,source,created,None,"active",json.dumps(value)))
            db.execute("INSERT INTO changes(notification_id,at) VALUES(?,?)",(nid,created))


def docker(*command):
    return subprocess.check_output(["docker", *command], text=True, stderr=subprocess.STDOUT).strip()


created = False
try:
    docker("volume", "create", name)
    docker("create", "--name", name, "--memory", args.memory, "--cpus", ".5", "--read-only", "--tmpfs", "/tmp:size=32m,mode=1777", "--mount", f"type=volume,source={name},target=/data", "-p", "127.0.0.1::8080", "-e", "ASPNETCORE_ENVIRONMENT=Development", "-e", "Notify__PublicUrl=http://localhost", "alxprgs-notify:local")
    created = True
    docker("cp", str(run / "notify.db"), name + ":/data/notify.db")
    docker("run", "--rm", "--user", "0", "--entrypoint", "/bin/sh", "--mount", f"type=volume,source={name},target=/data", "alxprgs-notify:local", "-c", "chown 1654:1654 /data /data/notify.db && chmod 700 /data && chmod 600 /data/notify.db")
    docker("start", name)
    port = docker("port", name, "8080").split(":")[-1]
    origin = "http://127.0.0.1:" + port
    deadline = time.monotonic() + 40
    while True:
        try: urllib.request.urlopen(origin + "/health/live",timeout=2).read(); break
        except Exception:
            if time.monotonic()>deadline: raise RuntimeError("Container failed to become live")
            time.sleep(.2)
    urllib.request.urlopen(origin + "/openapi/v1.json",timeout=20).read()
    def usage():
        lines = docker("exec",name,"cat","/sys/fs/cgroup/cpu.stat").splitlines()
        return int(next(line.split()[1] for line in lines if line.startswith("usage_usec ")))
    def request(i):
        payload=json.dumps(dict(kind="message",title="Benchmark",body="No external delivery")).encode()
        req=urllib.request.Request(origin+"/api/v1/notifications",data=payload,headers={"Authorization":"Bearer "+token,"Idempotency-Key":f"benchmark-{i:08}","Content-Type":"application/json"})
        start=time.perf_counter()
        with urllib.request.urlopen(req,timeout=20) as response:
            if response.status!=200: raise RuntimeError("Unexpected HTTP status")
            response.read()
        return (time.perf_counter()-start)*1000
    time.sleep(15)
    cpu=usage();time.sleep(10);idle_cpu=(usage()-cpu)/10000000
    stats_idle=json.loads(docker("stats",name,"--no-stream","--format","{{json .}}"))
    cpu=usage();start=time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=10) as pool: timings=sorted(pool.map(request,range(100)))
    elapsed=time.perf_counter()-start;load_cpu=(usage()-cpu)/1000000/elapsed
    req=urllib.request.Request(origin+"/api/v1/notifications?limit=200",headers={"Authorization":"Bearer "+token})
    page=json.load(urllib.request.urlopen(req,timeout=30))
    if args.large_history: assert len(page["items"])==200 and all(n["previewOnly"] and len(n["content"]["body"])<=1000 for n in page["items"])
    sync_start=time.perf_counter()
    req=urllib.request.Request(origin+"/api/v1/sync?after=0",headers={"Authorization":"Bearer "+device_token})
    raw=urllib.request.urlopen(req,timeout=30).read();snapshot=json.loads(raw)
    if args.large_history: assert len(snapshot["items"])==1000 and all(n["previewOnly"] for n in snapshot["items"])
    sync_ms=(time.perf_counter()-sync_start)*1000

    def http_json(path, body, auth):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(origin + path, data=data, headers={"Authorization": "Bearer " + auth, "Content-Type": "application/json"}, method="POST" if data is not None else "GET")
        with urllib.request.urlopen(req, timeout=20) as response:
            text = response.read()
            return json.loads(text) if text else None

    async def call_scenario(call_seconds):
        ws_origin = origin.replace("http://", "ws://")
        frames = max(1, int(call_seconds / 0.02))
        samples = []
        async with websockets.connect(ws_origin + "/api/v1/agents/connect", additional_headers={"Authorization": "Bearer " + token}, max_size=65536, max_queue=None, ping_interval=None) as control:
            await control.send(json.dumps({"type": "hello", "protocolVersion": "alxprgs.notify.agent.v1", "eventId": secrets.token_hex(16), "agentId": agent, "name": "Bench agent", "capabilities": ["realtime_audio"], "callProtocols": ["alxprgs.notify.call.v2"], "sessions": [{"id": "bench", "name": "Bench"}]}))
            json.loads(await control.recv())
            async def heartbeat():
                while True:
                    await asyncio.sleep(30)
                    await control.send(json.dumps({"type": "heartbeat", "protocolVersion": "alxprgs.notify.agent.v1", "eventId": secrets.token_hex(16), "sessions": [{"id": "bench", "name": "Bench"}]}))
            beat = asyncio.create_task(heartbeat())
            call = await asyncio.to_thread(http_json, "/api/v1/calls", {"sourceId": source, "sessionId": "bench"}, device_token)
            call_id = call["id"]
            invite = json.loads(await asyncio.wait_for(control.recv(), timeout=10)); assert invite["type"] == "call.invite"
            await control.send(json.dumps({"type": "call.accept", "protocolVersion": "alxprgs.notify.call.v2", "eventId": secrets.token_hex(16), "payload": {"callId": call_id}}))
            for _ in range(50):
                state = await asyncio.to_thread(http_json, "/api/v1/calls/" + call_id, None, device_token)
                if state["status"] in ("connecting", "active"): break
                await asyncio.sleep(.05)
            phone = await websockets.connect(ws_origin + f"/api/v1/calls/{call_id}/audio", additional_headers={"Authorization": "Bearer " + device_token}, max_size=65536, ping_interval=None)
            agent_media = await websockets.connect(ws_origin + f"/api/v1/agents/calls/{call_id}/audio", additional_headers={"Authorization": "Bearer " + token}, max_size=65536, ping_interval=None)
            async def sampler():
                while True:
                    try: samples.append(int(await asyncio.to_thread(docker, "exec", name, "cat", "/sys/fs/cgroup/memory.current")))
                    except Exception: pass
                    await asyncio.sleep(30)
            sample_task = asyncio.create_task(sampler())
            try:
                while True:
                    message = await asyncio.wait_for(phone.recv(), timeout=10)
                    if not isinstance(message, bytes):
                        event = json.loads(message)
                        if event.get("type") == "state" and event.get("status") == "active": break
                async def pump(socket, fill):
                    seen = 0
                    async def reader():
                        nonlocal seen
                        while seen < frames:
                            message = await asyncio.wait_for(socket.recv(), timeout=30)
                            if isinstance(message, bytes): seen += 1
                    reading = asyncio.create_task(reader())
                    try:
                        for index in range(frames):
                            await socket.send(struct.pack(">III", index, index * 20, 0) + bytes([fill]) * 640)
                            await asyncio.sleep(0.02)
                        await reading
                    finally:
                        reading.cancel(); await asyncio.gather(reading, return_exceptions=True)
                    return seen
                phone_seen, agent_seen = await asyncio.gather(pump(phone, 0x22), pump(agent_media, 0x33))
            finally:
                beat.cancel(); await asyncio.gather(beat, return_exceptions=True)
                sample_task.cancel(); await asyncio.gather(sample_task, return_exceptions=True)
                await asyncio.to_thread(http_json, "/api/v1/calls/" + call_id + "/end", {}, device_token)
                await phone.close(); await agent_media.close()
            history = await asyncio.to_thread(http_json, "/api/v1/calls", None, device_token)
            record = next((item for item in history if item["id"] == call_id), None)
            try: incoming = await asyncio.to_thread(http_json, "/api/v1/calls/incoming", None, device_token)
            except urllib.error.HTTPError as exc:
                incoming = None if exc.code == 404 else {"status": exc.code}
            post_memory = int(await asyncio.to_thread(docker, "exec", name, "cat", "/sys/fs/cgroup/memory.current"))
        return {"frames": frames, "phoneSeen": phone_seen, "agentSeen": agent_seen, "samples": samples, "history": record, "incoming": incoming, "postMemory": post_memory}

    base_peak = int(docker("exec", name, "cat", "/sys/fs/cgroup/memory.peak"))
    call_cpu = usage(); call_started = time.perf_counter()
    call = asyncio.run(asyncio.wait_for(call_scenario(args.call_seconds), timeout=args.call_seconds + 90))
    call_elapsed = time.perf_counter() - call_started
    call_cpu_cores = (usage() - call_cpu) / 1000000 / call_elapsed
    call_stats = json.loads(docker("stats", name, "--no-stream", "--format", "{{json .}}"))
    call_peak = int(docker("exec", name, "cat", "/sys/fs/cgroup/memory.peak"))
    call_oom = docker("inspect", "--format", "{{.State.OOMKilled}}", name)

    result={"platform":"Docker Desktop Linux / amd64","memoryLimit":args.memory,"cpuLimit":.5,"largeHistory":args.large_history,"requests":100,"concurrency":10,"elapsedSeconds":elapsed,"p50Ms":timings[50],"p95Ms":timings[95],"idleCpuCores":idle_cpu,"loadCpuCores":load_cpu,"idleDockerStats":stats_idle,"peakCgroupBytes":base_peak,"oomKilled":docker("inspect","--format","{{.State.OOMKilled}}",name),"externalProviders":"not called"}
    result.update(syncMs=sync_ms,syncBytes=len(raw),syncItems=len(snapshot["items"]),callSecondsRequested=args.call_seconds,callFrames=call["frames"],callFramesReceived=call["phoneSeen"]+call["agentSeen"],callSeconds=round(call_elapsed,3),callCpuCores=call_cpu_cores,callPeakCgroupBytes=call_peak,callMemorySamples=call["samples"],callMemoryMaxSampleBytes=max(call["samples"] or [0]),postCallCgroupBytes=call["postMemory"],postCallHistory=call["history"],postCallIncoming=call["incoming"],callOomKilled=call_oom,callDockerStats=call_stats)
    (run/"metrics.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps({"artifact":str(run/"metrics.json"),**result},indent=2))
finally:
    if created:
        (run/"server.log").write_text(docker("logs",name),encoding="utf-8")
        # Both names are generated above, never accepted from caller input.
        assert name.startswith("notify-benchmark-") and len(name)==29
        subprocess.run(["docker","rm","--force",name],check=False,stdout=subprocess.DEVNULL)
    subprocess.run(["docker","volume","rm",name],check=False,stdout=subprocess.DEVNULL)
