// Run with: npm run test:unit (bundles reading-progress.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { chapterKey, readChapterCount, isBookComplete, formatReadDate } = require('../.tmp/reading-progress.bundle.js');

// keys are "book.chapter" by canonical book number
assert.equal(chapterKey(43, 3), '43.3');

// nothing read
{
  const read = new Set();
  assert.equal(readChapterCount(read, 1, 50), 0);
  assert.equal(isBookComplete(read, 1, 50), false);
}

// partly read is counted but not complete; chapters of other books don't count
{
  const read = new Map([
    [chapterKey(1, 1), '2026-09-28T12:00:00Z'],
    [chapterKey(1, 2), '2026-09-28T12:00:00Z'],
    [chapterKey(2, 1), '2026-09-28T12:00:00Z'],
  ]);
  assert.equal(readChapterCount(read, 1, 50), 2);
  assert.equal(isBookComplete(read, 1, 50), false);
}

// a one-chapter book is complete the moment its chapter is marked
{
  const read = new Set([chapterKey(65, 1)]);
  assert.equal(isBookComplete(read, 65, 1), true);
}

// a multi-chapter book is complete only when every chapter is
{
  const read = new Set([chapterKey(8, 1), chapterKey(8, 2), chapterKey(8, 3)]);
  assert.equal(isBookComplete(read, 8, 4), false);
  read.add(chapterKey(8, 4));
  assert.equal(isBookComplete(read, 8, 4), true);
}

// chapters beyond the book's real length don't make it complete or inflate the count
{
  const read = new Set([chapterKey(65, 2)]);
  assert.equal(readChapterCount(read, 65, 1), 0);
  assert.equal(isBookComplete(read, 65, 1), false);
}

// a book with no chapters is never complete
assert.equal(isBookComplete(new Set(), 1, 0), false);

// dates: built from local-time parts so the result doesn't depend on the machine's timezone
{
  const now = new Date(2026, 8, 30, 12);
  const thisYear = new Date(2026, 8, 28, 12).toISOString();
  const lastYear = new Date(2025, 11, 31, 12).toISOString();
  assert.equal(formatReadDate(thisYear, now, 'en-US'), 'Sep 28');
  assert.equal(formatReadDate(lastYear, now, 'en-US'), 'Dec 31, 2025');
  assert.equal(formatReadDate('not a date', now, 'en-US'), '');
}

console.log('reading-progress tests passed');
