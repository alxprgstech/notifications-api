import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import contract from '../../docs/CONTRACT.md?raw';
import security from '../../docs/SECURITY.md?raw';
import operations from '../../docs/OPERATIONS.md?raw';
import home from '../../docs/HOME_HOSTING.md?raw';

export const guideLinks=[['contract','API, MCP и Python SDK'],['security','Вход и подтверждения'],['operations','Подключение и эксплуатация'],['home','Домашний Docker']];
export default function Guide(){
  const id=location.pathname.split('/').pop()??'contract';
  const contents:Record<string,string>={contract,security,operations,home};
  const files:Record<string,string>={'CONTRACT.md':'contract','SECURITY.md':'security','OPERATIONS.md':'operations','HOME_HOSTING.md':'home'};
  return <div className="guide-layout"><nav className="guide-nav" aria-label="Разделы документации"><a href="/docs">Справочник методов</a>{guideLinks.map(([key,title])=><a key={key} aria-current={id===key?'page':undefined} href={'/docs/guide/'+key}>{title}</a>)}</nav><article className="guide-body"><Markdown remarkPlugins={[remarkGfm]} components={{a:({href,children,title})=><a href={href&&files[href]?'/docs/guide/'+files[href]:href} title={title}>{children}</a>}}>{contents[id]??'# Раздел не найден\n\nВыберите раздел документации слева.'}</Markdown></article></div>;
}
