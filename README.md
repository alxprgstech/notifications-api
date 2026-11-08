# ALXPRGS Notify

Личный сервис сообщений и подтверждений для программ и ИИ-агентов. Сервер .NET 10 объединяет REST API, MCP и фоновые задания; Android-клиент находится в соседнем `notifications-app`.

## Документация

- `/docs` — единый справочник API: операции, авторизация, поля, примеры и фактические ответы. Откройте его после локального запуска (ниже).
- [Контракт API, MCP, SDK и webhook](docs/CONTRACT.md) — сценарии, подписи и протоколы.
- [Безопасность и SSO](docs/SECURITY.md) — вход, роли, elevated-ответы и границы доверия.
- [Установка и эксплуатация](docs/OPERATIONS.md) — Docker/nginx, SSO, backup, обновление.
- [Самостоятельное размещение](docs/HOME_HOSTING.md) — воспроизводимая установка с Caddy.
- [Приватный MCP-плагин для ChatGPT](docs/CHATGPT_MCP.md) — отдельный OAuth-адаптер и локальный голос Silero.
- [Исторический журнал приёмки](docs/ACCEPTANCE.md) — что проверено локально и что требует внешней настройки; это не руководство по развёртыванию.

Справочник генерируется сервером из зарегистрированных маршрутов: `/openapi/v1.json`. Если описание расходится с поведением, приоритет имеет проверенный HTTP-контракт и тест; сообщите о расхождении, не подстраивайте клиент под устаревший текст.

## Состояние 0.5.0

Реализованы сообщения, подтверждения да/нет/свой вариант, структурированные формы с подписью набора значений, голосовые сообщения Android ↔ агент с двухфазной загрузкой WAV, живые интернет-звонки в обе стороны (телефон → агент и агент → телефон с data-only FCM-приглашением), бинарный relay v2 с generation/barge-in, повышенные ответы с подписью устройства, SSO-привязка и восстановление, FCM HTTP v1, повторяемые подписанные webhooks, подключение агентов по исходящему WebSocket, кабинет, OpenAPI/Scalar, Python SDK и MCP HTTP/stdio.

Проверки на настоящем Firebase, production SSO, физическом телефоне и реальном речевом провайдере агента требуют конфигурации владельца; наличие кода и локальных тестов их не заменяет. Актуальные результаты — [приёмка](docs/ACCEPTANCE.md).

## Локальная сборка

Нужны .NET SDK 10.0.401, Node 24.20.0 и Python 3.11+.

```powershell
dotnet restore Notify.sln --locked-mode
dotnet test Notify.sln --no-restore -c Release
cd web
npm ci --ignore-scripts
npm run build
cd ..
dotnet publish src/Notify.Server -c Release -o artifacts/server
New-Item -ItemType Directory -Force artifacts/server/wwwroot
Copy-Item web/dist/* artifacts/server/wwwroot -Recurse -Force
```

Если `wwwroot` отсутствует, сначала создайте его. Для локального сервера используйте отдельный каталог данных, не общий сетевой диск:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8085'
$env:Notify__PublicUrl = 'http://127.0.0.1:8085'
$env:Notify__DataPath = "$PWD/data"
dotnet artifacts/server/Notify.Server.dll
```

Откройте `/docs`. Без SSO/Firebase `/health/ready` отвечает 503 с `configuration_required`; защищённые функции не получают обходного тестового входа. Для настоящего подключения следуйте [инструкции настройки](docs/OPERATIONS.md).

```powershell
python -m venv .venv
.venv/Scripts/python -m pip install -c packages/python-sdk/requirements-test-lock.txt './packages/python-sdk[mcp]'
.venv/Scripts/python -m unittest discover -s packages/python-sdk/tests -v
```

Передайте `NOTIFY_URL` и `NOTIFY_TOKEN` процессу агента через приватное окружение. Не вставляйте реальные значения в команды из истории терминала или переписку. Пример: `python examples/agent.py`; повышенный сценарий выбирается `NOTIFY_LEVEL=elevated`; форма — `python examples/form.py`.

## Подключение

Веб-кабинет использует SSO-cookie и CSRF. Отдельный токен интеграции создаётся только после подтверждения доверенным телефоном. SDK предназначен отправителям, он не умеет отвечать вместо пользователя.

Текущая сессия и CSRF: `GET /api/v1/session`. Справочник операций — `/docs`; сценарии и протоколы — [CONTRACT](docs/CONTRACT.md); модель безопасности — [SECURITY](docs/SECURITY.md); развёртывание — [OPERATIONS](docs/OPERATIONS.md).

План дальнейших работ для DeepSeek Harness остаётся внутренним: [HARNESS_PLAN](docs/HARNESS_PLAN.md).

Лицензия на публичное распространение не предоставлена. CI готовит локальные/приватные артефакты и ничего не развёртывает.
