// Run with: npm run test:unit (bundles reading-position.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { resolveRestoredPosition } = require('../.tmp/reading-position.bundle.js');

const modules = [{ moduleId: 'KJV' }, { moduleId: 'BSB' }];
const books = [{ abbreviation: 'Jhn', chapterCount: 21 }, { abbreviation: 'Gen', chapterCount: 50 }];

// missing module falls back to the first one
{
  const r = resolveRestoredPosition(
    JSON.stringify({ moduleId: 'NOPE', book: 'Jhn', chapter: 3, verse: 16 }),
    modules,
    books,
  );
  assert.equal(r.moduleId, 'KJV');
  assert.equal(r.book, 'Jhn');
  assert.equal(r.chapter, 3);
  assert.equal(r.verse, 16);
}

// unknown book restores nothing
{
  const r = resolveRestoredPosition(
    JSON.stringify({ moduleId: 'KJV', book: 'Xyz', chapter: 1, verse: null }),
    modules,
    books,
  );
  assert.equal(r, null);
}

// chapter above chapterCount is clamped
{
  const r = resolveRestoredPosition(
    JSON.stringify({ moduleId: 'KJV', book: 'Jhn', chapter: 999, verse: null }),
    modules,
    books,
  );
  assert.equal(r.chapter, 21);
}

// malformed JSON restores nothing
{
  const r = resolveRestoredPosition('{not json', modules, books);
  assert.equal(r, null);
}

console.log('reading position ok');
