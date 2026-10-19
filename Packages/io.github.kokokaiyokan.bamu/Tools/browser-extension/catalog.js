// Parse only BOOTH-owned download controls. Never infer purchase entitlement
// from a public item title, a file's name, or a third-party URL.
export function parseCatalog(document, pageUrl) {
  const page = new URL(pageUrl);
  if (page.origin !== 'https://accounts.booth.pm' || !/^\/library(?:\/(?:gifts|free_downloads))?\/?$/.test(page.pathname))
    throw new Error('Unexpected BOOTH page');
  const files = [];
  const controls = [...document.querySelectorAll('[data-dropdown-items]')];
  for (const control of controls) {
    let entries;
    try { entries = JSON.parse(control.getAttribute('data-dropdown-items')); } catch { continue; }
    for (const entry of entries) {
      if (!entry.deeplinkDownloadableUrl) continue;
      const handoff = new URL(entry.deeplinkDownloadableUrl, pageUrl);
      if (handoff.origin !== 'https://booth.pm' || handoff.searchParams.get('client') !== 'booth-library-manager') continue;
      const match = handoff.pathname.match(/^\/downloadables\/([1-9][0-9]*)\/deeplink$/);
      const variation = handoff.searchParams.get('variation_id');
      if (!match || !/^[1-9][0-9]*$/.test(variation || '')) continue;
      // Find the nearest card containing an item link, not a page-wide container.
      let card = control.parentElement;
      let itemId = null;
      while (card && card !== document.body) {
        const ids = [...card.querySelectorAll('a[href]')].map(a => {
          try {
            const u = new URL(a.getAttribute('href'), pageUrl);
            return u.origin === 'https://booth.pm' ? u.pathname.match(/^\/(?:ja\/)?items\/([1-9][0-9]*)$/)?.[1] : null;
          } catch { return null; }
        }).filter(Boolean);
        const unique = [...new Set(ids)];
        if (unique.length === 1) { itemId = unique[0]; break; }
        if (unique.length > 1) break;
        card = card.parentElement;
      }
      if (!itemId) continue;
      let row = control.parentElement;
      let name = '';
      while (row && row !== card.parentElement) {
        const label = row.querySelector('.min-w-0');
        if (label) { name = label.textContent.trim(); break; }
        row = row.parentElement;
      }
      if (!name || /[\r\n\x00]/.test(name)) continue;
      const title = [...card.querySelectorAll('a[href]')].filter(a => {
        try { return new URL(a.getAttribute('href'),pageUrl).pathname.match(/\/(?:ja\/)?items\/([0-9]+)$/)?.[1] === itemId; }
        catch { return false; }
      }).map(a => a.textContent?.trim()).filter(Boolean).sort((a,b)=>b.length-a.length)[0] || '';
      files.push({item_id:itemId, variation_id:variation, file_id:match[1], name, item_name:title});
    }
  }
  // Fail closed if a changed page exposes download controls we cannot parse.
  if (controls.length > 0 && files.length === 0) throw new Error('BOOTH page structure changed');
  const links = [...document.querySelectorAll('a[href]')].map(a => {
    try { return new URL(a.getAttribute('href'), pageUrl); } catch { return null; }
  }).filter(u => u && u.origin === page.origin && u.pathname.replace(/\/$/, '') === page.pathname.replace(/\/$/, ''));
  const current = Number(page.searchParams.get('page') || 1);
  const next = links.filter(u => Number(u.searchParams.get('page') || 1) === current + 1)[0]?.href || null;
  const login = !!document.querySelector('form[action*="sign_in"], input[type="password"]');
  return {files, next, login};
}

export function blmUrl(job) {
  if (![job.item_id,job.variation_id,job.file_id].every(s => /^[1-9][0-9]{0,17}$/.test(s))) throw new Error('Invalid BOOTH file ID');
  return `https://booth.pm/downloadables/${job.file_id}/deeplink?client=booth-library-manager&variation_id=${job.variation_id}`;
}
