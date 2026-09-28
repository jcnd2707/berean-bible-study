// Run with: npm run test:unit (bundles speech-word.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { wordForSpeech, overrideKey } = require('../.tmp/speech-word.bundle.js');

assert.equal(wordForSpeech('Jehová,'), 'Jehová');
assert.equal(wordForSpeech('(Moisés)'), 'Moisés');
assert.equal(overrideKey("Melchizedek's"), 'melchizedek');
assert.equal(wordForSpeech('LORD'), 'LORD');

console.log('speech word ok');
