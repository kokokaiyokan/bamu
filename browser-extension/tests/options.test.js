import test from 'node:test';
import assert from 'node:assert/strict';

test('popup shows stored failure details on open without requiring another sync',async()=>{
  const elements=new Map(['token','status','save','sync'].map(id=>[id,{textContent:'',addEventListener:()=>{}}]));
  globalThis.document={getElementById:id=>elements.get(id)};
  globalThis.chrome={storage:{local:{get:async key=>key==='token'?{token:'private-local-key'}:{lastResult:'BOOTHページを取得できません。'}}},
    runtime:{sendMessage:async()=>({browser_status:'ブラウザ側の確認に失敗しました。',
      products:[{registered:true},{registered:false}],files:[{},{}]})}};
  await import('../options.js');
  await new Promise(resolve=>setTimeout(resolve,0));
  const text=elements.get('status').textContent;
  assert.match(text,/直近の確認結果: BOOTHページを取得できません/);
  assert.match(text,/BLM登録済み 1商品 \/ 一覧取得済み 2商品/);
  assert.ok(!text.includes('private-local-key'));
});
