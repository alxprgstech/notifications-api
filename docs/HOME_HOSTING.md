# Самостоятельное размещение Notify

Это руководство описывает воспроизводимую установку Notify на собственном постоянном компьютере или сервере с Docker: один контейнер приложения, обратный прокси с TLS и внешний SSO. Примеры используют вымышленные `notify.example.com` и `auth.example.com`; замените их своими.

Notify — личный сервис для одного владельца. Он не рассчитан на публичную регистрацию, мультиарендность или горизонтальное масштабирование. Запускайте один экземпляр сервера с одной базой SQLite.

## Что понадобится

- Постоянно включённый компьютер или сервер с Docker Engine и Docker Compose v2. Для Docker Desktop включите автозапуск, если сервис должен подниматься после входа в систему.
- Публичное доменное имя, например `notify.example.com`.
- Возможность направить TCP 80 и 443 на прокси. UDP не нужен: используются HTTP/1.1, HTTP/2 и WebSocket по TCP.
- Настроенный OIDC-провайдер SSO с issuer `https://auth.example.com`. Notify не хранит пароль владельца и не заменяет SSO.
- Firebase-проект и серверный service account для FCM, если нужны push-уведомления.
- Android-клиент из соседнего репозитория `notifications-app` для привязки телефона. Сборка APK и App Links описаны в его README.

## Как устроена установка

| Компонент | Роль | Где настраивается |
|---|---|---|
| `notify` | .NET-сервер: REST, MCP, WebSocket агентов и звонков, SQLite и файлы | `compose.yml`, `home.env` |
| `caddy` | Публичный HTTP/HTTPS, автоматический TLS, проксирование WebSocket и MCP | `compose.home.yml`, `deploy/Caddyfile.home` |
| DNS | A/AAAA-запись домена на публичный адрес прокси | У регистратора/DNS-провайдера |
| SSO | Вход владельца и step-up подтверждение привязки устройства | Внешний OIDC-провайдер |
| Firebase | Доставка data-only push на Android | Firebase Console и service account |

Приложение слушает только loopback хоста на порту 8085 и не публикуется напрямую. Caddy принимает 80/443, получает и обновляет TLS-сертификат, перенаправляет HTTP на HTTPS и проксирует запросы к Notify, включая WebSocket upgrade. Access log фильтруется: URI, `Authorization` и `Cookie` удаляются, поэтому одноразовые SSO-callback значения не попадают в журнал.

Данные лежат в постоянных Docker volumes: база и Data Protection keys — в `notify_data`, TLS-состояние Caddy — в `notify_caddy_data`/`notify_caddy_config`. Обновление контейнера не удаляет данные; `docker compose down -v` удаляет, поэтому не используйте его для обновления.

## 1. Подготовьте DNS и сеть

1. Создайте A- (и при необходимости AAAA-) запись `notify.example.com` на публичный адрес вашего подключения или сервера.
2. Если используется Cloudflare или другой DNS-провайдер, выберите режим **DNS only**, чтобы Caddy сам получил сертификат по ACME. Режим прокси с TLS на стороне провайдера тоже возможен, но тогда настройте origin-сертификат и доверенную сеть прокси иначе; это отдельная схема, не описанная здесь.
3. Направьте TCP 80 и 443 с внешнего адреса на постоянный LAN-адрес компьютера с Docker. Зарезервируйте этот адрес в DHCP, чтобы правило не сломалось после перезагрузки.
4. Не открывайте порт 8085 наружу. Прокси — единственная публичная точка входа.

Проверка: с внешней сети `curl -I http://notify.example.com` должен получить ответ Caddy, а не таймаут. Сертификат появится после первого успешного ACME-challenge.

## 2. Зарегистрируйте SSO-клиенты

В SSO создайте два клиента. Notify использует их, но не меняет конфигурацию SSO.

**Публичный Android-клиент** (`notify-android`):

- Authorization Code + PKCE, без client secret в APK;
- scope `openid`;
- redirect `https://notify.example.com/android/oauth2redirect`;
- выдача Access Token с `token_use=access_token` для привязки устройства. ID Token вместо Access Token не принимается.

**Конфиденциальный веб-клиент** (`notify-web`):

- scope `openid`;
- redirects `https://notify.example.com/signin-oidc` и `https://notify.example.com/auth/step-up/callback`;
- client secret хранится только в приватном env-файле;
- external step-up включён, assurance `reauth_password_mfa`;
- actions `notify.enroll_device` и `notify.recover_devices`;
- максимальный TTL step-up — 300 секунд.

Владельцу SSO нужны актуальный пароль и подтверждённый TOTP/Passkey. UUID владельца (`sub`) возьмите из существующего управления SSO; не угадывайте его по email. Notify принимает только этот `sub` и отказывается стартовать при несовпадении.

## 3. Подготовьте Firebase

1. Создайте Android-приложение `tech.alxprgs.notify` в Firebase.
2. Скачайте `google-services.json` и положите его в `notifications-app/app/` **до** сборки APK. Файл не коммитится.
3. Создайте service account с правом отправки FCM. Сохраните JSON вне Git и OneDrive, например в `%LOCALAPPDATA%\ALXPRGS\Notify\firebase-service-account.json` или `/etc/alxprgs-notify/firebase.json`, с доступом только для пользователя/UID контейнера.
4. Не вставляйте ключ в чат, задачи, логи или публичные артефакты.

Без Firebase сервер запускается, но `/health/ready` отвечает 503 `configuration_required`, а push не отправляется. Это ожидаемо: защищённые функции не получают обходного входа.

## 4. Создайте приватную конфигурацию

Все секреты храните в одном каталоге вне репозитория, например `%LOCALAPPDATA%\ALXPRGS\Notify` на Windows или `/etc/alxprgs-notify` на Linux. Права — только владельцу (0700 для каталога, 0600 для файлов).

Создайте `home.env` по образцу `.env.example` и заполните:

```dotenv
ASPNETCORE_ENVIRONMENT=Production
NOTIFY_DOMAIN=notify.example.com
Notify__PublicUrl=https://notify.example.com
Notify__SsoIssuer=https://auth.example.com
Notify__OwnerSub=REPLACE_WITH_OWNER_UUID
Notify__WebClientId=notify-web
Notify__WebClientSecret=REPLACE_WITH_CONFIDENTIAL_CLIENT_SECRET
Notify__AndroidClientId=notify-android
Notify__AndroidSigningFingerprints__0=REPLACE_WITH_APK_SIGNING_SHA256
Notify__TrustedProxyNetwork=REPLACE_WITH_COMPOSE_SUBNET_CIDR
NOTIFY_MEMORY_LIMIT=192m
NOTIFY_CPU_LIMIT=0.5
FIREBASE_CREDENTIALS_FILE=REPLACE_WITH_ABSOLUTE_PATH_TO_FIREBASE_JSON
NOTIFY_ENV_FILE=REPLACE_WITH_ABSOLUTE_PATH_TO_THIS_HOME_ENV
```

- `NOTIFY_DOMAIN` — публичное имя для Caddy (без схемы); `Notify__PublicUrl` — тот же origin для приложения.
- `Notify__PublicUrl` должен точно совпадать с публичным HTTPS-origin: от него зависят cookie, redirect URI и проверка `Origin`.
- `Notify__TrustedProxyNetwork` — CIDR сети Compose, в которой работают Caddy и Notify. Узнайте его после первого создания сети:

  ```powershell
  docker network inspect alxprgs-notify_default --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
  ```

  Затем впишите CIDR в `home.env` и перезапустите Notify. Приложение принимает `X-Forwarded-Proto` только из этой сети.
- `Notify__AndroidSigningFingerprints__N` — SHA-256 сертификата подписи APK. Значение должно совпадать с фактическим APK, иначе App Link не вернёт браузер в приложение.
- `FIREBASE_CREDENTIALS_FILE` и `NOTIFY_ENV_FILE` — абсолютные пути. Второй параметр нужен, чтобы `docker compose` не искал env-файл в репозитории.
- `NOTIFY_MEMORY_LIMIT`/`NOTIFY_CPU_LIMIT` — стартовые лимиты. Меняйте их только после измерения под своей нагрузкой.

Если вы переносите уже существующую установку и хотите собрать `home.env` из заранее подготовленных файлов, используйте `scripts/prepare_home_config.py`. Скрипт не печатает значения, но рассчитан на определённую структуру приватного каталога; для новой установки проще заполнить таблицу выше вручную.

## 5. Запустите и обновите

Из корня `notifications-api`:

```powershell
$cfg = Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify\home.env'
docker compose --env-file $cfg -f compose.yml -f compose.home.yml config --quiet
docker compose --env-file $cfg -f compose.yml -f compose.home.yml up -d --build
docker compose --env-file $cfg -f compose.yml -f compose.home.yml ps
curl.exe --fail https://notify.example.com/health/live
```

`config --quiet` проверяет подстановку переменных, не печатая секреты. `up -d --build` собирает образ из текущего checkout и пересоздаёт контейнеры. Для обновления:

1. Зафиксируйте проверенный Git SHA и сделайте backup (см. ниже).
2. Пересоберите и поднимите новую версию теми же командами.
3. Проверьте `/health/ready`, `/docs` и контрольный запрос сессии.

`restart: unless-stopped` действует после запуска Docker Engine. Постоянная доступность требует, чтобы компьютер не уходил в сон, а Docker запускался при входе. Эти настройки ОС Notify не меняет.

## 6. Проверьте установку

Публичные проверки не требуют токена:

```powershell
curl.exe --fail https://notify.example.com/health/live
curl.exe --fail https://notify.example.com/health/ready
curl.exe --fail https://notify.example.com/api/config
curl.exe --fail https://notify.example.com/.well-known/assetlinks.json
```

Ожидаемо:

- `/health/live` — 200 `{"status":"alive"}`;
- `/health/ready` — 200 `{"status":"ready"}` только при настроенных SSO и Firebase; иначе 503 `configuration_required`;
- `/api/config` — версия, issuer, `capabilities` и версии протоколов;
- `assetlinks.json` — package `tech.alxprgs.notify` и отпечаток вашего APK.

Затем проверьте вход и сессию:

1. Откройте `https://notify.example.com/` и войдите через SSO. Должен произойти redirect на `auth.example.com`, затем возврат на `notify.example.com`.
2. Откройте `https://notify.example.com/docs` — Scalar должен загрузить `/openapi/v1.json` и показать справочник.
3. В браузере выполните `GET /api/v1/session` из консоли разработчика или через интерфейс Scalar с cookie сессии. Ответ должен содержать `actor.kind=web`, `actor.id=<sub>` и `csrfToken`. Без cookie тот же запрос возвращает 401 с пустым телом.
4. Привяжите телефон по инструкции Android-клиента. После подтверждения SSO телефон получит одноразовый credential; повторная выдача того же секрета невозможна.

Справочник операций, полей и кодов ошибок — `/docs` и `/openapi/v1.json`. Сценарии, подписи и протоколы — [CONTRACT](CONTRACT.md); модель доверия — [SECURITY](SECURITY.md); альтернативная установка на Ubuntu с nginx — [OPERATIONS](OPERATIONS.md).

## 7. Если что-то не работает

| Симптом | Что проверить |
|---|---|
| `/health/ready` отвечает 503 `configuration_required` | Поля `sso`/`fcm` в теле показывают причину. Заполните `Notify__OwnerSub`, `Notify__WebClientSecret` и проверьте, что файл Firebase существует и доступен контейнеру. |
| Нет HTTPS-сертификата | DNS должен указывать на публичный адрес, TCP 80/443 — доходить до Caddy, режим DNS — `DNS only`. Смотрите `docker compose logs caddy`; не отключайте ACME-check. |
| Браузер получает 401 на `/api/v1/session` | Нет cookie сессии или вход не завершён. Проверьте redirect URI в SSO: `/signin-oidc` и `/auth/step-up/callback` должны точно совпадать с `Notify__PublicUrl`. |
| Веб-POST получает 403 `invalid_origin`/`invalid_csrf` | `Notify__PublicUrl` должен точно совпадать с адресом в браузере; для POST нужны `X-CSRF-Token` из `/api/v1/session` и `Origin`. За прокси задайте `Notify__TrustedProxyNetwork`, иначе `X-Forwarded-Proto` не принимается. |
| App Link не возвращает в приложение | SHA-256 в `Notify__AndroidSigningFingerprints__0` должен совпадать с сертификатом установленного APK; проверьте `/.well-known/assetlinks.json` и domain verification на телефоне. |
| `/docs` отвечает 503 `web_build_required` | В каталоге приложения нет `wwwroot/index.html`. Соберите `web` и скопируйте `web/dist` в `wwwroot` либо используйте Docker-образ, который делает это на этапе сборки. |
| Контейнер unhealthy | HEALTHCHECK использует `/health/live`, а не readiness. Проверьте `docker compose ps` и логи контейнера; не подменяйте liveness на `/health/ready`. |

## 8. Приватные файлы и backup

В приватном каталоге хранятся:

- `home.env` — параметры и секреты; не печатайте его и не коммитьте;
- Firebase service account;
- SSO web client secret;
- UUID владельца;
- ключ подписи APK и пароль (в защищённом хранилище, отдельно от APK).

Для backup используйте SQLite Backup API, а не копирование живого `notify.db`:

```powershell
docker compose --env-file $cfg -f compose.yml -f compose.home.yml exec notify dotnet Notify.Server.dll backup /tmp/notify-backup.db
docker compose --env-file $cfg -f compose.yml -f compose.home.yml cp notify:/tmp/notify-backup.db .\notify-backup.db
```

Сохраните snapshot, каталог `/data/keys`, `/data/icons`, `/data/audio`, TLS-состояние Caddy и приватную конфигурацию. Не смешивайте новую базу со старыми Data Protection keys и не переносите старые `-wal`/`-shm` от другой базы. Подробная процедура восстановления и отката — в [OPERATIONS](OPERATIONS.md).

## 9. Ограничения и внешние проверки

- SSO, Firebase и DNS настраиваются вне Notify; без них readiness остаётся 503, а вход/привязка/push недоступны.
- Реальная доставка FCM, фоновая работа Android, системная биометрия/PIN и качество речи агента проверяются на физическом устройстве и у выбранного провайдера. Локальные тесты и симуляция SSO не заменяют эту проверку.
- Notify ретранслирует аудио звонка в памяти и не делает записей или расшифровок. Речевые модели работают у агента/провайдера.
- Один серверный процесс и одна база. Не запускайте второй экземпляр на тот же volume и не используйте общий сетевой диск для SQLite.
- Публикация APK, смена production SSO и DNS выполняются владельцем отдельно; это руководство не делает их автоматически.
