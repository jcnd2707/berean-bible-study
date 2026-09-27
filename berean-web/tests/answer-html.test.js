// Run with: npm run test:unit (bundles answer-html.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { renderAnswerHtml, escapeHtml } = require('../.tmp/answer-html.bundle.js');

const src = (id, tradition = 'Evangelical') => ({ id, kind: 'commentary', moduleId: 'barnes', displayName: "Barnes' Notes", tradition, era: '19th c.', label: "Barnes' Notes (Evangelical) — John 3:16", book: 'John', bookNumber: 43, chapter: 3, verse: 16, bookChapterIndex: null });
const label = s => `${s.displayName} · ${s.tradition}`;
const openable = () => true;
const R = (t, s) => renderAnswerHtml(t, s, label, openable);

// markdown
assert.match(R('# Title\n\nSome **bold** and *italic* and `code`.'), /<div class="md-h md-h1">Title<\/div><div class="md-gap"><\/div><div class="md-p">Some <strong>bold<\/strong> and <em>italic<\/em> and <code>code<\/code>\.<\/div>/);
assert.match(R('- one\n- two'), /<div class="md-li">one<\/div><div class="md-li">two<\/div>/);
assert.match(R('## H2\n### H3\n#### H4'), /md-h2.*md-h3.*md-h3/);
assert.match(R('---'), /<hr>/);
assert.equal(R('a\n\n\n\nb').match(/md-gap/g).length, 1);   // blank lines collapse
console.log('markdown ok');

// safety: nothing from the model becomes markup
const evil = R('<script>alert(1)</script> <img src=x onerror=alert(1)> **b**');
assert.ok(!evil.includes('<script') && !evil.includes('<img'));
assert.ok(evil.includes('&lt;script&gt;') && evil.includes('<strong>b</strong>'));
assert.equal(escapeHtml(`&<>"'`), '&amp;&lt;&gt;&quot;&#39;');
console.log('escaping ok');

// citations
const withChip = R('Barnes says [S1] and Clarke [S2].', [src('S1'), src('S2')]);
assert.match(withChip, /<button type="button" class="cite" data-cite="S1" title="[^"]+">Barnes&#39; Notes · Evangelical<\/button>/);
assert.equal(withChip.match(/data-cite/g).length, 2);
assert.match(R('See [S1].', [src('S1', 'Adventist')]), /class="cite cite--adv"/);
assert.match(R('See [S9].', [src('S1')]), /\[S9\]/);                  // unknown id stays text
assert.match(R('See [S1].', undefined), /\[S1\]/);                    // no sources sent
assert.match(R('**as [S1] says**', [src('S1')]), /<strong>as <button[^>]*>.*<\/button> says<\/strong>/);   // chip inside bold
const notOpenable = renderAnswerHtml('x [S1]', [src('S1')], label, () => false);
assert.match(notOpenable, /class="cite cite--static"/);
// a source label with quotes cannot break out of the attribute
const tricky = renderAnswerHtml('[S1]', [{ ...src('S1'), label: 'a" onclick="alert(1)' }], label, openable);
assert.ok(!/onclick="alert/.test(tricky) && tricky.includes('&quot; onclick=&quot;'));
console.log('citations ok');
