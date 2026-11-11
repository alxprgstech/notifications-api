# Code Mode, Asterisk и Home Assistant

## Выбор телефона и пауза

Укажите `target: "asterisk"` для GXP1400. Значение `android` используется по умолчанию. Маршрут входит в ключ идемпотентности вызова. При повторе передавайте те же параметры и тот же ключ. При недоступном мосте Asterisk сервер возвращает `asterisk_unavailable`.

В `Notify__CallAudioStartDelayMs` задаётся пауза после готовности медиа. По умолчанию 3 000 мс, допустимо 0–10 000 мс. В разговоре она действует до первой озвучки. Распознавание речи доступно сразу после соединения. Поднятие трубки подтверждает готовность к звонку. Для повышенного доступа используйте Android и системную проверку.

## Home Assistant

[Открыть в HACS](https://my.home-assistant.io/redirect/hacs_repository/?owner=alxprgstech&repository=notifications-api&category=integration)

[Добавить интеграцию в Home Assistant](https://my.home-assistant.io/redirect/config_flow_start/?domain=alxprgs_notify)

1. Если компонент уже установлен, перейдите к настройке по кнопке «Добавить интеграцию в Home Assistant». Установка через HACS доступна после публикации компонента в основной ветке публичного репозитория. Для установки из приватного репозитория скопируйте каталог `custom_components/alxprgs_notify` в одноимённый каталог конфигурации HA и перезапустите HA.
2. В кабинете Notify создайте отдельный источник с правом `calls:announce`. Подтвердите создание на Android.
3. Откройте настройку интеграции по кнопке. Введите HTTPS-адрес Notify и токен источника в поле пароля. Для отзыва доступа отзовите источник в кабинете Notify.
4. Запустите действие `alxprgs_notify.call` из «Инструменты разработчика → Действия» или добавьте его в автоматизацию:

```yaml
actions:
  - action: alxprgs_notify.call
    data:
      text: "Проверьте датчик у входной двери."
      target: asterisk
    response_variable: notify_call
```

Действие возвращает ID и начальное состояние. `ringing` означает ожидание ответа. Текущее состояние доступно по `GET /api/v1/calls/{id}`; `playback_sent` означает передачу аудиокадров. Чтобы безопасно повторить один вызов из автоматизации после сетевого сбоя, передайте один и тот же `idempotency_key` с теми же данными.

## Asterisk

Мост требует Python 3.11+, ARI и External Media. Установите `python3-venv`. На Python 3.13 зависимости включают совместимый пакет `audioop-lts`. В Asterisk заведите отдельного пользователя ARI с `read_only=no`. Используйте ARI по loopback. Если HTTP Asterisk уже обслуживает другие приложения, сохраните действующие настройки и используйте существующий loopback-порт. Проверьте зарегистрированный endpoint командой `asterisk -rx "pjsip show contacts"`. Включите ручной ответ на GXP1400.

В кабинете Notify создайте отдельный источник с правом `telephony:bridge`. Скопируйте `bridge.py` и `requirements.txt` из `deploy/asterisk/` в `/opt/alxprgs-notify/asterisk/`. Создайте там окружение командой `python3 -m venv /opt/alxprgs-notify/asterisk/venv` и установите зависимости через `venv/bin/pip install -r requirements.txt`. Установите `notify-asterisk.service` в systemd. В файл `/etc/alxprgs-notify/asterisk.env` с правами 0600 внесите:

```dotenv
NOTIFY_URL=https://notify.example.com
NOTIFY_BRIDGE_TOKEN=
ARI_PORT=8088
ARI_USER=
ARI_PASSWORD=
ASTERISK_ENDPOINT=PJSIP/gxp1400
```

Укажите фактический endpoint и заполните секреты локально. Выполните `systemctl daemon-reload`, затем `systemctl enable --now notify-asterisk`. Мост устанавливает исходящий WSS к Notify и использует локальный RTP. Для отката остановите службу и отзовите токен моста. [Документация Asterisk ARI](https://docs.asterisk.org/Development/Reference-Information/Asterisk-Framework-and-API-Examples/External-Media-and-ARI/)

Если аппарат имеет постоянный LAN-адрес и исходящий вызов работает, а список контактов Asterisk пуст, сначала проверьте входящий звонок по явному SIP URI:

```bash
sudo asterisk -rx 'channel originate PJSIP/101/sip:101@192.168.1.221:5060 application Echo'
```

Если телефон зазвонил и эхо слышно, можно добавить `contact = sip:101@192.168.1.221:5060` в существующую секцию AOR линии `101` с `type=aor`. Подставьте свой номер, адрес и порт. Перечитайте `res_pjsip.so` и проверьте `pjsip show contacts`. [Набор по SIP URI](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/Dialing-PJSIP-Channels/) · [Статический контакт AOR](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/PJSIP-Configuration-Sections-and-Relationships/)

## Локальный Code Mode

Подключите `compose.codemode.yml` к тем же файлам Compose, с которыми работает Notify, и выполните `docker compose … up -d --build notify-codemode`. Контейнер слушает на `127.0.0.1:8787`. Worker использует Cloudflare Dynamic Workers через локальный Wrangler, а запросы к Notify идут по действующему HTTPS-адресу. Для отдельного стенда установите `NOTIFY_ORIGIN` на HTTPS-адрес стенда.

Используйте токен отдельного источника с нужными правами. `calls:start` даёт запуск озвучки и разговоров. `agents:connect` поддерживает разговор с конкретным `sessionId` своего агента. Запросы, уведомления и иконки используют свои scopes. Код внутри Worker вызывает только опубликованные методы.

Для Codex задайте `NOTIFY_CODE_MODE_TOKEN` через приватное окружение, затем зарегистрируйте:

```powershell
codex mcp add notify-codemode --url http://127.0.0.1:8787/mcp --bearer-token-env-var NOTIFY_CODE_MODE_TOKEN
```

Для DeepSeek Harness укажите `NOTIFY_CODE_MODE_URL=http://127.0.0.1:8787/mcp` в окружении процесса Harness и перезапустите его. Плагин использует сохранённый токен Notify. Вызов `notify_code` без `code` показывает типы. Для операций записи вызовите `notify_code` с `mode: "write"` и подтвердите выполнение. Чтение выполняется через `/mcp/read`.

```javascript
async () => {
  const call = await codemode.start_call({
    title: "Notify",
    text: "Проверка завершена.",
    target: "asterisk",
    idempotencyKey: "integration-test-001",
  });
  return { id: call.id, status: call.status };
}
```

Развёртывание в Cloudflare требует отдельной конфигурации с HTTPS origin и плана Workers с Dynamic Workers. [Cloudflare Code Mode MCP](https://developers.cloudflare.com/agents/model-context-protocol/guides/build-codemode-mcp-server/) · [Dynamic Workers pricing](https://developers.cloudflare.com/dynamic-workers/pricing/)
