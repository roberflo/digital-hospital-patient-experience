import { NextRequest } from 'next/server';
import { authorizeInbox, errorResponse } from '@/lib/inbox-auth';
import { subscribe } from '@/lib/events';
export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';
export async function GET(req: NextRequest) {
  try {
    const { number, headers } = await authorizeInbox(req);
    const encoder = new TextEncoder();
    let stop = () => {};
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        let closed = false;
        const write = (value: string) => {
          if (!closed) controller.enqueue(encoder.encode(value));
        };
        const unsubscribe = subscribe(number, (event) =>
          write(`data: ${JSON.stringify(event)}\n\n`),
        );
        const ping = setInterval(() => write(': ping\n\n'), 25000);
        // Reconnect authenticates membership and revocation again at least once per minute.
        const expiry = setTimeout(() => stop(), 55000);
        stop = () => {
          if (closed) return;
          closed = true;
          clearInterval(ping);
          clearTimeout(expiry);
          unsubscribe();
          req.signal.removeEventListener('abort', stop);
          try {
            controller.close();
          } catch {
            /* Already canceled by the consumer. */
          }
        };
        req.signal.addEventListener('abort', stop, { once: true });
        if (req.signal.aborted) stop();
        else write('retry: 3000\n\n');
      },
      cancel() {
        stop();
      },
    });
    headers.set('Content-Type', 'text/event-stream');
    headers.set('Cache-Control', 'no-cache, no-store, no-transform');
    headers.set('X-Accel-Buffering', 'no');
    return new Response(stream, { headers });
  } catch (error) {
    return errorResponse(error);
  }
}
