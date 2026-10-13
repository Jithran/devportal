(() => {
  // Applied before first paint. app.js re-applies once /api/config tells the configured default.
  let stored = null;
  try { stored = JSON.parse(localStorage.getItem('devportal-theme'))?.value; } catch { }
  const preferred = matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  document.documentElement.dataset.theme = stored === 'light' || stored === 'dark' ? stored : preferred;
})();
