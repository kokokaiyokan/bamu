// Only the current item's header and tag list count, never recommendations.
export function parseMetadataJson(value,itemId) {
  if(String(value?.id)!==itemId || typeof value.name!=='string' || !value.name.trim() || !Array.isArray(value.tags))
    throw new Error('Unexpected item metadata');
  const tags=[...new Set(value.tags.map(t=>t?.name).filter(t=>typeof t==='string'&&t.trim()).map(t=>t.trim()))];
  return {item_id:itemId,name:value.name.trim(),shop:value.shop?.name||'',thumbnail_url:value.images?.[0]?.original||'',
    category:value.category?.name||'',tags,tag_source_version:1,
    vrchat_state:tags.some(t=>/^(VRChat|VRC想定モデル)$/i.test(t))?'yes':tags.length?'no':'unknown'};
}
export function parseMetadata(document,pageUrl,itemId) {
  const url=new URL(pageUrl);
  if(url.origin!=='https://booth.pm'||!new RegExp(`^/(?:ja/)?items/${itemId}$`).test(url.pathname))
    throw new Error('Unexpected item page');
  const header=document.querySelector('article header');
  const tagsRoot=document.querySelector('#js-item-tag-list');
  const tags=[...tagsRoot?.querySelectorAll('a[href]')||[]].map(a=>{
    try{return new URL(a.getAttribute('href'),pageUrl).searchParams.get('tags[]');}catch{return null;}
  }).filter(Boolean);
  const badge=!!header?.querySelector('img[alt="VRChat"]');
  const title=header?.querySelector('h2')?.textContent?.trim();
  if(!title)throw new Error('Item title missing');
  const shop=header.querySelector('a[href*=".booth.pm/"] span')?.textContent?.trim()||'';
  const image=document.querySelector('meta[property="og:image"]')?.getAttribute('content')||'';
  const category=document.querySelector('#js-item-category-breadcrumbs')?.textContent?.trim()||'';
  return {item_id:itemId,name:title,shop,thumbnail_url:image,tags:[...new Set(tags)],category,
    vrchat_state:badge||tags.some(t=>/^(VRChat|VRC想定モデル)$/i.test(t))?'yes':tags.length?'no':'unknown'};
}
