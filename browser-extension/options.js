const token=document.getElementById('token'), status=document.getElementById('status');
token.value=(await chrome.storage.local.get('token')).token||'';
async function refresh(){
 const result=await chrome.runtime.sendMessage({type:'status'});
 const {lastResult}=await chrome.storage.local.get('lastResult');
 const registered=result.products?.filter(p=>p.registered).length||0;
 status.textContent=result.error||`${result.browser_status}\nBLM登録済み ${registered}商品 / 一覧取得済み ${result.products.length}商品 / 配布ファイル ${result.files.length}件`;
 if(lastResult)status.textContent+='\n直近の確認結果: '+lastResult;
}
document.getElementById('save').addEventListener('click',async()=>{
 await chrome.storage.local.set({token:token.value.trim()});
 await refresh();
});
document.getElementById('sync').addEventListener('click',async()=>{
 status.textContent='BOOTHの購入済み・ギフト・無料ライブラリを確認しています…';
 await chrome.runtime.sendMessage({type:'sync'});await refresh();
});
refresh().catch(()=>{status.textContent='Unityを開き、接続キーを設定してください。';});
