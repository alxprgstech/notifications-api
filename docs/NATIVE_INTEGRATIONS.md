# Нативные агенты Notify

Agent v1 и Call v2 сохраняются. Серверные call-события по WSS агента используют v1-конверты. Клиентские call-команды объявляют v2. Элементы sessions в hello и heartbeat принимают необязательные булевы busy и conversationOnly. Ответ GET /api/v1/agents дополнительно возвращает phoneAssistantExtension для владельца.

Для маршрутизации нескольких SIP-номеров используйте массив PhoneAssistantRoutes (Extension, AgentId) и общий PhoneAssistantBridgeSourceId. Повтор Extension отклоняется при запуске. Legacy PhoneAssistantExtension и PhoneAssistantAgentId можно сохранить для отдельного старого номера. Одной записи моста разрешено несколько номеров в том же доверенном SIP-контексте. Медиатракт остаётся один на физический endpoint. Повтор incoming с тем же requestId на живом соединении моста возвращает исходный ack.

Источник с правом озвучивания может отменить свой активный announcement через POST /api/v1/calls/{id}/cancel-announcement. Этим маршрутом не завершаются чужие, аппаратные и обычные разговоры. Контракт указан в OpenAPI.

Dry-run выводит проверяемый dialplan-фрагмент через deploy/asterisk/render-assistant-route.py. Используйте общее руководство MIGRATION.md в codex-notify для резервного копирования, стадий переключения и восстановления.

deploy/asterisk/migrate-assistant-files.py подготавливает приватный снимок изменений, проверяет состояние файлов и выполняет файловые apply и rollback. Он рассчитан на существующий доверенный SIP-контекст и не запускает systemctl или Asterisk. Репетиция на копиях проходит командой python3 -B -m unittest discover -s deploy/asterisk -p test_route_config.py. Порядок применения на сервере указан в MIGRATION.md.

Контрактные сценарии NativeCodexConnectorAnswersTrustedSipThroughTheServerProtocol и NativeHarnessConnectorAnswersTrustedSipThroughTheServerProtocol используют Notify API в TestServer, временную SQLite, синтетические реплики и Node-обработчики соседних репозиториев через stdio. Задайте абсолютные пути к tests/fixtures/notify-contract-peer.mjs в переменных NOTIFY_CODEX_CONTRACT_PEER и NOTIFY_HARNESS_CONTRACT_PEER соответственно. Нужен Node 24 в PATH. Перед проверкой Harness выполните npm run build в его checkout. Сценарии проводят вызов 8260 или 8261 при доступном втором имитированном агенте. Harness дополнительно проверяет доставку реплики в выделенный чат через адаптер событий Host.
