import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:net';
import assert from 'node:assert/strict';

const root=fileURLToPath(new URL('../../',import.meta.url));
const artifact=new URL('../../artifacts/ui/',import.meta.url);await mkdir(artifact,{recursive:true});
const listener=createServer();await new Promise(resolve=>listener.listen(0,'127.0.0.1',resolve));const port=listener.address().port;await new Promise(resolve=>listener.close(resolve));
const origin=`http://127.0.0.1:${port}`;
const env=Object.fromEntries(Object.entries(process.env).filter(([k])=>!k.toLowerCase().startsWith('notify__')));
Object.assign(env,{ASPNETCORE_ENVIRONMENT:'Development',ASPNETCORE_URLS:origin,Notify__PublicUrl:origin,Notify__DataPath:fileURLToPath(new URL(`../../artifacts/ui/data-${Date.now()}/`,import.meta.url))});
const server=spawn('dotnet',[`${root}/src/Notify.Server/bin/Release/net10.0/Notify.Server.dll`,'--webroot',`${root}/web/dist`],{cwd:root,env,stdio:['ignore','pipe','pipe']});
let log='';server.stdout.on('data',b=>log+=b);server.stderr.on('data',b=>log+=b);
let browser;
try{
  const deadline=Date.now()+30000;while(true){try{if((await fetch(origin+'/health/live')).ok)break;}catch{}if(Date.now()>deadline)throw Error('UI test server not live');await new Promise(r=>setTimeout(r,200));}
  browser=await chromium.launch({headless:true});
  const context=await browser.newContext({viewport:{width:1440,height:1000}});const page=await context.newPage();const errors=[],external=[];
  page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>{if(!r.url().startsWith(origin)&&!r.url().startsWith('data:'))external.push(r.url());});
  await page.goto(origin);await page.getByRole('link',{name:/Войти через SSO/}).waitFor();await page.screenshot({path:fileURLToPath(new URL('login.png',artifact)),fullPage:true});
  const spec=await (await fetch(origin+'/openapi/v1.json')).json();assert.ok(spec.paths['/api/v1/notifications']);
  await page.goto(origin+'/docs');await page.getByRole('heading',{name:'Подключите свой источник',exact:true}).waitFor();await page.getByText('ALXPRGS Notify API',{exact:true}).first().waitFor({timeout:20000});
  await page.screenshot({path:fileURLToPath(new URL('docs.png',artifact)),fullPage:false});
  const guides={contract:'Контракт v1',security:'Безопасность и SSO',operations:'Установка и эксплуатация',home:'Notify на домашнем Windows-ПК'};
  for(const [id,heading] of Object.entries(guides)){await page.goto(origin+'/docs/guide/'+id);await page.getByRole('heading',{name:heading,exact:true}).waitFor({timeout:15000});await page.getByRole('navigation',{name:'Разделы документации'}).waitFor();}
  await page.screenshot({path:fileURLToPath(new URL('docs-guide.png',artifact)),fullPage:false});
  const id='a'.repeat(32);const now=Date.now();
  // Explicit UI fixtures only. Real authorization/CSRF/approvals are tested by Notify.Specs.
  const fixtures={session:{actor:{kind:'web'},csrfToken:'synthetic-ui-token'},notifications:{items:[{id,sourceId:'fixture',status:'pending',createdAt:now,previewOnly:true,content:{kind:'confirmation',title:'Подтвердить завершение задачи?',body:'Агент подготовил изменения и ожидает вашего решения в приложении.',securityLevel:'elevated',expiresAt:new Date(now+300000).toISOString()}}],next:null},sources:[{id:'fixture',name:'Агент разработки',preview:'hidden',scopes:['notify:send','agents:connect'],revoked:false}],devices:[{id:'phone',name:'Мой Android',revoked:false}],agents:[{id:'agent1',sourceId:'fixture',name:'Агент разработки',capabilities:['text_tasks','realtime_audio'],revoked:false,online:true,lastSeenAt:now,sessions:[{id:'task1',name:'Сборка'}]}],calls:[{id:'call1',sourceId:'fixture',agentId:'agent1',deviceId:'phone',sessionId:'task1',securityLevel:'standard',startedAt:now-60000,endedAt:now-30000,status:'ended',reason:'ended',durationMs:30000,direction:'outgoing'}]};
  await page.route('**/api/v1/**',async route=>{const path=new URL(route.request().url()).pathname.substring('/api/v1/'.length);if(path in fixtures)await route.fulfill({json:fixtures[path]});else await route.continue();});
  await page.goto(origin);await page.getByRole('heading',{name:'История',exact:true}).waitFor();await page.getByText('Подтвердить завершение задачи?',{exact:true}).waitFor();await page.screenshot({path:fileURLToPath(new URL('cabinet-light.png',artifact)),fullPage:true});
  await page.emulateMedia({colorScheme:'dark'});await page.screenshot({path:fileURLToPath(new URL('cabinet-dark.png',artifact)),fullPage:true});
  await page.setViewportSize({width:390,height:844});await page.screenshot({path:fileURLToPath(new URL('cabinet-mobile.png',artifact)),fullPage:true});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile cabinet overflows');
  await page.setViewportSize({width:1440,height:1000});await page.getByRole('button',{name:'Интеграции'}).click();await page.getByText('Агент «Агент разработки»',{exact:true}).waitFor();await page.getByText('На связи',{exact:true}).waitFor();await page.screenshot({path:fileURLToPath(new URL('sources-agents.png',artifact)),fullPage:true});
  await page.getByRole('button',{name:'Звонки'}).click();await page.getByText('История звонков',{exact:true}).waitFor();await page.getByText(/завершён · 30 с/).waitFor();await page.screenshot({path:fileURLToPath(new URL('calls-history.png',artifact)),fullPage:true});
  assert.deepEqual(errors,[]);assert.deepEqual(external,[],'Documentation attempted external asset requests');
  await writeFile(new URL('result.json',artifact),JSON.stringify({login:true,openapi:true,docs:true,guides:true,cabinet:'synthetic fixture',agents:true,calls:true,dark:true,mobileNoOverflow:true,pageErrors:errors,externalRequests:external},null,2));
  console.log('UI checks passed: public login/docs, fixture cabinet, both themes, mobile; screenshots artifacts/ui');
}finally{if(browser)await browser.close();server.kill();await writeFile(new URL('server.log',artifact),log);}
