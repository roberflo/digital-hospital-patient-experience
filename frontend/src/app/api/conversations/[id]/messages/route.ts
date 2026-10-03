import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse } from '@/lib/inbox-auth';
import { cursor, listMessages } from '@/lib/kapso';
export const runtime = 'nodejs';
export async function GET(req: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  try {
    const { number, headers } = await authorizeInbox(req);
    const { id } = await params;
    return Response.json(
      await listMessages(id, number, cursor(req.nextUrl.searchParams.get('cursor'))),
      { headers },
    );
  } catch (error) {
    return errorResponse(error);
  }
}
