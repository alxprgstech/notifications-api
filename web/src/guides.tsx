import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import contract from '../../docs/CONTRACT.md?raw';
import security from '../../docs/SECURITY.md?raw';
import operations from '../../docs/OPERATIONS.md?raw';

export const guideLinks=[['contract','API, MCP и Python SDK'],['security','Вход и подтверждения'],['operations','Подключение и эксплуатация']];
export default function Guide(){
  const id=location.pathname.split('/').pop()??'contract';
  const contents:Record<string,string>={contract,security,operations};
  return <div className="guide-layout"><nav className="guide-nav" aria-label="Разделы документации"><a href="/docs">Справочник методов</a>{guideLinks.map(([key,title])=><a key={key} aria-current={id===key?'page':undefined} href={'/docs/guide/'+key}>{title}</a>)}</nav><article className="guide-body"><Markdown remarkPlugins={[remarkGfm]}>{contents[id]??'# Раздел не найден\n\nВыберите раздел документации слева.'}</Markdown></article></div>;
}
