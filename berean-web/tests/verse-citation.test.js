// Run with: npm run test:unit (bundles verse-citation.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { formatVerseCitation } = require('../.tmp/verse-citation.bundle.js');

// HTML tags removed
{
  const r = formatVerseCitation({
    html: 'For God so loved <b>the</b> world…',
    bookName: 'John',
    chapter: 3,
    verse: 16,
    translation: 'KJV',
  });
  assert.equal(r, '"For God so loved the world…" — John 3:16 (KJV)');
}

// Strong's markup removed
{
  const r = formatVerseCitation({
    html: 'In the beginning <span class="strongs" data-num="H430">God</span> created',
    bookName: 'Genesis',
    chapter: 1,
    verse: 1,
    translation: 'KJV',
  });
  assert.equal(r, '"In the beginning God created" — Genesis 1:1 (KJV)');
}

// Whitespace collapsed
{
  const r = formatVerseCitation({
    html: '  For   God\n so loved   the world  ',
    bookName: 'John',
    chapter: 3,
    verse: 16,
    translation: 'KJV',
  });
  assert.equal(r, '"For God so loved the world" — John 3:16 (KJV)');
}

// The output format itself
{
  const r = formatVerseCitation({
    html: 'Test',
    bookName: 'Romans',
    chapter: 8,
    verse: 28,
    translation: 'BSB',
  });
  assert.equal(r, '"Test" — Romans 8:28 (BSB)');
}

console.log('verse citation ok');
