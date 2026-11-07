
import {
  createContext, useContext, useEffect, useId, useLayoutEffect, useRef, useState,
  type ReactNode,
} from 'react';

const glyphs = {
  bell: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
  history: 'M8 5H5v15h14V5h-3M9 3h6v4H9zM8 11h8m-8 4h6',
  link: 'm10 13 4-4m-7 6-1 1a3.5 3.5 0 0 0 5 5l4-4a3.5 3.5 0 0 0 0-5m2-3 1-1a3.5 3.5 0 0 0-5-5L9 7a3.5 3.5 0 0 0 0 5',
  phone: 'M7 3h10a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Zm4 15h2',
  call: 'M8 3H4l-1 2a17 17 0 0 0 16 16l2-1v-4l-5-2-2 2a12 12 0 0 1-6-6l2-2Z',
  book: 'M12 5v15M3 4h4a5 5 0 0 1 5 2 5 5 0 0 1 5-2h4v14h-4a5 5 0 0 0-5 2 5 5 0 0 0-5-2H3z',
  search: 'M19 19l-4-4M5 10a5 5 0 1 0 10 0 5 5 0 0 0-10 0Z',
  logout: 'M10 4H4v16h6m4-12 4 4-4 4m-5-4h9',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'm6 6 12 12M6 18 18 6',
  refresh: 'M20 10a8 8 0 0 0-14-4L4 8m0-5v5h5m-5 6a8 8 0 0 0 14 4l2-2m0 5v-5h-5',
  plus: 'M12 5v14M5 12h14',
  arrow: 'm9 6 6 6-6 6',
  external: 'M8 16 18 6M8 6h10v10',
  shield: 'M12 3 4 6v5c0 5 3 8 8 10 5-2 8-5 8-10V6Zm-3 9 2 2 4-4',
  message: 'M4 4h16v12H9l-5 4Zm4 5h8m-8 3h5',
  decision: 'M5 12l4 4L19 6',
  form: 'M6 3h12v18H6Zm3 5h6m-6 4h6m-6 4h4',
  audio: 'M3 11v2m4-5v8m5-12v16m5-12v8m4-5v2',
  clock: 'M3 12a9 9 0 1 0 18 0 9 9 0 0 0-18 0Zm9-5v5l3 2',
  agent: 'M5 7h14v11H5zm7-4v4m-4 5h1m6 0h1M3 10v5m18-5v5',
  copy: 'M8 8h12v12H8zM16 4H4v12',
  info: 'M3 12a9 9 0 1 0 18 0 9 9 0 0 0-18 0Zm9-1v5m0-8h.01',
  theme: 'M12 3a9 9 0 1 0 9 9 7 7 0 0 1-9-9Z',
} as const;
export type Glyph = keyof typeof glyphs;

export function Icon({ name, size = 20 }: { name: Glyph; size?: number }) {
  return <svg aria-hidden="true" width={size} height={size} viewBox="0 0 24 24"
    fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
    <path d={glyphs[name]} />
  </svg>;
}

export function Brand({ href = '/' }: { href?: string }) {
  return <a className="brand" href={href} aria-label="Notify — ALXPRGS">
    <span className="brand-mark"><Icon name="bell" size={21} /></span>
    <span className="brand-word">Notify <small>ALXPRGS</small></span>
  </a>;
}

type Theme = 'system' | 'light' | 'dark';
const themeKey = 'notify-theme';
const ThemeContext = createContext({
  preference: 'system' as Theme, resolved: 'light' as 'light' | 'dark',
  choose: (_value: Theme) => {},
});

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreference] = useState<Theme>(() => {
    try {
      const value = localStorage.getItem(themeKey);
      if (value === 'light' || value === 'dark') return value;
    } catch { /* Browser storage can be disabled. */ }
    return 'system';
  });
  const [systemDark, setSystemDark] = useState(() => matchMedia('(prefers-color-scheme: dark)').matches);
  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)');
    const update = () => setSystemDark(media.matches);
    media.addEventListener('change', update);
    update();
    return () => media.removeEventListener('change', update);
  }, []);
  const resolved = preference === 'system' ? (systemDark ? 'dark' : 'light') : preference;
  useLayoutEffect(() => { document.documentElement.dataset.theme = resolved; }, [resolved]);
  function choose(value: Theme) {
    setPreference(value);
    try { localStorage.setItem(themeKey, value); } catch { /* Keep the choice for this visit. */ }
  }
  return <ThemeContext.Provider value={{ preference, resolved, choose }}>{children}</ThemeContext.Provider>;
}

export function useTheme() { return useContext(ThemeContext); }

export function ThemeControl() {
  const { preference, choose } = useTheme();
  return <label className="theme-control">
    <Icon name="theme" size={17} /><span className="sr-only">Тема оформления</span>
    <select value={preference} onChange={event => choose(event.target.value as Theme)}>
      <option value="system">Как в системе</option>
      <option value="light">Светлая</option>
      <option value="dark">Тёмная</option>
    </select>
  </label>;
}

export function Dialog({ title, children, close, className = '' }: {
  title: string; children: ReactNode; close: () => void; className?: string;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useLayoutEffect(() => {
    const dialog = ref.current!;
    dialog.showModal();
    return () => { if (dialog.open) dialog.close(); };
  }, []);
  return <dialog ref={ref} className={'dialog ' + className} aria-labelledby={titleId}
    onCancel={event => { event.preventDefault(); close(); }}>
    <div className="dialog-header">
      <h2 id={titleId}>{title}</h2>
      <button type="button" className="icon-button" aria-label="Закрыть" onClick={close}><Icon name="close" /></button>
    </div>
    {children}
  </dialog>;
}

export function Empty({ icon, title, text, children }: {
  icon: Glyph; title: string; text: string; children?: ReactNode;
}) {
  return <section className="empty">
    <span className="empty-icon"><Icon name={icon} size={25} /></span>
    <h2>{title}</h2><p>{text}</p>{children}
  </section>;
}

export function Notice({ tone = 'info', title, children, dismiss }: {
  tone?: 'info' | 'success' | 'warning' | 'danger'; title: string; children?: ReactNode; dismiss?: () => void;
}) {
  return <div className={'notice ' + tone} role={tone === 'danger' ? 'alert' : 'status'}>
    <Icon name={tone === 'success' ? 'decision' : tone === 'warning' ? 'clock' : 'info'} />
    <div><strong>{title}</strong>{children}</div>
    {dismiss && <button className="icon-button notice-close" aria-label="Скрыть результат" onClick={dismiss}><Icon name="close" size={18} /></button>}
  </div>;
}

export function Badge({ tone = 'neutral', children }: { tone?: string; children: ReactNode }) {
  return <span className={'badge ' + tone}>{children}</span>;
}

export const sections = [
  { id: 'history', title: 'История', icon: 'history', description: 'Уведомления, запросы и решения за последние 30 дней.' },
  { id: 'sources', title: 'Интеграции', icon: 'link', description: 'Управляйте источниками уведомлений и подключёнными агентами.' },
  { id: 'devices', title: 'Устройства', icon: 'phone', description: 'Телефоны, которым вы доверяете подтверждать действия.' },
  { id: 'calls', title: 'Звонки', icon: 'call', description: 'Смотрите, когда и с кем проходили звонки в Notify.' },
] as const;
export type Section = (typeof sections)[number]['id'];
export const guideLinks = [
  ['contract', 'API, MCP и Python SDK'], ['security', 'Вход и подтверждения'],
  ['operations', 'Подключение и эксплуатация'], ['home', 'Домашний Docker'],
] as const;

export function QuickJump({ close, navigate }: { close: () => void; navigate: (id: Section) => void }) {
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const entries = [
    ...sections.map(section => ({ title: section.title, icon: section.icon, group: 'Кабинет', run: () => navigate(section.id) })),
    { title: 'Документация и справочник API', icon: 'book' as const, group: 'Документация', run: () => location.assign('/docs') },
    ...guideLinks.map(([id, title]) => ({ title, icon: 'book' as const, group: 'Документация', run: () => location.assign('/docs/guide/' + id) })),
  ].filter(item => (item.title + ' ' + item.group).toLocaleLowerCase('ru').includes(query.trim().toLocaleLowerCase('ru')));
  useLayoutEffect(() => { input.current?.focus(); }, []);
  useEffect(() => { document.getElementById(listId + '-' + active)?.scrollIntoView({ block: 'nearest' }); }, [active, listId]);
  return <Dialog title="Быстрый переход" close={close} className="quick-dialog">
    <div className="search-field"><Icon name="search" />
      <input ref={input} autoFocus role="combobox" aria-label="Найти раздел" aria-autocomplete="list"
        aria-expanded="true" aria-controls={listId} aria-activedescendant={entries.length ? listId + '-' + active : undefined}
        value={query} placeholder="Найти раздел…" onChange={event => { setQuery(event.target.value); setActive(0); }}
        onKeyDown={event => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (entries.length) setActive((active + (event.key === 'ArrowDown' ? 1 : -1) + entries.length) % entries.length);
          } else if (event.key === 'Enter' && entries[active]) {
            event.preventDefault();
            entries[active].run();
          }
        }} />
    </div>
    <div role="listbox" aria-label="Разделы" id={listId} className="quick-results">
      {entries.map((item, index) => <div key={item.title} role="option" id={listId + '-' + index}
        aria-selected={active === index} className="quick-option" onClick={item.run}>
        <Icon name={item.icon} /><span>{item.title}</span><small>{item.group}</small>
      </div>)}
    </div>
    {!entries.length && <p className="quick-empty" role="status">Разделы не найдены.</p>}
    <div className="quick-hint"><kbd>↑</kbd> <kbd>↓</kbd> Выбор <kbd>Enter</kbd> Открыть <kbd>Esc</kbd> Закрыть</div>
  </Dialog>;
}
