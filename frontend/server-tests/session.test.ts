import { test } from 'node:test';
import assert from 'node:assert/strict';
import { safeReturnPath, loginUrl, pathWithQuery } from '../src/lib/login-return.ts';
import { refreshSession, RefreshSessionError } from '../src/lib/refresh-session.ts';

test('login return preserves workspace context and rejects external or looping destinations', () => {
  for (const path of [
    '/?view=calendar',
    '/inbox?phoneNumberId=123',
    '/whatsapp?connection=returned',
    '/session-restored',
  ]) {
    assert.equal(safeReturnPath(path), path);
    assert.equal(
      new URL(loginUrl(path), 'https://local.test').searchParams.get('callbackUrl'),
      path,
    );
  }
  for (const path of [
    'https://evil.test',
    '//evil.test',
    '/\\evil.test',
    '/login',
    '/api/crm/me',
    '\n/inbox',
    ['/inbox'],
    null,
    7,
  ])
    assert.equal(safeReturnPath(path), '/');
  assert.equal(
    pathWithQuery('/', { view: 'inbox', label: 'cita médica', absent: undefined }),
    '/?view=inbox&label=cita+m%C3%A9dica',
  );
});
test('refresh coalesces rotating tokens; transient outages are retryable and expired grants request login', async () => {
  const saved = {
    issuer: process.env.KEYCLOAK_ISSUER,
    client: process.env.KEYCLOAK_CLIENT_ID,
    secret: process.env.KEYCLOAK_CLIENT_SECRET,
    fetch: globalThis.fetch,
  };
  process.env.KEYCLOAK_ISSUER = 'https://identity.example.invalid/realms/test';
  process.env.KEYCLOAK_CLIENT_ID = 'synthetic-client';
  process.env.KEYCLOAK_CLIENT_SECRET = 'synthetic-secret';
  let calls = 0;
  try {
    globalThis.fetch = async () => {
      calls++;
      await new Promise((resolve) => setTimeout(resolve, 10));
      return Response.json({
        access_token: 'new-access',
        refresh_token: 'rotated-refresh',
        expires_in: 300,
      });
    };
    const [first, second] = await Promise.all([
      refreshSession('concurrent-token'),
      refreshSession('concurrent-token'),
    ]);
    assert.deepEqual(first, second);
    assert.equal(calls, 1);
    assert.deepEqual(await refreshSession('concurrent-token'), first);
    assert.equal(calls, 1);
    assert.equal(first.refreshToken, 'rotated-refresh');
    globalThis.fetch = async () => {
      calls++;
      throw new TypeError('Network unavailable');
    };
    await assert.rejects(
      refreshSession('network-token'),
      (e: unknown) => e instanceof RefreshSessionError && e.status === 503,
    );
    globalThis.fetch = async () => Response.json({ access_token: 'recovered', expires_in: 60 });
    assert.equal((await refreshSession('network-token')).accessToken, 'recovered');
    globalThis.fetch = async () => Response.json({ error: 'invalid_grant' }, { status: 400 });
    await assert.rejects(
      refreshSession('expired-token'),
      (e: unknown) => e instanceof RefreshSessionError && e.status === 401,
    );
    globalThis.fetch = async () => Response.json({ error: 'invalid_client' }, { status: 401 });
    await assert.rejects(
      refreshSession('bad-config-token'),
      (e: unknown) => e instanceof RefreshSessionError && e.status === 503,
    );
    globalThis.fetch = async () => Response.json({ access_token: 'bad', expires_in: -1 });
    await assert.rejects(
      refreshSession('malformed-token'),
      (e: unknown) => e instanceof RefreshSessionError && e.status === 503,
    );
  } finally {
    globalThis.fetch = saved.fetch;
    for (const [name, value] of [
      ['KEYCLOAK_ISSUER', saved.issuer],
      ['KEYCLOAK_CLIENT_ID', saved.client],
      ['KEYCLOAK_CLIENT_SECRET', saved.secret],
    ]) {
      if (value === undefined) delete process.env[name!];
      else process.env[name!] = value;
    }
  }
});
