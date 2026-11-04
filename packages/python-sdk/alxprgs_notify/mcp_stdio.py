"""Local MCP transport; credentials stay in the environment, never tool arguments."""
import os
from mcp.server.mcpserver import MCPServer
from .client import NotifyClient

server = MCPServer("ALXPRGS Notify")


def client() -> NotifyClient:
    return NotifyClient(os.environ["NOTIFY_URL"], os.environ["NOTIFY_TOKEN"])


@server.tool()
def send_notification(title: str, body: str, idempotency_key: str, icon_id: str | None = None, correlation_id: str | None = None) -> dict:
    """Send a message without requesting approval."""
    with client() as api:
        return api.send(title, body, idempotency_key=idempotency_key, icon_id=icon_id, correlation_id=correlation_id)


@server.tool()
def request_confirmation(title: str, body: str, expires_at: str, idempotency_key: str, security_level: str = "standard", allow_custom: bool = True, icon_id: str | None = None, correlation_id: str | None = None) -> dict:
    """Request approval. Only status=answered and answer.decision=yes approves the described action."""
    with client() as api:
        return api.request(title, body, expires_at=expires_at, idempotency_key=idempotency_key, security_level=security_level, allow_custom=allow_custom, icon_id=icon_id, correlation_id=correlation_id)


@server.tool()
def request_form(title: str, body: str, expires_at: str, fields: list[dict], idempotency_key: str, security_level: str = "standard", icon_id: str | None = None, correlation_id: str | None = None) -> dict:
    """Request a structured form (text, boolean, single_choice, multi_choice, number). One final answer; expiry and cancellation never approve."""
    with client() as api:
        return api.request_form(title, body, expires_at=expires_at, fields=fields, idempotency_key=idempotency_key, security_level=security_level, icon_id=icon_id, correlation_id=correlation_id)


@server.tool()
def get_request(notification_id: str) -> dict:
    with client() as api:
        return api.get(notification_id)


@server.tool()
def wait_for_response(notification_id: str, seconds: int = 20) -> dict:
    """Wait at most 25 seconds; pending remains pending. Does not resume a stopped agent."""
    if not 0 <= seconds <= 25:
        raise ValueError("seconds must be 0..25")
    with client() as api:
        return api.wait(notification_id, timeout=seconds)


@server.tool()
def cancel_request(notification_id: str) -> dict:
    with client() as api:
        return api.cancel(notification_id)


def main() -> None:
    server.run(transport="stdio")


if __name__ == "__main__":
    main()
