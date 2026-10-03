import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse, requireOrigin } from '@/lib/inbox-auth';
import { InboxError, sendText } from '@/lib/kapso';
import { publish, readBody } from '@/lib/events';
export const runtime = 'nodejs';
export async function POST(req: NextRequest) {
  try {
    requireOrigin(req);
    const { number, headers } = await authorizeInbox(req);
    let body;
    try {
      body = JSON.parse((await readBody(req, 20000)).toString('utf8'));
    } catch {
      throw new InboxError(400, 'Mensaje inválido.');
    }
    if (
      !body ||
      typeof body.conversationId !== 'string' ||
      typeof body.to !== 'string' ||
      typeof body.text !== 'string' ||
      !body.text.trim() ||
      body.text.length > 4096
    )
      throw new InboxError(400, 'Escribe un mensaje de hasta 4096 caracteres.');
    const message = await sendText(body.conversationId, number, body.to, body.text.trim());
    publish({
      event: 'whatsapp.message.sent',
      phoneNumberId: number,
      conversationId: body.conversationId,
      payload: { message },
    });
    return Response.json({ message }, { headers });
  } catch (error) {
    return errorResponse(error);
  }
}
