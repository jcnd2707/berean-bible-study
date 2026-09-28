// Run with: npm run test:unit (bundles session-date-grouping.ts with esbuild, then runs these assertions with node).
//
// Times are built from a local `now` rather than hardcoded UTC strings, so the "days ago" math
// stays correct (and the test doesn't flake) regardless of which timezone it runs in.
const assert = require('node:assert');
const { groupByDate } = require('../.tmp/session-date-grouping.bundle.js');

const now = new Date(2026, 2, 10, 12, 0, 0); // local time: March 10, 2026, noon

function daysAgo(n, hour = 12) {
  const d = new Date(now);
  d.setDate(d.getDate() - n);
  d.setHours(hour, 0, 0, 0);
  return d.toISOString();
}

const item = (updatedAt) => ({ updatedAt, id: updatedAt });

// Today, this week, earlier
{
  const result = groupByDate(
    [
      item(daysAgo(0, 8)), // today, earlier in the day
      item(daysAgo(4)), // 4 days ago — this week
      item(daysAgo(40)), // long ago — earlier
    ],
    now,
  );
  assert.deepEqual(result.map((g) => g.group), ['Today', 'This week', 'Earlier']);
  assert.equal(result[0].items.length, 1);
  assert.equal(result[1].items.length, 1);
  assert.equal(result[2].items.length, 1);
}
console.log('today/this-week/earlier ok');

// Empty groups are omitted, order is preserved
{
  const result = groupByDate([item(daysAgo(0, 1))], now);
  assert.deepEqual(result.map((g) => g.group), ['Today']);
}
console.log('empty groups omitted ok');

// Boundary: exactly 6 days back is still "this week"; 7 days back is "earlier"
{
  const sixDaysBack = item(daysAgo(6));
  const sevenDaysBack = item(daysAgo(7));
  const result = groupByDate([sixDaysBack, sevenDaysBack], now);
  const thisWeek = result.find((g) => g.group === 'This week');
  const earlier = result.find((g) => g.group === 'Earlier');
  assert.ok(thisWeek.items.includes(sixDaysBack));
  assert.ok(earlier.items.includes(sevenDaysBack));
}
console.log('week boundary ok');

// A date that fails to parse falls back to Earlier rather than throwing
{
  const bad = item('not-a-date');
  const result = groupByDate([bad], now);
  assert.deepEqual(result.map((g) => g.group), ['Earlier']);
}
console.log('unparseable date ok');
