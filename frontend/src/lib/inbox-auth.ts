import 'server-only';
import { NextRequest, NextResponse } from 'next/server';
import { getToken } from 'next-auth/jwt';
import { forward, sameOrigin } from './crm-proxy.ts';
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
  const headers = new Headers({ 'Cache-Control': 'no-store' });
  for (const cookie of response.headers.getSetCookie()) headers.append('Set-Cookie', cookie);
  if (!response.ok)
    throw new InboxError(
      response.status,
      response.status === 401
        ? 'Sesión expirada. Inicia sesión de nuevo.'
        : 'No se pudo validar tu acceso.',
      headers,
    );
  const channels: { phoneNumberId: string }[] = await response.json();
  if (!Array.isArray(channels))
    throw new InboxError(502, 'No se pudieron validar los canales.', headers);
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
    throw new InboxError(403, 'Tu hospital no tiene acceso a este número de WhatsApp.', headers);
  return { number, headers };
}
// Any call may renew the session. Each later call in the same request carries the cookies the
// previous ones left, and `headers` collects every Set-Cookie for the response, error or not.
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
  for (const cookie of response.headers.getSetCookie()) headers.append('Set-Cookie', cookie);
  const result = await response.json().catch(() => null);
  // 403 here is TeamEndpoints.RequireEditable; as 403 the inbox would read it as lost access.
  if (response.status === 403 && init.body !== undefined)
    throw new InboxError(
      409,
      'Esta conversación la atiende otra persona; pídele que te la asigne.',
    );
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
    throw new InboxError(
      409,
      'Esta conversación aún no está en Recepción. Ábrela desde la bandeja cuando el paciente vuelva a escribir.',
    );
  const sent = await reception<ReceptionMessage>(
    req,
    headers,
    ['conversations', rows[0].conversation.id, 'messages'],
    { body: { body: input.text } },
  );
  if (sent.status === 'failed')
    throw new InboxError(502, 'WhatsApp no aceptó el mensaje; no salió. Revisa el historial.');
  // Stored but unconfirmed: shown as such, never as a failure that invites a retry with a new key.
  const status = sent.status === 'sent' ? 'sent' : 'uncertain';
  return {
    id: sent.externalId || sent.id,
    timestamp: String(Math.floor(Date.parse(sent.createdAt) / 1000)),
    type: 'text',
    text: { body: sent.body },
    kapso: {
      direction: 'outbound',
      status,
      whatsapp_conversation_id: input.conversationId,
    },
  };
}
export function errorResponse(error: unknown, headers?: Headers) {
  const response = NextResponse.json(
    { error: error instanceof InboxError ? error.message : 'No se pudo completar la operación.' },
    {
      status: error instanceof InboxError ? error.status : 500,
      headers: { 'Cache-Control': 'no-store' },
    },
  );
  const cookies = headers ?? (error instanceof InboxError ? error.headers : undefined);
  for (const cookie of cookies?.getSetCookie() ?? []) response.headers.append('Set-Cookie', cookie);
  return response;
}
export function requireOrigin(req: NextRequest) {
  if (!sameOrigin(req)) throw new InboxError(403, 'Origen inválido.');
}
