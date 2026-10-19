import {blmUrl} from './catalog.js';
import {parseMetadataJson} from './metadata.js';
const BASE = 'http://127.0.0.1:48763';
let running = false;
let parserPromise;

async function api(path, body) {
  const {token} = await chrome.storage.local.get('token');
  if (!token) throw new Error('接続キーを設定してください。');
  let response;
  try {
    response = await fetch(BASE+path,{method:body===undefined?'GET':'POST',
      headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},
      body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(15000)});
  } catch {
    throw new Error('Unityの受信へ接続できません。BOOTH Asset Management Utilityを導入したUnityを開き、コンパイル完了後にもう一度確認してください。');
  }
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || 'Unityの受信へ接続できません。');
  return result;
}
async function parse(html,url,extra={}) {
  if (!await chrome.offscreen.hasDocument()) {
    parserPromise ??= chrome.offscreen.createDocument({url:'offscreen.html',reasons:['DOM_PARSER'],
      justification:'BOOTHライブラリの配布ファイル情報を、スクリプトを実行せず読み取るため'}).finally(()=>{parserPromise=undefined;});
    await parserPromise;
  }
  const result = await chrome.runtime.sendMessage({target:'parser',html,url,...extra});
  if (!result?.ok) throw new Error('BOOTHページの形式が変わった可能性があります。');
  return result;
}
async function catalog() {
  const files = [], seen = new Set();
  let complete = true;
  // These are the three library source tabs actually exposed by BOOTH.
  for (const source of ['','/gifts','/free_downloads']) {
    let url = `https://accounts.booth.pm/library${source}`;
    for (let page=0; url && page<50; page++) {
      if (seen.has(url)) throw new Error('BOOTHのページ送りを確認できません。');
      seen.add(url);
      const response = await fetch(url,{credentials:'include',cache:'no-store',signal:AbortSignal.timeout(25000)});
      if (response.status===429) throw new Error('rate_limit');
      if (response.status===401 || response.status===403 || /sign_in|login/.test(response.url)) throw new Error('login');
      if (!response.ok) throw new Error('BOOTHページを取得できません。');
      const parsed = await parse(await response.text(),response.url);
      if (parsed.login) throw new Error('login');
      files.push(...parsed.files); url=parsed.next;
      if (url) await new Promise(resolve=>setTimeout(resolve,1500));
    }
    if (url) complete=false;
  }
  await api('/v1/catalog',{files,complete});
}
async function enrich(items) {
  for(const item_id of items) {
    if(!/^[1-9][0-9]{0,17}$/.test(item_id))continue;
    let product;
    try {
      // Tags are client-rendered on current BOOTH pages. The public item JSON
      // contains only this item's tags, without recommendations or HTML scripts.
      const response=await fetch(`https://booth.pm/ja/items/${item_id}.json`,{credentials:'include',cache:'no-store',signal:AbortSignal.timeout(25000)});
      if(response.status===429)throw new Error('rate_limit');
      if(!response.ok)throw new Error('metadata');
      product=parseMetadataJson(await response.json(),item_id);
    } catch(error) {
      if(error.message==='rate_limit')throw error;
    }
    if(!product) try {
      const response=await fetch(`https://booth.pm/ja/items/${item_id}`,{credentials:'include',cache:'no-store',signal:AbortSignal.timeout(25000)});
      if(response.status===429)throw new Error('rate_limit');
      if(!response.ok)throw new Error('metadata');
      const parsed=await parse(await response.text(),response.url,{mode:'metadata',item_id});
      product=parsed.product;
    } catch(error) {
      if(error.message==='rate_limit')throw error;
      product={item_id,failed:true};
    }
    await api('/v1/metadata',{products:[product]});
  }
}
async function tick(force=false) {
  if (running) return;
  running=true;
  try {
    const {backoffUntil=0} = await chrome.storage.local.get('backoffUntil');
    if (Date.now()<backoffUntil && !force) return;
    let status = await api('/v1/status');
    if (status.transport !== 'blm' || status.runtime !== 'unity') throw new Error('Unity側のBOOTH Asset Management Utilityを更新し、コンパイル完了後にもう一度確認してください。');
    if (force || status.requested || Date.now()/1000-status.last_catalog>=status.interval_hours*3600) {
      await catalog();status=await api('/v1/status');
    }
    await enrich(status.metadata_requests||[]);
    const {job} = await api('/v1/next',{});
    if (job) {
      try {
        // Opening the official handoff URL lets BOOTH and BLM handle authentication,
        // app launch, item registration and download. No signed URLs leave Chrome.
        await chrome.tabs.create({url:blmUrl(job),active:true});
      } catch {
        await api('/v1/failed',{job_id:job.id});
        throw new Error('BLM用リンクを開けませんでした。ChromeとBLMの状態を確認してください。');
      }
    }
    // A previous transient failure must not remain visible after recovery.
    await api('/v1/browser-status',{code:'connected'});
    await chrome.storage.local.set({lastResult:'接続済み。商品一覧・分類と、手動で一括取得を指示したファイルを確認しています。'});
  } catch (error) {
    const code=error.message==='login'?'login':error.message==='rate_limit'?'rate_limit':'error';
    if (code==='rate_limit') await chrome.storage.local.set({backoffUntil:Date.now()+30*60*1000});
    await api('/v1/browser-status',{code}).catch(()=>{});
    await chrome.storage.local.set({lastResult:code==='login'?'ChromeでBOOTHへログインしてください。':code==='rate_limit'?'BOOTHの制限で30分待機します。':error.message});
  } finally { running=false; }
}
chrome.runtime.onInstalled.addListener(()=>chrome.alarms.create('booth-sync',{periodInMinutes:1}));
chrome.runtime.onStartup.addListener(()=>{chrome.alarms.create('booth-sync',{periodInMinutes:1});tick();});
chrome.alarms.onAlarm.addListener(alarm=>{if(alarm.name==='booth-sync')tick();});
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if (sender.id!==chrome.runtime.id || message.target==='parser') return;
  if (message.type==='sync') {tick(true).then(()=>reply({ok:true}));return true;}
  if (message.type==='status') {api('/v1/status').then(reply).catch(error=>reply({error:error.message}));return true;}
});
