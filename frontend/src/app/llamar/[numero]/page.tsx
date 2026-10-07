import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { Phone } from 'lucide-react';
import { Button } from '@/components/ui/button';
// Where the agent's «Llamar al …» button lands: WhatsApp only accepts https in a button, so this
// public page opens the dialer. It reads no session and no data — the number is the whole request.
export const metadata: Metadata = { title: 'Llamar', robots: { index: false } };
export default async function Page({ params }: { params: Promise<{ numero: string }> }) {
  const numero = decodeURIComponent((await params).numero);
  if (!/^\+?\d{3,15}$/.test(numero)) notFound();
  return (
    <main className="login-shell">
      <section className="login-form">
        <meta httpEquiv="refresh" content={`0;url=tel:${numero}`} />
        <h1>Llamar al {numero}</h1>
        <p>Si la llamada no empieza sola, toca el botón.</p>
        <Button asChild>
          <a href={`tel:${numero}`}>
            <Phone /> Llamar al {numero}
          </a>
        </Button>
      </section>
    </main>
  );
}
