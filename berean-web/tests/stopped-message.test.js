// Run with: npm run test:unit (bundles stopped-message.ts with esbuild, then runs these assertions with node).
const assert = require('node:assert');
const { applyStoppedMessage } = require('../.tmp/stopped-message.bundle.js');

// Nothing streaming: no-op
{
  const messages = [{ role: 'user', text: 'hi' }];
  const r = applyStoppedMessage(messages);
  assert.equal(r.messages, messages); // same array reference
  assert.equal(r.stoppedQuestion, null);
}

// Streaming agent message with a preceding question
{
  const messages = [
    { role: 'user', text: 'What does hesed mean?' },
    { role: 'agent', text: 'It means...', streaming: true },
  ];
  const r = applyStoppedMessage(messages);
  assert.equal(r.messages.length, 2);
  assert.equal(r.messages[1].streaming, false);
  assert.equal(r.messages[1].stopped, true);
  assert.equal(r.messages[1].text, 'It means...'); // partial text kept
  assert.equal(r.stoppedQuestion, 'What does hesed mean?');
}

// Streaming agent message with no preceding user message
{
  const messages = [{ role: 'agent', text: '', streaming: true }];
  const r = applyStoppedMessage(messages);
  assert.equal(r.messages[0].stopped, true);
  assert.equal(r.stoppedQuestion, null);
}

console.log('stopped message ok');
