import { acceptReceipt, decodeEvents, publish, readBody, validSignature } from '@/lib/events';
export const runtime = 'nodejs';
export async function POST(req: Request) {
  let raw: Buffer;
  try {
    raw = await readBody(req);
  } catch {
    return new Response(null, { status: 413 });
  }
  if (
    !validSignature(raw, req.headers.get('x-webhook-signature'), process.env.KAPSO_WEBHOOK_SECRET)
  )
    return new Response(null, { status: 401 });
  const version = req.headers.get('x-webhook-payload-version');
  if (version && version !== 'v2') return new Response(null, { status: 400 });
  try {
    const events = decodeEvents(
      JSON.parse(raw.toString('utf8')),
      req.headers.get('x-webhook-event'),
      process.env.KAPSO_PHONE_NUMBER_ID ?? '',
    );
    const key = req.headers.get('x-idempotency-key');
    if (key && key.length > 200) return new Response(null, { status: 400 });
    if (acceptReceipt(key, raw)) for (const event of events) publish(event);
    return Response.json({ ok: true });
  } catch {
    return new Response(null, { status: 400 });
  }
}
