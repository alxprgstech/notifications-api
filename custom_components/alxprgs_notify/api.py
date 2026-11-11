"""HTTP transport for the Notify integration."""
from urllib.parse import urlsplit
from uuid import uuid4

from aiohttp import ClientError, ClientTimeout

DOMAIN = "alxprgs_notify"


class NotifyError(Exception):
    """A safe, user-facing Notify error."""


def validate_url(url):
    """Accept a TLS origin without embedded credentials."""
    parts = urlsplit(url)
    if (parts.scheme != "https" or not parts.hostname or parts.username is not None
            or parts.password is not None or parts.query or parts.fragment or parts.path not in ("", "/")):
        raise ValueError("invalid_url")
    return url.rstrip("/")


async def request(session, url, token, method, path, payload=None):
    """Call a fixed API path without forwarding credentials across redirects."""
    try:
        async with session.request(method, url + "/api/v1/" + path,
                                   headers={"Authorization": f"Bearer {token}"},
                                   json=payload, allow_redirects=False,
                                   timeout=ClientTimeout(total=30)) as response:
            if response.status in (401, 403):
                raise NotifyError("invalid_auth")
            try:
                body = await response.json()
            except (ValueError, ClientError) as error:
                raise NotifyError("unexpected_response") from error
            if not isinstance(body, dict):
                raise NotifyError("unexpected_response")
            if response.status >= 300:
                code = body.get("code")
                raise NotifyError(code if isinstance(code, str) and code.isidentifier() and len(code) < 64
                                  else "unexpected_response")
            return body
    except (ClientError, TimeoutError) as error:
        raise NotifyError("cannot_connect") from error


async def call(session, url, token, data):
    """Request one announcement and return its initial state."""
    result = await request(session, url, token, "POST", "calls/announce", {
        "text": data["text"],
        "title": data.get("title", "Home Assistant"),
        "target": data.get("target", "android"),
        "idempotencyKey": data.get("idempotency_key") or uuid4().hex,
    })
    return {key: result[key] for key in ("id", "status", "reason", "target") if key in result}
