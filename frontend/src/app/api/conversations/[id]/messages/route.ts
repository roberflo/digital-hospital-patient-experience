import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse } from '@/lib/inbox-auth';
import { cursor, listMessages } from '@/lib/kapso';
export const runtime = 'nodejs';
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  // A refresh that already rotated the token must reach the browser even when the read fails.
  let headers: Headers | undefined;
  try {
    const auth = await authorizeInbox(req);
    const number = auth.number;
    headers = auth.headers;
    const { id } = await params;
    return Response.json(
      await listMessages(id, number, cursor(req.nextUrl.searchParams.get('cursor'))),
      { headers },
    );
  } catch (error) {
    return errorResponse(error, headers);
  }
}
