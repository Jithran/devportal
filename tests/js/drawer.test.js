const test = require('node:test');
const assert = require('node:assert/strict');
const { encodeHash, decodeHash, itemsFor } = require('../../src/DevPortal/wwwroot/drawer.js');

test('hash round-trips keys with NUL, spaces, slashes, ampersands and unicode', () => {
  for (const key of ['shop\u0000C:\code\shop', 'a&b=c#d\u0000/x y/ü', 'plain\u0000']) {
    const hash = encodeHash(key);
    assert.match(hash, /^#project=[A-Za-z0-9%._~!*'()-]+$/);
    assert.equal(decodeHash(hash), key);
  }
});

test('decodeHash ignores other hashes, empty values and broken escapes', () => {
  for (const hash of ['', '#', '#ports', '#project=', '#project=%E0%A4%A', undefined]) assert.equal(decodeHash(hash), null);
});

test('itemsFor keeps the items of one project and skips projectless ones', () => {
  const items = [
    { project: 'shop', projectPath: 'C:/shop', key: 'a' },
    { project: 'shop', projectPath: 'C:/other', key: 'b' },
    { project: null, projectPath: null, key: 'c' },
  ];
  assert.deepEqual(itemsFor(items, 'shop\u0000C:/shop').map(i => i.key), ['a']);
});
