import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { registerHooks } from 'node:module';
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
import { getConversation, listMessages, listConversations, cursor } from '../src/lib/kapso.ts';
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
// MR-36: /inbox sends through Recepción's API (agent paused, Activity, idempotency); never to Kapso.
test('send goes through the Recepción API with the client key and never POSTs to Kapso', async (t) => {
  registerHooks({
    resolve: (specifier, context, next) =>
      next(specifier === 'next/server' ? 'next/server.js' : specifier, context),
  });
  const { NextRequest } = await import('next/server');
  const { encode } = await import('next-auth/jwt');
  const { authorizeInbox, sendThroughReception } = await import('../src/lib/inbox-auth.ts');
  process.env.AUTH_SECRET = 'synthetic-secret-for-tests-only';
  process.env.NEXTAUTH_URL = 'http://localhost:3215';
  process.env.API_URL = 'http://api.test';
  process.env.KAPSO_API_KEY = 'fixture-key';
  const reception = '22222222-2222-4222-8222-222222222222';
  const key = '33333333-3333-4333-8333-333333333333';
  const cookie =
    'next-auth.session-token=' +
    (await encode({
      token: { sub: 'staff', accessToken: 'h.e30.s', accessExpires: Date.now() + 600000 },
      secret: process.env.AUTH_SECRET,
    }));
  // What the API really answers: GET /api/conversations rows and the stored Message.
  let matches: unknown[] = [{ conversation: { id: reception }, contact: {}, channel: {} }];
  let sent: Response = Response.json({
    id: '44444444-4444-4444-8444-444444444444',
    conversationId: reception,
    externalId: 'wamid.sent',
    sender: 'human',
    body: 'test',
    type: 'text',
    status: 'sent',
    requestKey: key,
    createdAt: '2026-10-17T15:00:00+00:00',
  });
  const calls: { url: string; method: string; headers: Headers; body?: string }[] = [];
  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? 'GET';
    calls.push({
      url: String(url),
      method,
      headers: new Headers(init.headers),
      body: init.body ? Buffer.from(init.body as ArrayBuffer).toString() : undefined,
    });
    if (String(url) === 'http://api.test/api/channels')
      return Response.json([{ phoneNumberId: number }]);
    if (String(url).startsWith(`https://api.kapso.ai/platform/v1/whatsapp/conversations/${id}`))
      return Response.json({ data: conversation });
    if (String(url).startsWith('http://api.test/api/conversations?')) return Response.json(matches);
    if (
      method === 'POST' &&
      String(url) === `http://api.test/api/conversations/${reception}/messages`
    )
      return sent.clone();
    return new Response(null, { status: 404 });
  });
  const send = async (to: string, idempotencyKey = key) => {
    const req = new NextRequest(`http://localhost:3215/api/messages?phoneNumberId=${number}`, {
      method: 'POST',
      headers: { cookie, origin: 'http://localhost:3215', 'idempotency-key': idempotencyKey },
    });
    const { headers } = await authorizeInbox(req);
    return sendThroughReception(req, number, headers, { conversationId: id, to, text: 'test' });
  };
  const posts = () => calls.filter((c) => c.method === 'POST');

  const message = await send('+50370000000');
  assert.equal(message.id, 'wamid.sent');
  assert.equal(message.kapso?.status, 'sent');
  assert.equal(message.kapso?.whatsapp_conversation_id, id);
  const lookup = new URL(
    calls.find((c) => c.url.startsWith('http://api.test/api/conversations?'))!.url,
  );
  assert.equal(lookup.searchParams.get('phone'), '+50370000000');
  assert.equal(lookup.searchParams.get('phoneNumberId'), number);
  assert.equal(posts().length, 1);
  assert.equal(posts()[0].url, `http://api.test/api/conversations/${reception}/messages`);
  assert.equal(posts()[0].headers.get('idempotency-key'), key);
  assert.deepEqual(JSON.parse(posts()[0].body!), { body: 'test' });
  assert.equal(
    calls.filter((c) => c.url.startsWith('https://api.kapso.ai') && c.method !== 'GET').length,
    0,
  );

  calls.length = 0;
  await assert.rejects(send('50379999999'), { status: 400 });
  await assert.rejects(send('50370000000', 'not a key'), { status: 400 });
  matches = [];
  await assert.rejects(send('50370000000'), {
    status: 409,
    message: 'Esta conversación aún no está en Recepción',
  });
  matches = [{ conversation: { id: reception } }, { conversation: { id: id } }];
  await assert.rejects(send('50370000000'), {
    status: 409,
    message: 'Esta conversación aún no está en Recepción',
  });
  assert.equal(posts().length, 0);

  // The API decides whether manual sending is on; its refusal reaches the UI as it was written.
  matches = [{ conversation: { id: reception } }];
  sent = Response.json(
    { title: 'El envío manual está en pausa. Solicita a administración habilitarlo.', status: 400 },
    { status: 400 },
  );
  await assert.rejects(send('50370000000'), {
    status: 400,
    message: 'El envío manual está en pausa. Solicita a administración habilitarlo.',
  });
  sent = Response.json({
    id: reception,
    externalId: null,
    body: 'test',
    status: 'uncertain',
    createdAt: '2026-10-17T15:00:00+00:00',
  });
  await assert.rejects(send('50370000000'), { status: 502 });
  assert.equal(
    calls.filter((c) => c.url.startsWith('https://api.kapso.ai') && c.method !== 'GET').length,
    0,
  );
});

test('shared signed receiver routes each number separately without trusting inconsistent scopes', () => {
  const other = '1234567890';
  const items = [
    { phone_number_id: number, conversation },
    { phone_number_id: other, conversation: { ...conversation, phone_number_id: other } },
    { phone_number_id: number, conversation: { ...conversation, phone_number_id: other } },
  ];
  const events = decodeEvents({ batch: true, data: items }, 'whatsapp.conversation.created');
  assert.deepEqual(
    events.map((e) => e.phoneNumberId),
    [number, other],
  );
  let delivered = 0;
  const stop = subscribe(number, () => delivered++);
  events.forEach(publish);
  stop();
  assert.equal(delivered, 1);
});
