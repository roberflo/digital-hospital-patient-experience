import 'server-only';
import { getToken } from 'next-auth/jwt';
import { NextRequest, NextResponse } from 'next/server';
import { forward, sameOrigin } from './crm-proxy.ts';
import {
  actingCookieName,
  actingCookieOptions,
  isPlatformOwner,
  sealActing,
  UUID,
} from './acting.ts';
const noStore = { 'Cache-Control': 'no-store' };
const refuse = (status: number, title: string) =>
  NextResponse.json({ title }, { status, headers: noStore });
const unreadable = 'No se pudo leer la lista de recepciones. No se eligió ninguna.';
// POST /api/platform/acting { tenantId }: sella la elección solo si esa recepción está en la lista
// que el servidor acaba de leer con el token del dueño. El nombre sale de esa lista, no del cuerpo.
export async function chooseActing(req: NextRequest) {
  if (!sameOrigin(req)) return refuse(403, 'Origen inválido');
  const token = await getToken({ req, secret: process.env.AUTH_SECRET });
  if (!token?.accessToken || !token.sub) return refuse(401, 'Inicia sesión');
  if (!isPlatformOwner(token.accessToken))
    return refuse(403, 'Solo el dueño de plataforma elige una recepción.');
  const tenantId = (await req.json().catch(() => null))?.tenantId;
  if (typeof tenantId !== 'string' || !UUID.test(tenantId))
    return refuse(400, 'Recepción inválida.');
  const list = await forward(
    new NextRequest(new URL('/api/crm/platform/tenants', req.url), {
      headers: { cookie: req.headers.get('cookie') ?? '' },
    }),
    { params: Promise.resolve({ path: ['platform', 'tenants'] }) },
    false,
  );
  if (!list.ok) return refuse(list.status === 401 ? 401 : 502, unreadable);
  const tenants: unknown = (await list.json().catch(() => null))?.tenants;
  if (!Array.isArray(tenants)) return refuse(502, unreadable);
  const tenant = tenants.find(
    (t) => typeof t?.id === 'string' && t.id.toLowerCase() === tenantId.toLowerCase(),
  );
  if (!tenant) return refuse(404, 'Esa recepción no existe.');
  const response = new NextResponse(null, { status: 204, headers: noStore });
  response.cookies.set(
    actingCookieName(),
    await sealActing(token.sub, { tenantId: tenant.id, name: String(tenant.name ?? '') }),
    actingCookieOptions(),
  );
  // The list read may have refreshed the session; keep its cookies.
  for (const cookie of list.headers.getSetCookie()) response.headers.append('Set-Cookie', cookie);
  return response;
}
// DELETE /api/platform/acting: «Cambiar».
export async function clearActing(req: NextRequest) {
  if (!sameOrigin(req)) return refuse(403, 'Origen inválido');
  const response = new NextResponse(null, { status: 204, headers: noStore });
  response.cookies.set(actingCookieName(), '', actingCookieOptions(0));
  return response;
}
