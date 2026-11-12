
import { useEffect, useState } from 'react';
import { Badge, Dialog, Empty, Icon, Notice, type Glyph } from './ui';

export type Notification = {
  id: string; sourceId: string; status: string; createdAt: number; previewOnly?: boolean;
  content: {
    kind: string; title: string; body: string; securityLevel: string; expiresAt?: string;
    voice?: { durationMs: number; transcript?: string; transcriptProtected?: boolean };
  };
  answer?: { decision: string; text?: string };
};
export type Cursor = { before: number; beforeId: string };
export type HistoryPage = { items: Notification[]; next: Cursor | null };
export type Source = { id: string; name: string; preview: string; scopes: string[]; revoked: boolean; permissionsRevision: number; canEditPermissions: boolean };
export type Device = { id: string; name: string; revoked: boolean };
export type Agent = {
  id: string; sourceId: string; name: string; capabilities: string[]; revoked: boolean;
  online: boolean; lastSeenAt?: number; sessions: { id: string; name: string }[];
};
export type Call = {
  id: string; sourceId: string; agentId: string; sessionId?: string; securityLevel: string;
  startedAt: number; status: string; reason?: string; durationMs: number; direction?: string;
  mode?: string; hasTranscript?: boolean; transcriptPartial?: boolean;
};
export type ManagementInput =
  | { action: 'chatgpt.link' }
  | { action: 'chatgpt.permissions.update'; targetId: string; expectedRevision: number; scopes: string[] }
  | { action: 'integration.permissions.update'; targetId: string; expectedRevision: number; scopes: string[] }
  | { action: 'integration.create'; name: string; scopes: string[]; webhookUrl: string | null }
  | { action: 'source.update'; targetId: string; preview: 'full' | 'hidden' }
  | { action: 'integration.revoke' | 'device.revoke'; targetId: string }
  | { action: 'agent.register'; targetId: string; name: string; capabilities: string[] };
export type ManagementForm = { kind: 'create' }
  | { kind: 'agent'; source: Source }
  | { kind: 'source-permissions'; source: Source }
  | { kind: 'revoke-source'; source: Source }
  | { kind: 'revoke-device'; device: Device };

const kinds: Record<string, { label: string; icon: Glyph }> = {
  message: { label: 'Сообщение', icon: 'message' },
  confirmation: { label: 'Подтверждение', icon: 'decision' },
  form: { label: 'Форма', icon: 'form' },
  voice: { label: 'Голосовое', icon: 'audio' },
};
const states: Record<string, [string, string]> = {
  pending: ['Ожидает ответа', 'warning'], answered: ['Получен ответ', 'success'],
  expired: ['Срок истёк', 'neutral'], cancelled: ['Отменён', 'neutral'],
  active: ['Опубликовано', 'info'],
};
const capabilities: Record<string, string> = {
  text_tasks: 'Текстовые задачи', voice_messages: 'Голосовые сообщения', realtime_audio: 'Живые звонки',
};
const callReasons: Record<string, string> = {
  rejected: 'Отклонён', missed: 'Нет ответа', timeout: 'Время истекло', disconnected: 'Связь прервана',
  unsupported: 'Не поддерживается', busy: 'Занято', invalid_audio: 'Ошибка аудио',
  degraded: 'Проблемы с сетью', revoked: 'Доступ отозван', ended: 'Завершён',
  playback_sent: 'Фраза воспроизведена', interrupted: 'Прерван', stt_unavailable: 'STT недоступен',
  stt_timeout: 'STT не успел', stt_empty: 'Речь не распознана',
};
const dateTime = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' });
const dayFormat = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'full' });
const timeFormat = new Intl.DateTimeFormat('ru-RU', { timeStyle: 'short' });

function isPending(item: Notification, now: number) {
  return item.status === 'pending' && ['confirmation', 'form'].includes(item.content.kind)
    && Date.parse(item.content.expiresAt ?? '') > now;
}

function useDeadline(items: Notification[]) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const current = Date.now();
    const next = items.reduce((closest, item) => isPending(item, current)
      ? Math.min(closest, Date.parse(item.content.expiresAt!)) : closest, Infinity);
    if (Number.isFinite(next)) {
      const timer = window.setTimeout(() => setNow(Date.now()), Math.min(next - current + 1, 2_147_483_647));
      return () => clearTimeout(timer);
    }
  }, [items, now]);
  return Math.max(Date.now(), now);
}

function sourceName(item: Notification, sources: Source[]) {
  return sources.find(source => source.id === item.sourceId)?.name
    ?? (item.sourceId === '_system' ? 'Управление Notify' : 'Источник');
}

function status(item: Notification, now: number) {
  const { expiresAt } = item.content;
  return item.status === 'pending' && expiresAt && Date.parse(expiresAt) <= now ? 'expired' : item.status;
}

function EventStatus({ value }: { value: string }) {
  const [label, tone] = states[value] ?? [value, 'neutral'];
  return <Badge tone={tone}>{label}</Badge>;
}

export function History({ items, sources, filter, setFilter, open, next, more, loading, failed, integrations }: {
  items: Notification[]; sources: Source[]; filter: 'all' | 'pending'; setFilter: (value: 'all' | 'pending') => void;
  open: (item: Notification) => void; next: Cursor | null; more: () => void; loading: boolean;
  failed: boolean; integrations: () => void;
}) {
  const now = useDeadline(items);
  const pending = items.filter(item => isPending(item, now));
  const visible = filter === 'pending' ? pending : items;
  const groups = new Map<string, Notification[]>();
  for (const item of visible) {
    const day = dayFormat.format(item.createdAt);
    const group = groups.get(day);
    if (group) group.push(item);
    else groups.set(day, [item]);
  }
  return <>
    <div className="filter-bar">
      <div className="segmented" role="group" aria-label="Фильтр истории">
        <button type="button" aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>Все <span>{items.length}</span></button>
        <button type="button" aria-pressed={filter === 'pending'} onClick={() => setFilter('pending')}>
          В ожидании <span>{pending.length}</span>
        </button>
      </div>
      <span className="filter-note"><Icon name="clock" size={15} /> Последние 30 дней</span>
    </div>
    {loading && !items.length && <div className="loading-panel" role="status">Загружаем события…</div>}
    {!loading && !visible.length && (!failed || items.length > 0) && (filter === 'pending'
      ? <Empty icon="decision" title="Нет ожидающих запросов"
          text={next ? 'На загруженных страницах таких запросов нет. Можно проверить более ранние события.' : 'Новые запросы на подтверждение появятся здесь.'} />
      : <Empty icon="bell" title="Событий пока нет" text="Подключите источник, чтобы получать уведомления и запросы.">
          <button type="button" className="button secondary" onClick={integrations}>Перейти к интеграциям</button>
        </Empty>)}
    {[...groups].map(([day, entries]) => <section className="event-group" key={day}>
      <h2 className="date-heading">{day}</h2>
      <ul className="event-list panel">{entries.map(item => {
        const kind = kinds[item.content.kind] ?? { label: 'Событие', icon: 'bell' as const };
        return <li key={item.id}>
          <button type="button" className="event-row" onClick={() => open(item)}>
            <span className={'entity-icon ' + (item.content.kind === 'confirmation' ? 'accent' : '')}><Icon name={kind.icon} /></span>
            <span className="event-content">
              <span className="event-meta">{kind.label}<span aria-hidden="true">·</span>{sourceName(item, sources)}</span>
              <strong>{item.content.title}</strong>
              {item.content.body && <span className="event-preview">{item.content.body}</span>}
            </span>
            <span className="event-state">
              <time dateTime={new Date(item.createdAt).toISOString()}>{timeFormat.format(item.createdAt)}</time>
              <EventStatus value={status(item, now)} />
              {item.content.securityLevel === 'elevated' && <span className="security"><Icon name="shield" size={14} /> С проверкой личности</span>}
            </span>
            <span className="row-arrow"><Icon name="arrow" size={16} /></span>
          </button>
        </li>;
      })}</ul>
    </section>)}
    {next && <button type="button" className="button secondary more" disabled={loading} onClick={more}>
      {loading ? 'Загружаем…' : 'Загрузить ещё'}
    </button>}
  </>;
}

export function EventDetail({ preview, full, sources, error, retry, close }: {
  preview: Notification; full?: Notification; sources: Source[]; error: string; retry: () => void; close: () => void;
}) {
  const item = full ?? preview;
  const now = useDeadline([item]);
  const kind = kinds[item.content.kind];
  const answer = full?.answer;
  return <Dialog title="Событие" close={close} className="detail-dialog">
    <div className="detail-meta">
      <span className="entity-icon accent"><Icon name={kind?.icon ?? 'bell'} /></span>
      <div><p>{kind?.label ?? 'Событие'} · {sourceName(item, sources)}</p><time>{dateTime.format(item.createdAt)}</time></div>
    </div>
    <h3 className="detail-title">{item.content.title}</h3>
    <div className="detail-badges">
      <EventStatus value={status(item, now)} />
      {item.content.securityLevel === 'elevated' && <Badge tone="info"><Icon name="shield" size={14} /> С проверкой личности</Badge>}
    </div>
    {error ? <Notice title={error} tone="danger"><button className="button secondary" onClick={retry}>Повторить</button></Notice>
      : !full ? <p role="status">Загружаем полное событие…</p>
        : <>
          {full.content.body && <p className="full-body">{full.content.body}</p>}
          {answer && <p className="detail-answer"><strong>Ответ</strong> {answer.decision === 'yes' ? 'Да' : answer.decision === 'no' ? 'Нет' : answer.text}</p>}
          {full.content.kind === 'voice' && <p>Голосовое сообщение{full.content.voice ? ' · ' + Math.round(full.content.voice.durationMs / 1000) + ' с' : ''}. Прослушивание доступно в приложении.</p>}
          {full.content.kind === 'voice' && full.content.voice?.transcript && <p><strong>Расшифровка:</strong> {full.content.voice.transcript}</p>}
          {full.content.kind === 'voice' && full.content.voice?.transcriptProtected === true && !full.content.voice.transcript && <p>Расшифровка защищённого сообщения откроется в приложении после подтверждения личности.</p>}
          {full.content.kind === 'form' && full.status === 'answered' && <p>Ответ формы получен в приложении.</p>}
          {isPending(full, now) && <Notice title="Откройте Notify на телефоне, чтобы ответить" />}
        </>}
  </Dialog>;
}

function CopyId({ label, value }: { label: string; value: string }) {
  const [feedback, setFeedback] = useState('');
  return <div className="entity-controls">
    <div><strong>{label}</strong><p><code>{value}</code></p></div>
    <button className="button secondary" aria-label={`Скопировать ${label}`} onClick={async () => {
      try { await navigator.clipboard.writeText(value); setFeedback(`Скопирован ${label}`); }
      catch { setFeedback('Копирование недоступно. Выделите значение вручную.'); }
    }}><Icon name="copy" size={16} /> Скопировать</button>
    {feedback && <small role="status">{feedback}</small>}
  </div>;
}

export function Integrations({ sources, agents, busy, open, manage, blocked }: {
  sources: Source[]; agents: Agent[]; busy: boolean; open: (form: ManagementForm) => void;
  manage: (input: ManagementInput) => void; blocked: boolean;
}) {
  if (!sources.length && !blocked) return <Empty icon="link" title="Подключите первый источник"
    text="У каждого сервиса свой токен. Подтвердите создание на доверенном телефоне.">
    <button className="button primary" disabled={busy} onClick={() => open({ kind: 'create' })}><Icon name="plus" /> Подключить источник</button>
  </Empty>;
  return <div className="entity-list">{sources.map(source => {
    const agent = agents.find(entry => entry.sourceId === source.id && !entry.revoked);
    return <section className="panel entity-card" key={source.id}>
      <div className="entity-header">
        <span className="entity-icon accent"><Icon name="link" /></span>
        <div className="entity-main"><h2>{source.name}</h2></div>
        <Badge tone={source.revoked ? 'neutral' : 'success'}>{source.revoked ? 'Доступ отозван' : 'Активен'}</Badge>
      </div>
      <CopyId label="Source ID" value={source.id} />
      {!source.revoked && <>
        {source.canEditPermissions && <div className="entity-controls">
          <div><strong>Права источника</strong><p>{source.scopes.map(scope => sourcePermissionLabels[scope] ?? scope).join(', ') || 'Права не выданы'}</p></div>
          <button className="button secondary" disabled={busy || blocked}
            onClick={() => open({ kind: 'source-permissions', source })}>Изменить права</button>
        </div>}
        <div className="entity-controls">
          <div><strong>Экран блокировки</strong><p>{source.preview === 'hidden' ? 'Содержание скрыто' : 'Содержание может показываться'}</p></div>
          <button className="button secondary" disabled={busy}
            onClick={() => manage({ action: 'source.update', targetId: source.id, preview: source.preview === 'hidden' ? 'full' : 'hidden' })}>
            {source.preview === 'hidden' ? 'Показывать содержание' : 'Скрывать содержание'}
          </button>
        </div>
        {source.scopes.includes('agents:connect') && <div className="agent-block">
          <div className="agent-heading"><Icon name="agent" /><strong>{agent ? 'Агент «' + agent.name + '»' : 'Агент'}</strong>
            {agent && <Badge tone={agent.online ? 'success' : 'neutral'}>
              {agent.online ? 'На связи' : agent.lastSeenAt ? 'Не в сети' : 'Ещё не подключался'}
            </Badge>}
          </div>
          {agent ? <>
            <CopyId label="Agent ID" value={agent.id} />
            {!agent.online && !!agent.lastSeenAt && <p>Последнее подключение {dateTime.format(agent.lastSeenAt)}</p>}
            {agent.capabilities.length > 0 && <div className="tags">{agent.capabilities.map(cap => <Badge key={cap}>{capabilities[cap] ?? cap}</Badge>)}</div>}
            {agent.online && agent.sessions.length > 0 && <p>Задачи · {agent.sessions.map(session => session.name).join(', ')}</p>}
          </> : <>
            <p>Зарегистрируйте агента, который будет подключаться к Notify по WSS.</p>
            <button className="button secondary" disabled={busy || blocked} onClick={() => open({ kind: 'agent', source })}>Зарегистрировать агента</button>
          </>}
        </div>}
        <div className="entity-footer"><button className="button destructive" disabled={busy}
          onClick={() => open({ kind: 'revoke-source', source })}>Отозвать доступ</button></div>
      </>}
    </section>;
  })}</div>;
}

export function Devices({ devices, busy, open, blocked }: {
  devices: Device[]; busy: boolean; open: (form: ManagementForm) => void; blocked: boolean;
}) {
  return <>
    <Notice title="Привязка через приложение">
      <p>Откройте Notify на Android 11 или новее и войдите через SSO. Там же можно восстановить доступ без прежнего телефона.</p>
    </Notice>
    {!devices.length && !blocked && <Empty icon="phone" title="Телефон ещё не привязан"
      text="После привязки устройство появится здесь и сможет подтверждать запросы." />}
    <div className="entity-list">{devices.map(device => <section className="panel entity-card device-card" key={device.id}>
      <div className="entity-header">
        <span className="entity-icon accent"><Icon name="phone" /></span>
        <div className="entity-main"><h2>{device.name}</h2>
          <Badge tone={device.revoked ? 'neutral' : 'success'}>{device.revoked ? 'Устройство отозвано' : 'Доверенное устройство'}</Badge>
        </div>
        {!device.revoked && <button className="button destructive" disabled={busy}
          onClick={() => open({ kind: 'revoke-device', device })}>Отозвать</button>}
      </div>
    </section>)}</div>
  </>;
}

export function Calls({ calls, agents, sources, blocked }: { calls: Call[]; agents: Agent[]; sources: Source[]; blocked: boolean }) {
  const [transcripts, setTranscripts] = useState<Record<string, { turns: { id: string; speaker: string; text: string; status?: string; error?: string }[]; partial: boolean; error?: string }>>({});
  const [transcriptErrors, setTranscriptErrors] = useState<Record<string, string>>({});
  const loadTranscript = async (id: string) => {
    if (transcripts[id] !== undefined || transcriptErrors[id] !== undefined) return;
    try {
      const response = await fetch(`/api/v1/calls/${id}/transcript`, { credentials: 'same-origin' });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(typeof body.code === 'string' ? body.code : 'transcript_unavailable');
      setTranscripts(current => ({ ...current, [id]: {
        turns: Array.isArray(body.turns) ? body.turns : [],
        partial: body.partial === true,
        error: typeof body.error === 'string' ? body.error : undefined,
      } }));
    } catch (error) {
      setTranscriptErrors(current => ({ ...current, [id]: error instanceof Error ? error.message : 'transcript_unavailable' }));
    }
  };
  return <>
    <Notice title="История звонков"><p>Хранятся метаданные и расшифровки новых разговоров; аудио не сохраняется. Текст удаляется вместе с 30-дневной записью.</p></Notice>
    {!calls.length && !blocked && <Empty icon="call" title="Звонков пока нет"
      text="Позвоните агенту или примите входящий звонок в приложении Notify." />}
    {!!calls.length && <ul className="panel call-list">{calls.map(call => {
      const direction = call.direction === 'incoming' ? 'Входящий' : 'Исходящий';
      const peer = agents.find(agent => agent.id === call.agentId)?.name
        ?? sources.find(source => source.id === call.sourceId)?.name ?? 'Агент';
      const reason = call.reason || call.status;
      const seconds = Math.round(call.durationMs / 1000);
      const duration = seconds < 60 ? seconds + ' с' : Math.floor(seconds / 60) + ' мин ' + seconds % 60 + ' с';
      const transcript = transcripts[call.id];
      const transcriptError = transcriptErrors[call.id];
      return <li key={call.id}>
        <span className="entity-icon accent"><Icon name="call" /></span>
        <div className="call-party"><strong>{peer}</strong><p>{direction} · {dateTime.format(call.startedAt)}</p></div>
        <div className="call-result"><strong>{callReasons[reason] ?? (reason || 'Завершён')}</strong><p>{duration}{call.mode === 'conversation' ? ' · разговор' : ''}</p>
          {call.hasTranscript === true && <details onToggle={event => { if (event.currentTarget.open) void loadTranscript(call.id); }}>
            <summary>Расшифровка</summary>
            {transcript === undefined && transcriptError === undefined && <p>Загружаем…</p>}
            {transcriptError !== undefined && <p>Не удалось загрузить: {transcriptError}</p>}
            {transcript?.partial === true && <p>Распознано неполно; полученные реплики сохранены.</p>}
            {transcript?.turns.map(turn => <p key={turn.id}><strong>{turn.speaker === 'user' ? 'Вы' : 'Агент'}:</strong> {turn.status === 'error' ? `ошибка распознавания (${turn.error ?? 'unknown'})` : turn.text}</p>)}
          </details>}
        </div>
        <Badge tone={call.securityLevel === 'elevated' ? 'info' : 'neutral'}>
          {call.securityLevel === 'elevated' && <Icon name="shield" size={14} />}
          {call.securityLevel === 'elevated' ? 'С проверкой личности' : 'Обычный'}
        </Badge>
      </li>;
    })}</ul>}
  </>;
}

const sourceScopes = ['notify:send', 'requests:create', 'requests:read', 'requests:cancel', 'icons:write', 'agents:connect'];
const sourcePermissionLabels: Record<string, string> = {
  'notify:send': 'Отправка уведомлений', 'requests:create': 'Создание подтверждений и форм',
  'requests:read': 'Чтение своих запросов и ответов', 'requests:cancel': 'Отмена своих запросов',
  'icons:write': 'Загрузка иконок', 'agents:connect': 'Подключение агента',
  'calls:start': 'Звонки и разговоры Code Mode', 'calls:announce': 'Звонки с озвучкой',
  'telephony:bridge': 'Мост Asterisk',
};

export function ManagementDialog({ form, close, submit }: {
  form: ManagementForm; close: () => void; submit: (input: ManagementInput) => void;
}) {
  const [name, setName] = useState('');
  const [webhook, setWebhook] = useState('');
  const [sourceType, setSourceType] = useState('default');
  const [selected, setSelected] = useState(form.kind === 'source-permissions' ? form.source.scopes : ['text_tasks']);
  if (form.kind === 'source-permissions') {
    const { source } = form;
    const changed = selected.length !== source.scopes.length || selected.some(scope => !source.scopes.includes(scope));
    return <Dialog title="Изменить права источника" close={close}>
      <form className="management-form" onSubmit={event => {
        event.preventDefault();
        if (changed) submit({ action: 'integration.permissions.update', targetId: source.id, expectedRevision: source.permissionsRevision, scopes: selected });
      }}>
        <p>Источник <strong>{source.name}</strong>. После подтверждения на доверенном телефоне новые права получат действующие токены источника.</p>
        <fieldset><legend>Разрешения</legend>
          {Object.entries(sourcePermissionLabels).map(([scope, label]) => <label className="checkbox" key={scope}>
            <input type="checkbox" checked={selected.includes(scope)} onChange={event => {
              const checked = event.target.checked;
              setSelected(current => checked ? [...current, scope] : current.filter(value => value !== scope));
            }} /> {label}
          </label>)}
        </fieldset>
        <div className="dialog-actions"><button type="button" className="button secondary" onClick={close}>Отмена</button>
          <button className="button primary" disabled={!changed}>Сохранить с подтверждением</button>
        </div>
      </form>
    </Dialog>;
  }
  if (form.kind === 'revoke-source' || form.kind === 'revoke-device') {
    const source = form.kind === 'revoke-source';
    const name = source ? form.source.name : form.device.name;
    return <Dialog title={source ? 'Отозвать доступ' : 'Отозвать устройство'} close={close}>
      <p><strong>{name}</strong> {source ? 'потеряет доступ к Notify. Связанный агент будет отключён.' : 'потеряет доступ к уведомлениям и подтверждениям.'}</p>
      <p>Для отзыва потребуется подтверждение на доверенном телефоне.</p>
      <div className="dialog-actions"><button className="button secondary" onClick={close}>Отмена</button>
        <button className="button destructive" onClick={() => submit(source
          ? { action: 'integration.revoke', targetId: form.source.id }
          : { action: 'device.revoke', targetId: form.device.id })}>Запросить отзыв</button></div>
    </Dialog>;
  }
  const creating = form.kind === 'create';
  return <Dialog title={creating ? 'Подключить источник' : 'Зарегистрировать агента'} close={close}>
    <form className="management-form" onSubmit={event => {
      event.preventDefault();
      if (!name.trim()) return;
      if (form.kind === 'create') submit({ action: 'integration.create', name: name.trim(), scopes: sourceType === 'default' ? sourceScopes : sourceType === 'code' ? [...sourceScopes, 'calls:start'] : [sourceType], webhookUrl: sourceType === 'default' || sourceType === 'code' ? webhook.trim() || null : null });
      if (form.kind === 'agent' && selected.length) submit({ action: 'agent.register', targetId: form.source.id, name: name.trim(), capabilities: selected });
    }}>
      {form.kind === 'agent' && <p>Источник <strong>{form.source.name}</strong></p>}
      <label className="field">{creating ? 'Название источника' : 'Имя агента'}
        <input autoFocus value={name} onChange={event => setName(event.target.value)} required maxLength={80}
          placeholder={creating ? 'Например, мониторинг' : 'Например, рабочий компьютер'} />
      </label>
      {creating ? <>
        <label className="field">Права источника
          <select value={sourceType} onChange={event => setSourceType(event.target.value)}>
            <option value="default">Сообщения, запросы и подключение агента</option>
            <option value="code">Агентские операции и звонки Code Mode</option>
            <option value="calls:announce">Home Assistant · звонки с озвучкой</option>
            <option value="telephony:bridge">Мост Asterisk</option>
          </select>
        </label>
        {(sourceType === 'default' || sourceType === 'code') && <label className="field"><span>Webhook <small>Необязательно</small></span>
          <input value={webhook} onChange={event => setWebhook(event.target.value)} type="url" placeholder="https://service.example/notify" />
        </label>}
        <p>Токен действует 90 дней.</p>
      </> : <fieldset><legend>Возможности агента</legend>
        {Object.entries(capabilities).map(([id, label]) => <label className="checkbox" key={id}>
          <input type="checkbox" checked={selected.includes(id)}
            onChange={event => setSelected(old => event.target.checked ? [...old, id] : old.filter(cap => cap !== id))} /> {label}
        </label>)}
      </fieldset>}
      <div className="dialog-actions"><button type="button" className="button secondary" onClick={close}>Отмена</button>
        <button className="button primary" disabled={!name.trim() || (!creating && !selected.length)}>
          {creating ? 'Создать с подтверждением' : 'Зарегистрировать с подтверждением'}
        </button>
      </div>
    </form>
  </Dialog>;
}

export function Credentials({ result, close }: {
  result: { token: string; webhookSecret?: string; sourceId?: string }; close: () => void;
}) {
  const [feedback, setFeedback] = useState('');
  const values = [['Токен интеграции', result.token], ['Секрет webhook', result.webhookSecret], ['Источник', result.sourceId]];
  return <Dialog title="Доступ создан" close={close}>
    <p>Сохраните эти значения сейчас. Они показываются один раз.</p>
    {values.map(([label, value]) => value && <div className="credential" key={label}>
      <strong>{label}</strong><code>{value}</code>
      <button className="button secondary" onClick={async () => {
        try { await navigator.clipboard.writeText(value); setFeedback('Скопировано · ' + label); }
        catch { setFeedback('Копирование недоступно. Выделите значение вручную.'); }
      }}><Icon name="copy" size={16} /> Скопировать</button>
    </div>)}
    <p role="status">{feedback}</p>
    <div className="dialog-actions"><button className="button primary" onClick={close}>Я сохранил значения</button></div>
  </Dialog>;
}
