// Run with: npm run test:unit (bundles conversation-storage-key.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { conversationStorageKey, readingPositionStorageKey } = require('../.tmp/conversation-storage-key.bundle.js');

assert.equal(conversationStorageKey('abc123'), 'berean_conversationId:abc123');
assert.equal(conversationStorageKey(null), 'berean_conversationId');
assert.equal(readingPositionStorageKey('abc123'), 'berean_readingPosition:abc123');
assert.equal(readingPositionStorageKey(null), 'berean_readingPosition');
console.log('conversation storage key ok');
