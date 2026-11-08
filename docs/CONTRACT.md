# Контракт v1

Единый справочник операций, полей, авторизации и фактических ответов — `/docs` и `/openapi/v1.json`. Этот документ описывает сценарии, форматы и протоколы, которые не помещаются в REST-справочник. Если текст расходится с проверенным HTTP-контрактом, приоритет имеет код и тест: зафиксируйте расхождение, не подстраивайте клиент под устаревшее описание.

## Авторизация

| Способ | Когда используется | Что передаётся |
|---|---|---|
| Токен интеграции | Программы и агенты отправляют события и читают свои результаты | `Authorization: Bearer nt_…` |
| Токен устройства | Android отвечает, синхронизируется, загружает WAV и управляет звонком | `Authorization: Bearer nt_…` |
| Веб-сессия | Кабинет в браузере | HttpOnly cookie `notify.session`; для POST — `X-CSRF-Token` и точный `Origin` |
| SSO Access Token | Только первичная привязка Android | `Authorization: Bearer <SSO access token>` на `POST /api/v1/device-enrollments` |

Токены интеграции и устройства — случайные 256-битные значения; в БД хранятся только SHA-256 хеши. Интеграция живёт 90 дней, устройство — 30, веб-сессия — 8 часов без sliding expiration. Отдельного `sourceId` в запросе нет: источник определяется токеном.

Права интеграции: `notify:send`, `requests:create`, `requests:read`, `requests:cancel`, `icons:write`, `agents:connect`. Токен устройства — владелец и не имеет scopes; веб-сессия — владелец. Интеграции не могут отвечать на подтверждения, принимать звонки или читать чужие события.

`GET /api/v1/session` возвращает текущую учётную запись и CSRF-токен; тела, path- и query-параметров у запроса нет:

```json
{
  "actor": {
    "kind": "web",
    "id": "67560410-fdaf-427f-bf96-76c3cbba266b",
    "scopes": [],
    "sessionId": "EXAMPLE_SESSION_ID",
    "isOwner": true
  },
  "csrfToken": "EXAMPLE_CSRF_TOKEN"
}
```

`kind` — `integration`, `device` или `web`; `id` — id источника, устройства или SSO `sub`; `scopes` заполнены только у интеграции; `sessionId` — только у веб-сессии; `isOwner` — вычисляемый признак для `device`/`web`. `csrfToken` выдаётся при каждом успешном вызове вместе с cookie `notify.csrf`; он нужен только веб-сессии для POST. Bearer-клиенты могут его игнорировать. Без действующих учётных данных — 401 с пустым телом.

Веб-вход — OIDC Authorization Code + PKCE с confidential client. Cookie host-only, HttpOnly, SameSite=Lax, Secure в HTTPS. Для POST обязательны заголовок `X-CSRF-Token` из сессии и `Origin`, точно равный `Notify__PublicUrl`; иначе 403 `invalid_csrf`/`invalid_origin`. Токен не хранится в localStorage/sessionStorage.

Привязка Android: public client с PKCE → SSO Access Token (`token_use=access_token`, audience Android-клиента) → `POST /api/v1/device-enrollments` → подтверждение SSO с `reauth_password_mfa` → одноразовый credential через `GET /api/v1/device-enrollments/{id}`. ID Token вместо Access Token отклоняется. Подробнее — [SECURITY](SECURITY.md).

## Форматы ответов и ошибки

| Формат | Когда | Тело |
|---|---|---|
| JSON | Большинство операций | Объект/массив с `Content-Type: application/json` |
| Пустой 204 | `logout`, `reject`, `end`, `receipt`, `devices/fcm`, `DELETE voice-messages` | Нет |
| Пустой 401 | Нет/недействительны Bearer или cookie | Нет; это ответ middleware |
| 302 | `/auth/login` | Заголовок `Location` на SSO |
| Файл | `GET /api/v1/audio/{id}`, `GET /api/v1/icons/{id}` | `audio/wav` или `image/png`/`image/webp`; для аудио возможен 206 по `Range` |
| HTML | `/auth/step-up/callback`, `/android/oauth2redirect` | Страница возврата в приложение |
| WebSocket upgrade | `/api/v1/agents/connect`, `/api/v1/calls/{id}/audio`, `/api/v1/agents/calls/{id}/audio` | Бинарные/текстовые кадры после `101`; без upgrade — 400 `websocket_required` |
| ProblemDetails | `/auth/login` при выключенном SSO | `application/problem+json` |

Ошибки REST — HTTP 4xx/5xx и `{ "code": "..." }`, кроме пустых ответов middleware. Неизвестные JSON-поля отклоняются; malformed JSON и отсутствующее обязательное поле дают 400 `invalid_request`. Неверный `Content-Type` для JSON-маршрута может вернуть 404, потому что маршрут не совпадает по типу содержимого; используйте `application/json`. Общие ответы:

| Статус | Типичные коды и условия |
|---|---|
| 400 | `invalid_request`; `invalid_notification`, `invalid_form`, `invalid_form_answer`, `form_required_missing`, `invalid_answer`, `invalid_idempotency_key`, `invalid_pagination`, `invalid_wait_seconds`, `invalid_cursor`, `invalid_receipt`, `invalid_fcm_token`, `invalid_management_action`, `invalid_integration`, `invalid_agent`, `target_required`, `invalid_call`, `invalid_session`, `invalid_voice_message`, `voice_reply_target_required`, `webhook_not_allowlisted_https`, `webhook_private_address`, `invalid_device`, `invalid_device_key`, `invalid_audio`, `audio_too_long` |
| 401 | Пустое тело при отсутствии/недействительности токена или cookie; `sso_access_token_required` на enrollment; `invalid_sso_token`, `sso_signing_key_changed_retry`, `device_revoked` в гонке отзыва |
| 403 | `scope_required`, `owner_required`, `device_required`, `device_or_integration_required`, `web_required`; `invalid_origin`, `invalid_csrf`; `invalid_challenge`, `invalid_signature`, `call_verification_required`, `audio_confirmation_required`, `audio_grant_required`; `sso_owner_or_profile_mismatch`, `step_up_redemption_failed`, `step_up_binding_mismatch` |
| 404 | `notification_not_found`, `icon_not_found`, `audio_not_found`, `audio_upload_not_found`, `voice_message_not_found`, `agent_not_registered`, `call_not_found`, `no_incoming_call`, `management_not_found`, `source_not_found`, `device_not_found`, `enrollment_not_found`; неизвестный `/api/*` маршрут |
| 409 | `idempotency_conflict`, `request_not_pending`, `audio_upload_used`, `audio_confirmation_not_required`, `call_taken`, `call_missed`, `busy`, `agent_offline`, `agent_unsupported`, `call_protocol_unsupported`, `agent_already_registered`, `agent_registration_pending`, `agent_scope_required`, `trusted_device_required`, `icon_quota_exceeded`, `step_up_replayed` |
| 410 | `audio_upload_expired`, `audio_expired`, `enrollment_expired`, `management_expired`, `step_up_expired` |
| 413 | `audio_too_large`, `audio_quota_exceeded` |
| 429 | Общий лимит 300 HTTP-запросов/мин на IP — пустое тело; операционные: `source_daily_quota_exceeded`, `audio_upload_busy`, `too_many_pending_management_requests` |
| 500 | `internal_error` |
| 502/503 | Ошибки SSO: `invalid_sso_response`, `invalid_sso_authorization_origin`, `sso_not_configured`, `sso_metadata_unavailable`, `sso_step_up_unavailable`; readiness — 503 `configuration_required` |

Точный набор статусов и кодов для каждой операции — в `/docs`. Повтор с тем же `Idempotency-Key` и тем же содержимым не тратит дневную квоту; изменённое содержимое — 409.

## Отправитель

`POST /api/v1/notifications` создаёт событие. Обязателен `Idempotency-Key` длиной 8–128 символов без control-символов. Токен интеграции должен иметь `notify:send` для `message`/`voice` или `requests:create` для `confirmation`/`form`.

```json
{
  "kind": "confirmation",
  "title": "Перезапустить тестовый процесс?",
  "body": "Процесс будет остановлен и запущен заново.",
  "securityLevel": "elevated",
  "expiresAt": "2030-01-01T12:00:00Z",
  "allowCustom": true,
  "iconId": null,
  "correlationId": "task-42"
}
```

`kind=message` — информационное сообщение, только `standard`. `confirmation` — решение да/нет/свой вариант, `expiresAt` обязателен (в будущем, максимум 30 дней). `form` — структурированный набор значений (см. ниже). `voice` — уведомление с WAV-вложением (см. ниже). Заголовок до 160, тело до 16 000, `correlationId` до 128, `iconId` до 64. HTML не исполняется. Ответ — полная запись `Notification`, не preview.

История и чтение:

- `GET /api/v1/notifications?before=&beforeId=&limit=` — страница 1–200, ответ `{items,next}`. Владелец видит всё, интеграция с `requests:read` — только свои события; без scope — 403. `previewOnly=true`: body до 1 000 символов, текст ответа до 512, значения формы скрыты.
- `GET /api/v1/notifications/{id}` — полная запись. Интеграция без `requests:read` получает 404, чтобы не раскрывать чужие id.
- `GET /api/v1/notifications/{id}/wait?seconds=20` — long polling 0–25 секунд; таймаут возвращает `pending`, а не согласие. Доступно только интеграции с `requests:read`; владелец получает 403.
- `POST /api/v1/notifications/{id}/cancel` — только интеграция с `requests:cancel`; `pending → cancelled`, повтор/завершённое — 409.

Перед решением Android обязан заново получить полную запись: сокращённое preview нельзя подтверждать.

## Решение

Для `confirmation` состояние `pending → answered|expired|cancelled`; для `message` — `active`. `answer.decision` — `yes`, `no` или `custom` (текст до 4 000). Только `answered` вместе с `yes` разрешает описанное действие; `no`, `pending`, `expired`, `cancelled`, отказ, таймаут и отсутствие ответа — не согласие.

Обычный ответ: `POST /api/v1/notifications/{id}/response` с `{"answer":{"decision":"yes"}}`. Интеграции и веб-cookie отвечать не могут.

Elevated: сначала `POST /api/v1/notifications/{id}/challenge` с выбранным `Answer`. Сервер возвращает `id,payload,expiresAt`; `payload` — Base64 точных UTF-8 байтов протокола `alxprgs.notify.answer.v1`. Android после BiometricPrompt/системного PIN подписывает эти байты SHA256withECDSA / P-256 / DER и отправляет `answer`, `challengeId`, `signature` (Base64 DER). Challenge действует до 90 секунд и не дольше запроса; повтор и подмена ответа/устройства отклоняются. Ответ сохраняется один раз; повтор — 409 `request_not_pending`.

Формы: `POST .../{id}/form-challenge` с `{"values":{...}}` возвращает challenge `alxprgs.notify.form.v1`; подписывается весь набор значений. Ответ — `{"form":{"values":{...}},"challengeId":"...","signature":"..."}`.

Receipt: `POST /api/v1/notifications/{id}/receipt` с `{"status":"device_received"|"opened"}`. Это состояние доставки, не решение. `GET /api/v1/notifications/{id}/deliveries` возвращает записи устройств.

## Формы

`kind=form` создаёт запрос структурированного ответа; `expiresAt` обязателен, действуют те же правила TTL, отмены, единственного ответа и webhook, что у `confirmation`. `body` — обязательное строковое поле, но может быть пустым. `form.fields` содержит 1–20 полей:

```json
{
  "kind": "form",
  "title": "Параметры выкладки",
  "body": "Проверьте значения перед запуском.",
  "securityLevel": "elevated",
  "expiresAt": "2030-01-01T12:00:00Z",
  "form": {"fields": [
    {"id": "env", "label": "Среда", "type": "single_choice", "required": true,
     "options": [{"id": "staging", "label": "Staging"}, {"id": "prod", "label": "Prod"}]},
    {"id": "count", "label": "Количество", "type": "number", "required": true, "min": 1, "max": 10},
    {"id": "dry", "label": "Dry run", "type": "boolean", "required": true},
    {"id": "note", "label": "Комментарий", "type": "text", "maxLength": 2000}
  ]}
}
```

- `id` поля и варианта — 1–64 ASCII, начинается с буквы/цифры, далее буквы/цифры/`_`/`-`; уникален.
- `label` — 1–160, `description` — до 1 000. `required` учитывается при ответе.
- `type`: `text` (до 4 000, по умолчанию 1 000), `boolean`, `number` (конечное число, необязательные `min/max`), `single_choice`, `multi_choice` (1–20 уникальных вариантов).
- Ветвления, HTML, исполняемый JS и загрузки файлов не поддерживаются. Неизвестные поля спецификации отклоняются.
- Ответ: `POST .../{id}/response` с `{"form":{"values":{"env":"prod","count":3,"dry":true}}}`. Сервер проверяет типы без приведения: строка не становится boolean/number; неизвестные поля/варианты, дубли в multi_choice, пустые обязательные значения и выход за `min/max/maxLength` отклоняются. Отсутствующее необязательное поле допустимо.
- Для elevated сначала `form-challenge` с тем же набором; подпись привязана к устройству, запросу и значениям.
- Списки и sync скрывают значения ответа формы; полный ответ доступен только через GET по id.

`/api/config` возвращает `capabilities.forms` и `minClientVersion.forms`. Клиент, не поддерживающий `form`, обязан показать требование обновить приложение и не отвечать обычным подтверждением.

## Голосовые сообщения

Голосовое сообщение агента — уведомление `kind=voice` с вложением. Загрузка двухфазная:

1. `POST /api/v1/audio/uploads` с сырыми байтами WAV (`audio/wav` или `application/octet-stream`, до 10 МиБ). Сервер проверяет реальный RIFF/WAVE: PCM, mono, 16 кГц, 16 бит, data chunk, длительность до 5 минут. Возвращает `uploadId`, `durationMs`, `sizeBytes`; временная загрузка живёт 15 минут и удаляется, если не подтверждена.
2. `POST /api/v1/notifications` с `{"kind":"voice","expiresAt":"...","title":"...","body":"...","voiceUpload":{"uploadId":"..."}}`. В той же транзакции файл переименовывается в постоянный, создаётся уведомление и outbox. Ответ содержит `content.voice = {audioId,durationMs,sizeBytes,expiresAt}`. `expiresAt` обязателен (до 30 дней).

Обратное направление: Android загружает WAV через тот же upload и вызывает `POST /api/v1/voice-messages` с `{"uploadId":"...","notificationId":"<voice notification>"}`. Сервер создаёт durable-команду агенту `voice_message` в той же транзакции. Ответ: `id,status=queued,expiresAt`. Если агент offline, команда ждёт подключения до TTL (по умолчанию 7 дней). Статус: `queued → delivered` после ack агента, иначе `expired`/`failed`. `GET /api/v1/voice-messages/{id}` возвращает статус, `audioAvailable`, длительность и ссылку на уведомление; `DELETE` удаляет файл и метаданные.

Аудиофайлы лежат в приватном `data/audio`, метаданные — в SQLite; путь нельзя задать снаружи. `GET /api/v1/audio/{audioId}` требует токен устройства или владеющего источника; для elevated-уведомления сначала `POST .../notifications/{id}/audio-challenge`, подпись устройства, затем `POST .../audio-grant`; одноразовый grant на 60 секунд передаётся в заголовке `X-Notify-Audio-Grant`. Обычное проигрывание не является разрешением выполнить задачу. Поддерживается `Range`/206.

Операторская квота диска — 512 МиБ по умолчанию (`Notify__AudioQuotaBytes`); при заполнении новая загрузка получает 413 `audio_quota_exceeded`, свежие файлы не удаляются. Истечение TTL и очистка удаляют файл и метаданные; осиротевшие `.part`-файлы удаляются фоновой очисткой. FCM содержит только id уведомления, не аудио.

## Синхронизация телефона

`GET /api/v1/sync?after=cursor` возвращает `cursor,items,deleted,hasMore,reset`. Начальный/устаревший cursor даёт snapshot последних 1 000 событий. При `reset=true` очистите прежний кэш. Изменения идут страницами до 200 записей. Сначала атомарно сохраните данные вместе с cursor, затем переходите к следующей странице. Полная 30-дневная история доступна через пагинацию `/notifications`.

## Webhook

HTTPS URL задаётся при создании интеграции и должен входить в операторский allowlist `Notify__WebhookHosts`. Секрет показывается один раз. Перенаправления отключены, адреса DNS повторно проверяются при соединении; частные и служебные сети запрещены.

Тело: `{ "eventId": "...", "type": "notification.answered", "notification": { ... } }`. Также отправляются expired/cancelled. Заголовки:

- `X-Notify-Event-Id`: стабильный eventId;
- `X-Notify-Timestamp`: текущие Unix seconds;
- `X-Notify-Signature`: `v1=` + hex HMAC-SHA256(secret, timestamp + "." + исходное тело).

Проверяйте исходные байты, подпись constant-time, время ±300 секунд и eventId из подписанного тела. `occurredAt` — Unix milliseconds момента события, он сохраняется между повторами. HTTP 2xx подтверждает обработку. До 20 попыток в пределах 24 часов с ростом интервала до часа; Retry-After учитывается. Повтор имеет тот же eventId и тело, но свежую подпись. Неудача не отменяет ответ пользователя. Обработчик должен исключать повторные побочные эффекты.

Очистка истории выполняется фоново раз в час. Базовый срок — 30 дней от создания. Запросы решения (`confirmation` и `form`) у верхней границы TTL сохраняются ещё минимум сутки после своего deadline, чтобы ответ/истечение успели попасть отправителю; ожидающая доставка не удаляется раньше своей ограниченной retry-политики.

## Агенты

Агент — зарегистрированный endpoint источника, принадлежащего владельцу. Регистрация выполняется в кабинете через `POST /api/v1/management` с `{"action":"agent.register","targetId":"<sourceId>","name":"...","capabilities":["text_tasks","voice_messages","realtime_audio"]}` и подтверждается повышенным ответом телефона. У источника должен быть scope `agents:connect`; на источник можно зарегистрировать одного агента. `GET /api/v1/agents` возвращает зарегистрированных агентов, `online`, `lastSeenAt`, заявленные возможности и текущие задачи.

Агент подключается сам исходящим WebSocket/TLS к `wss://notify.example.com/api/v1/agents/connect` с `Authorization: Bearer <токен источника>`; входящий порт на компьютере агента не открывается. Это не REST-операция: без upgrade сервер отвечает 400 `websocket_required`, а протокол описан ниже. Ошибки доступа: `agents_scope_required`, `agent_not_registered`. Протокол — `alxprgs.notify.agent.v1`, текстовые JSON-кадры до 64 KiB.

Первый кадр — hello:

```json
{"type":"hello","protocolVersion":"alxprgs.notify.agent.v1","eventId":"...","agentId":"...","name":"Мой компьютер","capabilities":["text_tasks","voice_messages","realtime_audio"],"callProtocols":["alxprgs.notify.call.v2"],"sessions":[{"id":"task-1","name":"Сборка"}],"client":"sdk"}
```

`callProtocols` необязателен только для обратной совместимости. Если поле отсутствует или не содержит общую с сервером версию, живые звонки для этого агента недоступны: сервер отвечает `hello_ack.callProtocol=null` и `call_protocol_unsupported` на попытку звонка, не создавая ringing. Текстовые команды и голосовые сообщения продолжают работать. Сервер поддерживает `alxprgs.notify.call.v2` (`supportedCallProtocols` в `hello_ack`); молчаливое смешивание v1/v2 запрещено. Android показывает кнопки звонка только при совпавшем `callProtocol` и `realtime_audio`.

Сервер отвечает `hello_ack` с `heartbeatSeconds`, `offlineSeconds`, `agent`, `callProtocol`, `supportedCallProtocols` и числом ожидающих команд. Агент шлёт `heartbeat` каждые 30 секунд; отсутствие любых кадров 90 секунд означает offline и закрытие сессии. Эти значения — операторская конфигурация. `sessions` можно обновлять в heartbeat. Переподключение заменяет прежнюю сессию. Hello_ack, начальная очередь команд и все последующие отправки привязаны к жизни соединения и ограничены `AgentSendTimeoutSeconds` (по умолчанию 5 с); Disconnect, timeout или замена соединения прерывают ожидание SendLock/SendAsync, после чего сокет закрывается/Abort, а только это соединение удаляется из реестра.

Управляющие события содержат `eventId`, `protocolVersion`, `type` и correlation/session id. Долговечная команда (`type=command`, `command`, `payload`, `expiresAt`) сохраняется в SQLite, пока агент не подтвердит её кадром `{"type":"ack","commandEventId":"...","status":"done"}`. Неподтверждённые команды повторяются после переподключения с тем же `eventId`; повторный ack идемпотентен, получатель обязан дедуплицировать eventId. Истёкшие команды получают `expired`. Аудиопоток звонков не попадает в durable outbox.

Агент не исполняет команды оболочки из произвольного текста: среда агента сама решает, какие зарегистрированные задачи поддерживать. Отзыв источника закрывает его агентские соединения и отменяет неподтверждённые команды. MCP не умеет ставить, принимать или записывать звонки; это транспорт Android/SDK.

## Живые звонки

Один звонок одновременно, два направления: телефон владельца → агент (`outgoing`) и агент → владелец (`incoming`). Агент должен быть online и заявить `realtime_audio`; иначе `agent_offline`/`agent_unsupported`. Второй звонок получает 409 `busy`. История `GET /api/v1/calls` содержит `direction`, `deviceId`, `reason` и `durationMs` — без аудио и расшифровок.

Исходящий звонок: `POST /api/v1/calls` с `{"sourceId":"...","sessionId":"task-1","securityLevel":"standard"}`. `sessionId` необязателен и должен быть в hello-сессиях агента. Сервер отправляет агенту `call.invite`; ответ агента `call.accept` переводит звонок в `connecting`.

Входящий звонок: агент отправляет по control WebSocket `call.dial` с `payload {"sessionId":"...","securityLevel":"standard|elevated","title":"..."}`. Сервер отвечает `call.dial_ack` с `requestEventId`, `callId`, `inviteId`, `status=ringing`, `expiresAt`; при занятости — `ok=false,code=busy`, без общей call-версии — `call_protocol_unsupported`. Приглашение доступно всем доверенным устройствам: `GET /api/v1/calls/incoming` возвращает текущий ringing-вызов, а data-only FCM `{type:"call",call_id,call_state:"ringing"}` служит фоновым оповещением. FCM не содержит текста, имени или аудио. Приложение не включает микрофон при получении приглашения.

Android incoming notification разделяет показ и действие. Full-screen intent и нажатие тела уведомления несут действие `show` и только открывают карточку; кнопки «Принять»/«Отклонить» несут одноразовый токен, выданный для конкретного `callId` и действия. Внешний Intent к экспортированной `MainActivity` с произвольными extras не может принять/отклонить звонок. Elevated-подпись запрашивается только после явного принятия и до запуска микрофона.

Завершение звонка ограничено по времени: сигнализация агенту/телефону имеет таймауты, а закрытие/Abort медиа-сокетов, очередей и `currentId` выполняется в `finally` даже при зависшем peer. Конкурентные end/disconnect/revoke/timeout идемпотентны; следующий звонок не блокируется зависшим завершением.

Любое доверенное устройство может принять или отклонить входящий звонок. **Первый принятый ответ окончательный**: `POST /api/v1/calls/{id}/accept` переводит вызов в `connecting` и закрепляет `deviceId`; второй accept получает 409 `call_taken`. `POST /api/v1/calls/{id}/reject` завершает ringing-вызов с `rejected`. Истечение 45 секунд — `missed`. Отзыв источника или победившего устройства завершает вызов с `revoked`. `POST /api/v1/calls/{id}/end` завершает звонок своего устройства.

Elevated-звонок требует системной проверки до соединения. Исходящий: `POST /api/v1/calls/challenge` с `{"sourceId","sessionId","securityLevel":"elevated"}`. Входящий: `POST /api/v1/calls/{id}/challenge` (только elevated ringing и только до первого ответа). Сервер возвращает точные bytes `alxprgs.notify.call.v2`, содержащие `challengeId, deviceId, direction, action, sourceId, agentId, sessionId, callId, inviteId, securityLevel, nonce, expiresAt`; телефон подписывает их после биометрии/PIN. `POST /calls` или `POST /calls/{id}/accept` передаёт `challengeId,signature`. Несовпадение агента, сессии, направления, действия, вызова или приглашения, чужое устройство, повтор и истечение отклоняются. Вход в звонок и голосовое «да» не являются согласием на опасные действия.

Сигнализация агенту идёт по уже открытому control WebSocket: сервер шлёт `call.invite`, `call.dial_ack`, `call.accepted`, `call.active`, `call.ended`; агент шлёт `call.dial`, `call.accept`, `call.reject`, `call.end`, `call.interrupt`. Медиа — две исходящие WebSocket-сессии: телефон `wss://…/api/v1/calls/{id}/audio` с токеном устройства, агент `wss://…/api/v1/agents/calls/{id}/audio` с токеном источника. Без upgrade — 400 `websocket_required`. Сервер ретранслирует бинарные кадры в памяти, без STT/TTS/транскодирования и без E2EE; канал защищён TLS.

В `call.dial` агент может передать `payload.iconId` с ID своей сохранённой иконки или изображения владельца. Для отсутствующего/чужого ID сервер отвечает `icon_not_found`, для неверного формата — `invalid_call`. При пропуске используется иконка источника. FCM-приглашение по-прежнему содержит только служебные поля.

Формат бинарного кадра v2 — ровно 652 байта: `seq:uint32` + `timestampMs:uint32` + `generation:uint32` в network byte order и 640 байт PCM16LE mono 16 кГц (20 мс). `seq` увеличивается на 1 для каждого **отправленного** кадра; при mute кадры не отправляются и sequence не расходуется. `timestampMs` не уменьшается. Иное закрывает звонок с `invalid_audio`. Очередь каждого направления — 100 кадров (2 секунды); переполнение завершает сессию с `degraded`. Android дополнительно ограничивает локальную очередь.

Barge-in: агент шлёт `call.interrupt` с новым `generation`; сервер отбрасывает кадры старого generation, очищает очередь в сторону телефона, отправляет `interrupt` и принимает только новый generation. Телефон фильтрует кадры по generation; повторный/устаревший interrupt игнорируется.

Жизненный цикл: `ringing → connecting → active → ended`. Ожидание ответа — 45 секунд (`missed`), соединение медиа — 10 секунд (`timeout`), максимум разговора — 30 минут (`timeout`). Причины завершения: `rejected`, `missed`, `timeout`, `disconnected`, `unsupported`, `busy`, `invalid_audio`, `degraded`, `revoked`, `ended`. Записи, расшифровки и аудио не сохраняются; история звонка содержит только метаданные и очищается вместе с 30-дневной историей.

## MCP и Python

HTTP endpoint `/mcp`, тот же токен интеграции. Инструменты: `send_notification`, `request_confirmation`, `request_form`, `get_request`, `wait_for_response`, `cancel_request` (проверьте `tools/list` используемой версией клиента). `request_form` принимает `fieldsJson` — JSON-массив полей формы. Сервер поддерживается официальным C# SDK. Локальные MCP-клиенты могут использовать stdio-адаптер. Для ChatGPT доступен отдельный маршрут с OAuth и входом через существующий кабинет Notify.

```json
{
  "command": "notify-mcp",
  "env": {"NOTIFY_URL": "https://notify.example.com", "NOTIFY_TOKEN": "SET_PRIVATELY"}
}
```

Не храните реальный токен в общем конфигурационном файле. Установите SDK с extra `[mcp]`. SDK: `NotifyClient` / `AsyncNotifyClient`, методы `send`, `request`, `request_form`, `get`, `list`, `cancel`, `wait`; `verify_webhook` проверяет подпись, но дедупликация остаётся у получателя. Таймаут SDK возвращает последнее pending, а не разрешение. Подробности — `packages/python-sdk/README.md` в репозитории.

Агентский коннектор — отдельный extra `[agent]`: `AgentConnector` подключается к Notify, шлёт hello/heartbeat с `callProtocols`, вызывает переданный `AgentRuntime`/`CallbackRuntime` для каждой команды и подтверждает успешную обработку. Если `hello_ack.callProtocol` не равен v2, `dial_call`/`call_audio` сразу возвращают `call_protocol_unsupported`, а текстовые команды работают. `Transcriber` и `Synthesizer` — протоколы для среды агента; Notify не запускает речевые модели и не импортирует код из сети. `dial_call(session_id=..., security_level=...)` инициирует входящий звонок владельцу (без открытия микрофона), `wait_call(call_id)` ждёт accepted/ended; `CallAudio` в v2 передаёт `generation`. Необязательные обработчики `on_accepted`/`on_rejected` дополняют `on_invite/on_interrupt/on_ended`.

Голосовые методы SDK: `upload_audio(wav)`, `send_voice(..., wav=...)`, `download_audio(audio_id, grant=...)`, `voice_status(id)`; async-клиент повторяет их. [MCP для ChatGPT](CHATGPT_MCP.md) находится на `/chatgpt/mcp` и использует OAuth-токен выделенного источника. Инструмент `send_voice` принимает текст и синтезирует WAV через внутренний Silero.

Остановленного агента сервис сам не возобновляет. Среда агента должна ждать/опрашивать состояние либо принимать webhook. Skill находится в `skills/alxprgs-notify`; глобальная установка не требуется.

## Связанные руководства

- [Безопасность и SSO](SECURITY.md) — доверие, привязка, elevated, веб-кабинет.
- [Установка и эксплуатация](OPERATIONS.md) — Docker/nginx, SSO, Firebase, backup.
- [Самостоятельное размещение](HOME_HOSTING.md) — пошаговая установка с Caddy.
- `/docs` — операции, поля, примеры и точные коды ошибок.
