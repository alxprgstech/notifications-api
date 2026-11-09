# Статус безопасности F-01–F-10

Дата проверки: 2026-11-09. Исходный отчёт: локальный `notify-audit/SECURITY_AUDIT.md`
(в Git не входит). Статусы разделяют исправление, усилие защиты и ограничение
проверки; отсутствие физического Android не выдаётся за проверку.

| ID | Статус | Что сделано / доказательство | Остаток |
|---|---|---|---|
| F-01 | Исправлено | Runtime `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble`, пакеты `Microsoft.*` 10.0.14, lock-файл обновлён; Trivy по итоговому образу: 0 CRITICAL/HIGH с доступным исправлением. | Полное production-сканирование вне локального Docker. |
| F-02 | Исправлено по доступным пакетам | `deploy/silero/Dockerfile` закреплён на digest `python@sha256:935c…03df`; torch 2.10.0; setuptools/wheel/jaraco.context обновлены. Trivy по итоговому Silero-образу: 0 CRITICAL/HIGH с доступным исправлением (см. отчёт PR). Модель проверяется по SHA-256. | Полный hash-lock apt/pip не заявлен; это ограничение воспроизводимости. |
| F-03 | Исправлено | `harness-notify` использует `torch==2.13.0+cpu`; `silero_tts.py` проверяет SHA-256 `v5_6_ru.pt` перед `torch.package.load_pickle`. | Холодный запуск реальной модели требует файла модели у владельца. |
| F-04 | Исправлено в коде/конфигурации | Включены `X-Forwarded-For` и `X-Forwarded-Proto` только для явного `TrustedProxyNetwork`; nginx передаёт оба заголовка. | Нужен живой прогон за конкретным прокси; unit-тест не заменяет сеть. |
| F-05 | Исправлено | `WebhookPolicy.IsPublic` блокирует `2002::/16` (6to4), `2001:0::/32` (Teredo) и документационный `2001:db8::/32`; добавлены theory-тесты. | DNS rebinding/сетевые условия остаются ограничением. |
| F-06 | Исправлено | CSRF/Origin middleware применяется ко всем небезопасным методам, не только POST; добавлен негативный тест DELETE из web-сессии. | SameSite остаётся дополнительным слоем. |
| F-07 | Частично исправлено | CI выполняет `dotnet list package --vulnerable` и `npm audit --omit=dev --audit-level=high`; base digest закреплён. | Trivy/Gitleaks/SBOM и branch protection недоступны в текущем приватном GitHub Free; это внешнее ограничение. |
| F-08 | Принятый риск | `stt`/`silero` только во внутренней сети `speech`, без публикации портов, с лимитами памяти/CPU и read-only rootfs. | Внутренняя аутентификация не добавлена; сеть остаётся границей доверия. |
| F-09 | Обновлено, физическая проверка не выполнена | `androidx.biometric:1.4.0-beta01`; все elevated-пути (`answer`, form, audio grant, call challenge) используют `CryptoObject` и точные серверные bytes. OSV по AppAuth 0.11.1/Biometric не дал известных CVE. | Нужны Redmi: сильная биометрия, PIN, отмена, повтор, отзыв, конкурентность, Keystore. JVM не подтверждает hardware. |
| F-10 | Сохранено и усилено | PKCE S256, TLS, одноразовые challenge, атомарный ответ/outbox; текст звонка хранится в SQLite только в Data Protection-конверте; текст elevated-голоса не отдаётся без grant. | Полный disk forensics и production SSO не входили. |

## Отрицательные проверки

- `WebhookPolicyRejectsTunnelAndPrivateRanges` — 6to4/Teredo/NAT64/private.
- `WebManagementRequiresCsrfOriginAndPhoneAndShowsTokenOnce` — web DELETE без Origin отклоняется.
- `CallProofCannotAuthorizeASeparateAction`, `ElevatedAnswerIsBoundToDeviceAnswerAndOneUse`,
  `FormTests` — чужая/повторная/истёкшая elevated-подпись не даёт права.
- `Conversation...` — текст расшифровки отсутствует в открытом виде в SQLite.
- `AnnouncementSendsLastFrameThenEndedBeforeClose` — штатное завершение `playback_sent`,
  а не `disconnected`.

## Что требует владельца

1. Redmi: иконка в маске, маршрут до первого слова, Bluetooth, PIN, сильная биометрия,
   отмена, FCM, App Links, SSO, защищённый звонок.
2. Повторное сканирование итоговых Docker-образов и проверка CI/подписей PR.

