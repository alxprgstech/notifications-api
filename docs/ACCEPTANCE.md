# Приёмка 0.5.0 — 2026-11-04

## Фактически проверено локально

- .NET SDK 10.0.401 / runtime 10.0.12: **73 серверных теста PASS** на настоящей SQLite 3.53.3. Покрыты источники/права, ответы и подписи P-256, сроки/отмена, очистка 30-дневной границы, формы, агенты, голос, elevated audio grant, backup, SSO HTTP-стенд, CSRF/Origin, webhook retry, квоты и звонки: входящий вызов агента с несколькими устройствами, гонка accept, reject/busy/missed, source/device revoke, v2-кадры, barge-in generation, детерминированное переполнение очереди `degraded`, привязка call-proof к агенту/сессии/вызову/устройству/направлению/действию, ограниченное завершение при зависшем агенте/телефоне и занятой отправке, следующий звонок после зависшего end, согласование call-протокола (v2/старый hello/неизвестная версия), а также настоящий `AgentHub.Run` с управляемым WebSocket: зависший hello_ack/начальная очередь, Disconnect, timeout, замена соединения и повторная доставка команды с прежним eventId.
- Python 3.13: **16 SDK-тестов PASS** из исходников и в установленном wheel **0.5.0** (`.tools/wheel-venv`, пакет из `site-packages`). Wheel/sdist: `artifacts/sdk/alxprgs_notify-0.5.0*`. Покрыты `dial_call/wait_call`, v2 `seq/timestamp/generation`, call-аудио и reconnect.
- `scripts/sdk_smoke.py`: **PASS** на реальном loopback Kestrel: REST/SDK, обычный и elevated ответ, формы, голосовое уведомление агента + elevated grant, голосовой ответ Android → durable-команда → ack, **исходящий звонок**, **входящий звонок от агента с accepted/audio**, бинарный relay, **barge-in с generation**, история с direction, MCP HTTP. Итог: `artifacts/sdk-smoke-8ca67ce98f/result.json`. Подпись — software P-256, не аппаратная биометрия; агент — SDK/скрипт, не реальный речевой провайдер.
- Android 0.5.0: **assembleDebug, assembleRelease, testDebugUnitTest, assembleDebugAndroidTest PASS**. **10 JVM-тестов**: просроченный ответ, формы, WAV, v2 call-кадр (652 байта, generation), mute не расходует sequence + playback отбрасывает старый generation, версии capabilities, `CallProtocol.supportsAgent`, маршрутизация show/accept/decline и одноразовые токены уведомлений. **Instrumentation-тест** `IncomingNotificationRoutingTest` собран в `app-debug-androidTest.apk`, но **не запускался**: `adb devices` не содержит устройства. JVM-тест маршрутизации не выдаётся за системную проверку Android. Реализованы входящий экран/уведомление, accept/reject, foreground CallStyle service, elevated BiometricPrompt, локальный лимит очереди. Release unsigned.
- Веб: production build PASS. Playwright: login/docs, три guide route, кабинет, агенты, история звонков с направлением (только метаданные), обе темы, mobile без overflow, отсутствие JS errors и внешних запросов — **PASS** (`artifacts/ui/result.json`).
- Skill 0.5.0: `quick_validate.py` — **Skill is valid**.
- AAPT2 checksum Linux в `gradle/verification-metadata.xml` сохранён; Android lock scope не изменялся.

## Docker Desktop Linux / amd64, лимит 192 MiB / 0.5 CPU

`artifacts/notify-benchmark-c4040e2a2b6c/metrics.json`:

- 1 000 длинных записей + полная стартовая sync: 1 000 items, ~2.24 MB, ~819 ms.
- 100 POST / 10 параллельно: все успешны; p50 ~99 ms, p95 ~159 ms; `loadCpuCores` ~0.296 при лимите 0.5.
- Idle: ~41.7 MiB Docker stats; peak cgroup до звонка ~60.5 MB.
- **Разговор 600 секунд (10 минут):** 30 000 кадров в каждую сторону, **60 000 relayed**, фактическая длительность звонка ~630 с; call CPU в среднем ~0.034 ядра; peak cgroup ~78.9 MB; сэмплы памяти 56.9→74.2 MB (максимум сэмпла ~74.2 MB); Docker stats после звонка ~66.9 MiB; OOM=false.
- После завершения: `postCallIncoming=null`, история звонка `ended`, durationMs ~629 599, direction `outgoing`; очереди/сокеты закрыты. `postCallCgroupBytes` ~74.5 MB — .NET сохраняет рабочий heap, память не обязана немедленно вернуться ОС; утечки очередей не наблюдалось, рост между 2-й и 20-й минутой плато.
- Elevated token в benchmark-фикстуре имеет TTL 2 часа; это тестовая БД, не production credential. Docker HEALTHCHECK исправлен на `curl /health/live`: старый запуск второго .NET-процесса давал ложный unhealthy/расход памяти; readiness по-прежнему отдельно проверяет SSO/Firebase.
- Лимит `NOTIFY_MEMORY_LIMIT=192m`, CPU 0.5 оставлен: пик ~78.9 MB даёт запас; расширение функций требует новых замеров. Docker Desktop — не EC2 t3.small.

## Исправленные дефекты 0.5.0

- **Входящий звонок агента:** `call.dial` по авторизованному control WS, `call.dial_ack`, data-only FCM `{type:"call",call_id,call_state}` без содержимого, `/api/v1/calls/incoming`, accept/reject, 45-секундный ring, первый окончательный accept на нескольких телефонах, актуальное состояние остальным, `call.accepted/ended` агенту, история с `direction`. Микрофон не включается на приглашение.
- **Mute:** sequence расходуется только для реально отправленных кадров; Android `CallSequence` не увеличивает номер во время mute, timestamp остаётся неубывающим. Клиентская очередь ограничена, ошибка `send`/переполнение завершают звонок явно.
- **Barge-in:** протокол v2, 652-байтовый кадр `seq/timestamp/generation` + 640 PCM. Сервер очищает очередь старого generation, отбрасывает запоздавшие кадры, игнорирует повторный/устаревший interrupt; Android фильтрует playback по generation. Тест проверяет реальные кадры, включая накопленную очередь.
- **Call-proof:** точные bytes `alxprgs.notify.call.v2` содержат `deviceId,direction,action,sourceId,agentId,sessionId,callId,inviteId,securityLevel,nonce,expiresAt`; сервер сверяет все поля, устройство и срок, гасит challenge. Отдельно проверены подмена агента, сессии, устройства, вызова/направления и повтор.
- **Автоматическое принятие входящего:** full-screen intent и content intent теперь несут только `show`; кнопки accept/decline используют одноразовый токен, привязанный к `callId`/действию. Внешний Intent к экспортированной MainActivity без валидного токена понижается до показа карточки, неизвестное действие игнорируется. Микрофон запускается только после явного принятия и разрешения; elevated-биометрия остаётся обязательной. JVM-тесты проверяют маршрутизацию и одноразовость; instrumentation `IncomingNotificationRoutingTest` собран, но не запускался без устройства.
- **Ограниченное завершение:** `CallShutdown` ограничивает сигнализацию агенту/телефону, `MediaSide.TextAsync` имеет таймауты/Abort, `AgentHub.SendControlAsync` ограничен и закрывает зависшую сессию, а `EndAsync` выполняет закрытие сокетов/очередей в `finally`. Тесты с зависшим агентом/телефоном, занятой отправкой и конкурентными end доказывают, что очистка завершается и следующий звонок начинается.
- **Согласование call-протокола:** hello объявляет `callProtocols`; `hello_ack.callProtocol` содержит выбранную версию (только v2) или `null`. Старый/неизвестный агент не создаёт ringing (`call_protocol_unsupported`), но сохраняет текстовые/голосовые команды; Android показывает кнопки звонка только при совпавшей версии, SDK `dial_call`/`call_audio` сразу отклоняют несовместимость.
- **AgentHub и начальная очередь:** `Run` привязывает hello_ack и `SendPendingAsync` к `connection.Closing` и таймауту соединения; все `SendJsonAsync`/ожидания `SendLock` ограничены. Управляемый WebSocket-тест блокирует hello_ack и начальную очередь, проверяет Disconnect, timeout, закрытие/Abort, удаление только своего соединения, замену новым подключением и повторную доставку неподтверждённой команды с прежним eventId.
- **Дополнительно:** Docker healthcheck и медленное закрытие WebSocket при переполненной очереди/мёртвом peer (теперь close с таймаутом и Abort), иначе долгий звонок мог зависнуть в `EndAsync`.

## Что ещё не принято

1. Production не менялся. DNS/TLS, nginx, SSO clients/step-up policy и Firebase владелец ещё не настроил.
2. Нет подключённого ADB-устройства: реальный FCM incoming → CallStyle/full-screen intent (включая ограничения Android 14+) → accept → микрофон/динамик/Bluetooth, Doze/блокировка, process death и аппаратная биометрия **не проверены**. Instrumentation APK собран, но JVM-тесты маршрутизации не заменяют системную проверку; `adb devices` пуст.
3. Нет реального речевого провайдера/VAD/STT/TTS агента. Smoke подтверждает транспорт, generation и подписанные байты, но не качество ИИ-диалога.
4. APK release unsigned; личный signing key не создавался и не публиковался.
5. GitHub Actions не запускались в облаке; изменения сохранены локальными коммитами, push не выполнялся.
6. Docker Desktop Linux/amd64 — не EC2 t3.small; CPU credits, реальная сеть и production-нагрузка не измерены.
7. Incoming-уведомление и FCM data-only не проверены на физическом Android; если система отказывает в full-screen intent, остаётся уведомление/опрос `/calls/incoming`.

## Воспроизведение

```powershell
dotnet restore Notify.sln --locked-mode
dotnet test Notify.sln --no-restore -c Release
.tools/sdk-venv/Scripts/python.exe -m unittest discover -s packages/python-sdk/tests -v
.tools/wheel-venv/Scripts/python.exe -m unittest discover -s packages/python-sdk/tests -v
.tools/wheel-venv/Scripts/python.exe scripts/sdk_smoke.py
npm.cmd --prefix web ci --ignore-scripts
npm.cmd --prefix web run build
npm.cmd --prefix web run test:ui
docker build -t alxprgs-notify:local .
.tools/sdk-venv/Scripts/python.exe scripts/benchmark_container.py --memory 192m --large-history --call-seconds 600
```

Android: `.\gradlew.bat :app:assembleDebug :app:assembleRelease :app:testDebugUnitTest --no-daemon` с `JAVA_HOME` из `.tools/jdk` и `GRADLE_USER_HOME=$PWD/.gradle-cache`.

Следующие шаги: [HANDOFF_NOW.md](HANDOFF_NOW.md), план: [HARNESS_PLAN.md](HARNESS_PLAN.md).
