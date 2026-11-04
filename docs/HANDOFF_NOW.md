# Точка передачи 0.5.0 — продолжение ALXPRGS Notify

История предыдущего чата не нужна: состояние и решения зафиксированы в репозитории. Код не сбрасывать и не начинать с шаблонов. Локальные коммиты находятся в ветке `codex/calls-v0.5.0`; push и production-деплой не выполнялись.

## Репозитории

- `C:\Users\prosh\OneDrive\Документы\GitHub\notifications-api`
- `C:\Users\prosh\OneDrive\Документы\GitHub\notifications-app`
- SSO только для чтения: `C:\Users\prosh\OneDrive\Документы\GitHub\sso`. Код SSO не менять.
- Дизайн: `C:\Users\prosh\OneDrive\Документы\GitHub\.github\standards\design\ALXPRGS_DESIGN_LANGUAGE_v1.md`.

Сначала прочитать `AGENTS.md`, `README.md`, `docs/ACCEPTANCE.md`, `docs/CONTRACT.md`, `docs/SECURITY.md`, `docs/OPERATIONS.md`, `docs/HARNESS_PLAN.md`.

## Что реализовано и проверено локально

Продукт 0.5.0. Один .NET 10 процесс: REST + официальный MCP C# SDK + FCM HTTP v1 + durable SQLite outbox; сообщения, подтверждения, формы, голосовые сообщения, живые звонки в обе стороны, агентский WebSocket.

- **65 серверных тестов PASS** на реальной SQLite: формы, голос, elevated grants, backup, SSO-стенд, webhook retry; звонки — incoming dial, multi-device accept race, reject/busy/missed, source/device revoke, v2 relay, barge-in generation, детерминированный queue overflow `degraded`, точная привязка call-proof, запрет использовать его как отдельное разрешение.
- **Python SDK 0.5.0**: 15 тестов из исходников и установленного wheel; `dial_call/wait_call`, v2 `seq/timestamp/generation`, `CallAudio`, reconnect.
- **Smoke** `artifacts/sdk-smoke-5a1c25a7d6/result.json`: REST, формы, голос, elevated grant, исходящий и входящий звонок, accepted/audio, barge-in, история, MCP.
- **Android 0.5.0**: assembleDebug/assembleRelease/testDebugUnitTest PASS, 6 JVM-тестов. Входящий экран/уведомление, accept/reject, mute без разрыва sequence, playback generation, foreground CallStyle service, BiometricPrompt. Release unsigned.
- **Веб**: build + Playwright PASS; история звонков с направлением, агенты, темы, mobile. `artifacts/ui/result.json`.
- Skill 0.5.0 проходит `quick_validate.py`.
- **Docker Desktop Linux/amd64, 192 MiB/0.5 CPU**: `artifacts/notify-benchmark-9cb570f75874/metrics.json`. 10-минутный звонок: 30 000 кадров в каждую сторону, 60 000 relayed, peak cgroup ~83 MB, OOM=false, после завершения incoming=null и history ended. p95 HTTP ~149 ms; idle ~41.6 MiB.

Точные ограничения доказательств — в `ACCEPTANCE.md`. Не выдавать software-подпись, TestServer/Kestrel-стенд или fixtures за физическую биометрию, настоящий FCM/SSO или реальный речевой диалог.

## Ближайшие действия

1. **Физический Android** (внешний gate): собрать персональный APK с `google-services.json`, проверить FCM incoming → CallStyle/full-screen intent на Android 11–16, accept/reject, микрофон/динамик/Bluetooth, Doze/блокировку, process death, реальную биометрию/PIN, App Links. Без устройства эти пункты не закрыты.
2. **Подпись APK** (внешний gate): только приватный signing key владельца; не создавать ключ в репозитории. После подписи — `apksigner verify --print-certs`, assetlinks и проверка обновления поверх установленного приложения.
3. **Внешние сервисы**: Firebase credentials/service account, SSO clients `notify-android`/`notify-web` + step-up policy, DNS/TLS `notify.alxprgs.tech`, nginx. Секреты — только приватно, не в чат.
4. **Речевой провайдер/VAD/STT/TTS** у агента: SDK даёт транспорт и generation, но не изображает ИИ-диалог. Проверить реальный provider adapter и barge-in на живом VAD.
5. **EC2/CI**: Docker Desktop — не t3.small; после доступа измерить CPU credits, сеть, память и повторить 10-минутный сценарий на целевом хосте. GitHub Actions в облаке не запускались.
6. Перед production: выполнить `docker compose config`, backup/restore, readiness, отзыв устройств/источников; не публиковать автоматически.

## Внешние gates владельца

Нужны приватно, не в чат: Firebase credentials + `google-services.json`, SSO clients и step-up policy, DNS/TLS, personal APK signing key, Android-устройство с ADB, реальная среда агента/речевой провайдер. Production, DNS и публикацию автоматически не выполнять.

## Важные технические заметки

- Звонок v2: сигнализация по agent control WS, две media WS (`/api/v1/calls/{id}/audio`, `/api/v1/agents/calls/{id}/audio`), 652-байтовые кадры (`seq:uint32 BE + timestampMs:uint32 BE + generation:uint32 BE + 640 PCM16LE`), очередь 100 кадров, без записей/STT/TTS. WebSocket-эндпоинты удерживают HTTP-запрос до закрытия сокета.
- Mute: sequence увеличивается только для отправленных кадров; muted-кадры не передаются. Barge-in: сервер очищает очередь/фильтрует generation, Android отбрасывает playback старого generation; повторный/устаревший interrupt игнорируется.
- Входящий: `call.dial` → `call.dial_ack` → data-only FCM `{type:"call",call_id,call_state}`/`GET /calls/incoming`; первый accept окончателен, остальные видят `call_taken`/state. Микрофон не включается на приглашение.
- Elevated call-proof: точные bytes `alxprgs.notify.call.v2` с `deviceId,direction,action,sourceId,agentId,sessionId,callId,inviteId,securityLevel,nonce,expiresAt`; проверяются все поля и срок, challenge одноразовый. Для отдельного опасного действия нужно отдельное confirmation.
- Docker HEALTHCHECK использует `curl /health/live`; `/health/ready` отдельно проверяет SSO/Firebase. Не заменять readiness на liveness.
- Android lock: `kotlin-stdlib-common:2.2.20` — metadata-only; запись в `app/gradle.lockfile` должна оставаться только для compile-конфигураций, иначе strict locking Gradle 8.13 падает на runtime.
- При изменении Android-кода повторить debug/release/JUnit; при изменении сервера — `dotnet test`, smoke и Docker; при изменении SDK — пересобрать wheel и тестировать установленный пакет.

Документы: [ACCEPTANCE](ACCEPTANCE.md), [CONTRACT](CONTRACT.md), [SECURITY](SECURITY.md), [OPERATIONS](OPERATIONS.md), [HARNESS_PLAN](HARNESS_PLAN.md).
