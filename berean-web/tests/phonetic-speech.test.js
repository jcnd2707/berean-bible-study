// Run with: npm run test:unit (bundles phonetic-speech.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { phoneticToSpeech } = require('../.tmp/phonetic-speech.bundle.js');

// ag-ah'-pay: 3 syllables, stress on index 1
{
  const r = phoneticToSpeech("ag-ah'-pay");
  assert.deepEqual(r.syllables, ['ag', 'ah', 'pay']);
  assert.equal(r.stressIndex, 1);
  assert.equal(r.spokenNormal, 'ag ah pay');
  assert.equal(r.spokenSlow, 'ag, ah, pay');
}

// el-o-heem': stress on the last syllable
{
  const r = phoneticToSpeech("el-o-heem'");
  assert.deepEqual(r.syllables, ['el', 'o', 'heem']);
  assert.equal(r.stressIndex, 2);
}

// neh'-fesh: stress on the first
{
  const r = phoneticToSpeech("neh'-fesh");
  assert.deepEqual(r.syllables, ['neh', 'fesh']);
  assert.equal(r.stressIndex, 0);
}

// no stress mark
{
  const r = phoneticToSpeech('ag-ah-pay');
  assert.deepEqual(r.syllables, ['ag', 'ah', 'pay']);
  assert.equal(r.stressIndex, null);
}

// stray punctuation and extra spaces
{
  const r = phoneticToSpeech("  ag - ah' - pay!!  ");
  assert.deepEqual(r.syllables, ['ag', 'ah', 'pay']);
  assert.equal(r.stressIndex, 1);
}

// curly apostrophe treated the same as straight
{
  const r = phoneticToSpeech("ag-ah’-pay");
  assert.equal(r.stressIndex, 1);
}

// empty string returns null
{
  assert.equal(phoneticToSpeech(''), null);
}

console.log('phonetic speech ok');
