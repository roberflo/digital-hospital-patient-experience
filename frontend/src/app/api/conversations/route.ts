import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse } from '@/lib/inbox-auth';
import { cursor, listConversations } from '@/lib/kapso';
export const runtime = 'nodejs';
export async function GET(req: NextRequest) {
  // A refresh that already rotated the token must reach the browser even when the read fails.
  let headers: Headers | undefined;
  try {
    const auth = await authorizeInbox(req);
    const number = auth.number;
    headers = auth.headers;
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
    return errorResponse(error, headers);
  }
}
