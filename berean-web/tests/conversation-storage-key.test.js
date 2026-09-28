// Run with: npm run test:unit (bundles conversation-storage-key.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { conversationStorageKey } = require('../.tmp/conversation-storage-key.bundle.js');

assert.equal(conversationStorageKey('abc123'), 'berean_conversationId:abc123');
assert.equal(conversationStorageKey(null), 'berean_conversationId');
console.log('conversation storage key ok');
