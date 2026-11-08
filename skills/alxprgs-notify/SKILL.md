---
name: alxprgs-notify
description: Send notifications and request action-specific human decisions through an already configured ALXPRGS Notify MCP server or Python SDK. Use when the user wants mobile notifications or approval through Notify.
---

# ALXPRGS Notify

Use the connected Notify MCP tools. If only Python is configured, use the installed `alxprgs_notify` package with `NOTIFY_URL` and `NOTIFY_TOKEN` from the environment; do not read or print token values. `GET /api/v1/session` reports the current `actor` and `csrfToken`; an integration sender does not need CSRF, and an empty-body 401 means the token is missing, expired, or revoked.

For an informational update call `send_notification`. When a decision is needed, call `request_confirmation` with a concise action description, consequences, expiration and a stable idempotency key for that action. Use `elevated` for sensitive actions. Do not include credentials or unnecessary private data in notification text.

Keep the returned request ID. Call `wait_for_response` for at most 25 seconds per invocation; `get_request` can retrieve the result later. A saved response does not automatically wake or resume a stopped agent.

Only `status=answered` with `answer.decision=yes` approves the described action. A custom response is an instruction requiring interpretation, not implicit approval. `no`, `pending`, `expired`, `cancelled`, network failure and missing response do not grant permission. Do not repeatedly recreate requests after rejection or expiration. Cancel a pending request when its action is no longer relevant.

For a structured request use `request_form` with 1–20 fields (`text`, `boolean`, `single_choice`, `multi_choice`, `number`). Keep field IDs stable; read the returned `formAnswer.values` only when the status is achieved and validate each value before acting. A form answer is still only the user's instruction, not permission to run unrelated or dangerous actions. Use `elevated` for sensitive forms; the phone signs the whole value set.

Approval is bound to the displayed action. If its substance changes, obtain a new decision. Notify does not override the host environment's access rules or built-in permission checks.

Use `start_call` when it is available on the connected OAuth MCP server and the user asks to ring their Android phone. It reads the supplied Russian text after manual acceptance. A result of `ringing` means the invitation is pending. Repeat with identical arguments and the same idempotency key to check the state without placing another call. `playback_sent` confirms server transmission of the audio frames. Use `send_voice` for a voice message.

Live Android/SDK calls require an agent that negotiates `alxprgs.notify.call.v2` through `callProtocols` in hello. An old/unknown agent remains usable for text/voice messages. Audio is not recorded. Mute does not create sequence gaps, and barge-in cancels the old generation. See `/api/config` capabilities.
