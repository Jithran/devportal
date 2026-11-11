const test = require('node:test');
const assert = require('node:assert/strict');
const { classify, summarize, projectStatus, primaryVersion, owners, resourceTotals } = require('../../src/DevPortal/wwwroot/stats.js');

const host = (port, extra = {}) => ({ source: 'host', kind: 'node', port, addresses: ['127.0.0.1'], ...extra });
const docker = (port, name, extra = {}) => ({ source: 'docker', kind: 'docker', port, containerName: name, addresses: [`${port}->80/tcp`], ...extra });
const snapshot = projects => ({ generatedAt: '2026-11-11T10:00:00Z', projects });
const project = (name, kind, instances) => ({ name, kind, versions: [{ instances }] });

test('classify: 5xx fails, open binds warn, probe errors alone are fine', () => {
  assert.equal(classify(host(1, { statusCode: 503 })), 'failing');
  assert.equal(classify(host(1, { probeError: 'timeout' })), 'ok');
  assert.equal(classify(host(1, { probeError: 'no HTTP response' })), 'ok');
  assert.equal(classify(host(1, { statusCode: 404 })), 'ok');
  assert.equal(classify(host(1, { addresses: ['0.0.0.0'] })), 'warning');
  assert.equal(classify(host(1, { addresses: ['::'] })), 'warning');
});

test('summarize: counts follow the snapshot and do not double count a port', () => {
  const s = summarize(snapshot([
    project('web', 'repo', [host(3000, { statusCode: 200 }), host(3000, { statusCode: 200, pid: 2 }), host(5173, { statusCode: 500 })]),
    project('stack', 'compose', [docker(5432, 'db', { kind: 'unknown' }), docker(8080, 'app', { statusCode: 200 }), docker(8081, 'app', { statusCode: 200 })]),
    project('Other', 'other', [host(9229, { kind: 'unknown', addresses: ['0.0.0.0'] })]),
  ]));

  assert.deepEqual(s.projects, { running: 2, healthy: 1, warning: 0, failing: 1, compose: 1 });
  assert.deepEqual(s.web, { up: 4, total: 5 });
  assert.equal(s.containers, 2);
  assert.deepEqual(s.ports, { total: 6, open: 1, unknown: 2 });
  assert.equal(s.verdict, 'failing');
});

test('summarize: an empty snapshot is idle', () => {
  const s = summarize(snapshot([]));
  assert.equal(s.verdict, 'idle');
  assert.equal(s.projects.running, 0);
  assert.deepEqual(s.web, { up: 0, total: 0 });
});

test('projectStatus: running, degraded on a 5xx, stopped without instances', () => {
  assert.equal(projectStatus(project('a', 'repo', [host(1, { statusCode: 200 }), host(2, { probeError: 'timeout' })])), 'running');
  assert.equal(projectStatus(project('a', 'repo', [host(1, { statusCode: 200 }), host(2, { statusCode: 502 })])), 'degraded');
  assert.equal(projectStatus(project('a', 'repo', [])), 'stopped');
});

test('primaryVersion: the most recently started version wins', () => {
  const version = (label, startTime) => ({ label, instances: [host(1, { startTime })] });
  const p = { name: 'a', kind: 'repo', versions: [version('main', '2026-11-11T08:00:00Z'), version('feature', '2026-11-11T09:30:00Z'), version('old', '2026-11-10T09:30:00Z')] };
  assert.equal(primaryVersion(p).label, 'feature');
});

test('resource totals count each process or container once and stay null without readings', () => {
  const web = { source: 'docker', containerName: 'web', port: 8080, cpuPercent: 10, memoryBytes: 100 };
  const instances = [
    web,
    { ...web, port: 8443 },
    host(3000, { pid: 7, cpuPercent: 2.5, memoryBytes: 50 }),
    host(3001, { pid: 8, cpuPercent: null, memoryBytes: null }),
  ];

  assert.equal(owners(instances).length, 3);
  assert.deepEqual({ ...resourceTotals(instances), owners: undefined }, { cpu: 12.5, memory: 150, owners: undefined });
  assert.deepEqual({ ...resourceTotals([host(3001, { pid: 8 })]), owners: undefined }, { cpu: null, memory: null, owners: undefined });
});
