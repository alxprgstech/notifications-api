# Приёмка 0.5.0 — 2026-11-04

## Фактически проверено локально

- .NET SDK 10.0.401 / runtime 10.0.12: **65 серверных тестов PASS** на настоящей SQLite 3.53.3. Покрыты источники/права, ответы и подписи P-256, сроки/отмена, очистка 30-дневной границы, формы, агенты, голос, elevated audio grant, backup, SSO HTTP-стенд, CSRF/Origin, webhook retry, квоты и звонки: входящий вызов агента с несколькими устройствами, гонка accept, reject/busy/missed, source/device revoke, v2-кадры, barge-in generation, детерминированное переполнение очереди `degraded`, привязка call-proof к агенту/сессии/вызову/устройству/направлению/действию и невозможность использовать его как разрешение отдельного действия.
- Python 3.13: **15 SDK-тестов PASS** из исходников и в установленном wheel **0.5.0** (`.tools/wheel-venv`, пакет из `site-packages`). Wheel/sdist: `artifacts/sdk/alxprgs_notify-0.5.0*`. Покрыты `dial_call/wait_call`, v2 `seq/timestamp/generation`, call-аудио и reconnect.
- `scripts/sdk_smoke.py`: **PASS** на реальном loopback Kestrel: REST/SDK, обычный и elevated ответ, формы, голосовое уведомление агента + elevated grant, голосовой ответ Android → durable-команда → ack, **исходящий звонок**, **входящий звонок от агента с accepted/audio**, бинарный relay, **barge-in с generation**, история с direction, MCP HTTP. Итог: `artifacts/sdk-smoke-5a1c25a7d6/result.json`. Подпись — software P-256, не аппаратная биометрия; агент — SDK/скрипт, не реальный речевой провайдер.
- Android 0.5.0: **assembleDebug, assembleRelease, testDebugUnitTest PASS**. **6 JVM-тестов**: просроченный ответ, формы, WAV, v2 call-кадр (652 байта, generation), mute не расходует sequence + playback отбрасывает старый generation, версии capabilities. Реализованы входящий экран/уведомление, accept/reject, foreground CallStyle service, elevated BiometricPrompt, локальный лимит очереди. Release unsigned, физическое устройство не проверялось.
- Веб: production build PASS. Playwright: login/docs, три guide route, кабинет, агенты, история звонков с направлением (только метаданные), обе темы, mobile без overflow, отсутствие JS errors и внешних запросов — **PASS** (`artifacts/ui/result.json`).
- Skill 0.5.0: `quick_validate.py` — **Skill is valid**.
- AAPT2 checksum Linux в `gradle/verification-metadata.xml` сохранён; Android lock scope не изменялся.

## Docker Desktop Linux / amd64, лимит 192 MiB / 0.5 CPU

`artifacts/notify-benchmark-9cb570f75874/metrics.json`:

- 1 000 длинных записей + полная стартовая sync: 1 000 items, ~2.24 MB, ~820 ms.
- 100 POST / 10 параллельно: все успешны; p50 ~79 ms, p95 ~149 ms; `loadCpuCores` ~0.397 при лимите 0.5.
- Idle: ~41.6 MiB Docker stats; peak cgroup до звонка ~61.1 MB.
- **Разговор 600 секунд (10 минут):** 30 000 кадров в каждую сторону, **60 000 relayed**, фактическая длительность звонка ~630 с; call CPU в среднем ~0.035 ядра; peak cgroup ~83.0 MB; сэмплы памяти 57.8→78.5 MB (максимум сэмпла ~78.5 MB); Docker stats после звонка ~70.5 MiB; OOM=false.
- После завершения: `postCallIncoming=null`, история звонка `ended`, durationMs ~629 824, direction `outgoing`; очереди/сокеты закрыты. `postCallCgroupBytes` ~79.0 MB — .NET сохраняет рабочий heap, память не обязана немедленно вернуться ОС; утечки очередей не наблюдалось, рост между 2-й и 20-й минутой плато.
- Elevated token в benchmark-фикстуре имеет TTL 2 часа; это тестовая БД, не production credential. Docker HEALTHCHECK исправлен на `curl /health/live`: старый запуск второго .NET-процесса давал ложный unhealthy/расход памяти; readiness по-прежнему отдельно проверяет SSO/Firebase.
- Лимит `NOTIFY_MEMORY_LIMIT=192m`, CPU 0.5 оставлен: пик ~83 MB даёт запас; расширение функций требует новых замеров. Docker Desktop — не EC2 t3.small.

## Исправленные дефекты 0.5.0

- **Входящий звонок агента:** `call.dial` по авторизованному control WS, `call.dial_ack`, data-only FCM `{type:"call",call_id,call_state}` без содержимого, `/api/v1/calls/incoming`, accept/reject, 45-секундный ring, первый окончательный accept на нескольких телефонах, актуальное состояние остальным, `call.accepted/ended` агенту, история с `direction`. Микрофон не включается на приглашение.
- **Mute:** sequence расходуется только для реально отправленных кадров; Android `CallSequence` не увеличивает номер во время mute, timestamp остаётся неубывающим. Клиентская очередь ограничена, ошибка `send`/переполнение завершают звонок явно.
- **Barge-in:** протокол v2, 652-байтовый кадр `seq/timestamp/generation` + 640 PCM. Сервер очищает очередь старого generation, отбрасывает запоздавшие кадры, игнорирует повторный/устаревший interrupt; Android фильтрует playback по generation. Тест проверяет реальные кадры, включая накопленную очередь.
- **Call-proof:** точные bytes `alxprgs.notify.call.v2` содержат `deviceId,direction,action,sourceId,agentId,sessionId,callId,inviteId,securityLevel,nonce,expiresAt`; сервер сверяет все поля, устройство и срок, гасит challenge. Отдельно проверены подмена агента, сессии, устройства, вызова/направления и повтор.
- **Дополнительно:** Docker healthcheck и медленное закрытие WebSocket при переполненной очереди/мёртвом peer (теперь close с таймаутом и Abort), иначе долгий звонок мог зависнуть в `EndAsync`.

## Что ещё не принято

1. Production не менялся. DNS/TLS, nginx, SSO clients/step-up policy и Firebase владелец ещё не настроил.
2. Нет подключённого ADB-устройства: реальный FCM incoming → CallStyle/full-screen intent (включая ограничения Android 14+) → accept → микрофон/динамик/Bluetooth, Doze/блокировка, process death и аппаратная биометрия **не проверены**. Тесты подтверждают сервер/SDK/Compose-логику, не системное поведение.
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
