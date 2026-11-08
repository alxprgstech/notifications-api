# Приватный плагин Notify для ChatGPT

OAuth-адаптер принимает MCP-запросы ChatGPT, проверяет вход владельца через ALXPRGS SSO и вызывает REST Notify от отдельного источника. Docker Compose запускает адаптер и локальный Silero рядом с Notify. Android использует существующие экраны уведомлений, форм и повышенных подтверждений.

## Подготовка

1. Подготовьте рабочий Notify по [руководству домашнего размещения](HOME_HOSTING.md). Выберите отдельный HTTPS-хост для адаптера, например `notify-mcp.example.com`. Направьте DNS на Caddy и проверьте доступность 443. Используйте один экземпляр адаптера на один выделенный источник.
2. Зарегистрируйте в ALXPRGS SSO отдельный confidential OIDC-клиент со scope `openid`, Authorization Code с PKCE S256 и точным callback `https://notify-mcp.example.com/signin-oidc`. Укажите точный `sub` владельца. В кабинете Notify создайте отдельный источник и подтвердите его на доверенном телефоне. Выдайте только `notify:send`, `requests:create`, `requests:read`, `requests:cancel`.
3. Скачайте [официальный v5_6_ru.pt](https://models.silero.ai/models/tts/ru/v5_6_ru.pt) в приватный каталог. SHA-256 должен быть `04cc9b1d08ce38bcaa9d02c488d80d3e6806d17f7eaa7c95982f36a3cbf50b2e`. Контейнер сверяет файл перед загрузкой. Модель и голос `baya` используются для личного некоммерческого синтеза. [Лицензия моделей Silero](https://github.com/snakers4/silero-models#licence)
4. В приватном env-файле Compose укажите дополнительно к параметрам Notify:

   ```dotenv
   CHATGPT_DOMAIN=notify-mcp.example.com
   CHATGPT_ENV_FILE=/private/notify-chatgpt.env
   SILERO_MODEL_FILE=/private/v5_6_ru.pt
   ```

   Создайте `CHATGPT_ENV_FILE` вне репозитория. Ограничьте его чтение пользователем, который запускает Compose.

   ```dotenv
   ChatGpt__SsoIssuer=https://auth.example.com
   ChatGpt__SsoClientId=notify-chatgpt
   ChatGpt__SsoClientSecret=<secret-from-sso>
   ChatGpt__OwnerSub=<exact-owner-sub>
   ChatGpt__ClientId=notify-chatgpt-plugin
   ChatGpt__ClientSecret=<independently-generated-random-secret>
   ChatGpt__ClientRedirect=<exact-callback-from-chatgpt>
   ChatGpt__NotifyToken=<dedicated-notify-integration-token>
   ChatGpt__NotifySourceId=<dedicated-notify-source-id>
   ```

## Запуск и подключение

Откройте ChatGPT Plugins → Add custom MCP server. Введите `https://notify-mcp.example.com/mcp`, выберите OAuth со статическими credentials и используйте `ChatGpt__ClientId` и `ChatGpt__ClientSecret`. Скопируйте точный redirect URI, который показывает ChatGPT, в `ChatGpt__ClientRedirect`. Если интерфейс сначала требует доступный сервер, запустите адаптер с документированным стабильным callback `https://chatgpt.com/connector_platform_oauth_redirect` для обнаружения метаданных. После получения точного URI обновите конфигурацию и пересоздайте сервис `chatgpt` перед авторизацией. [OpenAI Docs](https://developers.openai.com/api/docs/guides/custom-mcp-server) · [Правила redirect URI](https://developers.openai.com/apps-sdk/build/auth/)

```powershell
docker compose --env-file <private-home.env> -f compose.yml -f compose.home.yml -f compose.chatgpt.yml up -d --build
```

Проверьте `/health/live`, `/.well-known/oauth-protected-resource/mcp` и `/.well-known/oauth-authorization-server` на хосте адаптера. Неавторизованный `GET /mcp` возвращает 401 с `WWW-Authenticate`. При подключении войдите в SSO, проверьте список прав на экране согласия и подтвердите подключение. Для обновляемой сессии клиент запрашивает `offline_access`; токен доступа действует 10 минут, refresh-сессия до 30 дней.

Caddy подключён к адаптеру по выделенной сети `172.30.89.0/28`. Если подсеть занята, согласованно измените CIDR, адреса обоих сервисов и `ChatGpt__TrustedProxy` в Compose. Порты Silero и адаптера наружу не пробрасываются. Держите access-лог на этом хосте выключенным. Compose по умолчанию ограничивает Silero одним CPU и 1 ГиБ RAM. При необходимости задайте `SILERO_CPU_LIMIT` и `SILERO_MEMORY_LIMIT` в приватном файле Compose. Занятый или недоступный Silero возвращает ошибку голосового инструмента. Текстовые операции доступны независимо от Silero.

## Инструменты

| Инструменты | Право |
| --- | --- |
| `send_notification`, `send_voice` | `notify:send` |
| `request_confirmation`, `request_form` | `requests:create` |
| `get_request`, `wait_for_response` | `requests:read` |
| `cancel_request` | `requests:cancel` |

`send_voice(title, text, expiresAt, idempotencyKey)` принимает русский текст до 1 000 символов и срок в пределах 30 дней. Ключ операции содержит от 8 до 128 печатных ASCII-символов без пробелов. Notify получает стандартное голосовое уведомление с заголовком и аудио. Текст передаётся только внутреннему сервису Silero. Для повтора сохраняйте исходный ключ и аргументы. Временный WAV хранится по правилам Notify. В ожидании решения состояние `pending` означает, что владелец ещё не ответил.

## Отзыв и откат

Для немедленного отключения инструментов отзовите выделенный источник в кабинете Notify. Для отключения OAuth-доступа уберите публикацию адаптера и остановите сервисы `chatgpt` и `silero`. Endpoint `/oauth/revoke` позволяет зарегистрированному OAuth-клиенту отозвать конкретный токен. Для полного сброса всех выданных OAuth-данных остановите адаптер, сохраните резервную копию при необходимости и пересоздайте только приватный том `chatgpt_data`. Том Notify содержит уведомления и данные устройств. После смены выделенного источника используйте новый том адаптера, чтобы его журнал повторов соответствовал этому источнику.

При откате уберите `compose.chatgpt.yml` из команды и пересоздайте Caddy базовыми Compose-файлами. Не используйте `docker compose down -v` для общего проекта. Оставшийся токен источника и OIDC-клиент отзываются отдельно.

## Проверка

Сборка и тесты выполняются командой `dotnet test Notify.sln -c Release`. После Release-сборки `scripts/chatgpt_smoke.py http://127.0.0.1:<порт>` проверяет MCP и обмен OAuth с изолированным OIDC-стендом. Передайте loopback-адрес контейнера Silero, опубликованный только для теста. Для прогона с контрольным WAV вместо модели используйте `--fixture-audio`. Используйте окружение Python из smoke Notify SDK с `cryptography`, `httpx` и `mcp`. Скрипт запускает отдельные процессы Notify и адаптера и использует синтетические SQLite и ключи. Он проверяет WAV, потерю ответа после создания уведомления, параллельный повтор, недоступность Silero и подпись P-256 на программном ключе. Живые SSO, ChatGPT и Android с биометрией требуют отдельной внешней приёмки.
