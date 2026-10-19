import test from 'node:test';
import assert from 'node:assert/strict';

async function runWorker({brokenTab=false,transport='blm',runtime='unity',metadata=false,metadataFails=false,jsonMetadata=false,receiverDown=false}={}) {
  const stored={token:'local-token'},listeners={},calls=[];
  const event=name=>({addListener:f=>{listeners[name]=f;}});
  globalThis.chrome={
    storage:{local:{get:async key=>typeof key==='string'?{[key]:stored[key]}:{...stored},
      set:async value=>Object.assign(stored,value)}},
    runtime:{id:'self',onInstalled:event('installed'),onStartup:event('startup'),onMessage:event('message'),
      sendMessage:async message=>{assert.equal(message.mode,'metadata');return {ok:true,product:{item_id:'123',vrchat_state:'yes'}};}},
    offscreen:{hasDocument:async()=>true},
    alarms:{create:()=>{},onAlarm:event('alarm')},
    tabs:{create:async options=>{calls.push(['tab',options]);if(brokenTab)throw new Error('blocked');return {id:17};}}
  };
  globalThis.fetch=async(url,options)=>{
    if(url==='https://booth.pm/ja/items/123.json') {
      calls.push(['json-metadata']);
      return {ok:jsonMetadata,status:jsonMetadata?200:404,json:async()=>({id:123,name:'Asset',tags:[{name:'ピアス'},{name:'ラムネ対応'}]})};
    }
    if(url==='https://booth.pm/ja/items/123') {
      calls.push(['public-metadata']);
      return {ok:!metadataFails,status:metadataFails?503:200,url,text:async()=>'<html></html>'};
    }
    assert.ok(url.startsWith('http://127.0.0.1:48763/v1/'));
    if(receiverDown)throw new TypeError('Failed to fetch');
    assert.equal(options.headers.Authorization,'Bearer local-token');
    const path=new URL(url).pathname,body=options.body&&JSON.parse(options.body);
    assert.equal(options.method,path==='/v1/status'?'GET':'POST');
    if(path==='/v1/next') assert.deepEqual(body,{});
    calls.push([path,body]);
    const value=path==='/v1/status'?{transport,runtime,last_catalog:Date.now()/1000,interval_hours:6,metadata_requests:metadata?['123']:[]}:
      path==='/v1/next'?{job:{id:'b'.repeat(32),name:'new.zip',item_id:'123',variation_id:'456',file_id:'789'}}:{ok:true};
    return {ok:true,json:async()=>value};
  };
  await import(`../worker.js?case=${Math.random()}`);
  listeners.alarm({name:'booth-sync'});
  for(let i=0;i<50&&!stored.lastResult;i++) await new Promise(r=>setTimeout(r,5));
  return {calls,stored};
}

test('dispatch once to official BLM URL without Chrome downloads API',async()=>{
  const {calls}=await runWorker();
  const tabs=calls.filter(c=>c[0]==='tab');assert.equal(tabs.length,1);
  assert.deepEqual(tabs[0][1],{url:'https://booth.pm/downloadables/789/deeplink?client=booth-library-manager&variation_id=456',active:true});
  assert.ok(!calls.some(c=>c[0]==='/v1/downloaded'||c[0]==='/v1/failed'));
  assert.deepEqual(calls.find(c=>c[0]==='/v1/browser-status')[1],{code:'connected'});
});
test('failed external-app handoff reported for review',async()=>{
  const {calls,stored}=await runWorker({brokenTab:true});
  assert.equal(calls.filter(c=>c[0]==='/v1/failed').length,1);
  assert.equal(calls.filter(c=>c[0]==='tab').length,1);
  assert.deepEqual(calls.find(c=>c[0]==='/v1/browser-status')[1],{code:'error'});
  assert.match(stored.lastResult,/BLM用リンクを開けません/);
});
test('old companion cannot silently use old normal-download transport',async()=>{
  const {calls,stored}=await runWorker({transport:'old'});
  assert.ok(!calls.some(c=>c[0]==='tab'||c[0]==='/v1/next'));
  assert.match(stored.lastResult,/Unity側.*更新/);
});
test('legacy Python companion cannot be mistaken for the Unity receiver',async()=>{
  const {calls,stored}=await runWorker({runtime:'python'});
  assert.ok(!calls.some(c=>c[0]==='tab'||c[0]==='/v1/next'));
  assert.match(stored.lastResult,/Unity側.*更新/);
  assert.doesNotMatch(stored.lastResult,/Python/);
});
test('closed Unity gives actionable feedback instead of Failed to fetch',async()=>{
  const {stored}=await runWorker({receiverDown:true});
  assert.match(stored.lastResult,/Unity/);
  assert.ok(!stored.lastResult.includes('Failed to fetch'));
});
test('item classification is posted before manual batch dispatch is evaluated',async()=>{
  const {calls}=await runWorker({metadata:true});
  const index=calls.findIndex(c=>c[0]==='/v1/metadata');
  assert.ok(index>=0 && index<calls.findIndex(c=>c[0]==='/v1/next'));
  assert.deepEqual(calls[index][1],{products:[{item_id:'123',vrchat_state:'yes'}]});
});
test('public JSON supplies client-rendered BOOTH tags without HTML fallback',async()=>{
  const {calls}=await runWorker({metadata:true,jsonMetadata:true});
  const product=calls.find(c=>c[0]==='/v1/metadata')[1].products[0];
  assert.deepEqual(product.tags,['ピアス','ラムネ対応']);
  assert.equal(product.tag_source_version,1);
  assert.ok(!calls.some(c=>c[0]==='public-metadata'));
});
test('failed metadata lookup does not lose the library or block unrelated jobs',async()=>{
  const {calls}=await runWorker({metadata:true,metadataFails:true});
  assert.deepEqual(calls.find(c=>c[0]==='/v1/metadata')[1],{products:[{item_id:'123',failed:true}]});
  assert.ok(calls.some(c=>c[0]==='/v1/next'));
});
