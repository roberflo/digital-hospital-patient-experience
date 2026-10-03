import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import {
  validSignature,
  acceptReceipt,
  decodeEvents,
  subscribe,
  publish,
  readBody,
} from '../src/lib/events.ts';
import { windowOpen, mergeMessages } from '../src/lib/kapso-types.ts';
import type { Conversation, Message } from '../src/lib/kapso-types.ts';
import {
  getConversation,
  listMessages,
  sendText,
  listConversations,
  cursor,
} from '../src/lib/kapso.ts';
const number = '613008901896177';
const id = '11111111-1111-4111-8111-111111111111';
const conversation: Conversation = {
  id,
  phone_number_id: number,
  phone_number: '+50370000000',
  status: 'active',
  kapso: { last_inbound_at: new Date().toISOString() },
};
const message: Message = {
  id: 'wamid.test',
  timestamp: '100',
  type: 'text',
  text: { body: 'prueba' },
  kapso: { direction: 'outbound', status: 'read' },
};
test('signature authenticates exact raw bytes, rejects missing, truncated and malformed signatures', () => {
  const body = Buffer.from('{"test":1}');
  const secret = 'fixture-secret';
  const signature = createHmac('sha256', secret).update(body).digest('hex');
  assert.equal(validSignature(body, signature, secret), true);
  for (const bad of [null, '', signature.slice(2), 'z'.repeat(64)])
    assert.equal(validSignature(body, bad, secret), false);
  assert.equal(validSignature(Buffer.from('{ "test":1}'), signature, secret), false);
  assert.equal(validSignature(body, signature, undefined), false);
});
test('receipt deduplicates retries, falls back to body hash and expires', () => {
  const body = Buffer.from('a');
  assert.equal(acceptReceipt('fixture-a', body, 0), true);
  assert.equal(acceptReceipt('fixture-a', body, 1), false);
  assert.equal(acceptReceipt('fixture-a', body, 3600001), true);
  assert.equal(acceptReceipt(null, body), true);
  assert.equal(acceptReceipt(null, body), false);
});
test('batch handles each item and ignores events from another number', () => {
  const item = { conversation, message, phone_number_id: number };
  const events = decodeEvents(
    { batch: true, data: [item, item, { ...item, phone_number_id: 'other' }] },
    'whatsapp.message.received',
    number,
  );
  assert.equal(events.length, 2);
  assert.equal(events[0].conversationId, id);
  assert.throws(() => decodeEvents({ batch: true, data: {} }, 'whatsapp.message.received', number));
  assert.equal(decodeEvents(item, 'unknown', number).length, 0);
});
test('bus isolates number subscriptions and removes listeners on cleanup', () => {
  let count = 0;
  const stop = subscribe(number, () => count++);
  publish({ event: 'whatsapp.message.read', phoneNumberId: 'other', payload: {} });
  publish({ event: 'whatsapp.message.read', phoneNumberId: number, payload: {} });
  stop();
  publish({ event: 'whatsapp.message.read', phoneNumberId: number, payload: {} });
  assert.equal(count, 1);
});
test('body size limit applies even without a content length header', async () => {
  await assert.rejects(
    readBody(new Request('http://localhost', { method: 'POST', body: '12345' }), 4),
  );
  assert.equal(
    (
      await readBody(new Request('http://localhost', { method: 'POST', body: '1234' }), 4)
    ).toString(),
    '1234',
  );
});
test('24h window rejects boundary, missing and future timestamps', () => {
  const now = Date.now();
  const at = (time: number) => ({
    ...conversation,
    kapso: { last_inbound_at: new Date(time).toISOString() },
  });
  assert.equal(windowOpen(at(now - 86400000 + 1), now), true);
  assert.equal(windowOpen(at(now - 86400000), now), false);
  assert.equal(windowOpen(at(now + 1), now), false);
  assert.equal(windowOpen({ ...conversation, kapso: {} }, now), false);
});
test('reconciliation deduplicates webhook/ACK and prevents read status regression', () => {
  const result = mergeMessages([message], [{ ...message, kapso: { status: 'sent' } }]);
  assert.equal(result.length, 1);
  assert.equal(result[0].kapso?.status, 'read');
  assert.equal(
    mergeMessages([message], [{ ...message, id: 'older', timestamp: '1' }])[0].id,
    'older',
  );
});
test('provider reads scope number, verify conversation ownership and reverse history', async (t) => {
  process.env.KAPSO_API_KEY = 'fixture-key';
  const paths: string[] = [];
  t.mock.method(globalThis, 'fetch', async (url: string) => {
    paths.push(url);
    return Response.json(
      url.includes(`/conversations/${id}`)
        ? { data: conversation }
        : url.includes('/messages?')
          ? {
              data: [
                { ...message, timestamp: '2' },
                { ...message, id: 'older', timestamp: '1' },
              ],
              paging: { next: 'next', cursors: { after: 'next-cursor' } },
            }
          : { data: [] },
    );
  });
  await listConversations(number, 'a&b');
  assert.match(paths[0], /phone_number_id=613008901896177/);
  assert.match(paths[0], /after=a%26b/);
  await assert.rejects(getConversation(id, 'other'), { status: 404 });
  const result = await listMessages(id, number, 'older');
  assert.equal(result.data[0].id, 'older');
  assert.equal(result.paging?.cursors?.after, 'next-cursor');
  assert.throws(() => cursor('a'.repeat(1001)), { status: 400 });
});
test('send checks fresh window and binds recipient before any provider send', async (t) => {
  let current = conversation;
  let writes = 0;
  process.env.KAPSO_MANUAL_SEND_ENABLED = 'true';
  process.env.KAPSO_API_KEY = 'fixture-key';
  t.mock.method(globalThis, 'fetch', async (_url: string, init: RequestInit) => {
    if (init.method === 'POST') {
      writes++;
      const data = JSON.parse(String(init.body));
      assert.equal(data.to, '50370000000');
      return Response.json({ messages: [{ id: 'wamid.sent' }] });
    }
    return Response.json({ data: current });
  });
  await assert.rejects(sendText(id, number, '50379999999', 'test'), { status: 400 });
  current = { ...conversation, phone_number: null };
  await assert.rejects(sendText(id, number, '50370000000', 'test'), { status: 400 });
  current = {
    ...conversation,
    kapso: { last_inbound_at: new Date(Date.now() - 86400001).toISOString() },
  };
  await assert.rejects(sendText(id, number, '50370000000', 'test'), { status: 409 });
  assert.equal(writes, 0);
  current = conversation;
  process.env.KAPSO_MANUAL_SEND_ENABLED = 'false';
  await assert.rejects(sendText(id, number, '50370000000', 'test'), { status: 403 });
  process.env.KAPSO_MANUAL_SEND_ENABLED = 'true';
  assert.equal((await sendText(id, number, '50370000000', 'test')).id, 'wamid.sent');
  assert.equal(writes, 1);
  process.env.KAPSO_MANUAL_SEND_ENABLED = 'false';
});
