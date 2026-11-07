# Контракт v1

HTTP JSON использует camelCase и UTC ISO 8601 для входных `expiresAt`. Выходные `createdAt`, `answeredAt`, challenge/enrollment `expiresAt` — Unix milliseconds. Ошибки: HTTP 4xx/5xx и `{ "code": "..." }`. Неизвестные JSON-поля отклоняются. `/openapi/v1.json` генерируется сервером; `/docs` содержит Scalar с локально собранными ресурсами.

Операторские лимиты по умолчанию: 300 HTTP-запросов в минуту на адрес, 2 000 новых уведомлений в сутки UTC для каждого источника (`Notify__SourceDailyLimit`), 256 иконок суммарно и 20 одновременно ожидающих запросов управления. Превышение возвращает 429/409; повторы с прежним ключом идемпотентности не тратят дневную квоту.

## Отправитель

Токен интеграции передаётся `Authorization: Bearer …`. Права: `notify:send`, `requests:create`, `requests:read`, `requests:cancel`, `icons:write`. Токен действует 90 дней, отзывается вместе с источником. Источник определяется токеном, чужой `sourceId` передать нельзя.

`POST /api/v1/notifications`, обязательный заголовок `Idempotency-Key` длиной 8–128:

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

Дата в примере условная: реальный срок должен быть в будущем, максимум через 30 дней. `kind=message` — информационное сообщение без решения, уровень только `standard`. `confirmation` поддерживает `standard/elevated`, отвечает да/нет/свой вариант. `form` запрашивает структурированный набор значений (см. ниже). Заголовок до 160, тело до 16 000 символов, correlationId до 128. HTML не исполняется.

Одинаковые ключ и содержимое возвращают прежнее уведомление; изменённое содержимое — 409. Сохраняйте ключ между сетевыми повторами.

| Метод | Назначение |
|---|---|
| `GET /api/v1/notifications` | Страница истории; limit 1–200, ответ `items,next` |
| `GET /api/v1/notifications/{id}` | Уведомление/решение своего источника |
| `GET /api/v1/notifications/{id}/wait?seconds=20` | Ожидание 0–25 секунд; pending сохраняется |
| `POST /api/v1/notifications/{id}/cancel` | Отмена своего ожидающего запроса |
| `POST /api/v1/notifications/{id}/form-challenge` | Elevated challenge для набора значений формы |
| `POST /api/v1/notifications/{id}/audio-challenge` | Elevated challenge для прослушивания голосового вложения |
| `POST /api/v1/notifications/{id}/audio-grant` | Одноразовый grant на скачивание после подписи |
| `POST /api/v1/audio/uploads` | Первая фаза загрузки WAV |
| `GET /api/v1/audio/{audioId}` | Защищённое получение WAV |
| `POST /api/v1/voice-messages` | Голосовой ответ Android → агент |
| `GET /api/v1/voice-messages/{id}` | Статус голосового ответа |
| `DELETE /api/v1/voice-messages/{id}` | Удалить свой голосовой ответ и файл |
| `GET /api/v1/notifications/{id}/deliveries` | Подтверждения получения устройствами |
| `GET /api/v1/agents` | Зарегистрированные агенты владельца и их online-состояние |
| `POST /api/v1/calls/challenge` | Elevated challenge исходящего звонка |
| `POST /api/v1/calls` | Начать исходящий звонок агенту |
| `GET /api/v1/calls` | История звонков (метаданные, direction) |
| `GET /api/v1/calls/incoming` | Текущий входящий ringing-вызов |
| `GET /api/v1/calls/{id}` | Состояние звонка |
| `POST /api/v1/calls/{id}/challenge` | Elevated challenge принятия входящего |
| `POST /api/v1/calls/{id}/accept` | Принять входящий звонок |
| `POST /api/v1/calls/{id}/reject` | Отклонить входящий звонок |
| `POST /api/v1/calls/{id}/end` | Завершить звонок |
| `POST /api/v1/icons` | Сырые байты PNG/WebP, до 256 KiB, до 512×512; возвращает id |
| `GET /api/v1/icons/{id}` | Защищённое получение ресурса |

Агент загружает изображение отдельным запросом с токеном источника и правом `icons:write`. Повторная загрузка одинаковых байтов от того же источника возвращает тот же ID. Сохранённый ID можно использовать в нескольких звонках без повторной загрузки. Иконки, загруженные владельцем, также можно указать по ID.

`next` содержит `before,beforeId`; передайте оба параметра следующему запросу. Иконка источника задаётся управлением, иконка события переопределяет её. Произвольные удалённые URL изображений не загружаются.

Списки и sync возвращают `previewOnly=true`: body до 1 000 символов, текст собственного ответа до 512. Полный текст доступен через GET по id. Перед выбором/подтверждением действия Android заново получает полную запись онлайн; нельзя подтверждать сокращённое превью.

## Решение

Для подтверждения состояния: `pending → answered|expired|cancelled`. `answer.decision` — `yes`, `no` или `custom`; текст собственного ответа до 4 000 символов. Только `answered` вместе с `yes` разрешает описанное действие. Для message состояние `active`, ответа нет.

Устройство вызывает `POST .../{id}/response` с `{ "answer": { "decision": "yes" } }`. Интеграции и веб-cookie отвечать не могут. Для elevated сначала `POST .../{id}/challenge` с выбранным Answer. Сервер возвращает `id,payload,expiresAt`; payload — Base64 точных UTF-8 байтов. Android после BiometricPrompt подписывает эти байты SHA256withECDSA / P-256 / DER и отправляет ответ с `challengeId,signature` (Base64 DER). Challenge действует до 90 секунд и не дольше запроса. После принятия повторный ответ получает 409.

FCM acceptance, `device_received` и `opened` — разные состояния доставки. Они не являются решениями. Устройство сообщает receipt только после получения/открытия данных.

## Формы

`kind=form` создаёт запрос структурированного ответа. `expiresAt` обязателен, действуют те же правила TTL, отмены, единственного окончательного ответа и webhook, что у `confirmation`. `body` — необязательное описание. `form.fields` содержит 1–20 полей:

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

- `id` поля и варианта — 1–64 символа ASCII, начинается с буквы или цифры, далее буквы/цифры/`_`/`-`; уникален.
- `label` — 1–160, `description` — до 1 000. `required` учитывается при ответе.
- `type`: `text` (до 4 000, по умолчанию 1 000), `boolean`, `number` (конечное число, необязательные `min/max`), `single_choice`, `multi_choice` (1–20 уникальных вариантов с `id` и `label`).
- Ветвления, HTML, исполняемый JS и загрузки файлов не поддерживаются. Неизвестные поля спецификации отклоняются.
- Ответ: `POST .../{id}/response` с `{"form":{"values":{"env":"prod","count":3,"dry":true}}}`. Сервер проверяет типы без приведения: строка не становится boolean/number, неизвестные поля и варианты, дубли в multi_choice, пустые обязательные значения и выход за `min/max/maxLength` отклоняются. Отсутствующее необязательное поле допустимо.
- Для elevated сначала `POST .../{id}/form-challenge` с тем же `{"values":{...}}`. Сервер возвращает challenge с Base64 точных байтов протокола `alxprgs.notify.form.v1`; Android подписывает весь набор значений. Ответ передаёт `form`, `challengeId` и `signature`. Challenge привязан к устройству, запросу и конкретным значениям; повтор и подмена значений отклоняются.
- Списки и sync скрывают значения ответа формы (`formAnswer.values` пуст), полный ответ доступен только через GET по id. Перед заполнением Android заново получает полную запись.

`/api/config` возвращает `capabilities.forms` и `minClientVersion.forms`. Клиент, не поддерживающий `form`, обязан показать требование обновить приложение и не отвечать обычным подтверждением.

## Голосовые сообщения

Голосовое сообщение агента — уведомление `kind=voice` с вложением. Загрузка двухфазная:

1. `POST /api/v1/audio/uploads` с сырыми байтами WAV (`application/octet-stream`, до 10 МиБ). Сервер проверяет реальный RIFF/WAVE: PCM, mono, 16 кГц, 16 бит, data chunk, длительность до 5 минут. Возвращает `uploadId`, `durationMs`, `sizeBytes`; временная загрузка живёт 15 минут и удаляется, если не подтверждена.
2. `POST /api/v1/notifications` с `{"kind":"voice", "expiresAt": "...", "title": "...", "body": "...", "voiceUpload": {"uploadId": "..."}}`. В той же транзакции файл переименовывается в постоянный, создаётся уведомление и outbox. Ответ содержит `content.voice = {audioId,durationMs,sizeBytes,expiresAt}`. `expiresAt` обязателен (до 30 дней).

Обратное направление: Android загружает WAV через тот же upload и вызывает `POST /api/v1/voice-messages` с `{"uploadId":"...","notificationId":"<voice notification>"}`. Сервер создаёт durable команду агенту `voice_message` в той же транзакции. Ответ: `id,status=queued,expiresAt`. Если агент offline, команда ждёт подключения до TTL (по умолчанию 7 дней). Статус: `queued → delivered` после ack агента, иначе `expired`/`failed`. `GET /api/v1/voice-messages/{id}` возвращает статус, `audioAvailable`, длительность и ссылку на уведомление; `DELETE` удаляет файл и метаданные.

Аудиофайлы лежат в приватном `data/audio`, метаданные — в SQLite; путь нельзя задать снаружи. `GET /api/v1/audio/{audioId}` требует токен устройства или владеющего источника. Для `elevated`-уведомления сначала `POST .../notifications/{id}/audio-challenge`, подпись устройства, затем `POST .../audio-grant`; одноразовый grant на 60 секунд передаётся в заголовке `X-Notify-Audio-Grant`. Обычное проигрывание не является разрешением выполнить задачу.

Операторская квота диска — 512 МиБ по умолчанию (`Notify__AudioQuotaBytes`); при заполнении новая загрузка получает 413 `audio_quota_exceeded`, свежие файлы не удаляются. Истечение TTL и очистка удаляют файл и метаданные; осиротевшие `.part`-файлы удаляются фоновой очисткой. FCM содержит только id уведомления, не аудио.

## Синхронизация телефона

`GET /api/v1/sync?after=cursor` возвращает `cursor,items,deleted,hasMore,reset`. Начальный/устаревший cursor даёт snapshot последних 1 000 событий. При reset очистите прежний кэш. Изменения идут страницами до 200 записей. Сначала атомарно сохраните данные вместе с cursor, затем переходите к следующей странице. Полная 30-дневная история доступна через пагинацию `/notifications`.

## Webhook

HTTPS URL задаётся при создании интеграции и должен входить в операторский allowlist. Секрет показывается один раз. Перенаправления отключены, адреса DNS повторно проверяются при соединении; частные и служебные сети запрещены.

Тело: `{ "eventId": "...", "type": "notification.answered", "notification": { ... } }`. Также отправляются expired/cancelled. Заголовки:

- `X-Notify-Event-Id`: стабильный eventId;
- `X-Notify-Timestamp`: текущие Unix seconds;
- `X-Notify-Signature`: `v1=` + hex HMAC-SHA256(secret, timestamp + "." + исходное тело).

Проверяйте исходные байты, подпись constant-time, время ±300 секунд и eventId из подписанного тела. `occurredAt` — Unix milliseconds момента события, он сохраняется между повторами. HTTP 2xx подтверждает обработку. До 20 попыток в пределах 24 часов с ростом интервала до часа; Retry-After учитывается. Повтор имеет тот же eventId и тело, но свежую подпись. Неудача не отменяет ответ пользователя. Обработчик должен исключать повторные побочные эффекты.

Очистка истории выполняется фоново раз в час. Базовый срок — 30 дней от создания. Запросы решения (`confirmation` и `form`) у верхней границы TTL сохраняются ещё минимум сутки после своего deadline, чтобы ответ/истечение успели попасть отправителю; ожидающая доставка не удаляется раньше своей ограниченной retry-политики.

## Агенты

Агент — зарегистрированный endpoint источника, принадлежащего владельцу. Регистрация выполняется в кабинете через `POST /api/v1/management` с `{"action":"agent.register","targetId":"<sourceId>","name":"...","capabilities":["text_tasks","voice_messages","realtime_audio"]}` и подтверждается повышенным ответом телефона. У источника должен быть scope `agents:connect`; на источник можно зарегистрировать одного агента. `GET /api/v1/agents` возвращает зарегистрированных агентов, `online`, `lastSeenAt`, заявленные возможности и текущие задачи.

Агент подключается сам исходящим WebSocket/TLS к `wss://notify.alxprgs.tech/api/v1/agents/connect` с `Authorization: Bearer <токен источника>`; входящий порт на компьютере агента не открывается. Ошибки доступа: `agents_scope_required`, `agent_not_registered`. Протокол — `alxprgs.notify.agent.v1`, текстовые JSON-кадры до 64 KiB.

Первый кадр — hello:

```json
{"type":"hello","protocolVersion":"alxprgs.notify.agent.v1","eventId":"...","agentId":"...","name":"Мой компьютер","capabilities":["text_tasks","voice_messages","realtime_audio"],"callProtocols":["alxprgs.notify.call.v2"],"sessions":[{"id":"task-1","name":"Сборка"}],"client":"sdk"}
```

`callProtocols` необязателен только для обратной совместимости. Если поле отсутствует или не содержит общую с сервером версию, живые звонки для этого агента недоступны: сервер отвечает `hello_ack.callProtocol=null` и `call_protocol_unsupported` на попытку звонка, не создавая ringing. Текстовые команды и голосовые сообщения продолжают работать. Сервер поддерживает `alxprgs.notify.call.v2` (`supportedCallProtocols` в `hello_ack`); молчаливое смешивание v1/v2 запрещено. Android показывает кнопки звонка только при совпавшем `callProtocol` и `realtime_audio`.

Сервер отвечает `hello_ack` с `heartbeatSeconds`, `offlineSeconds`, `agent`, `callProtocol`, `supportedCallProtocols` и числом ожидающих команд. Агент шлёт `heartbeat` каждые 30 секунд; отсутствие любых кадров 90 секунд означает offline и закрытие сессии. Эти значения — операторская конфигурация. `sessions` можно обновлять в heartbeat. Переподключение заменяет прежнюю сессию. Hello_ack, начальная очередь команд и все последующие отправки привязаны к жизни соединения и ограничены `AgentSendTimeoutSeconds` (по умолчанию 5 с); Disconnect, timeout или замена соединения прерывают ожидание SendLock/SendAsync, после чего сокет закрывается/Abort, а только это соединение удаляется из реестра.

Управляющие события содержат `eventId`, `protocolVersion`, `type` и correlation/session id. Долговечная команда (`type=command`, `command`, `payload`, `expiresAt`) сохраняется в SQLite, пока агент не подтвердит её кадром `{"type":"ack","commandEventId":"...","status":"done"}`. Неподтверждённые команды повторяются после переподключения с тем же `eventId`; повторный ack идемпотентен, получатель обязан дедуплицировать eventId. Истёкшие команды получают `expired`. Аудиопоток звонков не попадает в durable outbox.

Агент не исполняет команды оболочки из произвольного текста: среда агента сама решает, какие зарегистрированные задачи поддерживать. Отзыв источника закрывает его агентские соединения и отменяет неподтверждённые команды.

## Живые звонки

Один звонок одновременно, два направления: телефон владельца → агент (`outgoing`) и агент → владелец (`incoming`). Агент должен быть online и заявить `realtime_audio`; иначе `agent_offline`/`agent_unsupported`. Второй звонок получает 409 `busy`. История `GET /api/v1/calls` содержит `direction`, `deviceId` (победившее устройство или `null`), `reason` и `durationMs` — без аудио и расшифровок.

Исходящий звонок: `POST /api/v1/calls` с `{"sourceId":"...","sessionId":"task-1","securityLevel":"standard"}`. `sessionId` необязателен и должен быть в hello-сессиях агента. Сервер отправляет агенту `call.invite`; ответ агента `call.accept` переводит звонок в `connecting`.

Входящий звонок: агент отправляет по control WebSocket `call.dial` с `payload {"sessionId": "...", "securityLevel": "standard|elevated", "title": "..."}`. Сервер отвечает `call.dial_ack` с `requestEventId`, `callId`, `inviteId`, `status=ringing`, `expiresAt`; при занятости — `ok=false,code=busy`, без общей call-версии — `call_protocol_unsupported`. Приглашение доступно всем доверенным устройствам владельца: `GET /api/v1/calls/incoming` возвращает текущий ringing-вызов, а data-only FCM `{type:"call",call_id,call_state:"ringing"}` служит фоновым оповещением. FCM не содержит текста, имени или аудио. Приложение не включает микрофон при получении приглашения.

Android incoming notification разделяет показ и действие. Full-screen intent и нажатие тела уведомления несут действие `show` и только открывают карточку; кнопки «Принять»/«Отклонить» несут одноразовый токен, выданный для конкретного `callId` и действия. Внешний Intent к экспортированной `MainActivity` с произвольными extras не может принять/отклонить звонок: без валидного токена действие понижается до показа карточки, неизвестное действие игнорируется. Elevated-подпись запрашивается только после явного принятия и до запуска микрофона.

Завершение звонка ограничено по времени: сигнализация агенту/телефону имеет таймауты, а закрытие/Abort медиа-сокетов, очередей и `currentId` выполняется в `finally` даже при зависшем peer. Конкурентные end/disconnect/revoke/timeout идемпотентны; следующий звонок не блокируется зависшим завершением.

Любое доверенное устройство может принять или отклонить входящий звонок. **Первый принятый ответ окончательный**: `POST /api/v1/calls/{id}/accept` переводит вызов в `connecting` и закрепляет `deviceId`; второй accept получает 409 `call_taken`, а остальные устройства видят актуальное состояние. `POST /api/v1/calls/{id}/reject` завершает ringing-вызов с `rejected`. Истечение 45 секунд — `missed`. Отзыв источника или победившего устройства завершает вызов с `revoked`.

Elevated-звонок требует системной проверки до соединения. Исходящий: `POST /api/v1/calls/challenge` с `{"sourceId","sessionId","securityLevel":"elevated"}`. Входящий: `POST /api/v1/calls/{id}/challenge` (только для elevated ringing и только до первого ответа). Сервер возвращает точные bytes `alxprgs.notify.call.v2`, содержащие `challengeId, deviceId, direction, action, sourceId, agentId, sessionId, callId, inviteId, securityLevel, nonce, expiresAt`; телефон подписывает их после биометрии/PIN. `POST /calls` или `POST /calls/{id}/accept` передаёт `challengeId,signature`. Несовпадение агента, сессии, направления, действия, вызова или приглашения, чужое устройство, повтор и истечение отклоняются. Без подписи elevated-звонок не создаётся/не принимается. Вход в звонок и голосовое «да» не являются согласием на опасные действия: для них создаётся отдельное confirmation.

Сигнализация агенту идёт по уже открытому control WebSocket: сервер шлёт `call.invite`, `call.dial_ack`, `call.accepted`, `call.active`, `call.ended`; агент шлёт `call.dial`, `call.accept`, `call.reject`, `call.end`, `call.interrupt`. Медиа — две исходящие WebSocket-сессии: телефон `wss://…/api/v1/calls/{id}/audio` с токеном устройства, агент `wss://…/api/v1/agents/calls/{id}/audio` с токеном источника. Сервер ретранслирует бинарные кадры в памяти, без STT/TTS/транскодирования и без E2EE; канал защищён TLS.

В `call.dial` агент может передать `payload.iconId` с ID своей сохранённой иконки или изображения владельца. Для отсутствующего или чужого ID сервер отвечает `icon_not_found`, для неверного формата — `invalid_call`. При пропуске поля используется иконка источника, если она настроена. Поле `iconId` в ответах API вызовов доступно телефону до и после принятия. Телефон загружает изображение по защищённому `GET /api/v1/icons/{id}`. FCM-приглашение по-прежнему содержит только служебные поля вызова.

Формат бинарного кадра v2 — ровно 652 байта: `seq:uint32` + `timestampMs:uint32` + `generation:uint32` в network byte order и 640 байт PCM16LE mono 16 кГц (20 мс). `seq` увеличивается на 1 для каждого **отправленного** кадра; при mute кадры не отправляются и sequence не расходуется, поэтому после unmute разговор продолжается. `timestampMs` не уменьшается. Иное закрывает звонок с `invalid_audio`. Очередь каждого направления — 100 кадров (2 секунды); переполнение завершает сессию с `degraded`, не накапливая backlog. Android дополнительно ограничивает локальную очередь и при переполнении/ошибке отправки завершает звонок.

Barge-in: агент шлёт `call.interrupt` с новым `generation`; сервер отбрасывает кадры старого generation, очищает накопленную очередь в сторону телефона, отправляет `interrupt` и принимает только новый generation. Телефон также фильтрует кадры по generation; повторный/устаревший interrupt игнорируется. Старый звук после interruption не воспроизводится, новый ответ идёт с новым generation.

Жизненный цикл: `ringing → connecting → active → ended`. Ожидание ответа — 45 секунд (`missed`), соединение медиа — 10 секунд (`timeout`), максимум разговора — 30 минут (`timeout`). Причины завершения: `rejected`, `missed`, `timeout`, `disconnected`, `unsupported`, `busy`, `invalid_audio`, `degraded`, `revoked`, `ended`. Отключение любого участника завершает звонок и останавливает микрофон/воспроизведение; автоматического переподключения записи нет. Записи, расшифровки и аудио не сохраняются; история звонка содержит только метаданные и очищается вместе с 30-дневной историей. Лимиты — операторская конфигурация, расширение требует замеров.

## MCP и Python

HTTP endpoint `/mcp`, тот же токен интеграции. Инструменты: `send_notification`, `request_confirmation`, `request_form`, `get_request`, `wait_for_response`, `cancel_request` (проверьте tools/list используемой версией клиента). `request_form` принимает `fieldsJson` — JSON-массив полей формы. Сервер поддерживается официальным C# SDK. MCP-клиенты, требующие автоматический OAuth-вход, используют локальный stdio-адаптер: отдельный OAuth authorization server в Notify не реализован.

```json
{
  "command": "notify-mcp",
  "env": {"NOTIFY_URL": "https://notify.alxprgs.tech", "NOTIFY_TOKEN": "SET_PRIVATELY"}
}
```

Не храните реальный токен в общем конфигурационном файле. Установите SDK с extra `[mcp]`. SDK: `NotifyClient` / `AsyncNotifyClient`, методы `send`, `request`, `request_form`, `get`, `list`, `cancel`, `wait`; `verify_webhook` проверяет подпись, но дедупликация остаётся у получателя. Таймаут SDK возвращает последнее pending, а не разрешение.

Агентский коннектор — отдельный extra `[agent]`: `AgentConnector` подключается к Notify, шлёт hello/heartbeat с `callProtocols` (по умолчанию v2 при `realtime_audio`), вызывает переданный `AgentRuntime`/`CallbackRuntime` для каждой команды и подтверждает успешную обработку. Если `hello_ack.callProtocol` не равен v2, `dial_call`/`call_audio` сразу возвращают `call_protocol_unsupported`, а текстовые команды продолжают работать. `Transcriber` и `Synthesizer` — протоколы для среды агента; Notify не запускает речевые модели и не импортирует код из сети. `dial_call(session_id=..., security_level=...)` инициирует входящий звонок владельцу (без открытия микрофона), `wait_call(call_id)` ждёт accepted/ended; `CallAudio` в v2 передаёт `generation`, который агент обязан поднять перед новым ответом после `interrupt`. Необязательные обработчики `on_accepted`/`on_rejected` дополняют `on_invite/on_interrupt/on_ended`.

Голосовые методы SDK: `upload_audio(wav)`, `send_voice(..., wav=...)`, `download_audio(audio_id, grant=...)`, `voice_status(id)`; async-клиент повторяет их. MCP не передаёт бинарные файлы, поэтому голос доступен через SDK/HTTP.

Остановленного агента сервис сам не возобновляет. Среда агента должна ждать/опрашивать состояние либо принимать webhook. Skill находится в `skills/alxprgs-notify` и может быть установлен владельцем; глобальная установка в рамках этапа не выполнялась.
