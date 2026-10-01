// Run with: npm run test:unit (bundles reading-progress.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { chapterKey, readChapterCount, isBookComplete, formatReadDate, percentOf, computeProgress, nextMilestone } = require('../.tmp/reading-progress.bundle.js');

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


// ── whole-Bible progress ──
// A small stand-in Bible: Genesis (3 ch.) and Ruth (2 ch.) in the OT, Jude (1) and Acts (4) in the NT.
const books = [
  { number: 1, name: 'Genesis', abbreviation: 'Gen', chapterCount: 3 },
  { number: 8, name: 'Ruth', abbreviation: 'Rut', chapterCount: 2 },
  { number: 44, name: 'Acts', abbreviation: 'Act', chapterCount: 4 },
  { number: 65, name: 'Jude', abbreviation: 'Jud', chapterCount: 1 },
];
const keys = (...pairs) => new Set(pairs.map(([b, c]) => chapterKey(b, c)));

// percent rounds down, so 100 means everything
assert.equal(percentOf(0, 0), 0);
assert.equal(percentOf(1188, 1189), 99);
assert.equal(percentOf(1189, 1189), 100);

// nothing read: 0% everywhere, nothing complete
{
  const p = computeProgress(new Set(), books);
  assert.equal(p.totalRead, 0);
  assert.equal(p.totalChapters, 10);
  assert.equal(p.percent, 0);
  assert.equal(p.ot.percent, 0);
  assert.equal(p.nt.percent, 0);
  assert.ok(p.books.every((b) => !b.complete));
}

// everything read: 100% and every book complete
{
  const all = keys([1, 1], [1, 2], [1, 3], [8, 1], [8, 2], [44, 1], [44, 2], [44, 3], [44, 4], [65, 1]);
  const p = computeProgress(all, books);
  assert.equal(p.percent, 100);
  assert.equal(p.ot.booksComplete, 2);
  assert.equal(p.nt.booksComplete, 2);
  assert.ok(p.books.every((b) => b.complete));
}

// one partly-read book shows its own count, isn't complete, and the testaments add up to the whole
{
  const p = computeProgress(keys([1, 1], [1, 2], [65, 1]), books);
  const gen = p.books.find((b) => b.abbreviation === 'Gen');
  assert.equal(gen.read, 2);
  assert.equal(gen.complete, false);
  assert.equal(p.books.find((b) => b.abbreviation === 'Jud').complete, true);
  assert.equal(p.ot.read + p.nt.read, p.totalRead);
  assert.equal(p.ot.total + p.nt.total, p.totalChapters);
  assert.equal(p.ot.read, 2);
  assert.equal(p.nt.read, 1);
  assert.equal(p.nt.booksComplete, 1);
  assert.equal(p.percent, 30);
}

// no books loaded yet: zeroes, not NaN
{
  const p = computeProgress(new Set(), []);
  assert.equal(p.percent, 0);
  assert.equal(p.totalChapters, 0);
}

// next milestone: how far to the next percentage, then the one after
assert.deepEqual(nextMilestone({ totalRead: 0, totalChapters: 1189 }), { percent: 10, chaptersToGo: 119 });
assert.deepEqual(nextMilestone({ totalRead: 118, totalChapters: 1189 }), { percent: 10, chaptersToGo: 1 });
assert.deepEqual(nextMilestone({ totalRead: 119, totalChapters: 1189 }), { percent: 25, chaptersToGo: 179 });
assert.deepEqual(nextMilestone({ totalRead: 1188, totalChapters: 1189 }), { percent: 100, chaptersToGo: 1 });
assert.equal(nextMilestone({ totalRead: 1189, totalChapters: 1189 }), null);
assert.equal(nextMilestone({ totalRead: 0, totalChapters: 0 }), null);

console.log('reading-progress tests passed');
