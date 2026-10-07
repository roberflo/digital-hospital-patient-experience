// Dueño de plataforma (docs/platform-owner.md, AC 8 y decisión 2): la recepción elegida vive en
// una cookie sellada del servidor ligada al `sub`, y solo el proxy pone `X-Acting-Tenant`.
import { test, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { encode, decode } from 'next-auth/jwt';
// Node's ESM resolver wants the file extension Next's bundler adds on its own.
registerHooks({
  resolve: (specifier, context, next) =>
    next(specifier === 'next/server' ? 'next/server.js' : specifier, context),
});
const { NextRequest } = await import('next/server');
const { isPlatformOwner, sealActing, readActing, platformSession, actingCookieName } =
  await import('../src/lib/acting.ts');
const { proxy, forward } = await import('../src/lib/crm-proxy.ts');
const { chooseActing, clearActing } = await import('../src/lib/acting-route.ts');
const { authorizeInbox } = await import('../src/lib/inbox-auth.ts');
const { InboxError } = await import('../src/lib/kapso.ts');

process.env.AUTH_SECRET = 'synthetic-secret-for-tests-only';
process.env.NEXTAUTH_URL = 'http://localhost:3215';
process.env.API_URL = 'http://api.test';
const origin = 'http://localhost:3215';
const A = '11111111-1111-4111-8111-111111111111';
const B = '22222222-2222-4222-8222-222222222222';
const owner = 'sub-platform-owner';
const admin = 'sub-hospital-admin';

const access = (sub: string, roles: string[]) =>
  `h.${Buffer.from(JSON.stringify({ sub, realm_access: { roles } })).toString('base64url')}.s`;
const platformAccess = access(owner, ['platform-owner']);
const hospitalAccess = access(admin, ['Administrador']);
const sessionCookie = async (sub: string, accessToken: string) =>
  'next-auth.session-token=' +
  (await encode({
    token: { sub, accessToken, accessExpires: Date.now() + 600000 },
    secret: process.env.AUTH_SECRET!,
  }));
const actingCookie = async (sub: string, tenantId = A) =>
  `${actingCookieName()}=${await sealActing(sub, { tenantId, name: 'Recepción A' })}`;
const ctx = (...path: string[]) => ({ params: Promise.resolve({ path }) });
function request(path: string, cookies: string[], init: Record<string, unknown> = {}) {
  const { headers, ...rest } = init as { headers?: Record<string, string> };
  return new NextRequest(origin + path, {
    ...rest,
    headers: { cookie: cookies.join('; '), ...headers },
  });
}
// Records every upstream call; answers with `body`.
function upstream(t: TestContext, body: unknown, status = 200) {
  const calls: { url: string; headers: Headers }[] = [];
  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), headers: new Headers(init.headers) });
    return Response.json(body, { status });
  });
  return calls;
}

test('platform role is read from the access token; anything unreadable is not platform', () => {
  assert.equal(isPlatformOwner(platformAccess), true);
  assert.equal(isPlatformOwner(access(owner, ['Administrador', 'platform-owner'])), true);
  assert.equal(isPlatformOwner(hospitalAccess), false);
  for (const bad of [undefined, '', 'not-a-jwt', 'a.b.c', access(owner, [])])
    assert.equal(isPlatformOwner(bad), false);
});

test('the choice is sealed, bound to its sub, and a foreign or forged cookie reads as no choice', async () => {
  const sealed = await sealActing(owner, { tenantId: A, name: 'Recepción A' });
  assert.equal(sealed.includes(A), false);
  assert.deepEqual(await readActing(sealed, owner), { tenantId: A, name: 'Recepción A' });
  assert.equal(await readActing(sealed, 'another-sub'), null);
  assert.equal(await readActing(sealed, undefined), null);
  assert.equal(await readActing(undefined, owner), null);
  assert.equal(await readActing('garbage', owner), null);
  assert.equal(await readActing(sealed.slice(0, -4) + 'AAAA', owner), null);
  // Sealed with the same secret but as a session token (no salt): not an acting cookie.
  const unsalted = await encode({
    token: { sub: owner, tenantId: A, name: 'x' },
    secret: process.env.AUTH_SECRET!,
  });
  assert.equal(await readActing(unsalted, owner), null);
  // A sealed value whose tenant is not a UUID is not a choice.
  const odd = await encode({
    token: { sub: owner, tenantId: '../settings', name: 'x' },
    secret: process.env.AUTH_SECRET!,
    salt: 'recepcion.acting',
  });
  assert.equal(await readActing(odd, owner), null);
});

test('session exposes platform only for the platform role, with the sealed choice or null', async () => {
  const sealed = await sealActing(owner, { tenantId: A, name: 'Recepción A' });
  assert.equal(await platformSession(hospitalAccess, admin, undefined), undefined);
  // A hospital session never becomes platform because a cookie is lying around.
  assert.equal(
    await platformSession(
      hospitalAccess,
      admin,
      await sealActing(admin, { tenantId: A, name: 'x' }),
    ),
    undefined,
  );
  assert.deepEqual(await platformSession(platformAccess, owner, undefined), { actingFor: null });
  assert.deepEqual(await platformSession(platformAccess, owner, sealed), {
    actingFor: { tenantId: A, name: 'Recepción A' },
  });
  assert.deepEqual(await platformSession(platformAccess, 'another-sub', sealed), {
    actingFor: null,
  });
});

test('hospital session never sends X-Acting-Tenant: not with a leftover cookie, not from the browser', async (t) => {
  const calls = upstream(t, []);
  const response = await proxy(
    request(
      '/api/crm/channels',
      [await sessionCookie(admin, hospitalAccess), await actingCookie(admin)],
      { headers: { 'X-Acting-Tenant': B } },
    ),
    ctx('channels'),
  );
  assert.equal(response.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].headers.get('x-acting-tenant'), null);
  assert.equal(calls[0].headers.get('authorization'), `Bearer ${hospitalAccess}`);
});

test('platform session sends the sealed choice and nothing the browser says', async (t) => {
  const calls = upstream(t, []);
  const session = await sessionCookie(owner, platformAccess);
  // Chosen A in the cookie and painted on screen; the browser claims B in a header and the query.
  const ok = await proxy(
    request(`/api/crm/channels?actingTenant=${B}`, [session, await actingCookie(owner)], {
      headers: { 'X-Acting-Tenant': B, 'X-Acting-Expected': A },
    }),
    ctx('channels'),
  );
  assert.equal(ok.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].headers.get('x-acting-tenant'), A);
  // The expectation is consumed by the proxy: the API never sees it.
  assert.equal(calls[0].headers.get('x-acting-expected'), null);
  // The reception list is tenant-less: no expectation needed, no header sent even with a choice.
  await proxy(
    request('/api/crm/platform/tenants', [session, await actingCookie(owner)]),
    ctx('platform', 'tenants'),
  );
  assert.equal(calls.length, 2);
  assert.equal(calls[1].headers.get('x-acting-tenant'), null);
  // Server-internal reads can opt out of the header entirely.
  await forward(
    request('/api/crm/channels', [session, await actingCookie(owner)]),
    ctx('channels'),
    false,
  );
  assert.equal(calls[2].headers.get('x-acting-tenant'), null);
});

// REC-3: opening a hospital's reception happens before any reception is chosen.
test('listing hospitals and opening a reception need no chosen reception and never carry one', async (t) => {
  const calls = upstream(t, { hospitals: [] });
  const session = await sessionCookie(owner, platformAccess);
  // From the selector (no choice), and also with a leftover choice in the cookie.
  for (const cookies of [[session], [session, await actingCookie(owner)]]) {
    const list = await proxy(
      request('/api/crm/platform/hospitals', cookies),
      ctx('platform', 'hospitals'),
    );
    assert.equal(list.status, 200);
    const open = await proxy(
      request('/api/crm/platform/tenants', cookies, {
        method: 'POST',
        headers: { origin, 'content-type': 'application/json', 'X-Acting-Tenant': B },
        body: JSON.stringify({ hospitalId: A }),
      }),
      ctx('platform', 'tenants'),
    );
    assert.equal(open.status, 200);
  }
  assert.deepEqual(
    calls.map((c) => c.url),
    [
      'http://api.test/api/platform/hospitals',
      'http://api.test/api/platform/tenants',
      'http://api.test/api/platform/hospitals',
      'http://api.test/api/platform/tenants',
    ],
  );
  for (const call of calls) assert.equal(call.headers.get('x-acting-tenant'), null);
  // The exemption is those two paths, not everything under /platform.
  const scoped = await proxy(
    request('/api/crm/platform/installation', [session, await actingCookie(owner)]),
    ctx('platform', 'installation'),
  );
  assert.equal(scoped.status, 403);
  assert.equal(calls.length, 4);
  // A hospital session gets nothing special: the request goes as its own, the API refuses it.
  await proxy(
    request('/api/crm/platform/hospitals', [await sessionCookie(admin, hospitalAccess)]),
    ctx('platform', 'hospitals'),
  );
  assert.equal(calls[4].headers.get('authorization'), `Bearer ${hospitalAccess}`);
  assert.equal(calls[4].headers.get('x-acting-tenant'), null);
});

// Two tabs share the cookie. The tab that painted «en nombre de A» must never act on B.
test('a request acts only for the reception its screen painted; otherwise the API is not called', async (t) => {
  const calls = upstream(t, []);
  const session = await sessionCookie(owner, platformAccess);
  const refused = async (cookies: string[], headers: Record<string, string>, method = 'GET') => {
    const response = await proxy(
      request('/api/crm/channels/onboarding', cookies, {
        method,
        headers: { origin, ...headers },
        ...(method === 'GET' ? {} : { body: '{}' }),
      }),
      ctx('channels', 'onboarding'),
    );
    return { status: response.status, code: (await response.json()).code };
  };
  // Tab 1 painted A; tab 2 chose B meanwhile: the WhatsApp link of B is never generated for A.
  assert.deepEqual(
    await refused([session, await actingCookie(owner, B)], { 'X-Acting-Expected': A }, 'POST'),
    { status: 409, code: 'acting_changed' },
  );
  // The choice expired, or was sealed for someone else: said as such.
  assert.deepEqual(await refused([session], { 'X-Acting-Expected': A }), {
    status: 409,
    code: 'acting_expired',
  });
  assert.deepEqual(
    await refused([session, await actingCookie('another-sub')], { 'X-Acting-Expected': A }),
    { status: 409, code: 'acting_expired' },
  );
  // No reused component escapes: a tenant-scoped request without the expectation is refused,
  // with a valid choice and without one (a browser X-Acting-Tenant changes nothing).
  assert.deepEqual(await refused([session, await actingCookie(owner)], {}), {
    status: 403,
    code: 'acting_expected_required',
  });
  assert.deepEqual(await refused([session], { 'X-Acting-Tenant': B }), {
    status: 403,
    code: 'acting_expected_required',
  });
  // A stale platform tab after a hospital user signed in on the same browser.
  assert.deepEqual(
    await refused([await sessionCookie(admin, hospitalAccess), await actingCookie(admin)], {
      'X-Acting-Expected': A,
    }),
    { status: 409, code: 'acting_changed' },
  );
  assert.equal(calls.length, 0);
});

test('choosing validates the id against the list read on the server before sealing', async (t) => {
  const tenants = [
    { id: A, name: 'Recepción A', hospitalConfigured: true, whatsAppConnected: false },
  ];
  const calls = upstream(t, { tenants });
  const session = await sessionCookie(owner, platformAccess);
  const post = (cookies: string[], body: unknown, from = origin) =>
    chooseActing(
      request('/api/platform/acting', cookies, {
        method: 'POST',
        headers: { origin: from, 'content-type': 'application/json' },
        body: JSON.stringify(body),
      }),
    );
  const sealedIn = (response: Response) =>
    response.headers.getSetCookie().find((c) => c.startsWith(actingCookieName() + '='));

  const ok = await post([session], { tenantId: A, name: 'Nombre del navegador' });
  assert.equal(ok.status, 204);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'http://api.test/api/platform/tenants');
  assert.equal(calls[0].headers.get('x-acting-tenant'), null);
  const cookie = sealedIn(ok)!;
  assert.match(cookie, /HttpOnly/i);
  assert.match(cookie, /SameSite=lax/i);
  assert.match(cookie, /Path=\//);
  // Twelve hours, as in Hospital: an hour expired mid-work with the notice still on screen.
  assert.match(cookie, /Max-Age=43200/);
  assert.equal(cookie.includes(A), false);
  const value = decodeURIComponent(cookie.split(';')[0].split('=').slice(1).join('='));
  // The name comes from the server's list, never from the request body.
  assert.deepEqual(await readActing(value, owner), { tenantId: A, name: 'Recepción A' });
  const sealed = await decode({
    token: value,
    secret: process.env.AUTH_SECRET!,
    salt: 'recepcion.acting',
  });
  assert.equal(sealed?.sub, owner);

  // Not in the list → 404, nothing sealed.
  const missing = await post([session], { tenantId: B });
  assert.equal(missing.status, 404);
  assert.equal(sealedIn(missing), undefined);
  // Not a UUID → 400 without asking the API.
  const before = calls.length;
  const malformed = await post([session], { tenantId: 'recepcion-a' });
  assert.equal(malformed.status, 400);
  assert.equal(calls.length, before);
  // Hospital session → 403, nothing sealed, API not asked.
  const hospital = await post([await sessionCookie(admin, hospitalAccess)], { tenantId: A });
  assert.equal(hospital.status, 403);
  assert.equal(sealedIn(hospital), undefined);
  // Cross-origin → 403; anonymous → 401.
  assert.equal((await post([session], { tenantId: A }, 'https://evil.test')).status, 403);
  assert.equal((await post([], { tenantId: A })).status, 401);
  assert.equal(calls.length, before);
});

test('an unreadable list seals nothing, and clearing expires the cookie', async (t) => {
  upstream(t, { title: 'boom' }, 500);
  const session = await sessionCookie(owner, platformAccess);
  const failed = await chooseActing(
    request('/api/platform/acting', [session], {
      method: 'POST',
      headers: { origin, 'content-type': 'application/json' },
      body: JSON.stringify({ tenantId: A }),
    }),
  );
  assert.equal(failed.status, 502);
  assert.deepEqual(failed.headers.getSetCookie(), []);

  const cleared = await clearActing(
    request('/api/platform/acting', [session], { method: 'DELETE', headers: { origin } }),
  );
  assert.equal(cleared.status, 204);
  assert.match(
    cleared.headers.getSetCookie().find((c) => c.startsWith(actingCookieName() + '='))!,
    /Max-Age=0/,
  );
  const foreign = await clearActing(
    request('/api/platform/acting', [session], {
      method: 'DELETE',
      headers: { origin: 'https://evil.test' },
    }),
  );
  assert.equal(foreign.status, 403);
});

test('the inbox stays closed to the platform owner and unchanged for a hospital session', async (t) => {
  process.env.KAPSO_PHONE_NUMBER_ID = '';
  const calls = upstream(t, [{ phoneNumberId: '613008901896177' }]);
  // Platform owner acting for A: /api/channels would answer 200 with the header. It is never asked.
  await assert.rejects(
    authorizeInbox(
      request('/api/conversations', [
        await sessionCookie(owner, platformAccess),
        await actingCookie(owner),
      ]),
    ),
    (e: unknown) => e instanceof InboxError && e.status === 403,
  );
  assert.equal(calls.length, 0);
  // Hospital session: same authorisation as before, and no header even with a leftover cookie.
  const granted = await authorizeInbox(
    request('/api/conversations', [
      await sessionCookie(admin, hospitalAccess),
      await actingCookie(admin),
    ]),
  );
  assert.equal(granted.number, '613008901896177');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].headers.get('x-acting-tenant'), null);
});
