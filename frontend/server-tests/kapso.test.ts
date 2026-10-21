import { test, type TestContext } from 'node:test';
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
import {
  getConversation,
  listMessages,
  listConversations,
  cursor,
  InboxError,
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
  const old = process.env.KAPSO_API_KEY;
  t.after(() => {
    if (old === undefined) delete process.env.KAPSO_API_KEY;
    else process.env.KAPSO_API_KEY = old;
  });
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
// Node resolves neither the `@/` alias nor `next/server` without the extension Next's bundler adds.
registerHooks({
  resolve: (specifier, context, next) =>
    next(
      specifier === 'next/server'
        ? 'next/server.js'
        : specifier.startsWith('@/')
          ? new URL(`../src/${specifier.slice(2)}.ts`, import.meta.url).href
          : specifier,
      context,
    ),
});
const { NextRequest } = await import('next/server');
const { encode, decode } = await import('next-auth/jwt');
const { authorizeInbox, sendThroughReception } = await import('../src/lib/inbox-auth.ts');
const { POST } = await import('../src/app/api/messages/route.ts');
const { GET: listRoute } = await import('../src/app/api/conversations/route.ts');
const reception = '22222222-2222-4222-8222-222222222222';
const key = '33333333-3333-4333-8333-333333333333';
const stored = (status: string, externalId: string | null = 'wamid.sent') =>
  Response.json({
    id: '44444444-4444-4444-8444-444444444444',
    conversationId: reception,
    externalId,
    sender: 'human',
    body: 'test',
    type: 'text',
    status,
    requestKey: key,
    createdAt: '2026-10-17T15:00:00+00:00',
  });
// Sets env for one test and puts back what was there.
function env(t: TestContext, values: Record<string, string | undefined>) {
  const old = Object.fromEntries(Object.keys(values).map((k) => [k, process.env[k]]));
  const put = (v: Record<string, string | undefined>) => {
    for (const [k, value] of Object.entries(v))
      if (value === undefined) delete process.env[k];
      else process.env[k] = value;
  };
  put(values);
  t.after(() => put(old));
}
// What the API, Kapso and Keycloak really answer; every call recorded.
function stack(t: TestContext) {
  env(t, {
    AUTH_SECRET: 'synthetic-secret-for-tests-only',
    NEXTAUTH_URL: 'http://localhost:3215',
    API_URL: 'http://api.test',
    KAPSO_API_KEY: 'fixture-key',
    KEYCLOAK_ISSUER: 'http://keycloak.test/realms/r',
    KEYCLOAK_INTERNAL_ISSUER: undefined,
    KEYCLOAK_CLIENT_ID: 'web',
    KEYCLOAK_CLIENT_SECRET: 'synthetic',
  });
  const state = {
    kapso: conversation as Conversation,
    matches: [{ conversation: { id: reception }, contact: {}, channel: {} }] as unknown[],
    sent: () => stored('sent'),
    refreshes: [] as string[],
    calls: [] as { url: string; method: string; headers: Headers; body?: string }[],
  };
  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? 'GET';
    const body =
      init.body instanceof URLSearchParams
        ? init.body.toString()
        : init.body
          ? Buffer.from(init.body as ArrayBuffer).toString()
          : undefined;
    state.calls.push({ url: String(url), method, headers: new Headers(init.headers), body });
    if (String(url).startsWith('http://keycloak.test/')) {
      const n = state.refreshes.push(new URLSearchParams(body).get('refresh_token')!);
      // Short-lived on purpose: every call in the request renews again.
      return Response.json({ access_token: `h.e30.${n}`, refresh_token: `r${n}`, expires_in: 1 });
    }
    if (String(url) === 'http://api.test/api/channels')
      return Response.json([{ phoneNumberId: number }]);
    if (String(url).startsWith(`https://api.kapso.ai/platform/v1/whatsapp/conversations/${id}`))
      return Response.json({ data: state.kapso });
    if (String(url).startsWith('https://api.kapso.ai/platform/v1/whatsapp/conversations?'))
      return Response.json({ data: [state.kapso] });
    if (String(url).startsWith('http://api.test/api/conversations?'))
      return Response.json(state.matches);
    if (
      method === 'POST' &&
      String(url) === `http://api.test/api/conversations/${reception}/messages`
    )
      return state.sent();
    return new Response(null, { status: 404 });
  });
  return state;
}
const session = async (token: Record<string, unknown>) =>
  'next-auth.session-token=' +
  (await encode({ token: { sub: 'staff', ...token }, secret: process.env.AUTH_SECRET! }));
const fresh = () => session({ accessToken: 'h.e30.s', accessExpires: Date.now() + 600000 });
const inboxRequest = (cookie: string, body: unknown, idempotencyKey = key) =>
  new NextRequest(`http://localhost:3215/api/messages?phoneNumberId=${number}`, {
    method: 'POST',
    headers: {
      cookie,
      origin: 'http://localhost:3215',
      'content-type': 'application/json',
      'idempotency-key': idempotencyKey,
    },
    body: JSON.stringify(body),
  });
async function send(to: string, idempotencyKey = key) {
  const req = inboxRequest(await fresh(), {}, idempotencyKey);
  const { headers } = await authorizeInbox(req);
  return sendThroughReception(req, number, headers, { conversationId: id, to, text: 'test' });
}
const writes = <T extends { url: string; method: string }>(calls: T[]) =>
  calls.filter((c) => c.method !== 'GET' && !c.url.startsWith('http://keycloak.test/'));

test('send goes through the Recepción API with the client key and never POSTs to Kapso', async (t) => {
  const api = stack(t);
  const message = await send('+50370000000');
  assert.equal(message.id, 'wamid.sent');
  assert.equal(message.kapso?.status, 'sent');
  assert.equal(message.kapso?.whatsapp_conversation_id, id);
  const lookup = new URL(
    api.calls.find((c) => c.url.startsWith('http://api.test/api/conversations?'))!.url,
  );
  assert.equal(lookup.searchParams.get('phone'), '+50370000000');
  assert.equal(lookup.searchParams.get('phoneNumberId'), number);
  assert.equal(writes(api.calls).length, 1);
  assert.equal(writes(api.calls)[0].url, `http://api.test/api/conversations/${reception}/messages`);
  assert.equal(writes(api.calls)[0].headers.get('idempotency-key'), key);
  assert.deepEqual(JSON.parse(writes(api.calls)[0].body!), { body: 'test' });
});

test('send refuses a wrong recipient, a missing key and a conversation Recepción does not hold', async (t) => {
  const api = stack(t);
  await assert.rejects(send('50379999999'), { status: 400 });
  api.kapso = { ...conversation, phone_number: null };
  await assert.rejects(send('50370000000'), { status: 400 });
  api.kapso = conversation;
  await assert.rejects(send('50370000000', 'not a key'), { status: 400 });
  const absent = {
    status: 409,
    message:
      'Esta conversación aún no está en Recepción. Ábrela desde la bandeja cuando el paciente vuelva a escribir.',
  };
  api.matches = [];
  await assert.rejects(send('50370000000'), absent);
  api.matches = [{ conversation: { id: reception } }, { conversation: { id } }];
  await assert.rejects(send('50370000000'), absent);
  assert.equal(writes(api.calls).length, 0);
});

test('the API decides: its refusals reach the UI, failed is an error and uncertain is not', async (t) => {
  const api = stack(t);
  const paused = 'El envío manual está en pausa. Solicita a administración habilitarlo.';
  api.sent = () => Response.json({ title: paused, status: 400 }, { status: 400 });
  await assert.rejects(send('50370000000'), { status: 400, message: paused });
  // Taken by someone else: not 403, which the inbox reads as "you lost access to this number".
  api.sent = () =>
    Response.json({ title: 'No tienes permiso para esta operación', status: 403 }, { status: 403 });
  await assert.rejects(send('50370000000'), {
    status: 409,
    message: 'Esta conversación la atiende otra persona; pídele que te la asigne.',
  });
  api.sent = () => stored('failed', null);
  await assert.rejects(send('50370000000'), { status: 502 });
  api.sent = () => stored('uncertain', null);
  const uncertain = await send('50370000000');
  assert.equal(uncertain.kapso?.status, 'uncertain');
  assert.equal(uncertain.id, '44444444-4444-4444-8444-444444444444');
  assert.equal(writes(api.calls).filter((c) => c.url.startsWith('https://api.kapso.ai')).length, 0);
});

test('POST /api/messages sends through the API and keeps every renewed session cookie', async (t) => {
  const api = stack(t);
  // Expiring session: each of the three API calls renews it, each with the token the previous left.
  const cookie = await session({
    accessToken: 'h.e30.0',
    refreshToken: 'r0',
    accessExpires: Date.now(),
  });
  const body = { conversationId: id, to: '+50370000000', text: ' test ' };
  const response = await POST(inboxRequest(cookie, body));
  assert.equal(response.status, 200);
  assert.equal((await response.json()).message.id, 'wamid.sent');
  assert.deepEqual(api.refreshes, ['r0', 'r1', 'r2']);
  assert.deepEqual(JSON.parse(writes(api.calls)[0].body!), { body: 'test' });
  const last = response.headers
    .getSetCookie()
    .filter((c) => c.startsWith('next-auth.session-token='))
    .at(-1)!;
  const token = await decode({
    token: last.split(';')[0].split('=')[1],
    secret: process.env.AUTH_SECRET!,
  });
  assert.equal(token?.refreshToken, 'r3');

  // An error after a renewal still hands the browser the renewed cookie.
  api.matches = [];
  const refused = await POST(
    inboxRequest(
      await session({ accessToken: 'h.e30.x', refreshToken: 'rx', accessExpires: Date.now() }),
      body,
    ),
  );
  assert.equal(refused.status, 409);
  assert.ok(refused.headers.getSetCookie().some((c) => c.startsWith('next-auth.session-token=')));
});

test('the composer opens with the same rule the API applies', async (t) => {
  stack(t);
  const manual = async (values: Record<string, string | undefined>) => {
    env(t, values);
    const response = await listRoute(
      new NextRequest(`http://localhost:3215/api/conversations?phoneNumberId=${number}`, {
        headers: { cookie: await fresh() },
      }),
    );
    return (await response.json()).manualSendEnabled;
  };
  assert.equal(
    await manual({ SEND_ENABLED: undefined, KAPSO_MANUAL_SEND_ENABLED: undefined }),
    false,
  );
  assert.equal(await manual({ SEND_ENABLED: 'true', KAPSO_MANUAL_SEND_ENABLED: undefined }), true);
  assert.equal(await manual({ SEND_ENABLED: undefined, KAPSO_MANUAL_SEND_ENABLED: 'true' }), true);
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
test('an inbox error after a renewal still hands the browser the rotated session cookie', async () => {
  const { errorResponse } = await import('../src/lib/inbox-auth.ts');
  const renewed = new Headers();
  renewed.append('Set-Cookie', 'next-auth.session-token=rotated; Path=/; HttpOnly');
  const response = errorResponse(new InboxError(403, 'Sin acceso.', renewed));
  assert.equal(response.status, 403);
  assert.deepEqual(response.headers.getSetCookie(), [
    'next-auth.session-token=rotated; Path=/; HttpOnly',
  ]);
});
