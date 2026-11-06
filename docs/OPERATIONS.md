# Установка и эксплуатация

Текущий вариант владельца — [домашний Windows-ПК с Docker Desktop и OpenWrt](HOME_HOSTING.md). Ниже сохранён комплект для альтернативного размещения на Ubuntu/EC2.

Этот комплект подготовлен для существующего Ubuntu-сервера 54.152.17.1 рядом с SSO. Эти команды **не выполнялись на production**. Не заменяйте конфигурацию, данные, контейнеры или deployment SSO.

## Настройка владельцем

1. Создайте DNS `notify.alxprgs.tech`, отдельный TLS-сертификат и vhost по `deploy/nginx.conf`. API привязан только к host loopback 8085; MCP использует тот же HTTPS/443. Скрипты Notify не меняют DNS, firewall или AWS.
2. В существующем SSO зарегистрируйте public client `notify-android`, разрешённый scope `openid`, точный redirect `https://notify.alxprgs.tech/android/oauth2redirect`.
3. Зарегистрируйте confidential client `notify-web`, scope `openid`, redirects `https://notify.alxprgs.tech/signin-oidc` и `https://notify.alxprgs.tech/auth/step-up/callback`. Сохраните secret приватно. Включите external step-up глобально и у этого клиента, assurance `reauth_password_mfa`, actions `notify.enroll_device`, `notify.recover_devices`, max TTL 300. Это настройка существующего SSO, не изменение его кода.
4. У владельца SSO должны быть текущий пароль и подтверждённый TOTP/Passkey. Получите его UUID sub через существующее управление SSO, не по предположению из email.
5. В Firebase создайте Android app `tech.alxprgs.notify`. Поместите его `google-services.json` в `notifications-app/app` до сборки персонального APK. Серверному service account выдайте только необходимые права FCM sending. JSON сервера храните вне Git, например `/etc/alxprgs-notify/firebase.json`, доступный контейнерному UID 1654 только на чтение. Не присылайте ключ в чат.
6. Агент подключается исходящим WSS на `notify.alxprgs.tech:443`; входящие порты на его компьютере не открывайте. nginx должен проксировать WebSocket upgrade для `/api/v1/agents/connect` (готовый location в `deploy/nginx.conf`). Токен источника со scope `agents:connect` храните в приватном окружении агента; регистрацию подтвердите на телефоне. `Notify__AgentSendTimeoutSeconds` (по умолчанию 5, допустимо 1–30) ограничивает hello_ack и отправки команд агенту; при зависшем получателе соединение закрывается, а неподтверждённые команды остаются для повторной доставки.
7. Голосовые загрузки идут в `/api/v1/audio/uploads`; nginx должен разрешать тело до 11 МиБ и не буферизовать запрос на диск (готовые location в `deploy/nginx.conf`). Файлы хранятся в `/data/audio` внутри named volume, попадают в backup вместе с БД. Квоту задаёт `Notify__AudioQuotaBytes` (по умолчанию 512 МиБ); при заполнении новые загрузки отклоняются, старые файлы не удаляются молча. Речевые модели работают у агента/провайдера, не на этом сервере.
8. Для живых звонков nginx должен проксировать WebSocket upgrade для `/api/v1/calls/*/audio` и `/api/v1/agents/calls/*/audio` с длинным read timeout (готовые location в `deploy/nginx.conf`). Входящий звонок агенту доставляется data-only FCM (`call_id`/состояние, без содержимого), а приложение показывает системное CallStyle-уведомление и не включает микрофон до принятия. Android 14+ может ограничивать full-screen intent; если система не показала экран, остаётся уведомление/опрос `/api/v1/calls/incoming`. Микрофон и Bluetooth на Android запрашиваются только при звонке; foreground service живёт только во время разговора. Записей и расшифровок нет; история звонков — только метаданные, direction и reason.
9. Для HTTPS App Link получите SHA-256 сертификата подписи APK через `apksigner verify --print-certs`. Заполните `Notify__AndroidSigningFingerprints__0` и при необходимости следующие индексы. Проверьте `/.well-known/assetlinks.json` и Android domain verification. Без правильного App Link браузерный OIDC callback не вернётся в приложение.
10. Создайте приватный `.env` по `.env.example`, замените все placeholders, права 0600. Если нужны webhooks, перечислите точные public HTTPS hostnames в `Notify__WebhookHosts__N`.

```bash
docker compose config --quiet
docker compose build
docker compose up -d
docker compose ps
curl --fail http://127.0.0.1:8085/health/ready
```

Docker HEALTHCHECK использует `/health/live` (процесс жив), а `/health/ready` намеренно отвечает 503 `configuration_required`, пока не настроены SSO и Firebase. Не подменяйте readiness на liveness в оркестраторе: проверка конфигурации должна оставаться явной. Для контейнера установлен `curl`, чтобы healthcheck не запускал второй .NET-процесс и не расходовал память/CPU во время звонка.

Named volume создаётся с владельцем /data из образа. При ручном восстановлении проверьте UID 1654 и режим 0700 каталога. Не запускайте основной сервер под root. Значения memory/cpu выбираются по измерениям; перед запуском проверьте свободную память и нагрузку существующего SSO. Результат локального Docker Desktop не доказывает отсутствие CPU-credit расходов на EC2.

## Проверка на телефоне

Установите персонально собранный APK. Войдите, подтвердите привязку на SSO, разрешите уведомления. Создайте интеграцию в кабинете; подпишите запрос управления на телефоне и сохраните показанные один раз токен и webhook secret приватно. Запустите `examples/agent.py`. Проверьте обычный и elevated ответы, собственный ответ, отказ, истечение, блокировку экрана и временное отсутствие сети. Закрытие процесса приложения и принудительная остановка Android — разные режимы; FCM не обещает доставку после force-stop до следующего запуска приложения.

## Backup/restore

Не копируйте один живой `notify.db` без WAL: используйте Backup API.

```bash
docker compose exec notify dotnet Notify.Server.dll backup /tmp/notify-backup.db
```

Скопируйте полученную snapshot-БД и каталог `/data/keys` в приватное резервное хранилище. Также сохраняйте иконки `/data/icons`, аудио `/data/audio` и отдельно операторскую конфигурацию/Firebase credential. Не включайте их в публичные CI artifacts.

Для согласованного полного backup при активном управлении: на короткое время остановите приём запросов в новом vhost, дождитесь завершения in-flight операций, сделайте snapshot через Backup API и скопируйте keys/icons. Фоновая доставка может безопасно повториться после восстановления. Предпочтительно делать полный backup во время отсутствия изменений иконок/ключей; при остановленном сервисе можно копировать весь volume вместе со всеми SQLite файлами.

Восстанавливайте сначала в отдельный volume: остановленный сервер, snapshot как `notify.db`, соответствующие keys/icons и правильные права. Старые `-wal/-shm` файлы от другой БД не переносите. Проверьте `PRAGMA integrity_check`, историю и подписи, затем переключайте volume. Повторы webhook после отката возможны, поэтому получатель обязан дедуплицировать eventId. Не смешивайте новую БД со старыми Data Protection keys.

## Обновление и откат

Зафиксируйте проверенный Git SHA/образ, сделайте backup, соберите новую версию и выполните `docker compose up -d`. Миграции v1 идемпотентны, один серверный экземпляр обязателен. Перед откатом схемы восстановите соответствующий backup в новый volume; не запускайте старый код на неизвестной новой схеме. Успех устанавливают readiness, контрольный запрос и отсутствие ошибок доставки; GitHub Actions ничего автоматически не развёртывает.

Измерения: `python scripts/benchmark_container.py --memory 128m` (для приёмки 0.5.0 — `--memory 192m --large-history --call-seconds 600`) создаёт и удаляет только собственный временный контейнер/volume `notify-benchmark-*`. Он не использует реальные интеграции. Длительный звонок пишет сэмплы cgroup memory, CPU, число доставленных кадров, историю и состояние после завершения. Для Windows-процесса отдельно `python scripts/benchmark.py`; результаты имеют разные платформенные ограничения.
