# Notify на домашнем Windows-ПК

Размещение владельца с 2026-11-07: Docker Desktop, OpenWrt с TCP 80/443, Cloudflare DNS only. SSO остаётся на EC2; код SSO не менялся.

## Запуск и обновление

Из корня notifications-api:

```powershell
$cfg = Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify\home.env'
docker compose --env-file $cfg -f compose.yml -f compose.home.yml config --quiet
docker compose --env-file $cfg -f compose.yml -f compose.home.yml up -d --build
docker compose --env-file $cfg -f compose.yml -f compose.home.yml ps
curl.exe --fail https://notify.alxprgs.tech/health/ready
```

API доступен только на host loopback 8085. Caddy публикует HTTP/HTTPS, автоматически получает/обновляет TLS и проксирует WebSocket/MCP. Access log отключён; URI и чувствительные заголовки исключены из error logs. Данные и сертификаты находятся в постоянных Docker volumes. Не используйте `down -v` для обновления.

Для веб-сессии укажите в `home.env` параметр `Notify__TrustedProxyNetwork` с CIDR отдельной сети Compose, в которой работают Caddy и Notify. Узнать текущую подсеть можно командой `docker network inspect alxprgs-notify_default --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'`. После пересоздания сети проверьте CIDR перед запуском Notify. Приложение принимает `X-Forwarded-Proto` только от этой сети и выпускает CSRF-cookie с атрибутом `Secure`.

`restart: unless-stopped` действует после запуска Docker engine. Для постоянной доступности нужны работающий ПК без сна и автозапуск Docker Desktop при входе владельца. Эти настройки Windows автоматически не менялись.

## Приватные файлы

Каталог `%LOCALAPPDATA%\ALXPRGS\Notify` находится вне Git/OneDrive:

- `home.env` — рабочие параметры. Не печатать через `docker compose config` без `--quiet`.
- `firebase-service-account.json` — ключ FCM; `sso-web.json` — Web client ID/secret.
- `owner-sub.txt` — UUID владельца; `signing` — ключ APK, DPAPI-защищённый пароль и публичный сертификат.

Первичная генерация: `scripts/prepare_home_config.py --android-client-id <зарегистрированный-public-client-id>`. Не меняйте владельца для уже привязанной БД. Backup/restore — [OPERATIONS](OPERATIONS.md); дополнительно сохраняйте Caddy volumes и приватную операторскую конфигурацию.

## Сеть и SSO

DNS A `notify.alxprgs.tech` указывает на WAN роутера, DNS only. TCP 80/443 направляются на постоянный LAN-адрес ПК. UDP не нужен: используются HTTP/1.1, HTTP/2 и WebSocket по TCP. DHCP-привязку владелец выполнял вручную в своём браузере; её сохранение здесь не проверялось.

На SSO включён `FEATURE_EXTERNAL_STEP_UP_ENABLED=true`. Владелец выполнил `scripts/enable_sso_step_up.py --apply` через свой SSH: агент 1Password не подписывал запросы из процесса Codex. Скрипт использует блокировку штатного оператора, root-only backup runtime.env/app.env, меняет один ключ и пересоздаёт backend текущего release без деплоя образов/миграций. Публичный discovery независимо подтвердил true.

Public Android client использует PKCE и callback `/android/oauth2redirect`. Confidential Web client имеет scope openid, callbacks `/signin-oidc` и `/auth/step-up/callback`, assurance `reauth_password_mfa`, действия `notify.enroll_device`/`notify.recover_devices`, TTL 300 секунд.

## APK и проверки 2026-11-07

В notifications-app скрипт `scripts/build_personal_release.ps1` собирает release с реальным app/google-services.json, сохраняет существующий ключ и проверяет подпись. APK: `app/build/outputs/apk/release/app-release.apk`. Сохраните PKCS12 и пароль отдельно в личном безопасном хранилище: password.clixml привязан к Windows-пользователю через DPAPI и не заменяет переносимый backup пароля.

- Release и 10 JVM tests PASS; apksigner подтвердил APK v2.
- Compose/Caddy validation/build/start PASS; Notify healthy, публичный HTTPS readiness 200 с валидным TLS; HTTP перенаправляет на HTTPS/443. ACME challenge успешно проверен внешним CA.
- Assetlinks доступен по HTTPS, package и SHA-256 совпадают с APK; domain verification на телефоне ещё не проверена.
- Web OIDC redirect: правильный SSO/callback и PKCE S256; анонимный API возвращает 401.
- Google OAuth принимает service account для scope FCM. Доставка на устройство ещё не проверена.
- Web build и Playwright PASS: четыре guide routes, кабинет на fixtures, обе темы и mobile. Markdown-ссылки между опубликованными руководствами ведут на соответствующие веб-страницы.
- Краткий срез: Notify ~73.7 MiB/192 MiB, Caddy ~15.0 MiB/128 MiB; это не новый десятиминутный нагрузочный прогон.

Артефакты: artifacts/home-deployment/verification.json и firebase-oauth.json. ADB-устройства нет. Реальные вход/привязка, FCM в фоне, биометрия/PIN и ИИ-диалог остаются ручной приёмкой. Браузерная автоматизация прекращена по просьбе владельца.
