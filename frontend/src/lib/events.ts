import 'server-only';
import { EventEmitter } from 'node:events';
import { createHmac, timingSafeEqual, createHash } from 'node:crypto';
import type { InboxEvent } from './kapso-types';
// Single persistent Node process only. Replace this isolated transport with a shared
// broker (Pusher/Ably/Supabase) before running multiple replicas or serverless workers.
const globalBus = globalThis as typeof globalThis & {
  kapsoBus?: { emitter: EventEmitter; seen: Map<string, number> };
};
const bus = (globalBus.kapsoBus ??= {
  emitter: new EventEmitter().setMaxListeners(0),
  seen: new Map(),
});
export function subscribe(number: string, listener: (event: InboxEvent) => void) {
  const callback = (event: InboxEvent) => {
    if (event.phoneNumberId === number) listener(event);
  };
  bus.emitter.on('event', callback);
  return () => {
    bus.emitter.off('event', callback);
  };
}
export function publish(event: InboxEvent) {
  bus.emitter.emit('event', event);
}
export function validSignature(body: Buffer, signature: string | null, secret: string | undefined) {
  if (!secret || !signature || !/^[a-f\d]{64}$/i.test(signature)) return false;
  return timingSafeEqual(
    createHmac('sha256', secret).update(body).digest(),
    Buffer.from(signature, 'hex'),
  );
}
export function acceptReceipt(key: string | null, body: Buffer, now = Date.now()) {
  for (const [id, expiry] of bus.seen) {
    if (expiry > now) break;
    bus.seen.delete(id);
  }
  const id = key || createHash('sha256').update(body).digest('hex');
  if (bus.seen.has(id)) return false;
  if (bus.seen.size >= 10000) bus.seen.delete(bus.seen.keys().next().value!);
  bus.seen.set(id, now + 3600000);
  return true;
}
export const eventNames = new Set([
  'whatsapp.message.received',
  'whatsapp.message.sent',
  'whatsapp.message.delivered',
  'whatsapp.message.read',
  'whatsapp.message.failed',
  'whatsapp.conversation.created',
  'whatsapp.conversation.ended',
]);
export function decodeEvents(value: unknown, header: string | null, number: string): InboxEvent[] {
  if (!value || typeof value !== 'object') throw new Error('Invalid payload');
  const body = value as Record<string, unknown>;
  const event = header || String(body.type || '');
  if (!eventNames.has(event)) return [];
  const items = body.batch === true ? body.data : [body];
  if (!Array.isArray(items) || items.length > 1000) throw new Error('Invalid batch');
  return items.flatMap((item) => {
    if (!item || typeof item !== 'object') throw new Error('Invalid item');
    const payload = item as InboxEvent['payload'];
    const phone = payload.phone_number_id ?? payload.conversation?.phone_number_id;
    if (phone !== number) return [];
    const conversationId =
      payload.conversation?.id ?? payload.message?.kapso?.whatsapp_conversation_id;
    return [{ event, phoneNumberId: number, conversationId, payload }];
  });
}
export async function readBody(req: Request, limit = 2 * 1024 * 1024) {
  if (Number(req.headers.get('content-length')) > limit) throw new Error('Body too large');
  const reader = req.body?.getReader();
  if (!reader) return Buffer.alloc(0);
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.length;
      if (size > limit) {
        await reader.cancel();
        throw new Error('Body too large');
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  return Buffer.concat(chunks);
}
