import 'server-only';
import { refreshSession, RefreshSessionError } from './refresh-session.ts';
import { getToken, encode } from 'next-auth/jwt';
import { NextRequest, NextResponse } from 'next/server';
import { actingCookieName, isPlatformOwner, readActing } from './acting.ts';
const ALLOWED = new Set([
  'me',
  'overview',
  'contacts',
  'companies',
  'commercial',
  'opportunities',
  'activities',
  'activity-feed',
  'agent-metrics',
  'appointment-reminders',
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
export const sameOrigin = (req: NextRequest) =>
  req.headers.get('origin') === new URL(process.env.NEXTAUTH_URL!).origin;
type Context = { params: Promise<{ path: string[] }> };
// Rutas de plataforma sin recepción elegida: la lista de recepciones (y abrir una, REC-3) y los
// hospitales que aún no la tienen. Nunca llevan X-Acting-Tenant.
const TENANTLESS = new Set(['platform/tenants', 'platform/hospitals']);
const refuseActing = (status: number, code: string, title: string) =>
  NextResponse.json({ title, code }, { status, headers: { 'Cache-Control': 'no-store' } });
export const proxy = (req: NextRequest, context: Context) => forward(req, context);
// `acting: false` is for server-internal reads that must never run in the name of a reception.
export async function forward(req: NextRequest, { params }: Context, acting = true) {
  const { path } = await params;
  if (!ALLOWED.has(path[0]) || path.some((p) => p === '..' || p.includes('/') || p.includes('\\')))
    return new NextResponse(null, { status: 404 });
  if (!['GET', 'HEAD'].includes(req.method) && !sameOrigin(req))
    return NextResponse.json({ title: 'Origen inválido' }, { status: 403 });
  const token = await getToken({ req, secret: process.env.AUTH_SECRET });
  if (!token?.accessToken) return NextResponse.json({ title: 'Inicia sesión' }, { status: 401 });
  // Dueño de plataforma. La cookie de la elección es de todo el navegador, no de la pestaña: cada
  // petición dice en X-Acting-Expected qué recepción pintó su pantalla, y solo se actúa si es la
  // sellada para su `sub`. Si no, no se llama a la API. Antes del refresco: nada que conservar.
  const expected = req.headers.get('x-acting-expected');
  const platform = isPlatformOwner(token.accessToken);
  let actingTenant: string | undefined;
  if (expected && !platform)
    return refuseActing(409, 'acting_changed', 'La sesión de esta pestaña cambió.');
  if (acting && platform && !TENANTLESS.has(path.join('/'))) {
    if (!expected)
      return refuseActing(403, 'acting_expected_required', 'Elige una recepción para continuar.');
    const chosen = await readActing(req.cookies.get(actingCookieName())?.value, token.sub);
    if (!chosen)
      return refuseActing(
        409,
        'acting_expired',
        'Tu elección caducó; elige la recepción de nuevo.',
      );
    if (chosen.tenantId.toLowerCase() !== expected.toLowerCase())
      return refuseActing(409, 'acting_changed', 'Cambió de recepción en otra pestaña.');
    actingTenant = chosen.tenantId;
  }
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
  // Built from scratch: neither an X-Acting-Tenant sent by the browser nor X-Acting-Expected
  // reaches the API. The tenant comes only from the sealed cookie checked above.
  const headers = new Headers({ Authorization: `Bearer ${token.accessToken}` });
  if (actingTenant) headers.set('X-Acting-Tenant', actingTenant);
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
