import 'server-only';
import { NextRequest, NextResponse } from 'next/server';
import { proxy } from './crm-proxy';
import { InboxError } from './kapso';
// Reuse the BFF's refresh + cookie chunking; the API validates membership on every request.
export async function authorizeInbox(req: NextRequest) {
  const check = new NextRequest(new URL('/api/crm/channels', req.url), { headers: req.headers });
  const response = await proxy(check, { params: Promise.resolve({ path: ['channels'] }) });
  if (!response.ok)
    throw new InboxError(
      response.status,
      response.status === 401
        ? 'Sesión expirada. Inicia sesión de nuevo.'
        : 'No se pudo validar tu acceso.',
    );
  const channels: { phoneNumberId: string }[] = await response.json();
  if (!Array.isArray(channels)) throw new InboxError(502, 'No se pudieron validar los canales.');
  const requested = req.nextUrl.searchParams.get('phoneNumberId');
  const number =
    requested ||
    (channels.some((c) => c.phoneNumberId === process.env.KAPSO_PHONE_NUMBER_ID)
      ? process.env.KAPSO_PHONE_NUMBER_ID
      : channels.find((c) => /^\d+$/.test(c.phoneNumberId))?.phoneNumberId);
  if (
    !number ||
    !/^\d+$/.test(number) ||
    !Array.isArray(channels) ||
    !channels.some((c) => c.phoneNumberId === number)
  )
    throw new InboxError(403, 'Tu hospital no tiene acceso a este número de WhatsApp.');
  const headers = new Headers({ 'Cache-Control': 'no-store' });
  for (const cookie of response.headers.getSetCookie()) headers.append('Set-Cookie', cookie);
  return { number, headers };
}
export function errorResponse(error: unknown) {
  return NextResponse.json(
    { error: error instanceof InboxError ? error.message : 'No se pudo completar la operación.' },
    {
      status: error instanceof InboxError ? error.status : 500,
      headers: { 'Cache-Control': 'no-store' },
    },
  );
}
export function requireOrigin(req: NextRequest) {
  const origin = process.env.NEXTAUTH_URL;
  if (!origin || req.headers.get('origin') !== new URL(origin).origin)
    throw new InboxError(403, 'Origen inválido.');
}
