import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse } from '@/lib/inbox-auth';
import { cursor, listConversations } from '@/lib/kapso';
export const runtime = 'nodejs';
export async function GET(req: NextRequest) {
  try {
    const { number, headers } = await authorizeInbox(req);
    const result = await listConversations(number, cursor(req.nextUrl.searchParams.get('after')));
    return Response.json(
      // Same rule as the API's KapsoClient.CanSend(manual: true); the API still decides each send.
      {
        ...result,
        manualSendEnabled:
          process.env.SEND_ENABLED === 'true' || process.env.KAPSO_MANUAL_SEND_ENABLED === 'true',
      },
      { headers },
    );
  } catch (error) {
    return errorResponse(error);
  }
}
