// Run with: npm run test:unit (bundles layout-classification.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { classifyLayout } = require('../.tmp/layout-classification.bundle.js');

// Measured devices (MOBILE_PLAN.md §1)
assert.equal(classifyLayout(411, 789), 'phone');   // phone portrait
assert.equal(classifyLayout(789, 411), 'phone');   // phone landscape — same short side
assert.equal(classifyLayout(533, 752), 'tablet');  // tablet portrait
assert.equal(classifyLayout(752, 533), 'tablet');  // tablet landscape — same short side
console.log('measured devices ok');

// Boundaries
assert.equal(classifyLayout(479, 900), 'phone');
assert.equal(classifyLayout(480, 900), 'tablet');
assert.equal(classifyLayout(1199, 900), 'tablet');
assert.equal(classifyLayout(1200, 900), 'desktop');
console.log('boundaries ok');

// Desktop wins on width regardless of a short window
assert.equal(classifyLayout(1400, 300), 'desktop');
// A square-ish small window is tablet, not phone, once above the short-side cut
assert.equal(classifyLayout(700, 700), 'tablet');
console.log('edge cases ok');
