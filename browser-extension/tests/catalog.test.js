import test from 'node:test';
import assert from 'node:assert/strict';
import {parseCatalog, blmUrl} from '../catalog.js';

// Minimal DOM fixture follows BOOTH's observed card / row / dropdown structure.
function fixture({item='123',variation='456',host='booth.pm',name='衣装 v2.zip'}={}) {
  const body={};
  const link={getAttribute:()=>`https://booth.pm/ja/items/${item}`};
  const card={parentElement:body, querySelectorAll:()=>[link]};
  const row={parentElement:card,querySelectorAll:()=>[],querySelector:()=>({textContent:name})};
  const control={parentElement:row,getAttribute:()=>JSON.stringify([
    {deeplinkDownloadableUrl:`https://${host}/downloadables/789/deeplink?client=booth-library-manager&variation_id=${variation}`}
  ])};
  const next={getAttribute:()=>'/library?page=2'};
  return {body,querySelector:()=>null,querySelectorAll:s=>s==='[data-dropdown-items]'?[control]:[next]};
}
test('extract official IDs, Unicode filenames and pagination',()=>{
  const result=parseCatalog(fixture(),'https://accounts.booth.pm/library');
  assert.deepEqual(result.files,[{item_id:'123',variation_id:'456',file_id:'789',name:'衣装 v2.zip',item_name:''}]);
  assert.equal(result.next,'https://accounts.booth.pm/library?page=2');
});
test('reject unrelated download hosts and broken BOOTH structures',()=>{
  assert.throws(()=>parseCatalog(fixture({host:'example.com'}),'https://accounts.booth.pm/library'));
  assert.throws(()=>parseCatalog(fixture({variation:'invalid'}),'https://accounts.booth.pm/library'));
  assert.throws(()=>parseCatalog(fixture(),'https://example.com/library'));
});
test('official BLM handoff ignores injected URLs and rejects invalid identifiers',()=>{
  assert.equal(blmUrl({item_id:'123',variation_id:'456',file_id:'789',url:'https://example.com'}),
    'https://booth.pm/downloadables/789/deeplink?client=booth-library-manager&variation_id=456');
  assert.throws(()=>blmUrl({item_id:'123',variation_id:'../',file_id:'789'}));
});
