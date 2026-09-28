// Run with: npm run test:unit (bundles definition-parser.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { parseDefinition } = require('../.tmp/definition-parser.bundle.js');

// The Resource API strips the dictionary's HTML (tags -> single space, whitespace collapsed) before
// this ever sees it — these fixtures are that stripped form of real strong.dct rows (G1080, H430).

// A Greek (Thayer Definition) entry: the phonetic field used to have no boundary to stop at and
// swallowed the rest of the whole entry, which is what a user heard read aloud and saw on screen.
{
  const raw =
    "Original: γεννάω Transliteration: gennaō Phonetic: ghen-nah'-o Thayer Definition : of men who fathered children to be born to be begotten of women giving birth to children Origin: from a variation of G1085 TDNT entry: 12:05,1 Part(s) of speech: Verb Strong's Definition : From a variation of G1085 to procreate";
  const r = parseDefinition('G1080', raw);
  assert.equal(r.phonetic, "ghen-nah'-o");
  assert.equal(r.original, "γεννάω");
  assert.equal(r.transliteration, "gennaō");
}

// A Hebrew (BDB Definition) entry — the existing path, kept working.
{
  const raw =
    "Original: אלהים Transliteration: 'ĕlôhî̂ym Phonetic: el-o-heem' BDB Definition : (plural) rulers, judges divine ones angels gods Origin: plural of H433 TWOT entry: 93c Part(s) of speech: Noun Masculine Strong's Definition : Plural of H433; gods";
  const r = parseDefinition('H430', raw);
  assert.equal(r.phonetic, "el-o-heem'");
}

console.log('definition parser ok');
