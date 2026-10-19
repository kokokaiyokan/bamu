import test from 'node:test';
import assert from 'node:assert/strict';
import {parseMetadata,parseMetadataJson} from '../metadata.js';

function fixture({badge=false,tags=['accessory'],title='Asset'}={}) {
  const header={querySelector:s=>s==='h2'?{textContent:title}:s==='img[alt="VRChat"]'?(badge?{}:null):{textContent:'Shop'}};
  const tagList={querySelectorAll:()=>tags.map(t=>({getAttribute:()=>`/ja/items?tags%5B%5D=${encodeURIComponent(t)}`}))};
  return {querySelector:s=>s==='article header'?header:s==='#js-item-tag-list'?tagList:
    s==='meta[property="og:image"]'?{getAttribute:()=> 'https://booth.pximg.net/image'}:null};
}
test('public JSON tags are scoped to the matching product and exclude banner suggestions',()=>{
  const p=parseMetadataJson({id:123,name:'Asset',tags:[{name:'ピアス'},{name:'VRChat'},{name:'ピアス'}],
    tag_banners:[{name:'unrelated'}],category:{name:'3D装飾品'}},'123');
  assert.deepEqual(p.tags,['ピアス','VRChat']);assert.equal(p.vrchat_state,'yes');assert.equal(p.tag_source_version,1);
  assert.throws(()=>parseMetadataJson({id:999,name:'Other',tags:[]},'123'));
  assert.throws(()=>parseMetadataJson({id:123,name:'Asset'},'123'));
});
test('VRChat badge is authoritative even when client-rendered tags are empty',()=>{
  const p=parseMetadata(fixture({badge:true,tags:[]}),'https://booth.pm/ja/items/123','123');
  assert.equal(p.vrchat_state,'yes');assert.equal(p.shop,'Shop');assert.equal(p.thumbnail_url,'https://booth.pximg.net/image');
});
test('VRChat tag is scoped to current item; unrelated tags are not classified positive',()=>{
  assert.equal(parseMetadata(fixture({tags:['VRChat']}),'https://booth.pm/ja/items/123','123').vrchat_state,'yes');
  assert.equal(parseMetadata(fixture(),'https://booth.pm/ja/items/123','123').vrchat_state,'no');
  assert.equal(parseMetadata(fixture({tags:[]}),'https://booth.pm/ja/items/123','123').vrchat_state,'unknown');
});
test('wrong item pages and missing title cannot supply metadata',()=>{
  assert.throws(()=>parseMetadata(fixture(),'https://booth.pm/ja/items/999','123'));
  assert.throws(()=>parseMetadata(fixture(),'https://example.com/ja/items/123','123'));
  assert.throws(()=>parseMetadata(fixture({title:''}),'https://booth.pm/ja/items/123','123'));
});
