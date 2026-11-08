"""Notify contract v1. Only an explicit yes is an approval."""
from .client import AsyncNotifyClient, NotifyClient, NotifyError, Notification, verify_webhook

_AGENT_EXPORTS = {"AgentConnector", "AgentRuntime", "CallbackRuntime", "Transcriber", "Synthesizer", "PROTOCOL", "CallHandler", "CallAudio", "CALL_PROTOCOL", "NotifyCallError"}


def __getattr__(name: str):
    if name in _AGENT_EXPORTS:
        from . import agent

        return getattr(agent, name)
    raise AttributeError(name)


__all__ = ["AsyncNotifyClient", "NotifyClient", "NotifyError", "Notification", "verify_webhook", *_AGENT_EXPORTS]
