"""Exercise the real OAuth adapter and Notify with an isolated OIDC provider and SQLite."""
import asyncio
import base64
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
from html.parser import HTMLParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import sys
from threading import Thread
import time
from urllib.parse import parse_qs, urlencode, urlsplit
import wave

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, padding, rsa
import httpx
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client


ROOT = Path(__file__).resolve().parents[1]
OWNER = "synthetic-owner"
SCOPES = "notify:send requests:create requests:read requests:cancel offline_access"
CLIENT_SECRET = secrets.token_urlsafe(32)
SSO_SECRET = secrets.token_urlsafe(32)


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def digest(value):
    return b64(hashlib.sha256(value.encode()).digest())


def jwt(key, claims, kind="JWT", kid="sso-test"):
    header = b64(json.dumps({"alg": "RS256", "typ": kind, "kid": kid}).encode())
    message = f"{header}.{b64(json.dumps(claims).encode())}".encode()
    return f"{message.decode()}.{b64(key.sign(message, padding.PKCS1v15(), hashes.SHA256()))}"


def origin():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return f"http://127.0.0.1:{sock.getsockname()[1]}"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def form(self):
        return parse_qs(self.rfile.read(int(self.headers["Content-Length"])).decode())

    def send(self, status, value=None, location=None):
        self.send_response(status)
        self.send_header("Connection", "close")
        if location:
            self.send_header("Location", location)
        if value is not None:
            body = json.dumps(value).encode()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if value is not None:
            self.wfile.write(body)


class Sso(Handler):
    def do_GET(self):
        parts = urlsplit(self.path)
        server = self.server
        if parts.path == "/.well-known/openid-configuration":
            return self.send(200, {
                "issuer": server.url, "authorization_endpoint": server.url + "/authorize",
                "token_endpoint": server.url + "/token", "jwks_uri": server.url + "/jwks",
                "response_types_supported": ["code"], "subject_types_supported": ["public"],
                "id_token_signing_alg_values_supported": ["RS256"],
                "code_challenge_methods_supported": ["S256"],
            })
        if parts.path == "/jwks":
            public = server.key.public_key().public_numbers()
            return self.send(200, {"keys": [{
                "kid": "sso-test", "kty": "RSA", "use": "sig", "alg": "RS256",
                "e": b64(public.e.to_bytes(3, "big")), "n": b64(public.n.to_bytes(256, "big")),
            }]})
        assert parts.path == "/authorize"
        query = {name: values[0] for name, values in parse_qs(parts.query).items()}
        assert query["client_id"] == "notify-mcp-test"
        assert query["code_challenge_method"] == "S256" and len(query["state"]) <= 128
        code = secrets.token_urlsafe(24)
        server.codes[code] = query
        self.send(302, location=query["redirect_uri"] + "?" + urlencode({"code": code, "state": query["state"]}))

    def do_POST(self):
        assert self.path == "/token"
        fields = {name: values[0] for name, values in self.form().items()}
        assert fields["client_secret"] == SSO_SECRET and fields["client_id"] == "notify-mcp-test"
        query = self.server.codes.pop(fields["code"])
        assert digest(fields["code_verifier"]) == query["code_challenge"]
        assert fields["redirect_uri"] == query["redirect_uri"]
        now = int(time.time())
        claims = {"iss": self.server.url, "aud": query["client_id"], "sub": self.server.subject,
                  "iat": now, "exp": now + 120, "nonce": query["nonce"], "token_use": "id_token"}
        self.send(200, {"access_token": "synthetic-sso-token", "token_type": "Bearer",
                        "id_token": jwt(self.server.key, claims)})


class FaultProxy(Handler):
    """Send to real Notify, optionally lose exactly one successful create response."""
    def do_GET(self):
        self.forward()

    def do_POST(self):
        self.forward()

    def forward(self):
        if self.server.offline:
            return self.send(503, {"code": "fixture_offline"})
        length = int(self.headers.get("Content-Length", 0))
        headers = {name: self.headers[name] for name in ("Content-Type", "Authorization", "Idempotency-Key") if name in self.headers}
        with httpx.Client(timeout=30, trust_env=False) as client:
            response = client.request(self.command, self.server.upstream + self.path,
                                      headers=headers, content=self.rfile.read(length) if length else None)
        if self.path == "/api/v1/notifications" and response.is_success and headers.get("Idempotency-Key") == self.server.drop_key:
            self.server.drop_key = None
            # The gateway loses the successful upstream response after Notify has committed.
            return self.send(502, {"code": "notify_unavailable"})
        self.send_response(response.status_code)
        self.send_header("Connection", "close")
        if content_type := response.headers.get("content-type"):
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(response.content)))
        self.end_headers()
        self.wfile.write(response.content)


class FixtureSpeech(Handler):
    def do_POST(self):
        self.rfile.read(int(self.headers["Content-Length"]))
        with io.BytesIO() as output:
            with wave.open(output, "wb") as wav:
                wav.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                wav.writeframes(b"\x10\x00\xf0\xff" * 8000)
            body = output.getvalue()
        self.send_response(200)
        self.send_header("Connection", "close")
        self.send_header("Content-Type", "audio/wav")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


@contextmanager
def fixture(handler):
    with ThreadingHTTPServer(("127.0.0.1", 0), handler) as server:
        server.url = f"http://127.0.0.1:{server.server_port}"
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server
        finally:
            server.shutdown()
            thread.join()


@contextmanager
def start(project, url, values, directory):
    env = {key: value for key, value in os.environ.items() if not key.lower().startswith(("notify__", "chatgpt__"))}
    env.update(ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_URLS=url, **values)
    assembly = ROOT / f"src/{project}/bin/Release/net10.0/{project}.dll"
    with (directory / f"{project}.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen(["dotnet", str(assembly)], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 45
            while True:
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f"{project} failed to start. See {log.name}")
                try:
                    httpx.get(url + "/health/live", timeout=1).raise_for_status()
                    break
                except httpx.HTTPError:
                    time.sleep(0.2)
            yield
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)


class Consent(HTMLParser):
    def __init__(self, markup):
        super().__init__()
        self.fields = {}
        self.feed(markup)

    def handle_starttag(self, tag, attributes):
        if tag == "input":
            attrs = dict(attributes)
            self.fields[attrs["name"]] = attrs["value"]


def code_flow(browser, adapter, resource, scopes=SCOPES, *, unauthorized=False):
    verifier = secrets.token_urlsafe(48)
    state = secrets.token_urlsafe(24)
    callback = adapter + "/test-client/callback"
    consent = browser.get(adapter + "/oauth/authorize", params={
        "client_id": "chatgpt-test", "redirect_uri": callback, "response_type": "code",
        "code_challenge": digest(verifier), "code_challenge_method": "S256",
        "resource": resource, "scope": scopes, "state": state,
    }, follow_redirects=True)
    if unauthorized:
        assert consent.status_code == 401, (
            [(r.status_code, r.url.path) for r in [*consent.history, consent]],
            consent.text[:400],
        )
        return None
    consent.raise_for_status()
    fields = Consent(consent.text).fields
    assert fields["resource"] == resource
    csrf = fields.pop("__RequestVerificationToken")
    assert browser.post(adapter + "/oauth/authorize", data=fields | {"decision": "allow"}).status_code == 400
    result = browser.post(adapter + "/oauth/authorize", data=fields | {"decision": "allow", "__RequestVerificationToken": csrf})
    assert result.is_redirect, (result.status_code, result.text)
    returned = parse_qs(urlsplit(result.headers["location"]).query)
    assert returned.get("state") == [state] and "code" in returned, returned.keys()
    return {"grant_type": "authorization_code", "code": returned["code"][0], "code_verifier": verifier,
            "redirect_uri": callback, "resource": resource}


def seed(directory):
    data = directory / "notify"
    data.mkdir()
    schema = (ROOT / "src/Notify.Server/Database.cs").read_text(encoding="utf-8").split('"""')[1]
    device_key = ec.generate_private_key(ec.SECP256R1())
    public_key = device_key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    with sqlite3.connect(data / "notify.db") as db:
        db.executescript(schema)
        credentials = []
        for name in ("adapter", "other"):
            identifier, token = secrets.token_hex(16), "nt_" + secrets.token_urlsafe(32)
            source = {"id": identifier, "name": name, "scopes": SCOPES.split()[:-1], "preview": "hidden", "revoked": False}
            db.execute("INSERT INTO objects VALUES(?,?,?)", ("source", identifier, json.dumps(source)))
            credentials.append((token, "integration", identifier))
        device = secrets.token_hex(16)
        device_token = "nt_" + secrets.token_urlsafe(32)
        db.execute("INSERT INTO objects VALUES(?,?,?)", ("device", device, json.dumps({
            "id": device, "name": "Software test fixture", "publicKey": base64.b64encode(public_key).decode(), "revoked": False,
        })))
        credentials.append((device_token, "device", device))
        for token, kind, identifier in credentials:
            db.execute("INSERT INTO credentials VALUES(?,?,?,?)",
                       (hashlib.sha256(token.encode()).hexdigest(), kind, identifier, int(time.time() * 1000) + 600000))
    return data, credentials[0][0], credentials[0][2], credentials[1][0], device_token, device_key


async def mcp_call(adapter, token, name=None, args=None, *, error=None):
    async with httpx.AsyncClient(headers={"Authorization": "Bearer " + token}, timeout=45, trust_env=False) as client:
        async with streamable_http_client(adapter + "/mcp", http_client=client) as streams:
            async with ClientSession(*streams[:2]) as session:
                await session.initialize()
                if name is None:
                    return {
                        tool.name: tool.model_dump(by_alias=True)["_meta"]["securitySchemes"][0]["scopes"][0]
                        for tool in (await session.list_tools()).tools
                    }
                result = (await session.call_tool(name, args)).model_dump(by_alias=True)
                if error:
                    assert result["isError"] and error in result["content"][0]["text"], result
                    if error == "insufficient_scope":
                        assert "mcp/www_authenticate" in result["_meta"]
                    return None
                assert not result["isError"], result
                return result.get("structuredContent") or json.loads(result["content"][0]["text"])


def call(*args, **kwargs):
    return asyncio.run(mcp_call(*args, **kwargs))


def access_tokens(database):
    with sqlite3.connect(database) as db:
        return {row[0] for row in db.execute(
            "SELECT Id FROM OpenIddictTokens WHERE Type = ?",
            ("urn:ietf:params:oauth:token-type:access_token",),
        )}


def smoke(directory, silero):
    notify, adapter = origin(), origin()
    data, notify_token, source_id, other_token, device_token, device_key = seed(directory)
    adapter_data = directory / "adapter"
    with ExitStack() as stack:
        sso = stack.enter_context(fixture(Sso))
        sso.key, sso.codes, sso.subject = rsa.generate_private_key(public_exponent=65537, key_size=2048), {}, OWNER
        proxy = stack.enter_context(fixture(FaultProxy))
        proxy.upstream, proxy.drop_key, proxy.offline = notify, None, False
        speech = stack.enter_context(fixture(FaultProxy))
        speech.upstream, speech.drop_key, speech.offline = silero, None, False
        stack.enter_context(start("Notify.Server", notify, {"Notify__PublicUrl": notify, "Notify__DataPath": str(data)}, directory))
        settings = {"PublicUrl": adapter, "SsoIssuer": sso.url, "SsoClientId": "notify-mcp-test",
                    "SsoClientSecret": SSO_SECRET, "OwnerSub": OWNER, "ClientId": "chatgpt-test",
                    "ClientSecret": CLIENT_SECRET, "ClientRedirect": adapter + "/test-client/callback",
                    "NotifyUrl": proxy.url, "NotifyToken": notify_token, "NotifySourceId": source_id,
                    "SileroUrl": speech.url, "DataPath": str(adapter_data)}
        stack.enter_context(start("Notify.ChatGpt", adapter, {"ChatGpt__" + k: v for k, v in settings.items()}, directory))
        client = stack.enter_context(httpx.Client(timeout=15, trust_env=False))
        phone = stack.enter_context(httpx.Client(base_url=notify, headers={"Authorization": "Bearer " + device_token}))
        resource = adapter + "/mcp"
        metadata = client.get(adapter + "/.well-known/oauth-protected-resource/mcp").json()
        discovery = client.get(adapter + "/.well-known/oauth-authorization-server").json()
        assert metadata["resource"] == resource and metadata["authorization_servers"] == [discovery["issuer"]]
        assert discovery["code_challenge_methods_supported"] == ["S256"]
        challenge = client.get(resource)
        assert challenge.status_code == 401 and "resource_metadata=" in challenge.headers.get("www-authenticate", ""), (
            challenge.status_code, challenge.headers.get("www-authenticate"), challenge.text)
        sso.subject = "other-owner"
        code_flow(client, adapter, resource, unauthorized=True)
        sso.subject = OWNER
        fields = code_flow(client, adapter, resource)
        auth = ("chatgpt-test", CLIENT_SECRET)
        token_url = discovery["token_endpoint"]
        assert client.post(token_url, data=fields | {"resource": adapter + "/other"}, auth=auth).json()["error"] == "invalid_target"
        result = client.post(token_url, data=fields, auth=auth)
        assert result.is_success, (result.status_code, result.text)
        token, refresh = (result.json()[key] for key in ("access_token", "refresh_token"))
        used_code = fields
        fields = code_flow(client, adapter, resource, "requests:read")
        oauth_db = adapter_data / "adapter.db"
        before = access_tokens(oauth_db)
        restricted = client.post(token_url, data=fields, auth=auth).json()["access_token"]
        restricted_id, = access_tokens(oauth_db) - before
        assert call(adapter, token) == {
            "send_notification": "notify:send", "send_voice": "notify:send",
            "request_confirmation": "requests:create", "request_form": "requests:create",
            "get_request": "requests:read", "wait_for_response": "requests:read",
            "cancel_request": "requests:cancel",
        }
        message = {"title": "MCP test", "body": "Synthetic data", "idempotencyKey": secrets.token_hex(16)}
        call(adapter, restricted, "send_notification", message, error="insufficient_scope")
        assert call(adapter, token, "send_notification", message)["content"]["kind"] == "message"
        expiry = (datetime.now(timezone.utc) + timedelta(minutes=10)).isoformat()
        decision = message | {"expiresAt": expiry, "idempotencyKey": secrets.token_hex(16), "securityLevel": "elevated"}
        identifier = call(adapter, token, "request_confirmation", decision)["id"]
        assert call(adapter, restricted, "wait_for_response", {"id": identifier, "seconds": 0})["status"] == "pending"
        with sqlite3.connect(oauth_db) as db:
            db.execute("UPDATE OpenIddictTokens SET ExpirationDate = '2000-01-01 00:00:00' WHERE Id = ?", (restricted_id,))
        assert client.get(resource, headers={"Authorization": "Bearer " + restricted}).status_code in (401, 403)
        answer = {"decision": "yes"}
        endpoint = f"/api/v1/notifications/{identifier}"
        assert phone.post(endpoint + "/response", json={"answer": answer}).status_code == 403
        proof = phone.post(endpoint + "/challenge", json=answer).raise_for_status().json()
        signature = device_key.sign(base64.b64decode(proof["payload"]), ec.ECDSA(hashes.SHA256()))
        phone.post(endpoint + "/response", json={"answer": answer, "challengeId": proof["id"],
                                                  "signature": base64.b64encode(signature).decode()}).raise_for_status()
        assert call(adapter, token, "get_request", {"id": identifier})["status"] == "answered"
        fields_json = json.dumps([{"id": "ok", "label": "OK", "type": "boolean", "required": True}])
        form = call(adapter, token, "request_form", decision | {"idempotencyKey": secrets.token_hex(16), "fieldsJson": fields_json})
        assert call(adapter, token, "cancel_request", {"id": form["id"]})["status"] == "cancelled"
        foreign = client.post(notify + "/api/v1/notifications", json={"kind": "confirmation", "title": "Foreign", "body": "", "expiresAt": expiry},
                              headers={"Authorization": "Bearer " + other_token, "Idempotency-Key": secrets.token_hex(16)}).raise_for_status().json()
        call(adapter, token, "get_request", {"id": foreign["id"]}, error="notification_not_found")
        call(adapter, token, "cancel_request", {"id": foreign["id"]}, error="notification_not_found")
        rotated = client.post(token_url, data={"grant_type": "refresh_token", "refresh_token": refresh, "resource": resource}, auth=auth).raise_for_status().json()
        assert rotated["refresh_token"] != refresh
        client.post(discovery["revocation_endpoint"], data={"token": rotated["access_token"]}, auth=auth).raise_for_status()
        assert client.get(resource, headers={"Authorization": "Bearer " + rotated["access_token"]}).status_code == 401
        audio = {"title": "Тест Silero", "text": "Проверка голосового уведомления.", "expiresAt": expiry, "idempotencyKey": secrets.token_hex(16)}
        speech.offline = True
        call(adapter, token, "send_voice", audio, error="silero_unavailable")
        assert call(adapter, token, "send_notification", message | {"idempotencyKey": secrets.token_hex(16)})["content"]["kind"] == "message"
        speech.offline = False
        proxy.drop_key = audio["idempotencyKey"]
        call(adapter, token, "send_voice", audio, error="notify_unavailable")
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: call(adapter, token, "send_voice", audio), range(2)))
        assert results[0]["id"] == results[1]["id"]
        with sqlite3.connect(data / "notify.db") as db:
            assert db.execute("SELECT count(*) FROM notifications WHERE id = ?", (results[0]["id"],)).fetchone() == (1,)
            assert db.execute("SELECT count(*) FROM idempotency WHERE key = ?", (audio["idempotencyKey"],)).fetchone() == (1,)
        call(adapter, token, "send_voice", audio | {"text": "Другое сообщение."}, error="idempotency_conflict")
        voice = results[0]["content"]["voice"]
        wav = phone.get(f"/api/v1/audio/{voice['audioId']}").raise_for_status().content
        with wave.open(io.BytesIO(wav)) as sample:
            assert (sample.getnchannels(), sample.getsampwidth(), sample.getframerate()) == (1, 2, 16000)
            assert sample.getnframes() > 0 and any(sample.readframes(sample.getnframes()))
        assert client.post(token_url, data=used_code, auth=auth).json()["error"] == "invalid_grant"
        print("PASS OAuth, MCP, scope/ownership, elevated software signature, refresh/revocation, WAV and lost-response retry")


if __name__ == "__main__":
    if len(sys.argv) != 2 or not (sys.argv[1] == "--fixture-audio" or urlsplit(sys.argv[1]).hostname == "127.0.0.1"):
        raise SystemExit("Usage: python scripts/chatgpt_smoke.py [--fixture-audio | http://127.0.0.1:<test-silero-port>]")
    directory = ROOT / "artifacts" / ("chatgpt-smoke-" + secrets.token_hex(5))
    directory.mkdir(parents=True)
    if sys.argv[1] == "--fixture-audio":
        with fixture(FixtureSpeech) as speech:
            smoke(directory, speech.url)
    else:
        smoke(directory, sys.argv[1])
