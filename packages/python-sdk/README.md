# alxprgs-notify 0.5.0

Приватный Python-клиент ALXPRGS Notify. Python 3.11+, sync/async HTTP, проверка подписи webhook, optional MCP stdio adapter. Инструкции: `../../docs/CONTRACT.md`; операции, поля и коды ошибок: `/docs` (`/openapi/v1.json`); пример: `../../examples/agent.py`.

Установка из исходников: `pip install .` или `pip install '.[mcp]'`. Для исходящего подключения агента: `pip install '.[agent]'`. Установка из wheel не требует исходников сервера. Не публикуйте пакет без отдельного поручения владельца.

## Авторизация и ошибки

Токен интеграции передаётся конструктору `NotifyClient` / `AsyncNotifyClient` и уходит в `Authorization: Bearer`. Не выводите его в лог. Проверить токен и получить `actor`/`csrfToken` можно через `GET /api/v1/session`; SDK-отправителю CSRF не нужен. Для отправки используйте стабильный `Idempotency-Key` (8–128 символов): повтор с тем же содержимым возвращает прежнее событие, изменённое — 409.

Ошибки приходят как HTTP 4xx/5xx с `{"code":"..."}`; 401 без тела означает отсутствие/недействительность токена. 429 может быть общим лимитом IP (пустое тело) или `source_daily_quota_exceeded`. `wait()` возвращает pending при таймауте: это не согласие. `verify_webhook()` проверяет исходные bytes, timestamp и HMAC; дедупликация eventId выполняется получателем.

`AgentConnector` подключается к `/api/v1/agents/connect` исходящим WebSocket, шлёт hello/heartbeat, вызывает переданный `AgentRuntime` (например, `CallbackRuntime`) и подтверждает успешные команды. Он не открывает входящий порт и не импортирует код из сети. `Transcriber`/`Synthesizer` — протоколы для вашей среды агента; речевые модели не входят в сервер Notify. Пример: `../../examples/agent_connector.py`.

Голос: `upload_audio(wav)`, `send_voice(title, body, wav=..., expires_at=..., idempotency_key=...)`, `download_audio(audio_id, grant=...)`, `voice_status(id)`; async-клиент повторяет те же методы. WAV должен быть PCM16LE mono 16 кГц, до 5 минут и 10 МиБ; сервер проверяет байты, а не MIME. Голосовой ответ Android приходит агенту как durable-команда `voice_message`.

Звонки: `AgentConnector` принимает `call_handler` (`CallHandler.on_invite/on_interrupt/on_ended`, необязательные `on_accepted/on_rejected`), `call_protocols` (по умолчанию `[alxprgs.notify.call.v2]` при `realtime_audio`), методы `dial_call(session_id=..., security_level=...)` (агент звонит владельцу; микрофон не открывается), `wait_call(call_id)`, `accept_call`, `reject_call`, `end_call`, `interrupt`, контекст `call_audio(call_id)`. Сервер выбирает общую версию в `hello_ack.callProtocol`; если она не v2, `dial_call`/`call_audio` сразу возвращают `NotifyCallError("call_protocol_unsupported")`, а текстовые команды продолжают работать. `CallAudio.send(pcm)` ждёт ровно 640 байт и добавляет big-endian `seq/timestamp/generation` (v2, 652 байта); `stream()` отдаёт `("audio", pcm)` и `("event", {...})`. Перед новым ответом после `interrupt` вызовите `set_generation(new_generation)`, иначе сервер отбросит старые кадры. Входящий звонок принимает владелец на Android; FCM содержит только `call_id`/состояние. `interrupt` — barge-in от VAD/речи пользователя; SDK не содержит VAD/STT/TTS и не запускает ложный диалог без провайдера. Записи и расшифровки не сохраняются.
Для своей иконки звонка один раз загрузите PNG или WebP с правом `icons:write`. Полученный ID можно сохранить в конфигурации агента и использовать повторно.

```python
from pathlib import Path

icon_id = await connector.upload_icon(Path("avatar.png").read_bytes())
await connector.dial_call(title="Тестовый звонок", icon_id=icon_id)
```
