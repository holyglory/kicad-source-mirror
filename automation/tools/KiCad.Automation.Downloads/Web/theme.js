(() => {
  const root = document.documentElement;
  let saved = root.dataset.theme;
  if (!saved) { try { saved = localStorage.getItem('kicad-theme'); } catch {} }
  root.dataset.theme = saved === 'light' || saved === 'dark' ? saved : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
})();
