const test = require('node:test');
const assert = require('node:assert/strict');
const { summarize } = require('../../src/DevPortal/wwwroot/stats.js');
const { typeOf, matchesFilters, flatten, filterRows, sortRows } = require('../../src/DevPortal/wwwroot/ports.js');

const svc = (label, category = 'unknown', connectionString) => ({ label, category, connectionString });
const inst = (port, extra = {}) => ({ source: 'host', kind: 'node', port, pid: port, addresses: ['127.0.0.1'], bindAddress: '127.0.0.1', service: svc('Unknown'), ...extra });
const group = (name, kind, instances, extra = {}) => ({ name, kind, path: `C:/${name}`, versions: [{ label: 'main', instances }], ...extra });

const snapshot = {
  projects: [
    group('shop', 'repo', [inst(5173, { statusCode: 200, processName: 'node', service: svc('Vite', 'web') }), inst(3306, { source: 'docker', containerName: 'shop-db', image: 'mysql:8', service: svc('MySQL', 'database', 'mysql://localhost:3306'), bindAddress: '0.0.0.0', exposedToNetwork: true })]),
    group('Standalone containers', 'compose', [inst(6379, { source: 'docker', containerName: 'cache', service: svc('Redis', 'cache'), bindAddress: '127.0.0.1' })], { hasProject: false }),
    group('Other processes', 'other', [inst(9229, { processName: 'node', statusCode: 503, bindAddress: '192.168.1.5' }), inst(80, { processName: 'nginx' })], { hasProject: false }),
    group('empty', 'repo', [{ source: 'docker', kind: 'docker', containerName: 'no-port' }]),
  ],
};
const rows = flatten(snapshot);
const ports = list => list.map(r => r.instance.port);

test('flatten: one row per listening instance, no project for leftover groups', () => {
  assert.deepEqual(ports(rows), [5173, 3306, 6379, 9229, 80]);
  assert.deepEqual(rows.map(r => r.project), ['shop', 'shop', null, null, null]);
});

test('typeOf: probe wins, then service category, else other', () => {
  assert.equal(typeOf(rows[0].instance), 'web');
  assert.equal(typeOf(rows[1].instance), 'database');
  assert.equal(typeOf(rows[2].instance), 'other');
});

test('filters combine with AND and search is case-insensitive over port, service, project, process and path', () => {
  assert.deepEqual(ports(filterRows(rows, { status: 'all', type: 'database' }, '')), [3306]);
  assert.deepEqual(ports(filterRows(rows, { status: 'warning', type: 'database' }, '')), [3306]);
  assert.deepEqual(ports(filterRows(rows, { status: 'failing', type: 'database' }, '')), []);
  assert.deepEqual(ports(filterRows(rows, { status: 'all', type: 'all' }, 'MYSQL')), [3306]);
  assert.deepEqual(ports(filterRows(rows, { status: 'all', type: 'all' }, 'c:/shop')), [5173, 3306]);
  assert.deepEqual(ports(filterRows(rows, { status: 'all', type: 'all' }, ' NGINX ')), [80]);
  assert.deepEqual(ports(filterRows(rows, { status: 'all', type: 'all' }, '')), ports(rows));
});

test('matchesFilters treats a missing group as all', () => {
  assert.equal(matchesFilters(rows[0].instance, {}), true);
});

test('sort: ports are numeric and ties keep a stable order', () => {
  assert.deepEqual(ports(sortRows(rows, 'port')), [80, 3306, 5173, 6379, 9229]);
  assert.deepEqual(ports(sortRows(rows, 'port', 'desc')), [9229, 6379, 5173, 3306, 80]);
  assert.deepEqual(ports(sortRows(rows, 'bound')), [80, 5173, 6379, 9229, 3306]);
  assert.deepEqual(ports(sortRows(rows, 'bound', 'desc')), [3306, 9229, 80, 5173, 6379]);
});

test('sort: empty project and connection string go last in both directions', () => {
  assert.deepEqual(ports(sortRows(rows, 'project')).slice(0, 2), [3306, 5173]);
  assert.deepEqual(ports(sortRows(rows, 'project', 'desc')).slice(0, 2), [3306, 5173]);
  assert.equal(sortRows(rows, 'connect')[0].instance.port, 3306);
  assert.equal(sortRows(rows, 'connect', 'desc')[0].instance.port, 3306);
});

test('sort: status orders ok < warning < failing', () => {
  assert.equal(sortRows(rows, 'status', 'desc')[0].instance.port, 9229);
  assert.equal(sortRows(rows, 'status')[0].instance.port, 80);
});

test('summarize counts Docker ports published on all interfaces as open', () => {
  assert.equal(summarize(snapshot).ports.open, 1);
});
