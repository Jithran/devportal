(() => {
  const PREFIX = 'project=';

  // The key holds a NUL and may hold any character a folder name can, so it is always percent-encoded.
  const encodeHash = key => `#${PREFIX}${encodeURIComponent(key)}`;

  function decodeHash(hash) {
    const raw = String(hash || '').replace(/^#/, '');
    if (!raw.startsWith(PREFIX)) return null;
    try { return decodeURIComponent(raw.slice(PREFIX.length)) || null; } catch { return null; }
  }

  // Attention items are tied to a project by name + folder, the same key the page uses for rows.
  const itemsFor = (items, key) => items.filter(i => i.project != null && `${i.project}\u0000${i.projectPath || ''}` === key);

  const api = { encodeHash, decodeHash, itemsFor };
  globalThis.DevPortalDrawer = api;
  if (typeof module !== 'undefined') module.exports = api;
})();
