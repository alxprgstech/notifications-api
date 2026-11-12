
import { useCallback, useEffect, useRef, useState } from 'react';
import { Brand, Dialog, Icon, Notice, QuickJump, ThemeControl, sections, type Section } from './ui';
import {
  Calls, Credentials, Devices, EventDetail, History, Integrations, ManagementDialog,
  type Agent, type Call, type Cursor, type Device, type HistoryPage, type ManagementForm,
  type ManagementInput, type Notification, type Source,
} from './cabinet-sections';

type Session = { csrfToken: string };
type Detail = { preview: Notification; full?: Notification; error: string };
type Secret = { token: string; webhookSecret?: string; sourceId?: string };
type Outcome = { tone: 'success' | 'warning' | 'danger'; title: string; text?: string };
type Approval = { id: string; deadline: number; action: ManagementInput['action'] };
type ChatGptCredentials = { clientId: string; clientSecret: string };
type ChatGptStatus = {
  url: string; clientId: string | null; redirectUri: string | null; defaultRedirectUri: string;
  sourceId: string | null; scopes: string[]; permissionsRevision: number;
  availablePermissions: { scope: string; label: string }[];
};
type Resources = {
  history: HistoryPage; sources: Source[]; devices: Device[]; agents: Agent[]; calls: Call[];
};

const errors: Record<string, string> = {
  trusted_device_required: 'Сначала привяжите доверенный телефон через приложение.',
  webhook_not_allowlisted_https: 'Укажите HTTPS-адрес webhook, разрешённый в настройках сервера.',
  unauthenticated: 'Сессия завершена. Войдите снова.',
  invalid_csrf: 'Обновите страницу и повторите действие.',
  invalid_origin: 'Откройте Notify по основному адресу и повторите действие.',
  agent_already_registered: 'Для этого источника уже зарегистрирован агент.',
  agent_registration_pending: 'Запрос на регистрацию этого агента уже ожидает подтверждения.',
  invalid_chatgpt_callback: 'Введите точный HTTPS callback из настроек подключения ChatGPT.',
  chatgpt_link_changed: 'Подключение изменилось. Обновите кабинет и повторите действие.',
  chatgpt_permissions_changed: 'Разрешения ChatGPT уже изменились. Проверьте текущий набор и повторите действие.',
  source_permissions_changed: 'Права источника уже изменились. Проверьте текущий набор и повторите действие.',
};

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch('/api/v1' + path, { credentials: 'same-origin', ...options });
  if (response.status === 204) return undefined as T;
  const value = await response.json().catch(() => null);
  if (!response.ok || value === null) throw new ApiError(response.status, value?.code);
  return value as T;
}

class ApiError extends Error {
  constructor(readonly status: number, code?: string) {
    super(status === 401 ? errors.unauthenticated
      : errors[code ?? ''] ?? 'Не удалось выполнить запрос. Попробуйте ещё раз.');
  }
}

function errorText(error: unknown) {
  return error instanceof ApiError ? error.message : 'Не удалось связаться с Notify. Проверьте подключение и повторите попытку.';
}

function AuthScreen({ loading, error, retry }: { loading: boolean; error: string; retry: () => void }) {
  return <div className="public-shell">
    <header className="topbar public-topbar"><Brand /><div className="top-actions">
      <a className="button plain" href="/docs" aria-label="Документация"><Icon name="book" size={18} /> <span>Документация</span></a><ThemeControl />
    </div></header>
    <main className="auth-main">
      <section className="auth-panel">
        <div className="eyebrow"><span /> ЛИЧНОЕ ПРОСТРАНСТВО</div>
        <h1>События и решения<br />в одном месте</h1>
        <p>Получайте уведомления от сервисов и агентов. Важные действия подтверждайте в Notify на телефоне.</p>
        {error && <Notice title={error} tone="danger"><button className="button secondary" onClick={retry}>Проверить снова</button></Notice>}
        {loading ? <p role="status" className="auth-loading">Проверяем сессию…</p>
          : <a className="button primary auth-cta" href="/auth/login">Войти через SSO <Icon name="external" size={18} /></a>}
        <small className="auth-security"><Icon name="shield" size={17} /> Вход через ALXPRGS SSO</small>
      </section>
      <div className="auth-visual" aria-hidden="true">
        <div className="visual-grid" />
        <div className="visual-stack">
          <div className="visual-heading"><span className="visual-dot" /> Рабочее пространство <span>Notify</span></div>
          <div className="visual-card visual-first"><span className="entity-icon accent"><Icon name="message" /></span>
            <div><small>СООБЩЕНИЕ</small><strong>Задача завершена</strong></div><Icon name="decision" size={17} /></div>
          <div className="visual-card visual-second"><span className="entity-icon accent"><Icon name="shield" /></span>
            <div><small>ПОДТВЕРЖДЕНИЕ</small><strong>Ожидает вашего решения</strong></div><Icon name="clock" size={17} /></div>
          <div className="visual-card visual-third"><span className="entity-icon"><Icon name="phone" /></span>
            <div><small>УСТРОЙСТВО</small><strong>Подтверждение на телефоне</strong></div></div>
        </div>
      </div>
    </main>
    <footer className="public-footer">ALXPRGS Notify <a href="/docs">Документация и подключение <Icon name="external" size={14} /></a></footer>
  </div>;
}

export function Cabinet() {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    request<Session>('/session', { signal: controller.signal }).then(value => {
      if (!controller.signal.aborted) setSession(value);
    }).catch(reason => {
      if (controller.signal.aborted) return;
      if (!(reason instanceof ApiError && reason.status === 401)) setError(errorText(reason));
      setSession(null);
    });
    return () => controller.abort();
  }, [attempt]);
  if (!session) return <AuthScreen loading={session === undefined} error={error} retry={() => {
    setError(''); setSession(undefined); setAttempt(value => value + 1);
  }} />;
  return <Workspace session={session} expire={() => { setError(errors.unauthenticated); setSession(null); }} />;
}

function Workspace({ session, expire }: { session: Session; expire: () => void }) {
  const [tab, setTab] = useState<Section>('history');
  const [filter, setFilter] = useState<'all' | 'pending'>('all');
  const [resources, setResources] = useState<Resources>({
    history: { items: [], next: null }, sources: [], devices: [], agents: [], calls: [],
  });
  const [failures, setFailures] = useState<Partial<Record<keyof Resources, string>>>({});
  const [loading, setLoading] = useState(true);
  const loadingRef = useRef(false);
  const queuedRefresh = useRef(false);
  const [menu, setMenu] = useState(false);
  const [quick, setQuick] = useState(false);
  const [form, setForm] = useState<ManagementForm | null>(null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const detailRequest = useRef<AbortController | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [approval, setApproval] = useState<Approval | null>(null);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [secret, setSecret] = useState<Secret | null>(null);
  const [chatgptSecret, setChatgptSecret] = useState<ChatGptCredentials | null>(null);
  const [chatgptRevision, setChatgptRevision] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  const mutation = useRef(false);
  const mounted = useRef(true);
  const quickTrigger = useRef<HTMLButtonElement>(null);
  const menuTrigger = useRef<HTMLButtonElement>(null);
  const quickOrigin = useRef<HTMLElement | null>(null);
  const expireRef = useRef(expire);
  expireRef.current = expire;

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; detailRequest.current?.abort(); };
  }, []);

  const api = useCallback(async <T,>(path: string, body?: unknown, signal?: AbortSignal) => {
    try {
      return await request<T>(path, body === undefined ? { signal } : {
        method: 'POST', signal,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
        body: JSON.stringify(body),
      });
    } catch (error) {
      if (mounted.current && error instanceof ApiError && error.status === 401) expireRef.current();
      throw error;
    }
  }, [session.csrfToken]);

  const load = useCallback(async function refresh(cursor?: Cursor, afterApproval = false) {
    if (loadingRef.current) {
      if (afterApproval) queuedRefresh.current = true;
      return;
    }
    loadingRef.current = true;
    setLoading(true);
    async function fetchResource<K extends keyof Resources>(key: K, path: string, merge?: (old: Resources[K], value: Resources[K]) => Resources[K]) {
      try {
        const value = await api<Resources[K]>(path);
        if (!mounted.current) return;
        setResources(old => ({ ...old, [key]: merge ? merge(old[key], value) : value }));
        setFailures(old => ({ ...old, [key]: undefined }));
      } catch (error) {
        if (mounted.current) setFailures(old => ({ ...old, [key]: errorText(error) }));
      }
    }
    try {
      if (cursor) {
        const query = new URLSearchParams({ before: String(cursor.before), beforeId: cursor.beforeId });
        await fetchResource('history', '/notifications?' + query, (old, next) => {
          const seen = new Set(old.items.map(item => item.id));
          return { next: next.next, items: [...old.items, ...next.items.filter(item => !seen.has(item.id))] };
        });
      } else {
        await Promise.all([
          fetchResource('history', '/notifications'), fetchResource('sources', '/sources'),
          fetchResource('devices', '/devices'), fetchResource('agents', '/agents'), fetchResource('calls', '/calls'),
        ]);
      }
    } finally {
      loadingRef.current = false;
      if (mounted.current && queuedRefresh.current) {
        queuedRefresh.current = false;
        void refresh();
      } else if (mounted.current) setLoading(false);
    }
  }, [api]);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (!approval) return;
    const { id, deadline, action } = approval;
    let live = true;
    let timer: number;
    function finish(result: Outcome) {
      mutation.current = false;
      setOutcome(result);
      setApproval(null);
    }
    async function poll() {
      try {
        const state = await api<{ status: string; result?: Partial<Secret & ChatGptCredentials> }>('/management/' + encodeURIComponent(id));
        if (!live) return;
        if (state.status === 'approved') {
          if (state.result?.token) setSecret({ ...state.result, token: state.result.token });
          if (state.result?.clientId && state.result.clientSecret)
            setChatgptSecret({ clientId: state.result.clientId, clientSecret: state.result.clientSecret });
          if (action === 'chatgpt.link' || action === 'chatgpt.permissions.update') setChatgptRevision(value => value + 1);
          if ((action === 'integration.create' && !state.result?.token) ||
              (action === 'chatgpt.link' && !state.result?.clientSecret)) {
            finish({ tone: 'warning', title: 'Доступ создан, одноразовые данные недоступны' });
          } else {
            finish({ tone: 'success', title: 'Действие подтверждено' });
          }
          void load(undefined, true);
        } else if (state.status === 'conflict' && action === 'chatgpt.permissions.update') {
          setChatgptRevision(value => value + 1);
          finish({ tone: 'warning', title: 'Разрешения ChatGPT уже изменились', text: 'Проверьте текущие права и отправьте новый запрос.' });
        } else if (state.status === 'conflict' && action === 'integration.permissions.update') {
          finish({ tone: 'warning', title: errors.source_permissions_changed });
          void load(undefined, true);
        } else if (state.status === 'denied' || state.status === 'rejected') {
          finish({ tone: 'warning', title: 'Действие отклонено', text: 'Изменения не применены.' });
        } else if (state.status === 'expired' || (state.status === 'pending' && Date.now() >= deadline)) {
          finish({ tone: 'warning', title: 'Время подтверждения истекло', text: 'При необходимости отправьте новый запрос.' });
        } else if (state.status === 'pending') {
          timer = window.setTimeout(poll, 1500);
        } else {
          finish({ tone: 'danger', title: 'Не удалось определить результат подтверждения' });
        }
      } catch (error) {
        if (!live) return;
        if (error instanceof ApiError && error.status < 500) {
          finish({ tone: 'danger', title: errorText(error) });
        } else if (Date.now() >= deadline) {
          finish({ tone: 'danger', title: 'Не удалось проверить результат подтверждения', text: 'Обновите кабинет и проверьте состояние объекта.' });
        } else {
          setOutcome({ tone: 'warning', title: 'Связь прервалась. Проверим результат ещё раз.' });
          timer = window.setTimeout(poll, 3000);
        }
      }
    }
    timer = window.setTimeout(poll, 1500);
    return () => { live = false; clearTimeout(timer); };
  }, [api, approval, load]);

  const openQuick = useCallback((origin: HTMLElement | null) => {
    quickOrigin.current = origin;
    setMenu(false);
    setQuick(true);
  }, []);

  const closeQuick = useCallback(() => {
    setQuick(false);
    requestAnimationFrame(() => {
      const visible = (element: HTMLElement | null) => element?.isConnected && element.getClientRects().length;
      const target = quickOrigin.current;
      const fallback = quickTrigger.current;
      (visible(target) ? target : visible(fallback) ? fallback : menuTrigger.current)?.focus();
      quickOrigin.current = null;
    });
  }, []);

  useEffect(() => {
    function shortcut(event: KeyboardEvent) {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.key.toLowerCase() !== 'k') return;
      if (form || detail || secret || chatgptSecret || quick) return;
      event.preventDefault();
      const focus = menu ? menuTrigger.current : document.activeElement;
      openQuick(focus instanceof HTMLElement && focus !== document.body ? focus : null);
    }
    window.addEventListener('keydown', shortcut);
    return () => window.removeEventListener('keydown', shortcut);
  }, [form, detail, secret, chatgptSecret, quick, menu, openQuick]);

  function navigate(section: Section) {
    if (tab !== section) window.scrollTo({ top: 0 });
    setTab(section);
    setMenu(false);
  }

  function openDetail(preview: Notification) {
    detailRequest.current?.abort();
    const controller = new AbortController();
    detailRequest.current = controller;
    setDetail({ preview, error: '' });
    api<Notification>('/notifications/' + encodeURIComponent(preview.id), undefined, controller.signal).then(full => {
      if (!controller.signal.aborted) setDetail({ preview, full, error: '' });
    }).catch(error => {
      if (!controller.signal.aborted) setDetail({ preview, error: errorText(error) });
    });
  }

  async function manage(input: ManagementInput) {
    if (mutation.current || signingOut || secret || chatgptSecret) return;
    mutation.current = true;
    setForm(null);
    setSubmitting(true);
    setOutcome(null);
    try {
      const result = await api<{ id: string }>('/management', input);
      if (mounted.current) setApproval({ id: result.id, deadline: Date.now() + 300_000, action: input.action });
    } catch (error) {
      mutation.current = false;
      if (mounted.current) {
        if (input.action === 'chatgpt.permissions.update' && error instanceof ApiError && error.status === 409)
          setChatgptRevision(value => value + 1);
        if (input.action === 'integration.permissions.update' && error instanceof ApiError && error.status === 409)
          void load(undefined, true);
        setOutcome({ tone: 'danger', title: errorText(error) });
      }
    } finally {
      if (mounted.current) setSubmitting(false);
    }
  }

  async function logout() {
    setSigningOut(true);
    try {
      await api('/logout', {});
      location.assign('/');
    } catch (error) {
      if (mounted.current) {
        setSigningOut(false);
        setMenu(false);
        setOutcome({ tone: 'danger', title: errorText(error) });
      }
    }
  }

  const current = sections.find(section => section.id === tab)!;
  const failure = failures[tab] || (tab === 'sources' ? failures.agents : undefined);
  const busy = submitting || !!approval || !!secret || !!chatgptSecret || signingOut;
  const { history, sources, agents, devices, calls } = resources;
  const navigation = <nav aria-label="Основная навигация" className="section-nav">
    {sections.map(section => <button key={section.id} type="button"
      aria-current={tab === section.id ? 'page' : undefined} onClick={() => navigate(section.id)}>
      <Icon name={section.icon} /><span>{section.title}</span>{tab === section.id && <Icon name="arrow" size={15} />}
    </button>)}
  </nav>;
  const docsLink = <a className="sidebar-link" href="/docs"><Icon name="book" /> Документация <Icon name="external" size={14} /></a>;

  return <div className="workspace-shell">
    <a className="skip-link" href="#workspace-main">Перейти к содержимому</a>
    <header className="topbar app-topbar">
      <div className="top-start"><Brand /><span className="top-divider" /><span className="breadcrumb">Кабинет <Icon name="arrow" size={14} /> <strong>{current.title}</strong></span></div>
      <div className="top-actions">
        <button ref={quickTrigger} className="button secondary quick-trigger" onClick={event => openQuick(event.currentTarget)}>
          <Icon name="search" size={18} /> Быстрый переход <kbd>{/Mac|iPhone|iPad/.test(navigator.platform) ? '⌘K' : 'Ctrl K'}</kbd>
        </button>
        <div className="desktop-actions"><ThemeControl />
          <button className="button plain" disabled={signingOut} onClick={() => void logout()}><Icon name="logout" size={18} /> Выйти</button></div>
        <button ref={menuTrigger} className="icon-button mobile-menu" aria-label="Открыть меню" aria-expanded={menu}
          onClick={() => setMenu(true)}><Icon name="menu" /></button>
      </div>
    </header>
    <aside className="sidebar">
      <p className="nav-caption">РАБОЧЕЕ ПРОСТРАНСТВО</p>
      {navigation}
      <div className="sidebar-bottom">{docsLink}
        <div className="workspace-tag"><span className="workspace-avatar"><Icon name="shield" size={17} /></span>
          <div><strong>Личное пространство</strong><small>ALXPRGS Notify</small></div></div>
      </div>
    </aside>
    <main className="workspace" id="workspace-main" tabIndex={-1}>
      <header className="page-header">
        <div>
          <p className="eyebrow"><Icon name={current.icon} size={15} /> {tab === 'sources' || tab === 'devices' ? 'УПРАВЛЕНИЕ' : 'РАБОЧЕЕ ПРОСТРАНСТВО'}</p>
          <h1>{current.title}</h1><p>{current.description}</p>
        </div>
        <div className="page-actions">
          {tab === 'sources' && (sources.length > 0 || !!failure) && <button className="button primary" disabled={busy}
            onClick={() => setForm({ kind: 'create' })}><Icon name="plus" size={19} /> Подключить источник</button>}
          <button className="button secondary" disabled={loading} onClick={() => void load()}>
            <Icon name="refresh" size={17} /> {loading ? 'Обновляем…' : 'Обновить'}
          </button>
        </div>
      </header>
      <div className="workspace-body">
        {failure && <Notice tone="danger" title={failure}><button className="button secondary" disabled={loading} onClick={() => void load()}>Повторить</button></Notice>}
        {submitting && <Notice title="Отправляем запрос на подтверждение" />}
        {approval && <Notice title="Подтвердите действие на телефоне"><p>Откройте запрос в Notify. Потребуется биометрия или PIN Android.</p></Notice>}
        {outcome && <Notice title={outcome.title} tone={outcome.tone} dismiss={() => setOutcome(null)}>{outcome.text && <p>{outcome.text}</p>}</Notice>}
        {tab !== 'history' && loading && !resources[tab].length && <div className="loading-panel" role="status">Загружаем раздел…</div>}
        {tab === 'history' && <History items={history.items} sources={sources} filter={filter} setFilter={setFilter}
          open={openDetail} next={history.next} more={() => { if (history.next) void load(history.next); }} loading={loading}
          failed={!!failure} integrations={() => navigate('sources')} />}
        {tab === 'sources' && <Integrations sources={sources} agents={agents} busy={busy}
          open={setForm} manage={input => void manage(input)} blocked={loading || !!failure} />}
        {tab === 'sources' && <ChatGptPanel api={api} revision={chatgptRevision} busy={busy}
          connect={() => void manage({ action: 'chatgpt.link' })}
          manage={input => void manage(input)}
          changed={() => { setChatgptRevision(value => value + 1); void load(undefined, true); }} />}
        {tab === 'devices' && <Devices devices={devices} busy={busy} open={setForm} blocked={loading || !!failure} />}
        {tab === 'calls' && <Calls calls={calls} agents={agents} sources={sources} blocked={loading || !!failure} />}
      </div>
      <footer className="workspace-footer"><span>Notify · v0.5.0</span><span><Icon name="phone" size={15} /> Ответы принимаются в приложении Android</span></footer>
    </main>
    {menu && <Dialog title="Меню Notify" close={() => setMenu(false)} className="mobile-drawer">
      {navigation}{docsLink}
      <button className="button secondary" onClick={() => openQuick(menuTrigger.current)}><Icon name="search" /> Быстрый переход</button>
      <div className="drawer-actions"><ThemeControl />
        <button className="button plain" disabled={signingOut} onClick={() => void logout()}><Icon name="logout" /> Выйти</button></div>
    </Dialog>}
    {quick && <QuickJump close={closeQuick} navigate={section => { navigate(section); closeQuick(); }} />}
    {detail && <EventDetail {...detail} sources={sources} retry={() => openDetail(detail.preview)} close={() => {
      detailRequest.current?.abort(); setDetail(null);
    }} />}
    {form && <ManagementDialog form={form} close={() => setForm(null)} submit={input => void manage(input)} />}
    {secret && !form && !menu && !quick && !detail && <Credentials result={secret} close={() => setSecret(null)} />}
    {chatgptSecret && !form && !menu && !quick && !detail &&
      <Dialog title="Данные подключения ChatGPT" close={() => setChatgptSecret(null)}>
        <p>Скопируйте эти данные в настройки OAuth в ChatGPT. Секрет показывается один раз.</p>
        <div className="management-form">
          <label className="field">OAuth Client ID<input value={chatgptSecret.clientId} readOnly /></label>
          <label className="field">OAuth Client Secret<input value={chatgptSecret.clientSecret} readOnly autoComplete="off" /></label>
          <button className="button primary" onClick={() => setChatgptSecret(null)}>Готово</button>
        </div>
      </Dialog>}
  </div>;
}

function ChatGptPanel({ api, revision, busy, connect, manage, changed }: {
  api: <T>(path: string, body?: unknown, signal?: AbortSignal) => Promise<T>;
  revision: number; busy: boolean; connect: () => void; changed: () => void;
  manage: (input: ManagementInput) => void;
}) {
  const [status, setStatus] = useState<ChatGptStatus | null>(null);
  const [redirectUri, setRedirectUri] = useState('');
  const [scopes, setScopes] = useState<string[]>([]);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [confirmDisconnect, setConfirmDisconnect] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    api<ChatGptStatus>('/chatgpt', undefined, controller.signal).then(result => {
      if (controller.signal.aborted) return;
      setStatus(result);
      setRedirectUri(result.redirectUri ?? result.defaultRedirectUri);
      setScopes(result.scopes);
    }).catch(reason => { if (!controller.signal.aborted) setError(errorText(reason)); });
    return () => controller.abort();
  }, [api, revision]);
  const permissionsChanged = status?.availablePermissions.some(({ scope }) =>
    scopes.includes(scope) !== status.scopes.includes(scope));
  const canSavePermissions = status?.permissionsRevision === 0 || permissionsChanged;

  async function submit(path: string, body: unknown) {
    setSaving(true);
    setError('');
    try {
      await api(path, body);
      setConfirmDisconnect(false);
      changed();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setSaving(false);
    }
  }

  return <section className="panel entity-card chatgpt-panel" aria-labelledby="chatgpt-heading">
    <header>
      <h2 id="chatgpt-heading">ChatGPT</h2>
      <p>Подключите приватный MCP-сервер и подтвердите выдачу доступа на телефоне.</p>
    </header>
    {error && <Notice title={error} tone="danger" />}
    {status && <div className="management-form">
      <label className="field">Адрес MCP<input value={status.url} readOnly /></label>
      {status.clientId && <label className="field">OAuth Client ID<input value={status.clientId} readOnly /></label>}
      {status.clientId && <form className="management-form" onSubmit={event => {
        event.preventDefault();
        void submit('/chatgpt/callback', { redirectUri: redirectUri.trim() });
      }}>
        <label className="field">OAuth callback из ChatGPT<input type="url" required value={redirectUri}
          onChange={event => setRedirectUri(event.target.value)} /></label>
        <p>Стандартный адрес настроен автоматически. Если ChatGPT показывает другой адрес, введите и сохраните его здесь.</p>
        <button className="button secondary" disabled={busy || saving || redirectUri.trim() === (status.redirectUri ?? status.defaultRedirectUri)}>Сохранить callback</button>
      </form>}
      {status.sourceId && <form className="management-form" onSubmit={event => {
        event.preventDefault();
        if (!canSavePermissions || busy || saving) return;
        manage({ action: 'chatgpt.permissions.update', targetId: status.sourceId!, expectedRevision: status.permissionsRevision, scopes });
        setScopes(status.scopes);
      }}>
        <fieldset disabled={busy || saving}>
          <legend>Разрешения</legend>
          <div className="management-form">
            {status.availablePermissions.map(({ scope, label }) => <label key={scope}>
              <input type="checkbox" checked={scopes.includes(scope)} onChange={event => {
                const checked = event.target.checked;
                setScopes(current => checked ? [...current, scope] : current.filter(value => value !== scope));
              }} /> {label}
            </label>)}
          </div>
        </fieldset>
        <p>После подтверждения на телефоне изменения применятся ко всем действующим токенам этого подключения.</p>
        <button className="button secondary" disabled={busy || saving || !canSavePermissions}>Сохранить разрешения</button>
      </form>}
      <div className="top-actions">
        <button className="button primary" disabled={busy || saving} onClick={connect}>
          {status.clientId ? 'Перевыпустить доступ' : 'Подключить ChatGPT'}
        </button>
        {status.clientId && <button className="button secondary" disabled={busy || saving}
          onClick={() => setConfirmDisconnect(true)}>Отключить</button>}
      </div>
    </div>}
    {confirmDisconnect && <Dialog title="Отключить ChatGPT?" close={() => setConfirmDisconnect(false)}>
      <p>Подключение и выданные токены перестанут действовать.</p>
      <button className="button secondary" disabled={saving} onClick={() => void submit('/chatgpt/disconnect', {})}>Отключить ChatGPT</button>
    </Dialog>}
  </section>;
}
