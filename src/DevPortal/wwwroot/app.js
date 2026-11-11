(() => {
  const root = document.getElementById('projects');
  const search = document.getElementById('search');
  const showSystem = document.getElementById('show-system');
  const updated = document.getElementById('updated');
  const dockerError = document.getElementById('docker-error');
  const offline = document.getElementById('offline');
  const kpis = document.getElementById('kpis');
  const { classify, summarize } = DevPortalStats;
  const filter = { status: 'all', type: 'all' };
  // Each group is one chip row; add a group here and the filter bar and the row filter follow.
  const FILTERS = [
    { key: 'status', label: 'Status', options: [['all', 'All'], ['ok', 'Healthy'], ['warning', 'Warnings'], ['failing', 'Failing']], matches: (i, v) => classify(i) === v },
    { key: 'type', label: 'Type', options: [['all', 'All'], ['web', 'Web'], ['other', 'Other']], matches: (i, v) => !!i.statusCode === (v === 'web') },
  ];
  let snapshot = null;
  let refreshError = null;
  let scanAges = [];
  let config = { refreshSeconds: 5, showSystemByDefault: false, theme: 'system' };
  let timer = null;
  const expanded = new Set();

  function toggleExpanded(name, open) {
    if (search.value.trim()) return;
    if (open) expanded.add(name); else expanded.delete(name);
  }

  // A choice made on the page wins until the default in Settings changes; then the new default applies.
  function choice(key, setting) {
    try {
      const stored = JSON.parse(localStorage.getItem(key));
      if (stored && stored.basedOn === setting) return stored.value;
    } catch { }
    return null;
  }

  function remember(key, value, setting) {
    localStorage.setItem(key, JSON.stringify({ value, basedOn: setting }));
  }

  function applyConfig() {
    const osTheme = matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    document.documentElement.dataset.theme = choice('devportal-theme', config.theme) ?? (config.theme === 'system' ? osTheme : config.theme);
    showSystem.checked = choice('devportal-system', config.showSystemByDefault) ?? config.showSystemByDefault;
    document.getElementById('version').textContent = `v${config.version}`;
    document.getElementById('release-notes').href = `${config.repositoryUrl}/releases/tag/v${config.version}`;
    document.getElementById('repository').href = config.repositoryUrl;
  }

  document.getElementById('theme').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    document.documentElement.dataset.theme = next;
    remember('devportal-theme', next, config.theme);
  });
  showSystem.addEventListener('change', () => {
    remember('devportal-system', showSystem.checked, config.showSystemByDefault);
    load();
  });
  search.addEventListener('input', render);

  const tabs = [...document.querySelectorAll('[role="tab"]')];
  function selectTab(tab) {
    for (const t of tabs) {
      const selected = t === tab;
      t.setAttribute('aria-selected', selected);
      t.tabIndex = selected ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !selected;
    }
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => selectTab(tab));
    tab.addEventListener('keydown', e => {
      const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
      if (!step) return;
      const next = tabs[(index + step + tabs.length) % tabs.length];
      selectTab(next);
      next.focus();
    });
  });

  const filterBar = document.getElementById('filters');
  function renderFilters() {
    filterBar.replaceChildren(...FILTERS.map(group => el('div', { class: 'seg', role: 'group', 'aria-label': group.label },
      el('span', { class: 'lbl', text: group.label }),
      group.options.map(([value, text]) => el('button', {
        class: 'chip', type: 'button', text,
        'aria-pressed': String(filter[group.key] === value),
        onclick: () => { filter[group.key] = value; renderFilters(); render(); },
      })))));
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (value === undefined || value === null) continue;
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key.startsWith('data-')) node.setAttribute(key, value);
      else node[key] = value;
    }
    for (const child of children.flat(Infinity)) {
      if (child !== null && child !== undefined) node.append(child);
    }
    return node;
  }

  function uptime(seconds) {
    if (seconds === undefined || seconds === null) return '';
    const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
    if (d) return `${d}d ${h}h`;
    if (h) return `${h}h ${m}m`;
    return `${m}m`;
  }

  function haystack(project, version, instance) {
    return [project.name, project.path, version.label, version.worktree, version.commit, instance.port, instance.title,
      instance.processName, instance.appName, instance.containerName, instance.image, instance.composeService, instance.kind, instance.url]
      .filter(Boolean).join(' ').toLowerCase();
  }

  async function copy(text, button) {
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Failed';
    }
    setTimeout(() => { button.textContent = 'Copy'; }, 1200);
  }

  function statusBadge(instance) {
    if (instance.statusCode) {
      const tone = instance.statusCode < 400 ? 'ok' : instance.statusCode < 500 ? 'warn' : 'bad';
      return el('span', { class: `badge ${tone}`, text: String(instance.statusCode) });
    }
    if (instance.probeError) return el('span', { class: 'badge muted', text: instance.probeError });
    return null;
  }

  function row(instance) {
    const owner = instance.source === 'docker'
      ? el('span', { title: instance.image, text: instance.containerName })
      : el('span', { title: instance.commandLine || '', text: `${instance.processName} · ${instance.pid}` });
    const isWeb = !!instance.statusCode;
    const actions = instance.url ? [
      isWeb ? el('a', { class: 'btn', href: instance.url, target: '_blank', rel: 'noopener noreferrer', text: 'Open' }) : null,
      el('button', { class: 'btn', type: 'button', text: 'Copy', onclick: e => copy(instance.url, e.currentTarget) }),
    ] : [];

    return el('tr', { 'data-kind': instance.kind },
      el('td', { class: 'port' }, instance.port
        ? (isWeb ? el('a', { href: instance.url, target: '_blank', rel: 'noopener noreferrer', text: String(instance.port) }) : String(instance.port))
        : '—'),
      el('td', {}, el('span', { class: `kind kind-${instance.kind}`, text: instance.composeService || instance.kind })),
      el('td', { class: 'title' },
        instance.title || el('span', { class: 'muted', title: 'No <title> in the server-rendered HTML', text: instance.source === 'docker' ? instance.image : instance.appName }),
        ' ', statusBadge(instance)),
      el('td', { class: 'muted', title: instance.containerStatus || '' }, uptime(instance.uptimeSeconds)),
      el('td', { class: 'owner' }, owner),
      el('td', { class: 'actions' }, actions));
  }

  function render() {
    if (!snapshot) return;
    const query = search.value.trim().toLowerCase();
    const sections = [];

    for (const project of snapshot.projects) {
      const versions = [];
      for (const version of project.versions) {
        const instances = version.instances.filter(i =>
          (!query || haystack(project, version, i).includes(query))
          && FILTERS.every(g => filter[g.key] === 'all' || g.matches(i, filter[g.key])));
        if (!instances.length) continue;
        versions.push(el('section', { class: 'version' },
          el('h3', {},
            el('span', { class: 'branch', text: version.label }),
            version.worktree ? el('span', { class: 'tag', text: `worktree ${version.worktree}` }) : null,
            version.commit ? el('code', { class: 'muted', text: version.commit }) : null,
            version.path ? el('span', { class: 'path muted', text: version.path }) : null),
          el('table', {},
            el('thead', {}, el('tr', {}, ['Port', 'Kind', 'Title', 'Uptime', 'Process', ''].map(h => el('th', { text: h })))),
            el('tbody', {}, instances.map(row)))));
      }
      if (!versions.length) continue;
      const heading = el('h2', {}, project.name, el('span', { class: 'tag', text: project.kind }),
        project.path ? el('span', { class: 'path muted', text: project.path }) : null);
      const collapsible = project.kind === 'other' || project.kind === 'system';
      sections.push(el('article', { class: `project project-${project.kind}`, 'data-project': project.name },
        collapsible
          ? el('details', { open: !!query || expanded.has(project.name), ontoggle: e => toggleExpanded(project.name, e.currentTarget.open) },
              el('summary', {}, heading), versions)
          : [heading, versions]));
    }

    root.replaceChildren(...(sections.length ? sections : [el('p', { class: 'muted', text: query ? 'No matches.' : 'Nothing is listening.' })]));
    root.setAttribute('aria-busy', 'false');
  }

  const SVG = 'http://www.w3.org/2000/svg';
  function svg(tag, attrs, ...children) {
    const node = document.createElementNS(SVG, tag);
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
    node.append(...children);
    return node;
  }

  function ring(fraction, color, label) {
    const circle = extra => svg('circle', { cx: 18, cy: 18, r: 15, fill: 'none', 'stroke-width': 4, ...extra });
    return svg('svg', { class: 'ring', viewBox: '0 0 36 36', role: 'img', 'aria-label': label },
      circle({ stroke: 'var(--panel-2)' }),
      circle({ stroke: color, 'stroke-linecap': 'round', 'stroke-dasharray': `${fraction * 94.2} 94.2`, transform: 'rotate(-90 18 18)' }),
      svg('text', { x: 18, y: 22, 'text-anchor': 'middle', 'font-size': 10, 'font-weight': 700, fill: 'var(--text)' }, `${Math.round(fraction * 100)}%`));
  }

  // The page CSP blocks inline style attributes; setting a property through the style object is allowed.
  function split(parts) {
    return el('div', { class: 'split', 'aria-hidden': 'true' }, parts.filter(([, weight]) => weight > 0).map(([className, weight]) => {
      const node = el('i', { class: className });
      node.style.flexGrow = weight;
      return node;
    }));
  }

  const badge = (tone, text) => el('span', { class: `badge ${tone}`, text });
  const tile = (label, ...content) => el('div', { class: 'kpi' }, el('div', { class: 'label', text: label }), content);
  const value = (main, small) => el('div', { class: 'value' }, main, small ? el('small', { text: ` ${small}` }) : null);

  const VERDICTS = {
    ok: ['Healthy', 'var(--ok)'],
    warning: ['Warnings', 'var(--warn)'],
    failing: ['Failing', 'var(--bad)'],
    idle: ['Idle', 'var(--muted)'],
  };

  // The slots stay empty until the data exists (response-time history, scan duration).
  function renderKpis() {
    const s = summarize(snapshot);
    const [verdict, color] = VERDICTS[s.verdict];
    const webFraction = s.web.total ? s.web.up / s.web.total : 1;
    const down = s.web.total - s.web.up;
    const ageValue = el('span');
    scanAges = [ageValue];

    kpis.replaceChildren(
      el('div', { class: `kpi health ${s.verdict}` },
        ring(webFraction, color, `${Math.round(webFraction * 100)}% of web endpoints up`),
        el('div', {},
          el('div', { class: 'label', text: 'Health' }),
          el('div', { class: 'value', text: verdict }),
          el('div', { class: 'sub' },
            s.failing ? badge('bad', `${s.failing} failing`) : null,
            s.warnings ? badge('warn', `${s.warnings} ${s.warnings === 1 ? 'warning' : 'warnings'}`) : null,
            !s.failing && !s.warnings ? el('span', { text: s.verdict === 'idle' ? 'Nothing is listening' : 'Nothing needs attention' }) : null))),
      tile('Projects',
        value(String(s.projects.running), 'running'),
        split([['ok', s.projects.healthy], ['warning', s.projects.warning], ['failing', s.projects.failing]]),
        el('div', { class: 'sub', text: `${s.projects.healthy} healthy · ${s.projects.warning} with warnings · ${s.projects.failing} failing` })),
      tile('Web endpoints up',
        el('div', { class: 'row' }, value(String(s.web.up), `/ ${s.web.total}`), el('span', { class: 'slot', 'data-slot': 'web-p50' })),
        el('div', { class: 'sub', text: down ? `${down} returning an error` : 'All responding' })),
      tile('Containers',
        value(String(s.containers), 'running'),
        split([['brand', s.containers]]),
        el('div', { class: 'sub' },
          snapshot.dockerError
            ? [el('span', { class: 'dot warn' }), 'Docker unavailable']
            : [el('span', { class: 'dot up' }), `${s.projects.compose} compose ${s.projects.compose === 1 ? 'project' : 'projects'}`])),
      tile('Listening ports',
        value(String(s.ports.total)),
        el('div', { class: 'sub' },
          badge(s.ports.open ? 'warn' : 'neutral', `${s.ports.open} on 0.0.0.0`),
          badge('neutral', `${s.ports.unknown} unknown`))),
      tile('Last scan',
        el('div', { class: 'row' }, el('div', { class: 'value' }, ageValue, el('small', { text: ' ago' })), el('span', { class: 'slot', 'data-slot': 'scan-duration' })),
        el('div', { class: 'sub' }, 'every ', el('strong', { text: `${config.refreshSeconds}s` }))));

    document.getElementById('count-projects').textContent = String(snapshot.projects.length);
    document.getElementById('count-ports').textContent = String(s.ports.total);
  }

  function paintAge() {
    if (!snapshot) return;
    const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(snapshot.generatedAt)) / 1000));
    const age = seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m`;
    scanAges.forEach(node => { node.textContent = age; });
    updated.textContent = refreshError ? `Refresh failed: ${refreshError}` : `Scanned ${age} ago · every ${config.refreshSeconds}s`;
  }
  setInterval(paintAge, 1000);

  async function loadConfig() {
    try {
      const response = await fetch('/api/config', { cache: 'no-store' });
      if (!response.ok) return;
      const next = await response.json();
      const changed = JSON.stringify(next) !== JSON.stringify(config);
      config = next;
      if (changed) applyConfig();
    } catch { }
  }

  async function load() {
    try {
      const response = await fetch(`/api/instances?all=${showSystem.checked}`, { cache: 'no-store' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      snapshot = await response.json();
      refreshError = null;
      dockerError.hidden = !snapshot.dockerError;
      dockerError.textContent = snapshot.dockerError || '';
      renderKpis();
      paintAge();
      render();
      // Back after a restart or update: reload so the page runs the assets of the version now serving it.
      if (!offline.hidden) location.reload();
    } catch (error) {
      if (error instanceof TypeError) offline.hidden = false;
      else { refreshError = error.message; paintAge(); }
    }
  }

  async function tick() {
    clearTimeout(timer);
    if (!document.hidden) {
      await loadConfig();
      await load();
    }
    timer = setTimeout(tick, offline.hidden ? config.refreshSeconds * 1000 : 1000);
  }

  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
  renderFilters();
  tick();
})();
