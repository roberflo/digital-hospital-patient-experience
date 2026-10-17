import 'server-only';
import { NextRequest, NextResponse } from 'next/server';
import { getToken } from 'next-auth/jwt';
import { forward } from './crm-proxy.ts';
import { isPlatformOwner } from './acting.ts';
import { getConversation, InboxError, type Message } from './kapso.ts';
// Reuse the BFF's refresh + cookie chunking; the API validates membership on every request.
export async function authorizeInbox(req: NextRequest) {
  // El dueño de plataforma solo configura (docs/platform-owner.md, decisión 2). En nombre de una
  // recepción, GET /api/channels le respondería 200 y lo que sigue lee Kapso con la clave del
  // servidor: se niega aquí, y la comprobación de abajo nunca viaja con X-Acting-Tenant.
  const token = await getToken({ req, secret: process.env.AUTH_SECRET });
  if (isPlatformOwner(token?.accessToken))
    throw new InboxError(403, 'El dueño de plataforma no abre la bandeja de una recepción.');
  const check = new NextRequest(new URL('/api/crm/channels', req.url), { headers: req.headers });
  const response = await forward(check, { params: Promise.resolve({ path: ['channels'] }) }, false);
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
// The check above may have renewed the session. Later calls in the same request carry the renewed
// cookies, so a rotating refresh token is never spent twice.
function renewedCookies(req: NextRequest, headers: Headers) {
  const jar = new Map(req.cookies.getAll().map((c) => [c.name, c.value]));
  for (const line of headers.getSetCookie()) {
    const pair = line.split(';')[0];
    const at = pair.indexOf('=');
    if (pair.slice(at + 1)) jar.set(pair.slice(0, at), pair.slice(at + 1));
    else jar.delete(pair.slice(0, at));
  }
  return [...jar].map(([name, value]) => `${name}=${value}`).join('; ');
}
async function reception<T>(
  req: NextRequest,
  headers: Headers,
  path: string[],
  init: { search?: string; body?: unknown } = {},
): Promise<T> {
  const outgoing = new Headers(req.headers);
  outgoing.set('cookie', renewedCookies(req, headers));
  outgoing.set('content-type', 'application/json');
  outgoing.delete('content-length');
  const call = new NextRequest(new URL(`/api/crm/${path.join('/')}${init.search ?? ''}`, req.url), {
    method: init.body === undefined ? 'GET' : 'POST',
    headers: outgoing,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });
  const response = await forward(call, { params: Promise.resolve({ path }) }, false);
  const result = await response.json().catch(() => null);
  if (!response.ok)
    throw new InboxError(
      response.status,
      typeof result?.title === 'string'
        ? result.title
        : 'Recepción no pudo completar el envío. Revisa el historial antes de repetir.',
    );
  return result;
}
type ReceptionMessage = {
  externalId?: string | null;
  id: string;
  body: string;
  status: string;
  createdAt: string;
};
// MR-36: the reply goes through Recepción's API, which pauses the agent, leaves the Activity,
// decides whether manual sending is on and deduplicates by the client's Idempotency-Key.
export async function sendThroughReception(
  req: NextRequest,
  number: string,
  headers: Headers,
  input: { conversationId: string; to: string; text: string },
): Promise<Message> {
  if (
    !/^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i.test(
      req.headers.get('idempotency-key') ?? '',
    )
  )
    throw new InboxError(400, 'Envío sin clave de idempotencia.');
  const conversation = await getConversation(input.conversationId, number);
  if (
    !conversation.phone_number ||
    conversation.phone_number.replace(/^\+/, '') !== input.to.replace(/^\+/, '') ||
    !/^\+?\d{7,15}$/.test(input.to)
  )
    throw new InboxError(400, 'El destinatario no corresponde a esta conversación.');
  const rows = await reception<{ conversation: { id: string } }[]>(
    req,
    headers,
    ['conversations'],
    {
      search: '?' + new URLSearchParams({ phone: input.to, phoneNumberId: number }),
    },
  );
  if (!Array.isArray(rows) || rows.length !== 1)
    throw new InboxError(409, 'Esta conversación aún no está en Recepción');
  const sent = await reception<ReceptionMessage>(
    req,
    headers,
    ['conversations', rows[0].conversation.id, 'messages'],
    { body: { body: input.text } },
  );
  if (sent.status === 'failed')
    throw new InboxError(502, 'WhatsApp no aceptó el mensaje; no salió. Revisa el historial.');
  if (sent.status !== 'sent')
    throw new InboxError(
      502,
      'No se pudo confirmar el envío. Revisa el historial antes de repetir.',
    );
  return {
    id: sent.externalId || sent.id,
    timestamp: String(Math.floor(Date.parse(sent.createdAt) / 1000)),
    type: 'text',
    text: { body: sent.body },
    kapso: {
      direction: 'outbound',
      status: 'sent',
      whatsapp_conversation_id: input.conversationId,
    },
  };
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
