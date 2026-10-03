import 'server-only';
import { windowOpen } from './kapso-types.ts';
import type { Conversation, Message, Page } from './kapso-types.ts';
export type { Conversation, Message, Page } from './kapso-types.ts';
export class InboxError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}
export function phoneNumberId() {
  const id = process.env.KAPSO_PHONE_NUMBER_ID;
  if (!id || !/^\d+$/.test(id)) throw new InboxError(503, 'WhatsApp no está configurado.');
  return id;
}
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  if (!process.env.KAPSO_API_KEY) throw new InboxError(503, 'WhatsApp no está configurado.');
  let response: Response;
  try {
    response = await fetch(`https://api.kapso.ai${path}`, {
      ...init,
      headers: { 'X-API-Key': process.env.KAPSO_API_KEY, 'Content-Type': 'application/json' },
      cache: 'no-store',
      redirect: 'error',
      signal: AbortSignal.timeout(8000),
    });
  } catch {
    throw new InboxError(
      502,
      init?.method === 'POST'
        ? 'No se pudo confirmar el envío. Revisa el historial antes de repetir.'
        : 'Kapso no está disponible. Inténtalo de nuevo.',
    );
  }
  if (!response.ok)
    throw new InboxError(
      response.status === 429 ? 429 : 502,
      'Kapso no pudo completar la operación. Revisa el historial antes de repetir un envío.',
    );
  return response.json();
}
export function cursor(value: string | null) {
  if (value && (value.length > 1000 || /[\x00-\x1f]/.test(value)))
    throw new InboxError(400, 'Cursor inválido.');
  return value || undefined;
}
export function listConversations(number: string, after?: string) {
  const query = new URLSearchParams({ phone_number_id: number, limit: '50' });
  if (after) query.set('after', after);
  return request<Page<Conversation>>(`/platform/v1/whatsapp/conversations?${query}`);
}
export async function getConversation(id: string, number: string) {
  if (!/^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i.test(id))
    throw new InboxError(400, 'Conversación inválida.');
  const result = await request<{ data: Conversation }>(`/platform/v1/whatsapp/conversations/${id}`);
  if (!result.data || result.data.phone_number_id !== number)
    throw new InboxError(404, 'Conversación no disponible.');
  return result.data;
}
export async function listMessages(conversationId: string, number: string, after?: string) {
  const conversation = await getConversation(conversationId, number);
  const query = new URLSearchParams({ conversation_id: conversationId, limit: '100' });
  if (after) query.set('after', after);
  const result = await request<Page<Message>>(`/platform/v1/whatsapp/messages?${query}`);
  return { ...result, data: result.data.slice().reverse(), conversation };
}
export async function sendText(conversationId: string, number: string, to: string, text: string) {
  const conversation = await getConversation(conversationId, number);
  if (
    !conversation.phone_number ||
    conversation.phone_number.replace(/^\+/, '') !== to.replace(/^\+/, '') ||
    !/^\+?\d{7,15}$/.test(to)
  )
    throw new InboxError(400, 'El destinatario no corresponde a esta conversación.');
  if (!windowOpen(conversation))
    throw new InboxError(409, 'Ventana de 24h cerrada: usa una plantilla');
  if (process.env.KAPSO_MANUAL_SEND_ENABLED !== 'true')
    throw new InboxError(403, 'El envío manual aún no está habilitado.');
  const result = await request<{ messages: { id: string }[] }>(
    `/meta/whatsapp/v24.0/${number}/messages`,
    {
      method: 'POST',
      body: JSON.stringify({
        messaging_product: 'whatsapp',
        to: to.replace(/^\+/, ''),
        type: 'text',
        text: { body: text },
      }),
    },
  );
  if (!result.messages?.[0]?.id)
    throw new InboxError(502, 'Envío sin confirmación. Revisa el historial antes de repetir.');
  return {
    id: result.messages[0].id,
    timestamp: String(Math.floor(Date.now() / 1000)),
    type: 'text',
    text: { body: text },
    kapso: { direction: 'outbound', status: 'sent', whatsapp_conversation_id: conversationId },
  } satisfies Message;
}
