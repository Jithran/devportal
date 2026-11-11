(() => {
  const OPEN_BINDS = ['0.0.0.0', '::'];

  // Docker-published ports carry no host address, so the server's exposedToNetwork flag covers them.
  const isOpenBind = instance => !!instance.exposedToNetwork || (instance.addresses || []).some(a => OPEN_BINDS.includes(a));

  // One rule for "needs a look", shared by the KPI strip and the filter chips (and later the attention rail).
  // Probe errors are not failures: databases answer HTTP with nothing, and a cold first scan times out on busy ports.
  function classify(instance) {
    if (instance.statusCode >= 500) return 'failing';
    if (isOpenBind(instance)) return 'warning';
    return 'ok';
  }

  const worst = states => states.includes('failing') ? 'failing' : states.includes('warning') ? 'warning' : 'ok';

  // Rows only exist for things that are listening, so "stopped" means a project with no instance left to show.
  // Only 5xx degrades a project, for the same reason classify ignores probe errors.
  function projectStatus(project) {
    const instances = project.versions.flatMap(v => v.instances);
    if (!instances.length) return 'stopped';
    return instances.some(i => classify(i) === 'failing') ? 'degraded' : 'running';
  }

  // The version that started most recently is the one a worktree-heavy project is "on" right now.
  function primaryVersion(project) {
    const started = v => Math.max(0, ...v.instances.map(i => Date.parse(i.startTime) || 0));
    return project.versions.reduce((best, v) => started(v) > started(best) ? v : best);
  }

  function summarize(snapshot) {
    const instances = snapshot.projects.flatMap(p => p.versions.flatMap(v => v.instances));
    const states = instances.map(classify);
    const projects = snapshot.projects.filter(p => p.kind === 'repo' || p.kind === 'compose');
    const projectStates = projects.map(p => worst(p.versions.flatMap(v => v.instances).map(classify)));
    const count = (list, state) => list.filter(s => s === state).length;
    const ports = new Set(instances.filter(i => i.port).map(i => i.port));
    const web = instances.filter(i => i.statusCode);
    const failing = count(states, 'failing');
    const warnings = count(states, 'warning');

    return {
      verdict: !instances.length ? 'idle' : failing ? 'failing' : warnings ? 'warning' : 'ok',
      failing,
      warnings,
      projects: {
        running: projects.length,
        healthy: count(projectStates, 'ok'),
        warning: count(projectStates, 'warning'),
        failing: count(projectStates, 'failing'),
        compose: projects.filter(p => p.kind === 'compose').length,
      },
      web: { up: web.filter(i => i.statusCode < 500).length, total: web.length },
      containers: new Set(instances.filter(i => i.source === 'docker').map(i => i.containerName)).size,
      ports: {
        total: ports.size,
        open: new Set(instances.filter(isOpenBind).map(i => i.port)).size,
        unknown: new Set(instances.filter(i => i.kind === 'unknown' && i.port).map(i => i.port)).size,
      },
    };
  }

  // Every port of a process or container repeats that owner's CPU and memory, so each owner counts once.
  function owners(instances) {
    const byKey = new Map();
    for (const instance of instances) {
      const key = instance.source === 'docker' ? `container ${instance.containerName}` : `pid ${instance.pid}`;
      if (!byKey.has(key)) byKey.set(key, instance);
    }
    return [...byKey.values()];
  }

  // A total stays null until some owner has a reading, so the page shows a dash instead of a false 0.
  function resourceTotals(instances) {
    const list = owners(instances);
    const total = field => list.some(i => i[field] != null) ? list.reduce((sum, i) => sum + (i[field] ?? 0), 0) : null;
    return { cpu: total('cpuPercent'), memory: total('memoryBytes'), owners: list };
  }

  const api = { classify, summarize, projectStatus, primaryVersion, owners, resourceTotals };
  globalThis.DevPortalStats = api;
  if (typeof module !== 'undefined') module.exports = api;
})();
