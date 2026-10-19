import {parseCatalog} from './catalog.js';
import {parseMetadata} from './metadata.js';
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message.target !== 'parser' || sender.id !== chrome.runtime.id) return;
  try {
    const document = new DOMParser().parseFromString(message.html, 'text/html');
    reply({ok:true,...(message.mode==='metadata'?{product:parseMetadata(document,message.url,message.item_id)}:parseCatalog(document,message.url))});
  } catch { reply({ok:false,error:'BOOTHのページ構造を確認できません。'}); }
});
