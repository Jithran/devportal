(() => {
  const stored = localStorage.getItem('devportal-theme');
  const preferred = matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  document.documentElement.dataset.theme = stored || preferred;
})();
