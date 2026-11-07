
import Guide from './guides';
import { ApiReferenceReact } from '@scalar/api-reference-react';
import '@scalar/api-reference-react/style.css';
import { Brand, Icon, ThemeControl, guideLinks, useTheme } from './ui';

const example = [
  'with NotifyClient(url, token) as client:',
  '    client.send("Готово", "Задача завершена",',
  '        idempotency_key="task-42-complete")',
].join('\n');

export default function Reference() {
  const { resolved } = useTheme();
  const guide = location.pathname.startsWith('/docs/guide/');
  return <div className="public-shell docs-shell">
    <header className="topbar public-topbar">
      <div className="top-start"><Brand href="/docs" /><span className="top-divider" /><a className="breadcrumb" href="/docs">Документация</a></div>
      <div className="top-actions">
        <ThemeControl /><a className="button secondary" href="/">Кабинет <Icon name="external" size={16} /></a>
      </div>
    </header>
    {guide ? <Guide /> : <div className="docs-main">
      <section className="docs-intro" aria-labelledby="docs-title">
        <p className="eyebrow"><Icon name="book" size={16} /> ДОКУМЕНТАЦИЯ ДЛЯ РАЗРАБОТЧИКОВ</p>
        <h1 id="docs-title">Подключите свой источник</h1>
        <p className="docs-lead">Создайте интеграцию в кабинете и подтвердите её на телефоне. Передавайте токен через <code>Authorization: Bearer</code>, ключ повторной отправки — через <code>Idempotency-Key</code>.</p>
        <nav className="guide-links" aria-label="Руководства">
          {guideLinks.map(([id, title]) => <a className="button secondary" key={id} href={'/docs/guide/' + id}>{title} <Icon name="arrow" size={15} /></a>)}
        </nav>
        <div className="docs-grid">
          <section className="panel doc-card"><span className="entity-icon accent"><Icon name="form" /></span><h2>Python SDK</h2><pre>{example}</pre></section>
          <section className="panel doc-card"><span className="entity-icon accent"><Icon name="agent" /></span><h2>MCP и решения</h2>
            <p>HTTP: <code>/mcp</code> с токеном интеграции. Локальный адаптер: <code>notify-mcp</code>.</p>
            <p>Согласие означает только <code>answered + yes</code>.</p></section>
          <section className="panel doc-card"><span className="entity-icon accent"><Icon name="link" /></span><h2>Webhooks</h2>
            <p>Проверяйте HMAC-SHA256 от <code>timestamp + "." + raw_body</code> и свежесть timestamp. При повторной доставке проверяйте <code>eventId</code>.</p></section>
        </div>
        <p>Доступные возможности и версии опубликованы в <a href="/api/config">конфигурации Notify</a>.</p>
      </section>
      <section className="reference-section" aria-label="Справочник методов">
        <ApiReferenceReact key={resolved} configuration={{
          url: '/openapi/v1.json', withDefaultFonts: false, showDeveloperTools: 'never',
          agent: { disabled: true }, hideClientButton: true, persistAuth: false,
          defaultOpenAllTags: false, hideDarkModeToggle: true, forceDarkModeState: resolved,
          authentication: { preferredSecurityScheme: 'Bearer' },
        }} />
      </section>
    </div>}
    <footer className="public-footer">Notify · v0.5.0 <a href="/">Кабинет Notify <Icon name="external" size={14} /></a></footer>
  </div>;
}
