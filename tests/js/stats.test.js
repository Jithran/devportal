const test = require('node:test');
const assert = require('node:assert/strict');
const { classify, summarize } = require('../../src/DevPortal/wwwroot/stats.js');

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
