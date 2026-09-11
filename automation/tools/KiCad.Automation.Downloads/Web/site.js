(() => {
  const root = document.documentElement, toggle = document.getElementById('theme-toggle');
  const explicit = new URLSearchParams(location.search).get('theme');
  let manual = explicit === 'light' || explicit === 'dark';
  try { manual ||= !!localStorage.getItem('kicad-theme'); } catch {}
  function showTheme() {
    const dark = root.dataset.theme === 'dark';
    toggle.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
    document.getElementById('theme-icon').src = '/site/' + (dark ? 'sun' : 'moon') + '.svg';
  }
  toggle.hidden = false; showTheme();
  toggle.addEventListener('click', () => {
    root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark'; manual = true;
    try { localStorage.setItem('kicad-theme', root.dataset.theme); } catch {}
    const url = new URL(location.href); url.searchParams.delete('theme'); history.replaceState(null, '', url);
    showTheme();
  });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', event => {
    if (!manual) { root.dataset.theme = event.matches ? 'dark' : 'light'; showTheme(); }
  });
  const releases = document.getElementById('release-list');
  function openReleases() { if (location.hash === '#releases') releases.open = true; }
  openReleases(); window.addEventListener('hashchange', openReleases);
  document.querySelectorAll('a[href="#releases"]').forEach(link => link.addEventListener('click', () => { releases.open = true; }));
  const filter = document.getElementById('platform-filter');
  filter.parentElement.hidden = false;
  filter.addEventListener('change', () => {
    let count = 0;
    document.querySelectorAll('.release-row').forEach(row => {
      row.hidden = filter.value !== 'all' && row.dataset.platform !== filter.value;
      if (!row.hidden) count++;
    });
    document.getElementById('no-releases').hidden = count !== 0;
  });
  // Optional browser-provided CPU information improves the Mac recommendation.
  // No fingerprinting, network lookup or guess based on a misleading Intel UA.
  if (!new URLSearchParams(location.search).has('platform') && navigator.userAgentData?.getHighEntropyValues) {
    navigator.userAgentData.getHighEntropyValues(['architecture', 'bitness']).then(info => {
      if (navigator.userAgentData.mobile || info.platform !== 'macOS') return;
      const platform = info.architecture === 'arm' ? 'osx-arm64' : info.architecture === 'x86' && info.bitness === '64' ? 'osx-x64' : null;
      const data = JSON.parse(document.getElementById('download-data').textContent), build = data[platform];
      if (!build) return;
      const link = document.createElement('a'); link.className = 'button primary'; link.href = build.url; link.download = '';
      link.textContent = 'Download for macOS';
      const meta = document.createElement('p'); meta.className = 'download-meta'; meta.textContent = build.architecture + ' · ' + build.size + ' · Preview';
      document.getElementById('recommended').replaceChildren(link, meta);
    }).catch(() => {});
  }
})();
