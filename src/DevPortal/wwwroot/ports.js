(() => {
  const { classify } = typeof module !== 'undefined' ? require('./stats.js') : globalThis.DevPortalStats;

  // Type comes from the recognised service; the HTTP probe wins because a listener that answers HTTP is a web endpoint.
  function typeOf(instance) {
    if (instance.statusCode) return 'web';
    return instance.service?.category === 'database' ? 'database' : 'other';
  }

  // One predicate per filter group, shared by the Projects tab and the All ports tab.
  const MATCHERS = {
    status: (instance, value) => classify(instance) === value,
    type: (instance, value) => typeOf(instance) === value,
  };

  const matchesFilters = (instance, filter) =>
    Object.entries(MATCHERS).every(([key, matches]) => !filter[key] || filter[key] === 'all' || matches(instance, filter[key]));

  // A listener without a project (system or stray processes, standalone containers) gets project: null instead of a group name.
  function flatten(snapshot) {
    return snapshot.projects.flatMap(project => project.versions.flatMap(version =>
      version.instances.filter(i => i.port).map(instance => ({
        instance,
        project: project.hasProject === false ? null : project.name,
        projectPath: project.hasProject === false ? null : project.path,
        version,
      }))));
  }

  const searchText = row => [row.instance.port, row.instance.service?.label, row.project, row.projectPath, row.instance.processName,
    row.instance.containerName, row.instance.image, row.instance.path, row.instance.appName, row.instance.title, row.instance.bindAddress]
    .filter(Boolean).join(' ').toLowerCase();

  const filterRows = (rows, filter, query) => {
    const needle = (query || '').trim().toLowerCase();
    return rows.filter(row => matchesFilters(row.instance, filter) && (!needle || searchText(row).includes(needle)));
  };

  // Loopback < a specific interface < all interfaces: the further a port is exposed, the higher it sorts.
  function bindRank(instance) {
    if (instance.exposedToNetwork) return 2;
    return /^(127\.|::1|localhost)/.test(instance.bindAddress || '127.0.0.1') ? 0 : 1;
  }

  const STATE_RANK = { ok: 0, warning: 1, failing: 2 };
  const text = value => (value || '').toLowerCase();

  // Empty values (no project, no connection string) sort last in both directions, so they never crowd the top of the table.
  const COLUMNS = {
    port: { value: r => r.instance.port },
    service: { value: r => text(r.instance.service?.label) },
    project: { value: r => r.project === null ? null : text(r.project) },
    owner: { value: r => text(r.instance.containerName || r.instance.processName) },
    bound: { value: r => bindRank(r.instance) },
    status: { value: r => STATE_RANK[classify(r.instance)] },
    connect: { value: r => r.instance.service?.connectionString || null },
  };

  function sortRows(rows, key, direction = 'asc') {
    const column = COLUMNS[key] || COLUMNS.port;
    const sign = direction === 'desc' ? -1 : 1;
    const compare = (a, b) => {
      const x = column.value(a), y = column.value(b);
      if (x === y) return 0;
      if (x === null) return 1;
      if (y === null) return -1;
      return (x < y ? -1 : 1) * sign;
    };
    return [...rows].sort((a, b) => compare(a, b) || a.instance.port - b.instance.port || (a.instance.pid ?? 0) - (b.instance.pid ?? 0));
  }

  const api = { typeOf, matchesFilters, flatten, filterRows, sortRows, bindRank, COLUMNS };
  globalThis.DevPortalPorts = api;
  if (typeof module !== 'undefined') module.exports = api;
})();
