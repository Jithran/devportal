(() => {
  const root = document.getElementById('projects');
  const search = document.getElementById('search');
  const showSystem = document.getElementById('show-system');
  const updated = document.getElementById('updated');
  const dockerError = document.getElementById('docker-error');
  const summary = document.getElementById('summary');
  let snapshot = null;
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
        const instances = version.instances.filter(i => !query || haystack(project, version, i).includes(query));
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
    renderSummary();
  }

  function renderSummary() {
    const instances = snapshot.projects.flatMap(p => p.versions.flatMap(v => v.instances));
    const projects = snapshot.projects.filter(p => p.kind === 'repo' || p.kind === 'compose').length;
    const containers = new Set(instances.filter(i => i.source === 'docker').map(i => i.containerName)).size;
    const count = (n, word) => el('span', {}, el('strong', { text: String(n) }), ` ${word}${n === 1 ? '' : 's'}`);
    summary.replaceChildren(
      count(instances.length, 'instance'),
      count(projects, 'project'),
      count(containers, 'container'),
      el('span', {}, el('span', { class: `dot ${snapshot.dockerError ? 'off' : 'on'}` }), snapshot.dockerError ? 'Docker unavailable' : 'Docker connected'));
  }

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
      updated.textContent = `Updated ${new Date(snapshot.generatedAt).toLocaleTimeString()}`;
      dockerError.hidden = !snapshot.dockerError;
      dockerError.textContent = snapshot.dockerError || '';
      render();
    } catch (error) {
      updated.textContent = `Refresh failed: ${error.message}`;
    }
  }

  async function tick() {
    clearTimeout(timer);
    if (!document.hidden) {
      await loadConfig();
      await load();
    }
    timer = setTimeout(tick, config.refreshSeconds * 1000);
  }

  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
  tick();
})();
