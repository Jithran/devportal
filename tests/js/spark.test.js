const test = require('node:test');
const assert = require('node:assert/strict');
const { median, medianSeries, sparkShape } = require('../../src/DevPortal/wwwroot/spark.js');

test('median ignores gaps and handles even counts', () => {
  assert.equal(median([4, null, 2, 8]), 4);
  assert.equal(median([1, 3]), 2);
  assert.equal(median([null]), null);
});

test('medianSeries aligns histories on their newest sample', () => {
  assert.deepEqual(medianSeries([[1, 2, 3], [10, 20]]), [1, 6, 11.5]);
});

test('no shape until two real samples exist', () => {
  assert.equal(sparkShape([5], 70, 26), null);
  assert.equal(sparkShape([5, null], 70, 26), null);
  assert.ok(sparkShape([5, 6], 70, 26));
});

test('a gap splits the line and is reported, not drawn as a latency', () => {
  const shape = sparkShape([5, 6, null, 7, 8], 70, 26);
  assert.equal(shape.runs.length, 2);
  assert.equal(shape.gaps.length, 1);
});

test('tiny latencies stay near the baseline thanks to the minimum scale', () => {
  const { runs } = sparkShape([1, 2], 70, 26);
  assert.ok(runs[0].every(([, y]) => y > 20));
});
