const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluate, verdictOf, portMix } = require('../../src/DevPortal/wwwroot/attention.js');
const { summarize } = require('../../src/DevPortal/wwwroot/stats.js');

const NOW = Date.parse('2026-11-11T10:00:00Z');
const svc = (category, label = category) => ({ category, label });
const host = (port, extra = {}) => ({ source: 'host', kind: 'node', port, pid: port, processName: 'node', addresses: ['127.0.0.1'], service: svc('unknown'), ...extra });
const project = (name, instances, extra = {}) => ({ name, kind: 'repo', path: `C:/dev/${name}`, versions: [{ instances }], ...extra });
const snapshot = (projects, extra = {}) => ({ generatedAt: '2026-11-11T10:00:00Z', projects, ...extra });
const kinds = items => items.map(i => i.kind);

test('healthy system: nothing needs attention', () => {
  const items = evaluate(snapshot([project('web', [host(3000, { statusCode: 200, responseMs: 40 })])]), new Map(), NOW);
  assert.deepEqual(items, []);
});

test('5xx is a bad item that names project, port and status', () => {
  const [item] = evaluate(snapshot([project('ma', [host(7270, { statusCode: 503, responseMs: 1800 })])]), new Map(), NOW);
  assert.equal(item.severity, 'bad');
  assert.equal(item.kind, 'health-failing');
  assert.equal(item.project, 'ma');
  assert.match(item.detail, /:7270 → 503 in 1800 ms/);
});

test('a probe error alone is not an item, like in the KPI strip', () => {
  assert.deepEqual(evaluate(snapshot([project('web', [host(3000, { probeError: 'timeout' })])]), new Map(), NOW), []);
});

test('database on 0.0.0.0 and a docker-published database warn', () => {
  const items = evaluate(snapshot([project('stack', [
    host(3306, { addresses: ['0.0.0.0'], service: svc('database', 'MariaDB'), processName: 'mariadbd' }),
    { source: 'docker', kind: 'docker', port: 5432, containerName: 'pg', exposedToNetwork: true, bindAddress: '0.0.0.0', addresses: ['5432->5432/tcp'], service: svc('database', 'PostgreSQL') },
  ])]), new Map(), NOW);
  assert.deepEqual(kinds(items), ['database-exposed', 'database-exposed']);
  assert.ok(items.every(i => i.severity === 'warn'));
  assert.match(items[0].title, /MariaDB exposed/);
});

test('unknown listener on all interfaces warns, unknown on loopback is ignored', () => {
  const items = evaluate(snapshot([project('x', [host(50123, { addresses: ['::'] }), host(50124)])]), new Map(), NOW);
  assert.deepEqual(kinds(items), ['unknown-exposed']);
  assert.equal(items[0].port, 50123);
});

test('one port bound twice is one item', () => {
  const items = evaluate(snapshot([project('x', [host(8080, { addresses: ['0.0.0.0'], pid: 1 }), host(8080, { addresses: ['::'], pid: 1 })])]), new Map(), NOW);
  assert.equal(items.length, 1);
});

test('slow response, high CPU and high memory are info', () => {
  const items = evaluate(snapshot([project('x', [host(3000, { statusCode: 200, responseMs: 2500, cpuPercent: 95, memoryBytes: 3 * 1024 ** 3 })])]), new Map(), NOW);
  assert.deepEqual(kinds(items).sort(), ['high-cpu', 'high-memory', 'slow-response']);
  assert.ok(items.every(i => i.severity === 'info'));
});

test('thresholds are exclusive: 80% CPU and 2000 ms are fine', () => {
  assert.deepEqual(evaluate(snapshot([project('x', [host(3000, { statusCode: 200, responseMs: 2000, cpuPercent: 80 })])]), new Map(), NOW), []);
});

test('docker unreachable is an info item', () => {
  const items = evaluate(snapshot([], { dockerError: 'Docker is not running' }), new Map(), NOW);
  assert.deepEqual(kinds(items), ['docker-unreachable']);
  assert.equal(items[0].severity, 'info');
});

test('stopped project: shown after it vanishes, dropped after the grace period, gone when it returns', () => {
  const seen = new Map();
  const running = snapshot([project('ma', [host(7270, { statusCode: 200 })])]);
  const stopped = snapshot([project('ma', [])]);
  assert.deepEqual(evaluate(running, seen, NOW), []);

  const [item] = evaluate(stopped, seen, NOW + 2 * 60000);
  assert.equal(item.kind, 'project-stopped');
  assert.equal(item.severity, 'info');
  assert.match(item.detail, /2 min ago/);

  assert.equal(evaluate(snapshot([]), seen, NOW + 10 * 60000).length, 1);
  assert.deepEqual(evaluate(running, seen, NOW + 11 * 60000), []);
  assert.deepEqual(evaluate(stopped, seen, NOW + 60 * 60000), []);
});

test('a project never seen running, and leftover groups, are not "stopped"', () => {
  const seen = new Map();
  evaluate(snapshot([project('Other', [host(1)], { hasProject: false })]), seen, NOW);
  assert.deepEqual(evaluate(snapshot([]), seen, NOW + 60000), []);
});

test('severity order: bad before warn before info, then by port', () => {
  const items = evaluate(snapshot([project('x', [
    host(9000, { statusCode: 200, responseMs: 3000 }),
    host(8000, { addresses: ['0.0.0.0'] }),
    host(7000, { statusCode: 500 }),
  ])]), new Map(), NOW);
  assert.deepEqual(items.map(i => i.severity), ['bad', 'warn', 'info']);
});

test('no flapping: identical snapshots give identical items', () => {
  const s = snapshot([project('x', [host(7000, { statusCode: 500 }), host(8000, { addresses: ['0.0.0.0'] })])]);
  assert.deepEqual(evaluate(s, new Map(), NOW), evaluate(s, new Map(), NOW + 5000));
});

test('rail verdict equals the KPI ring verdict', () => {
  const cases = [
    [host(1, { statusCode: 200 })],
    [host(1, { statusCode: 200 }), host(2, { addresses: ['0.0.0.0'] })],
    [host(1, { statusCode: 200 }), host(2, { addresses: ['0.0.0.0'] }), host(3, { statusCode: 503 })],
    [host(1, { statusCode: 200, responseMs: 5000, cpuPercent: 99 })],
  ];
  for (const instances of cases) {
    const s = snapshot([project('x', instances)]);
    assert.equal(verdictOf(evaluate(s, new Map(), NOW)), summarize(s).verdict);
  }
});

test('port mix counts each port once and lets an HTTP answer win', () => {
  const mix = portMix(snapshot([project('x', [
    host(3000, { statusCode: 200 }),
    host(3000, { statusCode: 200, addresses: ['::1'] }),
    host(3306, { service: svc('database') }),
    host(6379, { service: svc('cache') }),
    host(9999),
    host(8080, { statusCode: 200, service: svc('database') }),
  ])]));
  assert.equal(mix.total, 5);
  assert.deepEqual(mix.parts.map(p => [p.key, p.count]), [['web', 2], ['database', 1], ['cache', 1], ['unknown', 1]]);
});
