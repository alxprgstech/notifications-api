"""Real loopback REST/MCP/SDK interoperability; no production credentials or Android claim."""
import asyncio
import base64
from datetime import datetime,timedelta,timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import struct
import subprocess
import sys
import time
import urllib.request
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import ec
import httpx
import httpx2
import websockets
from alxprgs_notify import AgentConnector, CallbackRuntime, NotifyClient
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client

ROOT=Path(__file__).resolve().parents[1]
run=ROOT/"artifacts"/("sdk-smoke-"+secrets.token_hex(5));run.mkdir(parents=True)
with socket.socket() as listener: listener.bind(("127.0.0.1",0));port=listener.getsockname()[1]
origin=f"http://127.0.0.1:{port}"
source=secrets.token_hex(16);device=secrets.token_hex(16);token="nt_"+secrets.token_urlsafe(32);device_token="nt_"+secrets.token_urlsafe(32)
agent_id=secrets.token_hex(16);command_id=secrets.token_hex(16)
key=ec.generate_private_key(ec.SECP256R1())
schema=(ROOT/"src/Notify.Server/Database.cs").read_text(encoding="utf-8").split('"""')[1]
data=run/"data";data.mkdir()
with sqlite3.connect(data/"notify.db") as db:
    db.executescript(schema)
    db.execute("INSERT INTO objects VALUES(?,?,?)",("source",source,json.dumps(dict(id=source,name="Synthetic SDK smoke",scopes=["notify:send","requests:create","requests:read","requests:cancel","agents:connect"],preview="hidden",revoked=False))))
    db.execute("INSERT INTO objects VALUES(?,?,?)",("device",device,json.dumps(dict(id=device,name="Software signing fixture",publicKey=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)).decode(),revoked=False))))
    db.execute("INSERT INTO objects VALUES(?,?,?)",("agent",agent_id,json.dumps(dict(id=agent_id,sourceId=source,name="Synthetic smoke agent",capabilities=["text_tasks"],createdAt=int(time.time()*1000),lastSeenAt=None,revoked=False))))
    db.execute("INSERT INTO agent_commands(id,agent_id,source_id,type,payload,created,expires,status,attempts) VALUES(?,?,?,?,?,?,?,'queued',0)",(command_id,agent_id,source,"task",json.dumps(dict(taskId="smoke-task")),int(time.time()*1000),int(time.time()*1000)+600000))
    for credential,kind,identifier in [(token,"integration",source),(device_token,"device",device)]: db.execute("INSERT INTO credentials VALUES(?,?,?,?)",(hashlib.sha256(credential.encode()).hexdigest(),kind,identifier,int(time.time()*1000)+600000))
env={k:v for k,v in os.environ.items() if not k.lower().startswith("notify__")}
env.update(ASPNETCORE_ENVIRONMENT="Development",ASPNETCORE_URLS=origin,Notify__PublicUrl=origin,Notify__DataPath=str(data))
log=open(run/"server.log","w",encoding="utf-8")
process=subprocess.Popen(["dotnet",str(ROOT/"src/Notify.Server/bin/Release/net10.0/Notify.Server.dll")],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
try:
    end=time.monotonic()+30
    while True:
        try:urllib.request.urlopen(origin+"/health/live",timeout=2).read();break
        except Exception:
            if time.monotonic()>end:raise RuntimeError("Smoke server failed to start")
            time.sleep(.2)
    with NotifyClient(origin,token) as api, httpx.Client(base_url=origin,headers={"Authorization":"Bearer "+device_token}) as phone:
        expiry=(datetime.now(timezone.utc)+timedelta(minutes=5)).isoformat()
        for level in ("standard","elevated"):
            notification=api.request("Synthetic decision","No external action",expires_at=expiry,idempotency_key=secrets.token_hex(16),security_level=level)
            body={"answer":{"decision":"yes"}}
            if level=="elevated":
                response=phone.post(f"/api/v1/notifications/{notification['id']}/challenge",json=body["answer"]);response.raise_for_status();challenge=response.json()
                signature=key.sign(base64.b64decode(challenge["payload"]),ec.ECDSA(hashes.SHA256()))
                body.update(challengeId=challenge["id"],signature=base64.b64encode(signature).decode())
            response=phone.post(f"/api/v1/notifications/{notification['id']}/response",json=body);response.raise_for_status()
            result=api.wait(notification["id"],timeout=1)
            assert result["status"]=="answered" and result["answer"]["decision"]=="yes"
        fields=[{"id":"env","label":"Среда","type":"single_choice","required":True,"options":[{"id":"staging","label":"Staging"},{"id":"prod","label":"Prod"}]},{"id":"count","label":"Количество","type":"number","required":True,"min":1,"max":10},{"id":"dry","label":"Dry run","type":"boolean","required":True},{"id":"note","label":"Комментарий","type":"text","maxLength":100}]
        values={"env":"prod","count":3,"dry":True,"note":"smoke"}
        for level in ("standard","elevated"):
            notification=api.request_form("Synthetic form","No external action",expires_at=expiry,fields=fields,idempotency_key=secrets.token_hex(16),security_level=level)
            body={"form":{"values":values}}
            if level=="elevated":
                response=phone.post(f"/api/v1/notifications/{notification['id']}/form-challenge",json={"values":values});response.raise_for_status();challenge=response.json()
                signature=key.sign(base64.b64decode(challenge["payload"]),ec.ECDSA(hashes.SHA256()))
                body.update(challengeId=challenge["id"],signature=base64.b64encode(signature).decode())
            response=phone.post(f"/api/v1/notifications/{notification['id']}/response",json=body);response.raise_for_status()
            result=api.wait(notification["id"],timeout=1)
            assert result["status"]=="answered" and result["formAnswer"]["values"]["env"]=="prod",result
        sample=b"RIFF"+struct.pack("<I",36+32000)+b"WAVE"+b"fmt "+struct.pack("<I",16)+struct.pack("<HHIIHH",1,1,16000,32000,2,16)+b"data"+struct.pack("<I",32000)+b"\0"*32000
        voice=api.send_voice("Synthetic voice","No external action",wav=sample,expires_at=expiry,idempotency_key=secrets.token_hex(16))
        assert voice["content"]["voice"]["durationMs"]==1000,voice
        response=phone.get(f"/api/v1/audio/{voice['content']['voice']['audioId']}");response.raise_for_status();assert response.content==sample
        elevated_voice=api.send_voice("Synthetic secret voice","No external action",wav=sample,expires_at=expiry,idempotency_key=secrets.token_hex(16),security_level="elevated")
        assert phone.get(f"/api/v1/audio/{elevated_voice['content']['voice']['audioId']}").status_code==403
        challenge=phone.post(f"/api/v1/notifications/{elevated_voice['id']}/audio-challenge");challenge.raise_for_status();challenge=challenge.json()
        signature=key.sign(base64.b64decode(challenge["payload"]),ec.ECDSA(hashes.SHA256()))
        granted=phone.post(f"/api/v1/notifications/{elevated_voice['id']}/audio-grant",json={"challengeId":challenge["id"],"signature":base64.b64encode(signature).decode()});granted.raise_for_status();granted=granted.json()
        response=phone.get(f"/api/v1/audio/{elevated_voice['content']['voice']['audioId']}",headers={"X-Notify-Audio-Grant":granted["grant"]});response.raise_for_status();assert response.content==sample
        uploaded=phone.post("/api/v1/audio/uploads",content=sample);uploaded.raise_for_status();uploaded=uploaded.json()
        reply=phone.post("/api/v1/voice-messages",json={"uploadId":uploaded["uploadId"],"notificationId":voice["id"]});reply.raise_for_status();reply=reply.json();reply_id=reply["id"]
    async def mcp():
        async with httpx2.AsyncClient(headers={"Authorization":"Bearer "+token}) as http:
            async with streamable_http_client(origin+"/mcp",http_client=http) as streams:
                async with ClientSession(streams[0],streams[1]) as session:
                    await session.initialize();tools=await session.list_tools();names={t.name for t in tools.tools}
                    assert "send_notification" in names,names
                    result=await session.call_tool("send_notification",{"title":"SDK smoke","body":"No approval requested","idempotencyKey":secrets.token_hex(16)})
                    assert not result.is_error,result
                    form=await session.call_tool("request_form",{"title":"SDK form","body":"No external action","expiresAt":expiry,"idempotencyKey":secrets.token_hex(16),"fieldsJson":json.dumps(fields)})
                    assert not form.is_error,form
                    return sorted(names)
    names=asyncio.run(asyncio.wait_for(mcp(),timeout=30))
    async def agent():
        handled=asyncio.Event(); voice_handled=asyncio.Event(); call_done=asyncio.Event(); incoming_done=asyncio.Event(); call_state={}
        class Calls:
            async def on_invite(self, connector, call):
                call_state["id"]=call["callId"]; await connector.accept_call(call["callId"])
                try:
                    async with connector.call_audio(call["callId"]) as audio:
                        async for kind,payload in audio.stream():
                            if kind=="audio":
                                assert payload==b"\x22"*640
                                await audio.send(b"\x11"*640); await connector.interrupt(call["callId"],4); audio.set_generation(4); await audio.send(b"\x33"*640); call_state["audio"]=True
                except Exception as exc:
                    call_state["error"]=type(exc).__name__
            async def on_interrupt(self, call_id, generation): return None
            async def on_ended(self, call_id, reason):
                if call_state.get("incoming")==call_id:
                    call_state["incomingReason"]=reason; incoming_done.set()
                else:
                    call_state["callReason"]=reason; call_done.set()
            async def on_accepted(self, connector, call): call_state["accepted"]=call.get("callId")
        async def runtime(command):
            if command["type"]=="task":
                assert command["payload"]["taskId"]=="smoke-task",command
                handled.set()
            elif command["type"]=="voice_message":
                async with httpx.AsyncClient(headers={"Authorization":"Bearer "+token}) as client:
                    response=await client.get(origin+"/api/v1/audio/"+command["payload"]["audioId"]);response.raise_for_status();assert response.content==sample
                voice_handled.set()
        connector=AgentConnector(origin,token,agent_id=agent_id,name="Smoke agent",capabilities=["text_tasks","voice_messages","realtime_audio"],sessions=[{"id":"smoke-task","name":"Smoke task"}],runtime=CallbackRuntime(runtime),call_handler=Calls())
        task=asyncio.create_task(connector.run())
        try:
            await asyncio.sleep(0.4)
            async with httpx.AsyncClient(base_url=origin,headers={"Authorization":"Bearer "+device_token}) as phone_api:
                started=await phone_api.post("/api/v1/calls",json={"sourceId":source,"sessionId":"smoke-task"});started.raise_for_status();call=started.json();call_id=call["id"]
                for _ in range(50):
                    status=(await phone_api.get("/api/v1/calls/"+call_id)).json()["status"]
                    if status in ("connecting","active"):break
                    await asyncio.sleep(0.1)
                assert status in ("connecting","active"),status
                async with websockets.connect(("ws://"+origin.split("//",1)[1]+f"/api/v1/calls/{call_id}/audio"),additional_headers={"Authorization":"Bearer "+device_token},ping_interval=20) as media:
                    seen={"agent1":False,"agent2":False,"interrupt":False,"active":False}
                    while not seen["active"]:
                        message=await asyncio.wait_for(media.recv(),timeout=8)
                        if not isinstance(message,bytes):
                            event=json.loads(message)
                            if event.get("type")=="state" and event.get("status")=="active":seen["active"]=True
                    await media.send(struct.pack(">III",0,0,0)+b"\x22"*640)
                    while not (seen["agent1"] and seen["agent2"] and seen["interrupt"]):
                        message=await asyncio.wait_for(media.recv(),timeout=8)
                        if isinstance(message,bytes):
                            if message[12:]==b"\x11"*640:seen["agent1"]=True
                            elif message[12:]==b"\x33"*640:seen["agent2"]=True
                        else:
                            event=json.loads(message)
                            if event.get("type")=="state" and event.get("status")=="active":seen["active"]=True
                            if event.get("type")=="interrupt" and event.get("generation")==4:seen["interrupt"]=True
                    await phone_api.post("/api/v1/calls/"+call_id+"/end")
                    await asyncio.wait_for(call_done.wait(),timeout=8)
                    history=(await phone_api.get("/api/v1/calls")).json();assert history[0]["id"]==call_id and history[0]["reason"]=="ended",history[0]
                    call_state.update(seen)
                incoming=await connector.dial_call(session_id="smoke-task",title="Smoke callback")
                incoming_id=incoming["callId"]; call_state["incoming"]=incoming_id
                invite=(await phone_api.get("/api/v1/calls/incoming")).json();assert invite["id"]==incoming_id and invite["direction"]=="incoming",invite
                await phone_api.post("/api/v1/calls/"+incoming_id+"/accept",json={})
                accepted=await connector.wait_call(incoming_id,timeout=8);assert accepted.get("deviceId"),accepted
                async with connector.call_audio(incoming_id) as agent_audio:
                    async with websockets.connect(("ws://"+origin.split("//",1)[1]+f"/api/v1/calls/{incoming_id}/audio"),additional_headers={"Authorization":"Bearer "+device_token},ping_interval=20) as incoming_media:
                        while True:
                            message=await asyncio.wait_for(incoming_media.recv(),timeout=8)
                            if not isinstance(message,bytes) and json.loads(message).get("status")=="active":break
                        await incoming_media.send(struct.pack(">III",0,0,0)+b"\x44"*640)
                        async for kind,payload in agent_audio.stream():
                            if kind=="audio":
                                assert payload==b"\x44"*640
                                await agent_audio.send(b"\x55"*640)
                                break
                        while True:
                            message=await asyncio.wait_for(incoming_media.recv(),timeout=8)
                            if isinstance(message,bytes): assert message[12:]==b"\x55"*640; break
                await phone_api.post("/api/v1/calls/"+incoming_id+"/end")
                await asyncio.wait_for(incoming_done.wait(),timeout=8)
                call_state["incomingAudio"]=True
            await asyncio.wait_for(handled.wait(),timeout=10); await asyncio.wait_for(voice_handled.wait(),timeout=10)
        finally: await connector.stop(); task.cancel(); await asyncio.gather(task,return_exceptions=True)
        return call_state
    call_state=asyncio.run(asyncio.wait_for(agent(),timeout=60))
    assert call_state.get("audio") and call_state.get("callReason") in ("ended","disconnected") and call_state.get("interrupt") and call_state.get("active"),call_state
    assert call_state.get("incomingAudio") and call_state.get("accepted")==call_state.get("incoming"),call_state
    with sqlite3.connect(data/"notify.db") as db:
        assert db.execute("SELECT status FROM agent_commands WHERE id=?",(command_id,)).fetchone()[0]=="acked"
        assert db.execute("SELECT status FROM agent_commands WHERE id=?",(reply_id,)).fetchone()[0]=="acked"
    with httpx.Client(base_url=origin,headers={"Authorization":"Bearer "+device_token}) as phone_check:
        response=phone_check.get("/api/v1/voice-messages/"+reply_id);response.raise_for_status();assert response.json()["status"]=="delivered",response.text
    result={"restSdk":True,"standardAnswer":True,"elevatedSoftwareSignature":True,"formsRest":True,"formsElevated":True,"voiceNotification":True,"voiceElevatedGrant":True,"voiceReplyDelivered":True,"callRelay":True,"callBargeIn":True,"callHistory":True,"incomingCall":True,"incomingCallAudio":True,"agentConnector":True,"agentCommandAcked":True,"mcpHttpTools":names,"androidHardwareVerified":False,"externalProvidersCalled":False}
    (run/"result.json").write_text(json.dumps(result,indent=2),encoding="utf-8");print(json.dumps(result,indent=2))
    (run/"openapi.v1.json").write_bytes(urllib.request.urlopen(origin+"/openapi/v1.json").read())
finally:
    process.terminate()
    try:process.wait(timeout=10)
    except subprocess.TimeoutExpired:process.kill();process.wait()
    log.close()
