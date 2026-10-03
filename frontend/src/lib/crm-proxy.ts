import 'server-only';
import { refreshSession, RefreshSessionError } from './refresh-session';
import { getToken, encode } from 'next-auth/jwt';
import { NextRequest, NextResponse } from 'next/server';
const ALLOWED = new Set([
  'me',
  'overview',
  'contacts',
  'companies',
  'opportunities',
  'activities',
  'members',
  'settings',
  'audit',
  'jobs',
  'conversations',
  'messages',
  'channels',
  'hospital',
  'google',
  'platform',
  'assistant',
  'saved-replies',
  'inbox-views',
  'macros',
]);
export async function proxy(req: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const { path } = await params;
  if (!ALLOWED.has(path[0]) || path.some((p) => p === '..' || p.includes('/') || p.includes('\\')))
    return new NextResponse(null, { status: 404 });
  if (!['GET', 'HEAD'].includes(req.method)) {
    const origin = req.headers.get('origin');
    if (!origin || origin !== new URL(process.env.NEXTAUTH_URL!).origin)
      return NextResponse.json({ title: 'Origen inválido' }, { status: 403 });
  }
  const token = await getToken({ req, secret: process.env.AUTH_SECRET });
  if (!token?.accessToken) return NextResponse.json({ title: 'Inicia sesión' }, { status: 401 });
  let refreshed = false;
  if ((token.accessExpires ?? 0) < Date.now() + 15000) {
    if (!token.refreshToken)
      return NextResponse.json(
        { title: 'Sesión expirada. Inicia sesión de nuevo.' },
        { status: 401 },
      );
    try {
      const renewed = await refreshSession(token.refreshToken);
      token.accessToken = renewed.accessToken;
      token.refreshToken = renewed.refreshToken;
      token.accessExpires = renewed.accessExpires;
      refreshed = true;
    } catch (error) {
      return NextResponse.json(
        {
          title:
            error instanceof RefreshSessionError
              ? error.message
              : 'No se pudo renovar la sesión. Inténtalo de nuevo.',
        },
        { status: error instanceof RefreshSessionError ? error.status : 503 },
      );
    }
  }
  const headers = new Headers({ Authorization: `Bearer ${token.accessToken}` });
  for (const name of ['content-type', 'idempotency-key']) {
    const v = req.headers.get(name);
    if (v) headers.set(name, v);
  }
  const body = ['GET', 'HEAD'].includes(req.method) ? undefined : await req.arrayBuffer();
  if (body && body.byteLength > 20 * 1024 * 1024) return new NextResponse(null, { status: 413 });
  try {
    const upstream = await fetch(
      `${process.env.API_URL}/api/${path.map(encodeURIComponent).join('/')}${req.nextUrl.search}`,
      {
        method: req.method,
        headers,
        body,
        cache: 'no-store',
        signal: AbortSignal.timeout(120000),
        redirect: 'manual',
      },
    );
    const response = new NextResponse(upstream.body, {
      status: upstream.status,
      headers: {
        'Content-Type': upstream.headers.get('content-type') ?? 'application/json',
        'Cache-Control': 'no-store',
      },
    });
    const disposition = upstream.headers.get('content-disposition');
    if (disposition) response.headers.set('Content-Disposition', disposition);
    if (refreshed) {
      const secure = process.env.NEXTAUTH_URL?.startsWith('https:');
      const name = secure ? '__Secure-next-auth.session-token' : 'next-auth.session-token';
      const options = { httpOnly: true, secure, path: '/', sameSite: 'lax' as const, maxAge: 3600 };
      // Keycloak tokens can exceed the browser's 4 KiB cookie limit. Match NextAuth chunking
      // and expire obsolete chunks when the refreshed token becomes smaller.
      for (const cookie of req.cookies.getAll()) {
        if (cookie.name === name || cookie.name.startsWith(name + '.')) {
          response.cookies.set(cookie.name, '', { ...options, maxAge: 0 });
        }
      }
      const encoded = await encode({ token, secret: process.env.AUTH_SECRET!, maxAge: 3600 });
      const chunks = encoded.match(/.{1,3800}/g) ?? [];
      chunks.forEach((chunk, i) =>
        response.cookies.set(chunks.length === 1 ? name : `${name}.${i}`, chunk, options),
      );
    }
    return response;
  } catch {
    return NextResponse.json(
      {
        title:
          'No se pudo conectar con recepción. Si enviaste una operación, comprueba el historial antes de repetir.',
      },
      { status: 502 },
    );
  }
}
