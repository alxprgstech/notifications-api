# Установка и эксплуатация

Notify можно развернуть двумя способами:

- **Docker Compose на постоянном домашнем ПК или сервере** — приложение и Caddy с автоматическим TLS. Пошаговая инструкция: [HOME_HOSTING](HOME_HOSTING.md).
- **Ubuntu/EC2 с nginx** — если nginx и TLS уже обслуживаются на хосте. Шаблон vhost: `deploy/nginx.conf`.

Оба варианта используют один и тот же серверный образ и одну базу SQLite. Выберите один; не запускайте второй экземпляр на тот же volume.

## Роли компонентов

| Компонент | Что делает | Что настраивается отдельно |
|---|---|---|
| Docker | Изолирует приложение, хранит данные и TLS-состояние в volumes | Автозапуск Docker, лимиты памяти/CPU |
| DNS | Направляет публичное имя на прокси | A/AAAA-запись, TTL, режим DNS only |
| TLS | Шифрует HTTPS/WSS; Caddy получает сертификат по ACME, nginx — из вашего хранилища | Домен, сертификат, OCSP/обновление |
| Обратный прокси | Принимает 80/443, завершает TLS, проксирует HTTP и WebSocket upgrade | Caddyfile или nginx vhost |
| SSO | Аутентифицирует владельца и подтверждает привязку устройства | Два OIDC-клиента, owner sub, step-up policy |
| Firebase | Доставляет data-only push | Android app, service account |

Прокси — единственная публичная точка входа. Приложение слушает loopback и не должно быть доступно напрямую. Не включайте access log на callback-путях: query и заголовки содержат одноразовые credentials. Не публикуйте Scalar с включённым хранением токена.

## Вариант A. Docker Compose и Caddy

Краткий порядок; полные команды и проверки — в [HOME_HOSTING](HOME_HOSTING.md).

1. Создайте DNS-запись и направьте TCP 80/443 на хост с Docker. UDP не нужен.
2. Создайте приватный `home.env` по `.env.example`, заполните домен, SSO, Firebase, owner sub и отпечаток APK.
3. Соберите и запустите:

```powershell
docker compose --env-file $cfg -f compose.yml -f compose.home.yml config --quiet
docker compose --env-file $cfg -f compose.yml -f compose.home.yml up -d --build
docker compose --env-file $cfg -f compose.yml -f compose.home.yml ps
```

4. Проверьте `/health/live`, `/health/ready`, `/docs` и вход через SSO.

`compose.yml` публикует приложение только на `127.0.0.1:8085`. `compose.home.yml` добавляет Caddy с портами 80/443 и `flush_interval -1` для WebSocket. Если Caddy работает в отдельной сети Compose, задайте `Notify__TrustedProxyNetwork` с её CIDR: приложение принимает `X-Forwarded-Proto` только из этой сети. После пересоздания сети проверьте CIDR снова.

## Вариант B. Ubuntu/EC2 с nginx

Шаблон `deploy/nginx.conf` использует вымышленный `notify.example.com`. Замените имя и пути сертификатов на свои, не заменяя конфигурацию SSO.

1. Создайте DNS `notify.example.com`, отдельный TLS-сертификат и vhost по шаблону. API привязывается к loopback `127.0.0.1:8085`; MCP использует тот же HTTPS/443. Если nginx передаёт `X-Forwarded-Proto`, задайте `Notify__TrustedProxyNetwork` с адресом/сетью nginx: приложение принимает этот заголовок только из явно доверенной сети.
2. В SSO зарегистрируйте public client `notify-android`: scope `openid`, redirect `https://notify.example.com/android/oauth2redirect`, Authorization Code + PKCE.
3. Зарегистрируйте confidential client `notify-web`: scope `openid`, redirects `https://notify.example.com/signin-oidc` и `https://notify.example.com/auth/step-up/callback`. Сохраните secret приватно. Включите external step-up, assurance `reauth_password_mfa`, actions `notify.enroll_device`, `notify.recover_devices`, TTL не больше 300 секунд.
4. У владельца SSO должны быть текущий пароль и подтверждённый TOTP/Passkey. UUID `sub` получите через управление SSO, не по предположению из email.
5. В Firebase создайте Android app `tech.alxprgs.notify`, положите `google-services.json` в `notifications-app/app` до сборки APK. Серверному service account выдайте только права FCM sending. JSON храните вне Git, например `/etc/alxprgs-notify/firebase.json`, доступный UID контейнера только на чтение.
6. Агент подключается исходящим WSS на `notify.example.com:443`; входящий порт на компьютере агента не нужен. nginx должен проксировать WebSocket upgrade для `/api/v1/agents/connect` (готовый location в шаблоне). Токен источника со scope `agents:connect` храните в приватном окружении агента; регистрацию подтвердите на телефоне. `Notify__AgentSendTimeoutSeconds` (по умолчанию 5, допустимо 1–30) ограничивает hello_ack и отправку команд.
7. Голосовые загрузки идут в `/api/v1/audio/uploads`; nginx должен разрешать тело до 11 МиБ и не буферизовать запрос на диск. Файлы лежат в `/data/audio` внутри named volume и входят в backup вместе с БД. Квоту задаёт `Notify__AudioQuotaBytes` (по умолчанию 512 МиБ); при заполнении новые загрузки отклоняются, старые файлы не удаляются молча. Речевые модели работают у агента/провайдера, не на сервере Notify.
8. Для живых звонков nginx должен проксировать WebSocket upgrade для `/api/v1/calls/*/audio` и `/api/v1/agents/calls/*/audio` с длинным read timeout. Входящий звонок доставляется data-only FCM (`call_id`/состояние, без содержимого). Android 14+ может ограничивать full-screen intent; если система не показала экран, остаётся уведомление/опрос `/api/v1/calls/incoming`. Микрофон и Bluetooth запрашиваются только при звонке; foreground service живёт только во время разговора. Записей и расшифровок нет.
9. Для HTTPS App Link получите SHA-256 сертификата подписи APK через `apksigner verify --print-certs`. Заполните `Notify__AndroidSigningFingerprints__0` и при необходимости следующие индексы. Проверьте `/.well-known/assetlinks.json` и Android domain verification. Без правильного App Link браузерный OIDC callback не вернётся в приложение.
10. Создайте приватный `.env` по `.env.example`, замените placeholders, права 0600. Если нужны webhooks, перечислите точные public HTTPS hostnames в `Notify__WebhookHosts__N`. Не используйте имена из примеров как production-значения.

```bash
docker compose config --quiet
docker compose build
docker compose up -d
docker compose ps
curl --fail http://127.0.0.1:8085/health/ready
```

Docker HEALTHCHECK использует `/health/live` (процесс жив), а `/health/ready` намеренно отвечает 503 `configuration_required`, пока не настроены SSO и Firebase. Не подменяйте readiness на liveness в оркестраторе: проверка конфигурации должна оставаться явной. В образе есть `curl`, чтобы healthcheck не запускал второй .NET-процесс.

Named volume создаётся с владельцем `/data` из образа. При ручном восстановлении проверьте UID 1654 и режим 0700 каталога. Не запускайте основной сервер под root. Лимиты memory/cpu выбирайте по измерениям; перед запуском проверьте свободную память и нагрузку соседних сервисов.

## Проверка на телефоне

Установите собранный APK. Войдите, подтвердите привязку на SSO, разрешите уведомления. Создайте интеграцию в кабинете; подпишите запрос управления на телефоне и сохраните показанные один раз токен и webhook secret приватно. Запустите `examples/agent.py`. Проверьте обычный и elevated ответы, собственный ответ, отказ, истечение, блокировку экрана и временное отсутствие сети. Закрытие процесса приложения и принудительная остановка Android — разные режимы; FCM не обещает доставку после force-stop до следующего запуска приложения.

## Backup/restore

Не копируйте один живой `notify.db` без WAL: используйте Backup API.

```bash
docker compose exec notify dotnet Notify.Server.dll backup /tmp/notify-backup.db
```

Скопируйте полученную snapshot-БД и каталог `/data/keys` в приватное резервное хранилище. Также сохраняйте иконки `/data/icons`, аудио `/data/audio` и отдельно операторскую конфигурацию/Firebase credential. Не включайте их в публичные CI artifacts.

Для согласованного полного backup при активном управлении: на короткое время остановите приём запросов в vhost, дождитесь завершения in-flight операций, сделайте snapshot через Backup API и скопируйте keys/icons. Фоновая доставка может безопасно повториться после восстановления. Предпочтительно делать полный backup во время отсутствия изменений иконок/ключей; при остановленном сервисе можно копировать весь volume вместе со всеми SQLite файлами.

Восстанавливайте сначала в отдельный volume: остановленный сервер, snapshot как `notify.db`, соответствующие keys/icons и правильные права. Старые `-wal/-shm` файлы от другой БД не переносите. Проверьте `PRAGMA integrity_check`, историю и подписи, затем переключайте volume. Повторы webhook после отката возможны, поэтому получатель обязан дедуплицировать eventId. Не смешивайте новую БД со старыми Data Protection keys.

## Обновление и откат

Зафиксируйте проверенный Git SHA/образ, сделайте backup, соберите новую версию и выполните `docker compose up -d`. Миграции v1 идемпотентны, один серверный экземпляр обязателен. Перед откатом схемы восстановите соответствующий backup в новый volume; не запускайте старый код на неизвестной новой схеме. Успех устанавливают readiness, контрольный запрос и отсутствие ошибок доставки; CI ничего автоматически не развёртывает.

## Измерения

```powershell
python scripts/benchmark_container.py --memory 192m --large-history --call-seconds 600
```

Скрипт создаёт и удаляет только собственный временный контейнер/volume `notify-benchmark-*` и не использует реальные интеграции. Длительный звонок пишет сэмплы cgroup memory, CPU, число доставленных кадров, историю и состояние после завершения. Для Windows-процесса отдельно: `python scripts/benchmark.py`; результаты имеют разные платформенные ограничения. Не переносите замеры с Docker Desktop на реальный сервер без повторной проверки.
