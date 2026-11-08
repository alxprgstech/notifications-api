# Подключение Notify к ChatGPT

На одном домене Notify обслуживает кабинет, API, существующий MCP и OAuth для ChatGPT. Вход владельца использует действующий ALXPRGS SSO и callback `/signin-oidc`. ChatGPT подключается к `https://notify.alxprgs.tech/chatgpt/mcp` и выполняет операции от выделенного источника.

## Запуск Compose

Используйте существующий приватный `home.env` и те же Docker-тома. Для озвучки добавьте `compose.chatgpt.yml`. Он подключает Notify к внутреннему Silero, который использует CPU и по умолчанию ограничен 1 ГиБ памяти. Скачайте [официальный v5_6_ru.pt](https://models.silero.ai/models/tts/ru/v5_6_ru.pt) вне Git. Контейнер проверяет SHA-256 `04cc9b1d08ce38bcaa9d02c488d80d3e6806d17f7eaa7c95982f36a3cbf50b2e` перед загрузкой. Голос `baya` используется для личного некоммерческого синтеза. [Лицензия моделей Silero](https://github.com/snakers4/silero-models#licence)

```powershell
$notifyHome = Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify'
$notifyEnv = Join-Path $notifyHome 'home.env'
$env:SILERO_MODEL_FILE = Join-Path $notifyHome 'v5_6_ru.pt'
$env:NOTIFY_IMAGE = 'alxprgs-notify:chatgpt-local'
docker compose --env-file $notifyEnv -f compose.yml -f compose.home.yml -f compose.chatgpt.yml config --quiet
docker compose --env-file $notifyEnv -f compose.yml -f compose.home.yml -f compose.chatgpt.yml up -d --build
```

При обновлении сохраните SQLite и проверьте образ для отката до пересоздания контейнера. Для отката передайте его тег в `NOTIFY_IMAGE` и запустите исходный Compose с `--no-build`. Сохраняйте тома Notify и Caddy. Состояние OAuth в отдельном файле `chatgpt/oauth.db` и ключи находятся в постоянном томе Notify. При резервировании OAuth-состояния останавливайте сервис или используйте SQLite online backup.

## Подключение в кабинете

1. Войдите в кабинет Notify и откройте **Интеграции → ChatGPT**. Нажмите **Подключить ChatGPT** и подпишите подтверждение на доверенном телефоне.
2. Скопируйте OAuth Client ID и Client Secret из одноразового окна. В **ChatGPT → Plugins → Add custom MCP server** укажите адрес MCP из кабинета и выберите OAuth со статическими credentials. Введите выданную пару. [OpenAI Docs](https://developers.openai.com/api/docs/guides/custom-mcp-server)
3. Скопируйте точный redirect URI из интерфейса ChatGPT и сохраните в поле **OAuth callback из ChatGPT**. Если настройка ChatGPT требует активного OAuth-клиента до показа redirect URI, сначала сохраните предложенный в кабинете стабильный адрес `https://chatgpt.com/connector_platform_oauth_redirect`, затем замените его фактическим адресом перед авторизацией. [Правила redirect URI](https://developers.openai.com/apps-sdk/build/auth/)
4. Установите плагин, войдите через действующий Notify и подтвердите запрошенные права. Затем проверьте обычный пуш и голосовое сообщение.

Токен доступа действует 10 минут. При запрошенном `offline_access` refresh-сессия длится до 30 дней. Команда **Отключить** отзывает доступ источника сразу. Чтобы получить новый секрет, нажмите **Перевыпустить доступ** и подтвердите действие на телефоне.

## Инструменты и проверка

| Инструменты | Право |
| --- | --- |
| `send_notification`, `send_voice` | `notify:send` |
| `request_confirmation`, `request_form` | `requests:create` |
| `get_request`, `wait_for_response` | `requests:read` |
| `cancel_request` | `requests:cancel` |

`send_voice(title, text, expiresAt, idempotencyKey)` принимает до 1 000 символов русского текста. Для повторов сохраняйте исходный ключ и аргументы. При отказе Silero остаются доступны текстовые инструменты. Повышенные подтверждения требуют системной биометрии или PIN и подписи Android.

На домене Notify проверьте `/.well-known/oauth-protected-resource/chatgpt/mcp` и `/.well-known/oauth-authorization-server`. Запрос к `/chatgpt/mcp` без токена возвращает 401 с `WWW-Authenticate`. Для прежних клиентов используйте прежний `/mcp` с токеном интеграции.

Соберите решение командой `dotnet build Notify.sln -c Release` и выполните `dotnet test Notify.sln --no-build -c Release`. Для smoke установите в отдельное окружение Python пакеты командой `python -m pip install "mcp==2.3.0" "httpx==0.28.1" "cryptography==50.0.2"`. `scripts/chatgpt_smoke.py --fixture-audio` использует изолированную SQLite, OIDC-стенд, программную подпись и тестовый WAV. Передайте вместо флага `http://127.0.0.1:<порт>`, чтобы использовать реальный Silero на изолированном тестовом порту.
