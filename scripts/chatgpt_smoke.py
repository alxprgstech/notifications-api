"""Exercise the integrated Notify OAuth endpoint with an isolated OIDC provider and SQLite."""
import asyncio
import base64
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import hmac
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
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.keywrap import aes_key_unwrap
import httpx
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client


ROOT = Path(__file__).resolve().parents[1]
OWNER = "00000000-0000-4000-8000-000000000001"
SCOPES = "notify:send requests:create requests:read requests:cancel offline_access"
SSO_SECRET = secrets.token_urlsafe(32)


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def digest(value):
    return b64(hashlib.sha256(value.encode()).digest())


def decode64(value):
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def wrong_audience_token(token, oauth_directory, audience):
    """Re-sign and encrypt a fixture token, changing only its audience."""
    header, wrapped, iv, ciphertext, _ = token.split(".")
    assert json.loads(decode64(header))["enc"] == "A256CBC-HS512"
    key = aes_key_unwrap((oauth_directory / "encryption.key").read_bytes(), decode64(wrapped))
    decryptor = Cipher(algorithms.AES(key[32:]), modes.CBC(decode64(iv))).decryptor()
    plain = decryptor.update(decode64(ciphertext)) + decryptor.finalize()
    signing_header, claims, _ = plain[:-plain[-1]].decode().split(".")
    claims = json.loads(decode64(claims))
    assert claims["aud"] == audience or claims["aud"] == [audience]
    signing_info = json.loads(decode64(signing_header))
    signing_key = serialization.load_pem_private_key((oauth_directory / "signing.pem").read_bytes(), password=None)
    claims["aud"] = audience + "/wrong"
    signed = jwt(signing_key, claims, kind=signing_info["typ"], kid=signing_info["kid"]).encode()
    padding_size = 16 - len(signed) % 16
    iv = secrets.token_bytes(16)
    encryptor = Cipher(algorithms.AES(key[32:]), modes.CBC(iv)).encryptor()
    encrypted = encryptor.update(signed + bytes([padding_size]) * padding_size) + encryptor.finalize()
    aad = header.encode()
    tag = hmac.digest(key[:32], aad + iv + encrypted + (len(aad) * 8).to_bytes(8, "big"), "sha512")[:32]
    return ".".join([header, wrapped, b64(iv), b64(encrypted), b64(tag)])


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
    if not assembly.is_file():
        raise RuntimeError(f"Missing Release build for {project}. Run dotnet build Notify.sln -c Release.")
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


def code_flow(browser, adapter, resource, client_id, scopes=SCOPES, *, reuse_session=True, callback=None):
    verifier = secrets.token_urlsafe(48)
    state = secrets.token_urlsafe(24)
    callback = callback or adapter + "/test-client/callback"
    consent = browser.get(adapter + "/chatgpt/oauth/authorize", params={
        "client_id": client_id, "redirect_uri": callback, "response_type": "code",
        "code_challenge": digest(verifier), "code_challenge_method": "S256",
        "resource": resource, "scope": scopes, "state": state,
    }, follow_redirects=True)
    consent.raise_for_status()
    assert 'class="consent-scopes"' in consent.text and '<meta name="viewport"' in consent.text
    assert "no-store" in consent.headers["cache-control"].split(", ")
    callback_origin = urlsplit(callback)
    assert consent.headers["content-security-policy"].endswith(
        f"form-action 'self' {callback_origin.scheme}://{callback_origin.netloc}")
    descriptions = {
        "notify:send": "Отправлять вам текстовые и голосовые уведомления.",
        "requests:create": "Запрашивать у вас подтверждения и отправлять формы для заполнения.",
        "requests:read": "Читать статус и ваши ответы на запросы, созданные этим подключением.",
        "requests:cancel": "Отменять незавершённые запросы, созданные этим подключением.",
        "offline_access": "Продлевать доступ без повторного входа в течение 30 дней.",
    }
    for scope, description in descriptions.items():
        assert (f"<code>{scope}</code><p>{description}</p>" in consent.text) == (scope in scopes.split())
    if reuse_session:
        assert not consent.history
    else:
        assert "/signin-oidc" in {response.url.path for response in consent.history}
    fields = Consent(consent.text).fields
    assert fields["resource"] == resource
    csrf = fields.pop("__RequestVerificationToken")
    assert browser.post(adapter + "/chatgpt/oauth/authorize", data=fields | {"decision": "allow"}).status_code == 400
    result = browser.post(adapter + "/chatgpt/oauth/authorize", data=fields | {"decision": "allow", "__RequestVerificationToken": csrf})
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


async def mcp_call(adapter, token, name=None, args=None, *, error=None, legacy=False):
    async with httpx.AsyncClient(headers={"Authorization": "Bearer " + token}, timeout=45, trust_env=False) as client:
        async with streamable_http_client(adapter + ("/mcp" if legacy else "/chatgpt/mcp"), http_client=client) as streams:
            async with ClientSession(*streams[:2]) as session:
                await session.initialize()
                if name is None:
                    tools = (await session.list_tools()).tools
                    if legacy:
                        return {tool.name for tool in tools}
                    return {
                        tool.name: tool.model_dump(by_alias=True)["_meta"]["securitySchemes"][0]["scopes"][0]
                        for tool in tools
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
    notify = adapter = origin()
    data, legacy_token, _, other_token, device_token, device_key = seed(directory)
    with ExitStack() as stack:
        sso = stack.enter_context(fixture(Sso))
        sso.key, sso.codes, sso.subject = rsa.generate_private_key(public_exponent=65537, key_size=2048), {}, OWNER
        speech = stack.enter_context(fixture(FaultProxy))
        speech.upstream, speech.drop_key, speech.offline = silero, None, False
        settings = {"Notify__PublicUrl": notify, "Notify__SsoIssuer": sso.url, "Notify__WebClientId": "notify-mcp-test",
                    "Notify__WebClientSecret": SSO_SECRET, "Notify__OwnerSub": OWNER, "Notify__DataPath": str(data),
                    "ChatGpt__SileroUrl": speech.url}
        with sqlite3.connect(data / "notify.db") as db:
            db.execute("INSERT INTO objects VALUES(?,?,?)", ("meta", "owner", json.dumps(OWNER)))
        stack.enter_context(start("Notify.Server", notify, settings, directory))
        client = stack.enter_context(httpx.Client(timeout=15, trust_env=False))
        phone = stack.enter_context(httpx.Client(base_url=notify, headers={"Authorization": "Bearer " + device_token}))
        resource = notify + "/chatgpt/mcp"
        metadata = client.get(notify + "/.well-known/oauth-protected-resource/chatgpt/mcp").json()
        discovery = client.get(notify + "/.well-known/oauth-authorization-server").json()
        assert metadata["resource"] == resource and metadata["authorization_servers"] == [discovery["issuer"]]
        assert discovery["code_challenge_methods_supported"] == ["S256"]
        challenge = client.get(resource)
        assert challenge.status_code == 401 and "resource_metadata=" in challenge.headers.get("www-authenticate", ""), challenge.status_code
        assert "send_voice" not in call(notify, legacy_token, legacy=True)
        assert phone.get("/api/v1/chatgpt").status_code == 403
        assert phone.post("/api/v1/management", json={"action": "chatgpt.link"}).status_code == 403
        sso.subject = "other-owner"
        assert client.get(notify + "/auth/login", follow_redirects=True).status_code == 401
        sso.subject = OWNER
        login = client.get(notify + "/auth/login", follow_redirects=True)
        assert login.url.path == "/" and "/signin-oidc" in {response.url.path for response in login.history}
        session = client.get(notify + "/api/v1/session").raise_for_status().json()
        browser_headers = {"Origin": notify, "X-CSRF-Token": session["csrfToken"]}
        management = client.post(notify + "/api/v1/management", json={"action": "chatgpt.link"},
                                 headers=browser_headers).raise_for_status().json()
        history = phone.get("/api/v1/notifications").raise_for_status().json()["items"]
        link_notice = next(item["id"] for item in history if item.get("managementId") == management["id"])
        answer = {"decision": "yes"}
        endpoint = f"/api/v1/notifications/{link_notice}"
        assert phone.post(endpoint + "/response", json={"answer": answer}).status_code == 403
        proof = phone.post(endpoint + "/challenge", json=answer).raise_for_status().json()
        signature = device_key.sign(base64.b64decode(proof["payload"]), ec.ECDSA(hashes.SHA256()))
        phone.post(endpoint + "/response", json={"answer": answer, "challengeId": proof["id"],
                                                  "signature": base64.b64encode(signature).decode()}).raise_for_status()
        management_path = notify + "/api/v1/management/" + management["id"]
        connection = client.get(management_path).raise_for_status().json()["result"]
        assert "result" not in client.get(management_path).raise_for_status().json()
        default_callback = "https://chatgpt.com/connector_platform_oauth_redirect"
        status = client.get(notify + "/api/v1/chatgpt").raise_for_status().json()
        assert status["redirectUri"] == status["defaultRedirectUri"] == default_callback
        auth = (connection["clientId"], connection["clientSecret"])
        fields = code_flow(client, adapter, resource, auth[0], callback=default_callback)
        client.post(discovery["token_endpoint"], data=fields, auth=auth).raise_for_status()
        callback = notify + "/test-client/callback"
        assert client.post(notify + "/api/v1/chatgpt/callback", json={"redirectUri": callback},
                           headers={"Origin": notify}).status_code == 403
        assert client.post(notify + "/api/v1/chatgpt/callback", json={"redirectUri": "https://invalid.example/callback"},
                           headers=browser_headers).status_code == 400
        client.post(notify + "/api/v1/chatgpt/callback", json={"redirectUri": callback},
                    headers=browser_headers).raise_for_status()
        assert client.get(notify + "/api/v1/chatgpt").json()["redirectUri"] == callback
        with httpx.Client(timeout=15, trust_env=False) as fresh:
            fields = code_flow(fresh, adapter, resource, auth[0], reuse_session=False)
        token_url = discovery["token_endpoint"]
        assert client.post(token_url, data=fields | {"resource": adapter + "/other"}, auth=auth).json()["error"] == "invalid_target"
        result = client.post(token_url, data=fields, auth=auth)
        assert result.is_success, (result.status_code, result.text)
        token, refresh = (result.json()[key] for key in ("access_token", "refresh_token"))
        wrong_aud = wrong_audience_token(token, data / "chatgpt", resource)
        assert client.get(resource, headers={"Authorization": "Bearer " + wrong_aud}).status_code == 401
        used_code = fields
        fields = code_flow(client, adapter, resource, auth[0], "requests:read")
        oauth_db = data / "chatgpt" / "oauth.db"
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
        bearer = {"Authorization": "Bearer " + token}
        assert client.get(notify + "/api/v1/session", headers=bearer).status_code == 401
        assert client.post(notify + "/mcp", headers=bearer, json={}).status_code == 401
        assert client.post(notify + endpoint + "/response", headers=bearer, json={"answer": answer}).status_code == 401
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
        # Drop the first client result, then retry with Silero offline to prove upload reuse.
        call(adapter, token, "send_voice", audio)
        speech.offline = True
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
        client.post(notify + "/api/v1/chatgpt/disconnect", json={}, headers=browser_headers).raise_for_status()
        live_bearer = {"Authorization": "Bearer " + token}
        assert client.get(resource, headers=live_bearer).status_code in (401, 403)
        assert client.get(notify + "/api/v1/chatgpt").json()["clientId"] is None
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
