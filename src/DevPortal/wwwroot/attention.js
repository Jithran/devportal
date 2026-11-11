(() => {
  const { classify, owners } = typeof module !== 'undefined' ? require('./stats.js') : globalThis.DevPortalStats;
  const { flatten, typeOf } = typeof module !== 'undefined' ? require('./ports.js') : globalThis.DevPortalPorts;

  // Constants for now; settings for them are out of scope (DP-10).
  const SLOW_MS = 2000;
  const HIGH_CPU_PERCENT = 80;
  const HIGH_MEMORY_BYTES = 2 * 1024 ** 3;
  const STOPPED_MINUTES = 30;

  // Worst first. Only bad and warn can move the KPI verdict; info is context.
  const SEVERITY_RANK = { bad: 0, warn: 1, info: 2 };
  

  const DATABASE = 'database';
  const isDatabase = instance => instance.service?.category === DATABASE;
  const isUnknown = instance => !instance.service || instance.service.category === 'unknown';
  const where = instance => instance.containerName || instance.processName || (instance.pid ? `pid ${instance.pid}` : 'unknown process');
  const bound = instance => instance.exposedToNetwork && instance.bindAddress ? instance.bindAddress : (instance.addresses || []).find(a => a === '0.0.0.0' || a === '::') || '0.0.0.0';
  const ms = value => value < 10 ? value.toFixed(1) : String(Math.round(value));

  // A project is its name plus folder: the same name can live in two folders.
  const projectKey = project => `${project.name}\u0000${project.path || ''}`;

  // The rail reuses classify, so an item exists for every instance the KPI strip counts as failing or warning.
  function instanceItems(row) {
    const { instance, project, projectPath } = row;
    const base = { project, projectPath, port: instance.port };
    const state = classify(instance);
    const items = [];

    if (state === 'failing') {
      items.push({ ...base, severity: 'bad', kind: 'health-failing', key: `health:${instance.port}`,
        title: 'Health check failing', detail: `${project || where(instance)} · :${instance.port} → ${instance.statusCode}${instance.responseMs != null ? ` in ${ms(instance.responseMs)} ms` : ''}` });
    } else if (state === 'warning' && isDatabase(instance)) {
      items.push({ ...base, severity: 'warn', kind: 'database-exposed', key: `exposed:${instance.port}`,
        title: `${instance.service.label} exposed on the network`, detail: `${where(instance)} binds ${bound(instance)}:${instance.port}` });
    } else if (state === 'warning') {
      const unknown = isUnknown(instance);
      items.push({ ...base, severity: 'warn', kind: unknown ? 'unknown-exposed' : 'port-exposed', key: `exposed:${instance.port}`,
        title: unknown ? 'Unknown port on all interfaces' : `${instance.service.label} on all interfaces`, detail: `${where(instance)} · ${bound(instance)}:${instance.port}` });
    }

    if (state !== 'failing' && instance.responseMs > SLOW_MS) {
      items.push({ ...base, severity: 'info', kind: 'slow-response', key: `slow:${instance.port}`,
        title: 'Slow response', detail: `${project || where(instance)} · :${instance.port} answered in ${ms(instance.responseMs)} ms` });
    }
    return items;
  }

  function resourceItems(snapshot) {
    return snapshot.projects.flatMap(project => {
      const name = project.hasProject === false ? null : project.name;
      return owners(project.versions.flatMap(v => v.instances)).flatMap(instance => {
        const base = { project: name, projectPath: name ? project.path : null, port: instance.port };
        const owner = where(instance);
        return [
          instance.cpuPercent > HIGH_CPU_PERCENT ? { ...base, severity: 'info', kind: 'high-cpu', key: `cpu:${owner}`, title: 'High CPU', detail: `${owner} uses ${Math.round(instance.cpuPercent)}% of one core` } : null,
          instance.memoryBytes > HIGH_MEMORY_BYTES ? { ...base, severity: 'info', kind: 'high-memory', key: `mem:${owner}`, title: 'High memory', detail: `${owner} uses ${(instance.memoryBytes / 1024 ** 3).toFixed(1)} GB` } : null,
        ].filter(Boolean);
      });
    });
  }

  // `seen` maps a project key to what it was when it last ran and lives for the page session only (D-4: no persistence).
  // A project that vanishes stays on the rail for STOPPED_MINUTES, so a restart that takes a minute shows no stale item.
  function stoppedItems(snapshot, seen, now) {
    const running = new Set();
    for (const project of snapshot.projects) {
      if (project.hasProject === false || !project.versions.some(v => v.instances.length)) continue;
      const key = projectKey(project);
      running.add(key);
      seen.set(key, { name: project.name, path: project.path || null, lastSeen: now });
    }
    const items = [];
    for (const [key, entry] of seen) {
      if (running.has(key)) continue;
      const minutes = (now - entry.lastSeen) / 60000;
      if (minutes > STOPPED_MINUTES) { seen.delete(key); continue; }
      items.push({ severity: 'info', kind: 'project-stopped', key: `stopped:${key}`, project: entry.name, projectPath: entry.path, port: null,
        title: `${entry.name} stopped`, detail: `Nothing is listening for it any more · last seen ${minutes < 1 ? 'just now' : `${Math.round(minutes)} min ago`}` });
    }
    return items;
  }

  // Several listeners of one port (IPv4 and IPv6, or a process and its children) are one item.
  const unique = items => [...new Map(items.map(item => [item.key, item])).values()];

  function evaluate(snapshot, seen = new Map(), now = Date.now()) {
    const items = unique([
      ...flatten(snapshot).flatMap(instanceItems),
      ...resourceItems(snapshot),
      ...stoppedItems(snapshot, seen, now),
      ...(snapshot.dockerError ? [{ severity: 'info', kind: 'docker-unreachable', key: 'docker', project: null, projectPath: null, port: null, title: 'Docker unreachable', detail: snapshot.dockerError }] : []),
    ]);
    return items.sort((a, b) => SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] || (a.port ?? 0) - (b.port ?? 0) || a.key.localeCompare(b.key));
  }

  // What the KPI ring says when it only looks at the rail; summarize().verdict must agree whenever something listens.
  const verdictOf = items => items.some(i => i.severity === 'bad') ? 'failing' : items.some(i => i.severity === 'warn') ? 'warning' : 'ok';

  const MIX = [
    ['web', 'Web (http/https)', 'var(--accent)'],
    ['database', 'Databases', 'var(--brand)'],
    ['cache', 'Cache', 'var(--bad)'],
    ['queue', 'Queues', 'var(--warn)'],
    ['search', 'Search', 'var(--link)'],
    ['mail', 'Mail', 'var(--link)'],
    ['debug', 'Debuggers', 'var(--muted)'],
    ['unknown', 'Unknown', 'color-mix(in srgb, var(--muted) 45%, transparent)'],
  ];

  // One count per port number. An HTTP answer wins over the recognised service, as in the Type filter.
  function portMix(snapshot) {
    const byPort = new Map();
    for (const { instance } of flatten(snapshot)) {
      const category = typeOf(instance) === 'web' ? 'web' : (instance.service?.category || 'unknown');
      if (!byPort.has(instance.port) || category !== 'unknown') byPort.set(instance.port, category);
    }
    const counts = {};
    for (const category of byPort.values()) counts[category] = (counts[category] || 0) + 1;
    return { total: byPort.size, parts: MIX.filter(([key]) => counts[key]).map(([key, label, color]) => ({ key, label, color, count: counts[key] })) };
  }

  const api = { evaluate, verdictOf, portMix, projectKey, SEVERITY_RANK, STOPPED_MINUTES };
  globalThis.DevPortalAttention = api;
  if (typeof module !== 'undefined') module.exports = api;
})();
