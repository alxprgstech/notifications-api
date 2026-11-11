
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import contract from '../../docs/CONTRACT.md?raw';
import security from '../../docs/SECURITY.md?raw';
import operations from '../../docs/OPERATIONS.md?raw';
import integrations from '../../docs/INTEGRATIONS.md?raw';
import home from '../../docs/HOME_HOSTING.md?raw';
import { Icon, guideLinks } from './ui';

const contents: Record<string, string> = { contract, security, operations, home, integrations };
const files: Record<string, string> = {
  'CONTRACT.md': 'contract', 'SECURITY.md': 'security', 'OPERATIONS.md': 'operations', 'HOME_HOSTING.md': 'home', 'INTEGRATIONS.md': 'integrations',
};

export default function Guide() {
  const id = location.pathname.split('/').filter(Boolean).pop() ?? '';
  return <main className="guide-layout">
    <nav className="guide-nav panel" aria-label="Разделы документации">
      <p className="nav-caption">ДОКУМЕНТАЦИЯ</p>
      <a href="/docs"><Icon name="book" size={18} /> Справочник методов</a>
      {guideLinks.map(([key, title]) => <a key={key} aria-current={id === key ? 'page' : undefined}
        href={'/docs/guide/' + key}>{title}</a>)}
    </nav>
    <article className="guide-body panel">
      <Markdown remarkPlugins={[remarkGfm]} components={{
        a: ({ href, children, title }) => <a className={href?.startsWith("https://my.home-assistant.io/redirect/") ? "button secondary" : undefined} href={href && files[href] ? '/docs/guide/' + files[href] : href} title={title}>{children}</a>,
        table: ({ children }) => <div className="table-scroll"><table>{children}</table></div>,
      }}>{Object.hasOwn(contents, id) ? contents[id] : '# Раздел не найден\n\nВыберите раздел документации в навигации.'}</Markdown>
    </article>
  </main>;
}
