(() => {
  const root = document.getElementById('projects');
  const search = document.getElementById('search');
  const showSystem = document.getElementById('show-system');
  const updated = document.getElementById('updated');
  const dockerError = document.getElementById('docker-error');
  const offline = document.getElementById('offline');
  const kpis = document.getElementById('kpis');
  const { classify, summarize, projectStatus, primaryVersion, owners, resourceTotals } = DevPortalStats;
  const { matchesFilters, flatten, filterRows, sortRows } = DevPortalPorts;
  const { median, medianSeries, sumSeries, sparkline } = DevPortalSpark;
  // One state object for both tabs, kept in localStorage so a reload keeps it.
  const FILTER_KEY = 'devportal-filters';
  const SORT_KEY = 'devportal-ports-sort';
  const stored = (key, fallback) => {
    try { return { ...fallback, ...JSON.parse(localStorage.getItem(key)) }; } catch { return fallback; }
  };
  const filter = stored(FILTER_KEY, { status: 'all', type: 'all' });
  const sort = stored(SORT_KEY, { key: 'port', direction: 'asc' });
  // Each group is one chip row; add a group here and the filter bar follows (the predicate lives in ports.js).
  const FILTERS = [
    { key: 'status', label: 'Status', options: [['all', 'All'], ['ok', 'Healthy'], ['warning', 'Warnings'], ['failing', 'Failing']] },
    { key: 'type', label: 'Type', options: [['all', 'All'], ['web', 'Web'], ['database', 'Database'], ['other', 'Other']] },
  ];
  const portsRoot = document.getElementById('view-ports');
  let snapshot = null;
  let refreshError = null;
  let scanAges = [];
  let config = { refreshSeconds: 5, showSystemByDefault: false, theme: 'system' };
  let timer = null;
  // Keyed by name + path: the same project name can live in two folders.
  const expanded = new Set();
  const projectKey = project => `${project.name}\u0000${project.path || ''}`;

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
  let searchTimer = null;
  search.addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(render, 150);
  });

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
        onclick: () => { filter[group.key] = value; localStorage.setItem(FILTER_KEY, JSON.stringify(filter)); renderFilters(); render(); },
      })))));
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (value === undefined || value === null) continue;
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key.startsWith('data-') || key.startsWith('aria-')) node.setAttribute(key, value);
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

  async function copy(text, button, label = 'Copy') {
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Failed';
    }
    setTimeout(() => { button.textContent = label; }, 1200);
  }

  function statusBadge(instance) {
    if (instance.statusCode) {
      const tone = instance.statusCode < 400 ? 'ok' : instance.statusCode < 500 ? 'warn' : 'bad';
      return el('span', { class: `badge ${tone}`, text: String(instance.statusCode) });
    }
    if (instance.probeError) return el('span', { class: 'badge neutral', text: instance.probeError });
    return null;
  }

  const STATUS_LABEL = { running: 'Running', degraded: 'Degraded', stopped: 'Stopped' };
  const STATUS_DOT = { running: 'up', degraded: 'failing', stopped: 'off' };
  const pillTone = i => !i.statusCode ? 'neutral' : i.statusCode < 400 ? 'ok' : i.statusCode < 500 ? 'warn' : 'bad';
  const isWeb = i => !!(i.statusCode && i.url);

  function endpoints(instances) {
    const byPort = new Map();
    for (const i of instances) if (i.port && !byPort.has(i.port)) byPort.set(i.port, i);
    return [...byPort.values()].sort((a, b) => a.port - b.port);
  }

  function pill(instance) {
    const props = { class: `pill ${pillTone(instance)}`, text: String(instance.port), title: instance.statusCode ? `HTTP ${instance.statusCode}` : instance.probeError || 'Not an HTTP endpoint' };
    return isWeb(instance)
      ? el('a', { ...props, href: instance.url, target: '_blank', rel: 'noopener noreferrer' })
      : el('span', props);
  }

  const formatMs = ms => ms < 10 ? ms.toFixed(1) : String(Math.round(ms));

  // Probes run about every ProbeCacheSeconds, so this is a sample per probe, not per request.
  function responseMetric(instance) {
    if (instance.responseMs == null) return null;
    const history = instance.responseHistory || [];
    const gaps = history.filter(v => v === null).length;
    const title = `Last probe answered in ${formatMs(instance.responseMs)} ms. ${history.length} recent probes${gaps ? `, ${gaps} without a response (marked red)` : ''}.`;
    return el('div', { class: 'metric', title },
      el('span', { class: 'ms' }, el('b', { text: formatMs(instance.responseMs) }), ' ms'),
      sparkline(history, 90, 22, `Response time over the last ${history.length} probes`));
  }

  // CPU is percent of one core, as docker stats reports it; the mockup turns the line amber above this.
  const CPU_WARN_PERCENT = 15;
  const CPU_TITLE = 'Percent of one core (can exceed 100% on several cores), sampled on every scan';
  const MEMORY_TITLE = 'Working set for processes, memory without page cache for containers';
  const formatCpu = value => value == null ? '—' : `${value < 10 ? value.toFixed(1) : Math.round(value)}%`;
  const formatBytes = bytes => {
    if (bytes == null) return '—';
    const mb = bytes / 1048576;
    return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
  };

  function cpuMetric(value, history, owner) {
    const title = value == null ? 'Measuring: CPU needs two consecutive samples' : `${formatCpu(value)} of one core. ${history.length} recent samples.`;
    return el('div', { class: 'metric res', title },
      sparkline(history, 64, 20, `CPU of ${owner} over the last ${history.length} samples`, 5, value > CPU_WARN_PERCENT ? 'warn' : 'cpu'),
      el('b', { text: formatCpu(value) }));
  }

  function memoryMetric(value, history, owner) {
    return el('div', { class: 'metric res', title: value == null ? 'No memory reading' : `${formatBytes(value)}. ${history.length} recent samples.` },
      sparkline(history, 64, 20, `Memory of ${owner} over the last ${history.length} samples`, 0),
      el('b', { text: formatBytes(value) }));
  }

  function ownersTable(list) {
    return el('table', { class: 'ports' },
      el('thead', {}, el('tr', {},
        el('th', { scope: 'col', text: 'Process / container' }),
        el('th', { scope: 'col', title: CPU_TITLE, text: 'CPU' }),
        el('th', { scope: 'col', title: MEMORY_TITLE, text: 'Memory' }))),
      el('tbody', {}, list.map(instance => {
        const name = instance.containerName || instance.processName || `pid ${instance.pid}`;
        return el('tr', {},
          el('td', {}, ownerCell(instance)),
          el('td', {}, cpuMetric(instance.cpuPercent, instance.cpuHistory || [], name)),
          el('td', {}, memoryMetric(instance.memoryBytes, instance.memoryHistory || [], name)));
      })));
  }

  function webCard(instance) {
    return el('div', { class: 'endpoint' },
      el('div', { class: 'ep-head' },
        el('span', { class: 'ep-title', text: instance.title || instance.composeService || instance.appName || instance.image || `Port ${instance.port}` }),
        instance.service.confidence === 'known' ? el('span', { class: 'tag', text: instance.service.label }) : null,
        statusBadge(instance)),
      el('code', { class: 'ep-url', text: instance.url }),
      responseMetric(instance),
      el('div', { class: 'ep-actions' },
        el('a', { class: 'btn', href: instance.url, target: '_blank', rel: 'noopener noreferrer', text: 'Open' }),
        el('button', { class: 'btn', type: 'button', text: 'Copy URL', onclick: e => copy(instance.url, e.currentTarget, 'Copy URL') })));
  }

  const serviceName = service => service.confidence === 'guess' ? `${service.label}?` : service.label;
  const serviceHint = service =>
    service.confidence === 'guess' ? 'Guessed from the port number only' : service.confidence === 'none' ? 'Not recognised' : '';

  function serviceCell(instance) {
    const s = instance.service;
    return el('span', { class: `svc t-${s.category}`, title: serviceHint(s) },
      el('span', { class: 'mono-ico', 'aria-hidden': 'true', text: s.monogram }),
      el('span', { class: 'svc-text' },
        el('span', { class: s.confidence === 'none' ? 'svc-name muted' : 'svc-name', text: serviceName(s) }),
        s.confidence === 'none' ? el('span', { class: 'kind', text: 'not recognised' }) : null));
  }

  function ownerCell(instance) {
    const container = instance.source === 'docker';
    return el('div', { class: 'owner' },
      el('span', { class: 'o1', text: container ? instance.containerName : instance.processName || instance.kind }),
      el('span', { class: 'o2', text: container ? instance.image : instance.pid ? `pid ${instance.pid}` : '' }));
  }

  function bindCell(instance) {
    if (!instance.bindAddress) return el('span', { class: 'muted', text: '—' });
    return instance.exposedToNetwork
      ? el('span', { class: 'bind lan', title: 'Reachable from your network', text: `${instance.bindAddress} · all interfaces` })
      : el('span', { class: 'bind', text: instance.bindAddress });
  }

  function connectionCell(instance) {
    const text = instance.service.connectionString;
    return text
      ? el('button', { class: 'btn copy', type: 'button', title: `Copy connection string: ${text}`, text, onclick: e => copy(text, e.currentTarget, text) })
      : el('span', { class: 'muted', text: '—' });
  }

  function portsTable(instances) {
    return el('table', { class: 'ports' },
      el('thead', {}, el('tr', {}, ['Port', 'Service', 'Process / container', 'Bound to', 'Connection'].map(h => el('th', { scope: 'col', text: h })))),
      el('tbody', {}, instances.map(instance => el('tr', {},
        el('td', {}, el('span', { class: 'portnum', text: String(instance.port) }), ' ', el('span', { class: 'proto', text: 'tcp' })),
        el('td', {}, serviceCell(instance)),
        el('td', {}, ownerCell(instance)),
        el('td', {}, bindCell(instance)),
        el('td', {}, connectionCell(instance))))));
  }

  function versionDetail(version) {
    const list = endpoints(version.instances);
    const web = list.filter(isWeb);
    const other = list.filter(i => !isWeb(i));
    const processes = owners(version.instances);
    return el('section', { class: 'version' },
      el('h3', {},
        el('span', { class: 'branch', text: version.label }),
        version.worktree ? el('span', { class: 'tag', text: `worktree ${version.worktree}` }) : null,
        version.commit ? el('code', { class: 'muted', text: version.commit }) : null,
        version.path ? el('span', { class: 'path muted', text: version.path }) : null),
      web.length ? el('div', { class: 'endpoints' }, web.map(webCard)) : null,
      other.length ? el('div', { class: 'other-ports' }, el('div', { class: 'sect-h', text: `Other ports · ${other.length}` }), portsTable(other)) : null,
      processes.length ? el('div', { class: 'other-ports' }, el('div', { class: 'sect-h', text: `Processes & containers · ${processes.length}` }), ownersTable(processes)) : null,
      !list.length ? el('p', { class: 'muted', text: 'No published ports.' }) : null);
  }

  function projectRows(project, open, detailId) {
    const key = projectKey(project);
    const status = projectStatus(project);
    const primary = primaryVersion(project);
    const instances = project.versions.flatMap(v => v.instances);
    const firstWeb = endpoints(primary.instances).find(isWeb);
    const compose = [...new Set(instances.map(i => i.composeProject).filter(Boolean))];
    const longest = Math.max(...primary.instances.map(i => i.uptimeSeconds ?? 0));
    const totals = resourceTotals(instances);

    const summary = el('tr', { class: `prow${open ? ' open' : ''}`, 'data-status': status },
      el('td', { class: 'name' },
        el('button', {
          class: 'toggle', type: 'button', 'aria-expanded': String(open), 'aria-controls': detailId, 'data-key': key,
          onclick: () => { if (expanded.has(key)) expanded.delete(key); else expanded.add(key); render(); },
        },
          el('span', { class: 'chev', 'aria-hidden': 'true', text: '▸' }),
          el('span', { class: `dot ${STATUS_DOT[status]}`, 'aria-hidden': 'true' }),
          el('span', { class: 'pname', text: project.name }),
          el('span', { class: 'sr-only', text: STATUS_LABEL[status] }))),
      el('td', {}, el('span', { class: 'tag', text: project.kind })),
      el('td', {}, el('div', { class: 'branchcell' },
        el('span', { class: 'branch', text: primary.label }),
        project.versions.length > 1 ? el('span', { class: 'tag', title: project.versions.map(v => v.label).join(', '), text: `+${project.versions.length - 1}` }) : null)),
      el('td', { class: 'muted', text: compose.join(', ') }),
      el('td', {}, el('div', { class: 'pills' }, endpoints(instances).map(pill))),
      el('td', {}, cpuMetric(totals.cpu, sumSeries(totals.owners.map(i => i.cpuHistory || [])), project.name)),
      el('td', {}, memoryMetric(totals.memory, sumSeries(totals.owners.map(i => i.memoryHistory || [])), project.name)),
      el('td', { class: 'muted num', text: uptime(longest) }),
      el('td', { class: 'actions' }, firstWeb ? el('a', { class: 'btn', href: firstWeb.url, target: '_blank', rel: 'noopener noreferrer', text: 'Open' }) : null));

    const detail = el('tr', { class: 'drow', id: detailId, hidden: !open },
      el('td', { colSpan: 9 }, project.versions.map(versionDetail)));
    return [summary, detail];
  }

  const PORT_COLUMNS = [['port', 'Port'], ['service', 'Service'], ['project', 'Project'], ['owner', 'Process / container'], ['bound', 'Bound to'], ['status', 'Status'], ['connect', 'Connect']];

  function statusCell(instance) {
    const state = classify(instance);
    const tone = { ok: 'ok', warning: 'warn', failing: 'bad' }[state];
    return el('span', { class: `badge ${tone}`, text: instance.statusCode ? String(instance.statusCode) : state === 'warning' ? 'Open' : 'Listening' });
  }

  function projectCell(row) {
    return row.project === null
      ? el('span', { class: 'muted', title: 'Not part of a project', text: 'No project' })
      : el('span', { class: 'pname', title: row.projectPath || '', text: row.project });
  }

  function renderPorts(query) {
    const all = flatten(snapshot);
    const rows = sortRows(filterRows(all, filter, query), sort.key, sort.direction);
    document.getElementById('count-ports').textContent = String(rows.length);
    const head = PORT_COLUMNS.map(([key, label]) => el('th', { scope: 'col', 'aria-sort': sort.key === key ? (sort.direction === 'asc' ? 'ascending' : 'descending') : 'none' },
      el('button', {
        class: 'sort', type: 'button', text: label, 'data-sort': key,
        onclick: () => {
          sort.direction = sort.key === key && sort.direction === 'asc' ? 'desc' : 'asc';
          sort.key = key;
          localStorage.setItem(SORT_KEY, JSON.stringify(sort));
          render();
        },
      })));
    portsRoot.replaceChildren(rows.length
      ? el('table', { class: 'ports all-ports' },
          el('thead', {}, el('tr', {}, head)),
          el('tbody', {}, rows.map(row => el('tr', {},
            el('td', {}, el('span', { class: 'portnum', text: String(row.instance.port) }), ' ', el('span', { class: 'proto', text: 'tcp' })),
            el('td', {}, serviceCell(row.instance)),
            el('td', {}, projectCell(row)),
            el('td', {}, ownerCell(row.instance)),
            el('td', {}, bindCell(row.instance)),
            el('td', {}, statusCell(row.instance)),
            el('td', {}, connectionCell(row.instance))))))
      : el('div', { class: 'card empty', text: all.length ? 'No ports match the current filters.' : 'Nothing is listening.' }));
  }

  function render() {
    if (!snapshot) return;
    const query = search.value.trim().toLowerCase();
    const focus = document.activeElement?.dataset;
    renderPorts(query);
    const bodies = [];

    for (const project of snapshot.projects) {
      const versions = project.versions
        .map(version => ({
          ...version,
          instances: version.instances.filter(i =>
            (!query || haystack(project, version, i).includes(query)) && matchesFilters(i, filter)),
        }))
        .filter(version => version.instances.length);
      if (!versions.length) continue;
      bodies.push(el('tbody', {}, projectRows({ ...project, versions }, !!query || expanded.has(projectKey(project)), `detail-${bodies.length}`)));
    }

    root.replaceChildren(...(bodies.length
      ? [el('table', { class: 'projects' },
          el('thead', {}, el('tr', {}, [['Project'], ['Kind'], ['Branch'], ['Compose'], ['Endpoints'], ['CPU', CPU_TITLE], ['Memory', MEMORY_TITLE], ['Uptime'], ['']]
            .map(([text, title]) => el('th', { scope: 'col', text, title })))),
          bodies)]
      : [el('p', { class: 'muted', text: query ? 'No matches.' : 'Nothing is listening.' })]));
    root.setAttribute('aria-busy', 'false');
    if (focus?.key) [...root.querySelectorAll('[data-key]')].find(n => n.dataset.key === focus.key)?.focus();
    if (focus?.sort) portsRoot.querySelector(`[data-sort="${focus.sort}"]`)?.focus();
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

  const P50_TITLE = 'Median of the latest probe of each web endpoint. Probes run about every 30 s, so this is over probes, not requests.';

  // The slots stay empty until two samples exist.
  function renderKpis() {
    const s = summarize(snapshot);
    const [verdict, color] = VERDICTS[s.verdict];
    const webFraction = s.web.total ? s.web.up / s.web.total : 1;
    const down = s.web.total - s.web.up;
    const webInstances = flatten(snapshot).map(row => row.instance).filter(i => i.statusCode);
    const webHistories = webInstances.map(i => i.responseHistory || []);
    const p50 = median(webInstances.map(i => i.responseMs));
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
        el('div', { class: 'row' }, value(String(s.web.up), `/ ${s.web.total}`),
          el('span', { class: 'slot', 'data-slot': 'web-p50', title: P50_TITLE }, sparkline(medianSeries(webHistories), 70, 26, 'Median response time over recent probes'))),
        el('div', { class: 'sub' },
          down ? `${down} returning an error` : 'All responding',
          p50 === null ? null : el('span', { class: 'muted', title: P50_TITLE, text: `p50 ${formatMs(p50)} ms` }))),
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
        el('div', { class: 'row' }, el('div', { class: 'value' }, ageValue, el('small', { text: ' ago' })),
          el('span', { class: 'slot', 'data-slot': 'scan-duration', title: `Scan duration over the last ${(snapshot.scanHistory || []).length} scans` }, sparkline(snapshot.scanHistory || [], 70, 26, 'Scan duration over recent scans'))),
        el('div', { class: 'sub' }, 'every ', el('strong', { text: `${config.refreshSeconds}s` }), snapshot.scanMs ? ` · took ${Math.round(snapshot.scanMs)} ms` : '')));

    document.getElementById('count-projects').textContent = String(snapshot.projects.length);
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
