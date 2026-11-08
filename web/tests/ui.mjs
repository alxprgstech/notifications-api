
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:net';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../../', import.meta.url));
const artifact = new URL('../../artifacts/ui/redesign/', import.meta.url);
const screenshots = new URL('screenshots/', artifact);
await mkdir(screenshots, { recursive: true });
const listener = createServer();
await new Promise((resolve, reject) => {
  listener.once('error', reject);
  listener.listen(0, '127.0.0.1', resolve);
});
const port = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const origin = 'http://127.0.0.1:' + port;
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toLowerCase().startsWith('notify__')));
Object.assign(env, {
  ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: origin, Notify__PublicUrl: origin,
  Notify__DataPath: fileURLToPath(new URL('data-' + Date.now() + '/', artifact)),
});
const server = spawn('dotnet', [root + 'src/Notify.Server/bin/Release/net10.0/Notify.Server.dll',
  '--webroot', root + 'web/dist'], { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
let log = '';
server.stdout.on('data', chunk => log += chunk);
server.stderr.on('data', chunk => log += chunk);
let browser;

try {
  const deadline = Date.now() + 30_000;
  while (true) {
    try { if ((await fetch(origin + '/health/live')).ok) break; } catch {}
    if (Date.now() > deadline || server.exitCode !== null) throw Error('UI test server not live');
    await new Promise(resolve => setTimeout(resolve, 200));
  }

  browser = await chromium.launch({ headless: true, executablePath: process.env.NOTIFY_UI_CHROMIUM });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, colorScheme: 'light' });
  await context.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: async value => { window.syntheticClipboard = value; } } });
  });
  const page = await context.newPage();
  const errors = [];
  const external = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (!request.url().startsWith(origin) && !request.url().startsWith('data:')) external.push(request.url());
  });
  const shot = name => page.screenshot({ path: fileURLToPath(new URL(name + '.png', screenshots)), animations: 'disabled' });
  const heading = name => page.getByRole('heading', { name, exact: true });
  const button = name => page.getByRole('button', { name, exact: typeof name === 'string' });
  const theme = () => page.getByLabel('Тема оформления');
  const waitTheme = value => page.waitForFunction(expected => document.documentElement.dataset.theme === expected, value);
  const noOverflow = async name => assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), name + ' overflows');

  await page.goto(origin);
  const login = page.getByRole('link', { name: /Войти через SSO/ });
  await login.waitFor();
  assert.equal(await login.getAttribute('href'), '/auth/login');
  await theme().selectOption('dark');
  await waitTheme('dark');
  await shot('login-dark');
  await page.reload();
  await waitTheme('dark');
  assert.equal(await theme().inputValue(), 'dark');
  await theme().selectOption('system');
  await waitTheme('light');
  await shot('login-light');
  await page.goto(origin + '/android/oauth2redirect');
  await heading('Вернитесь в приложение Notify').waitFor();

  const spec = await (await fetch(origin + '/openapi/v1.json')).json();
  assert.ok(spec.paths['/api/v1/notifications']);
  await page.goto(origin + '/docs');
  await heading('Подключите свой источник').waitFor();
  await page.getByText('ALXPRGS Notify API', { exact: true }).first().waitFor({ timeout: 20_000 });
  await shot('docs-light');
  await page.getByText('Сессия', { exact: true }).first().click();
  await page.getByText('Текущая сессия, actor и CSRF', { exact: true }).first().click();
  await page.getByText(/Нет или недействительны Bearer-токен либо cookie веб-сессии/).first().waitFor({ timeout: 10_000 });
  await theme().selectOption('dark');
  await waitTheme('dark');
  await page.waitForFunction(() => {
    const content = document.querySelector('.reference-section .references-rendered');
    return content?.textContent.includes('ALXPRGS Notify API')
      && getComputedStyle(content).getPropertyValue('--scalar-background-1').trim() === '#10141b';
  }, null, { timeout: 15_000 });
  await shot('docs-dark');
  const guides = {
    contract: 'Контракт v1', security: 'Безопасность и SSO', operations: 'Установка и эксплуатация',
    home: 'Самостоятельное размещение Notify',
  };
  for (const [id, name] of Object.entries(guides)) {
    await page.goto(origin + '/docs/guide/' + id);
    await heading(name).waitFor({ timeout: 15_000 });
    await page.getByRole('navigation', { name: 'Разделы документации' }).waitFor();
    assert.equal(await theme().inputValue(), 'dark');
  }
  await page.goto(origin + '/docs/guide/contract');
  await heading(guides.contract).waitFor();
  await shot('docs-guide-dark');
  await page.goto(origin + '/docs/guide/home');
  await heading(guides.home).waitFor();
  assert.equal(await page.getByText(/2026-11-07|Размещение владельца/).count(), 0);
  assert.equal(await page.getByRole('navigation', { name: 'Разделы документации' }).getByRole('link').count(), 5);
  await shot('docs-home-dark');
  await page.getByRole('link', { name: 'CONTRACT', exact: true }).first().click();
  await heading(guides.contract).waitFor();
  await page.goto(origin + '/docs/guide/home');
  await heading(guides.home).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await noOverflow('Mobile home guide');
  await shot('docs-home-mobile');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(origin + '/docs/guide/contract');
  await heading(guides.contract).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await noOverflow('Mobile guide');
  await shot('docs-guide-mobile');
  await page.goto(origin + '/docs');
  await page.getByText('ALXPRGS Notify API', { exact: true }).first().waitFor({ timeout: 20_000 });
  await noOverflow('Mobile API reference');
  await shot('docs-mobile');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await theme().selectOption('system');
  await waitTheme('light');

  // Browser fixtures isolate the owner flows. Notify.Specs exercises real cookie, CSRF and approvals.
  const now = Date.now();
  const liveId = 'a'.repeat(32);
  const baseId = 'b'.repeat(32);
  const fixtureToken = 'synthetic-ui-token';
  const fixtureSecret = 'synthetic-one-time-value';
  const confirmation = {
    id: liveId, sourceId: 'fixture', status: 'pending', createdAt: now - 172_800_000, previewOnly: true,
    content: { kind: 'confirmation', title: 'Подтвердить завершение задачи?',
      body: 'Агент ожидает вашего решения.', securityLevel: 'elevated', expiresAt: new Date(now + 600_000).toISOString() },
  };
  const cursor = { before: now - 7_200_000, beforeId: baseId };
  const initial = [
    {
      id: 'c'.repeat(32), sourceId: 'fixture', status: 'active', createdAt: now - 300_000, previewOnly: true,
      content: { kind: 'message', title: 'Сборка завершена', body: 'Артефакты готовы к проверке.', securityLevel: 'standard' },
    },
    {
      id: 'd'.repeat(32), sourceId: '_system', status: 'answered', createdAt: now - 3_600_000, previewOnly: true,
      content: { kind: 'confirmation', title: 'Изменение видимости источника', body: 'Содержание скрыто на экране блокировки.', securityLevel: 'elevated' },
      answer: { decision: 'yes' },
    },
    {
      id: baseId, sourceId: 'fixture', status: 'pending', createdAt: cursor.before, previewOnly: true,
      content: { kind: 'form', title: 'Просроченная форма', body: 'Параметры сборки.', securityLevel: 'standard', expiresAt: new Date(now - 60_000).toISOString() },
    },
  ];
  const sources = [
    { id: 'fixture', name: 'Агент разработки', preview: 'hidden', scopes: ['notify:send', 'agents:connect'], revoked: false },
    { id: 'other', name: 'Мониторинг', preview: 'full', scopes: ['notify:send', 'agents:connect'], revoked: false },
  ];
  const devices = [{ id: 'phone', name: 'Мой Android', revoked: false }];
  const fixtures = {
    session: { actor: { kind: 'web' }, csrfToken: fixtureToken },
    chatgpt: { url: origin + '/chatgpt/mcp', clientId: null, redirectUri: null },
    sources, devices,
    agents: [{ id: 'agent1', sourceId: 'fixture', name: 'Агент разработки', capabilities: ['text_tasks', 'realtime_audio'],
      revoked: false, online: true, lastSeenAt: now, sessions: [{ id: 'task1', name: 'Сборка' }] }],
    calls: [{ id: 'call1', sourceId: 'fixture', agentId: 'agent1', sessionId: 'task1', securityLevel: 'standard',
      startedAt: now - 60_000, status: 'ended', reason: 'ended', durationMs: 30_000, direction: 'outgoing' },
      { id: 'call2', sourceId: 'fixture', agentId: 'agent1', securityLevel: 'elevated',
        startedAt: now - 120_000, status: 'ended', reason: 'missed', durationMs: 0, direction: 'incoming' }],
  };
  const changes = [];
  let signedIn = true;
  let historyFailure = true;
  let emptyHistory = true;
  let detailReads = 0;
  let pollCount = 0;
  let activePolls = 0;
  let overlapping = false;
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.slice('/api/v1/'.length);
    if (path === 'session' && !signedIn) return route.fulfill({ status: 401, json: { code: 'unauthenticated' } });
    if (path === 'notifications') {
      if (historyFailure) return route.fulfill({ status: 503, json: { code: 'internal_error' } });
      if (emptyHistory) return route.fulfill({ json: { items: [], next: null } });
      if (url.searchParams.has('before')) {
        assert.equal(url.searchParams.get('before'), String(cursor.before));
        assert.equal(url.searchParams.get('beforeId'), cursor.beforeId);
        return route.fulfill({ json: { items: [confirmation], next: null } });
      }
      return route.fulfill({ json: { items: initial, next: cursor } });
    }
    if (path === 'notifications/' + liveId) {
      detailReads++;
      if (detailReads === 1) return route.fulfill({ status: 503, json: { code: 'internal_error' } });
      return route.fulfill({ json: { ...confirmation, previewOnly: false, content: {
        ...confirmation.content, body: 'Полный текст события получен отдельным запросом.',
      } } });
    }
    if (path === 'management') {
      assert.equal(request.method(), 'POST');
      assert.equal(request.headers()['x-csrf-token'], fixtureToken);
      changes.push(request.postDataJSON());
      pollCount = 0;
      return route.fulfill({ json: { id: 'management-' + changes.length } });
    }
    if (path.startsWith('management/')) {
      assert.equal(path, 'management/management-' + changes.length);
      if (++activePolls > 1) overlapping = true;
      try {
        const action = changes.at(-1).action;
        if (action === 'chatgpt.link') {
          fixtures.chatgpt.clientId = 'synthetic-chatgpt-client';
          return await route.fulfill({ json: { status: 'approved', result: {
            clientId: fixtures.chatgpt.clientId, clientSecret: 'synthetic-chatgpt-secret',
          } } });
        }
        if (action === 'integration.create') {
          if (++pollCount === 1) {
            await new Promise(resolve => setTimeout(resolve, 1700));
            return await route.fulfill({ json: { status: 'pending' } });
          }
          return await route.fulfill({ json: { status: 'approved', result: {
            sourceId: 'new-source', token: fixtureSecret, webhookSecret: 'synthetic-webhook-value',
          } } });
        }
        const status = action === 'device.revoke' ? 'denied' : action === 'integration.revoke' ? 'expired' : 'approved';
        if (action === 'source.update') sources[0].preview = changes.at(-1).preview;
        return await route.fulfill({ json: { status } });
      } finally { activePolls--; }
    }
    if (path === 'logout') {
      assert.equal(request.method(), 'POST');
      assert.equal(request.headers()['x-csrf-token'], fixtureToken);
      signedIn = false;
      return route.fulfill({ status: 204 });
    }
    if (path === 'chatgpt/callback' || path === 'chatgpt/disconnect') {
      assert.equal(request.method(), 'POST');
      assert.equal(request.headers()['x-csrf-token'], fixtureToken);
      if (path.endsWith('/callback')) fixtures.chatgpt.redirectUri = request.postDataJSON().redirectUri;
      else Object.assign(fixtures.chatgpt, { clientId: null, redirectUri: null });
      return route.fulfill({ status: 204 });
    }
    if (Object.hasOwn(fixtures, path)) return route.fulfill({ json: fixtures[path] });
    throw Error('Unexpected browser fixture request: ' + path);
  });

  await page.goto(origin);
  await heading('История').waitFor();
  await page.getByRole('alert').waitFor();
  assert.equal(await heading('Событий пока нет').count(), 0);
  historyFailure = false;
  await button('Повторить').click();
  await heading('Событий пока нет').waitFor();
  emptyHistory = false;
  await button('Обновить').click();
  await page.getByText('Сборка завершена', { exact: true }).waitFor();
  await page.getByText('Срок истёк', { exact: true }).waitFor();
  await shot('history-light');
  await page.emulateMedia({ colorScheme: 'dark' });
  await waitTheme('dark');
  await shot('history-dark');

  await button(/^В ожидании/).click();
  await heading('Нет ожидающих запросов').waitFor();
  await button('Загрузить ещё').click();
  const event = button(/Подтвердить завершение задачи/);
  await event.waitFor();
  assert.equal(await page.getByText('Просроченная форма', { exact: true }).count(), 0);
  await event.click();
  const detail = page.getByRole('dialog', { name: 'Событие' });
  await detail.getByRole('alert').waitFor();
  await detail.getByRole('button', { name: 'Повторить' }).click();
  await detail.getByText('Полный текст события получен отдельным запросом.').waitFor();
  await detail.getByText('Откройте Notify на телефоне, чтобы ответить').waitFor();
  await shot('history-detail-dark');
  await page.keyboard.press('Escape');
  await detail.waitFor({ state: 'hidden' });
  assert.equal(detailReads, 2);
  assert.equal(await event.evaluate(element => element === document.activeElement), true);

  await button(/^Все/).click();
  await page.setViewportSize({ width: 390, height: 650 });
  await event.scrollIntoViewIfNeeded();
  const scrollBeforeDetail = await page.evaluate(() => scrollY);
  assert.ok(scrollBeforeDetail > 0);
  await event.click();
  await detail.getByText('Полный текст события получен отдельным запросом.').waitFor();
  await detail.getByRole('button', { name: 'Закрыть' }).click();
  await detail.waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => scrollY), scrollBeforeDetail);
  await page.setViewportSize({ width: 1440, height: 1000 });

  const jump = button(/Быстрый переход/);
  const allFilter = button(/^Все/);
  await allFilter.focus();
  await page.keyboard.press('Control+k');
  await page.getByRole('combobox', { name: 'Найти раздел' }).waitFor();
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => document.activeElement?.textContent?.startsWith('Все'));
  await event.focus();
  await page.keyboard.press('Control+k');
  const search = page.getByRole('combobox', { name: 'Найти раздел' });
  await search.waitFor();
  await page.waitForFunction(() => document.activeElement?.getAttribute('role') === 'combobox');
  await search.fill('нет такого раздела');
  await page.getByText('Разделы не найдены.').waitFor();
  await search.fill('');
  await search.press('ArrowDown');
  assert.equal(await page.getByRole('option', { name: /Интеграции/ }).getAttribute('aria-selected'), 'true');
  await shot('quick-jump-dark');
  await search.press('Enter');
  await heading('Интеграции').waitFor();
  await page.waitForFunction(() => document.activeElement?.textContent?.includes('Быстрый переход'));
  await jump.click();
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => document.activeElement?.textContent?.includes('Быстрый переход'));

  await page.getByText('На связи', { exact: true }).waitFor();
  await button('Интеграции').click();
  await shot('integrations-dark');
  await button('Показывать содержание').click();
  await page.getByText('Подтвердите действие на телефоне', { exact: true }).waitFor();
  assert.equal(await button('Подключить источник').isDisabled(), true);
  await page.getByText('Действие подтверждено', { exact: true }).waitFor();
  await button('Скрывать содержание').first().waitFor();

  await button('Зарегистрировать агента').click();
  const agent = page.getByRole('dialog', { name: 'Зарегистрировать агента' });
  await agent.getByLabel('Имя агента').fill('Тестовый агент');
  await agent.getByLabel('Голосовые сообщения').check();
  await agent.getByRole('button', { name: 'Зарегистрировать с подтверждением' }).click();
  await page.getByText('Действие подтверждено', { exact: true }).waitFor();
  assert.deepEqual(changes.at(-1), { action: 'agent.register', targetId: 'other', name: 'Тестовый агент', capabilities: ['text_tasks', 'voice_messages'] });

  await button('Отозвать доступ').first().click();
  const revokeSource = page.getByRole('dialog', { name: 'Отозвать доступ' });
  await revokeSource.getByText(/Связанный агент будет отключён/).waitFor();
  await revokeSource.getByRole('button', { name: 'Отмена' }).click();
  assert.equal(changes.length, 2);
  await button('Отозвать доступ').first().click();
  await revokeSource.getByRole('button', { name: 'Запросить отзыв' }).click();
  await page.getByText('Время подтверждения истекло', { exact: true }).waitFor();

  await button('Подключить источник').click();
  const create = page.getByRole('dialog', { name: 'Подключить источник' });
  await create.getByLabel('Название источника').fill('Новый источник');
  await create.getByLabel(/Webhook/).fill('https://service.example/notify');
  await shot('integration-form-dark');
  await create.getByRole('button', { name: 'Создать с подтверждением' }).click();
  await page.getByText('Подтвердите действие на телефоне', { exact: true }).waitFor();
  const credentials = page.getByRole('dialog', { name: 'Доступ создан' });
  await credentials.getByText(fixtureSecret, { exact: true }).waitFor();
  assert.equal(overlapping, false);
  assert.equal(pollCount, 2);
  assert.equal(await page.evaluate(() => window.syntheticClipboard), undefined);
  await credentials.getByRole('button', { name: 'Скопировать' }).first().click();
  await credentials.getByText('Скопировано · Токен интеграции').waitFor();
  assert.equal(await page.evaluate(() => window.syntheticClipboard), fixtureSecret);
  assert.equal(await page.evaluate(value => JSON.stringify([localStorage, sessionStorage]).includes(value), fixtureSecret), false);
  await credentials.getByRole('button', { name: 'Я сохранил значения' }).click();
  await credentials.waitFor({ state: 'hidden' });
  assert.equal(await page.getByText(fixtureSecret, { exact: true }).count(), 0);

  await button('Подключить ChatGPT').click();
  const chatgpt = page.getByRole('dialog', { name: 'Данные подключения ChatGPT' });
  await chatgpt.waitFor();
  assert.deepEqual(changes.at(-1), { action: 'chatgpt.link' });
  assert.equal(await chatgpt.getByLabel('OAuth Client Secret').inputValue(), 'synthetic-chatgpt-secret');
  await chatgpt.getByRole('button', { name: 'Готово' }).click();
  const callback = page.getByLabel('OAuth callback из ChatGPT');
  await callback.fill('https://chatgpt.com/connector/oauth/example');
  await button('Сохранить callback').click();
  await button('Сохранить callback').waitFor();
  assert.equal(await callback.inputValue(), 'https://chatgpt.com/connector/oauth/example');
  await button('Отключить').click();
  await page.getByRole('dialog', { name: 'Отключить ChatGPT?' }).getByRole('button', { name: 'Отключить ChatGPT' }).click();
  await button('Подключить ChatGPT').waitFor();

  await button('Устройства').click();
  await heading('Мой Android').waitFor();
  await shot('devices-dark');
  await button('Отозвать').click();
  const revokeDevice = page.getByRole('dialog', { name: 'Отозвать устройство' });
  await revokeDevice.getByText('Мой Android', { exact: true }).waitFor();
  await revokeDevice.getByRole('button', { name: 'Запросить отзыв' }).click();
  await page.getByText('Действие отклонено', { exact: true }).waitFor();
  await page.getByText('Доверенное устройство', { exact: true }).waitFor();
  await button('Скрыть результат').click();

  await button('Звонки').click();
  await heading('Звонки').waitFor();
  await page.getByText(/Исходящий ·/).waitFor();
  await page.getByText(/Входящий ·/).waitFor();
  await page.getByText('30 с', { exact: true }).waitFor();
  await page.getByText('С проверкой личности', { exact: true }).waitFor();
  await shot('calls-dark');

  await page.setViewportSize({ width: 390, height: 844 });
  await noOverflow('Mobile calls');
  await button('Открыть меню').click();
  const menu = page.getByRole('dialog', { name: 'Меню Notify' });
  await menu.getByRole('link', { name: 'Документация' }).waitFor();
  await menu.getByRole('button', { name: 'Выйти' }).waitFor();
  await menu.getByLabel('Тема оформления').selectOption('light');
  await waitTheme('light');
  await shot('menu-mobile');
  await menu.getByRole('button', { name: 'История' }).click();
  await button(/^Все/).click();
  await noOverflow('Mobile history');
  await shot('history-mobile');
  await button('Открыть меню').click();
  await menu.getByRole('button', { name: 'Интеграции' }).click();
  await noOverflow('Mobile integrations');
  await shot('integrations-mobile');
  await button('Открыть меню').click();
  await menu.getByRole('button', { name: 'Устройства' }).click();
  await noOverflow('Mobile devices');
  await button('Открыть меню').click();
  await menu.getByRole('button', { name: 'Выйти' }).click();
  await login.waitFor();
  assert.equal(changes.length, 6);
  assert.deepEqual(errors, []);
  assert.deepEqual(external, [], 'Notify attempted external asset requests');
  await writeFile(new URL('result.json', artifact), JSON.stringify({
    login: true, returnPage: true, openapi: true, docs: true, guides: true, cabinet: 'synthetic fixtures',
    history: true, management: true, chatgpt: true, theme: true, keyboard: true, mobileNoOverflow: true,
    pageErrors: errors, externalRequests: external,
  }, null, 2));
  console.log('UI checks passed. Screenshots: artifacts/ui/redesign/screenshots');
} finally {
  if (browser) await browser.close();
  server.kill();
  await writeFile(new URL('server.log', artifact), log);
}
