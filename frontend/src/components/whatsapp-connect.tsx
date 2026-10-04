'use client';
import Link from 'next/link';
import useSWR from 'swr';
import { ArrowLeft, MessageCircle, ShieldCheck } from 'lucide-react';
import { useWhatsAppConnect, WhatsAppLinkFlow, whatsappApi as api } from './whatsapp-link-flow';
type Channel = {
  id: string;
  name: string;
  phoneNumberId: string;
  coexistence: boolean;
  doctorId?: string;
};
export default function WhatsAppConnect() {
  const { data: me, error: authError } = useSWR<{ role: string; name: string }>('/me', api);
  const { data: channels, mutate } = useSWR<Channel[]>('/channels', api);
  const admin = !!me && me.role === 'admin';
  const flow = useWhatsAppConnect(admin, mutate);
  return (
    <main className="mx-auto max-w-4xl p-5 md:p-10">
      <Link href="/?view=inbox" className="inline-flex items-center gap-2 text-sm text-primary">
        <ArrowLeft size={16} /> Volver a Bandeja de entrada
      </Link>
      <div className="mt-8 flex items-center gap-4">
        <span className="rounded-2xl bg-emerald-50 p-4 text-primary">
          <MessageCircle size={28} />
        </span>
        <div>
          <h1 className="text-2xl font-semibold">Conecta el WhatsApp de tu hospital</h1>
          <p className="mt-2 text-sm text-slate-500">
            Administra los números conectados. La atención del equipo y el seguimiento se gestionan
            en Bandeja de entrada.
          </p>
        </div>
      </div>
      {authError && (
        <p role="alert" className="mt-6 text-red-700">
          No se pudo validar tu sesión. <Link href="/login">Inicia sesión</Link>.
        </p>
      )}
      {!me && !authError && <p className="mt-6">Comprobando acceso…</p>}
      {me && !admin && (
        <section className="mt-8 rounded-xl border bg-white p-6">
          <h2>Conexión administrada por tu hospital</h2>
          <p className="mt-2 text-sm text-slate-500">
            Un administrador del negocio debe conectar los números de recepción y de los doctores.
          </p>
        </section>
      )}
      {admin && (
        <section className="mt-8 rounded-2xl border bg-white p-6 md:p-8">
          <ol className="space-y-5 text-sm">
            <li>
              <strong>1. Inicia la conexión segura</strong>
              <p className="mt-1 text-slate-500">
                Genera el enlace de tu hospital. No necesitas claves API ni identificadores
                técnicos.
              </p>
            </li>
            <li>
              <strong>2. Vincula tu número en Kapso</strong>
              <p className="mt-1 text-slate-500">
                Inicia sesión con la cuenta de Meta que administra tu negocio y completa la
                verificación. Elige Coexistence si quieres seguir usando WhatsApp Business en tu
                teléfono.
              </p>
            </li>
            <li>
              <strong>3. Vuelve a Recepción</strong>
              <p className="mt-1 text-slate-500">
                Verificaremos el número y lo agregaremos a este hospital. Puedes conectar varios
                números repitiendo el proceso.
              </p>
            </li>
          </ol>
          <p className="mt-6 rounded-lg bg-slate-50 p-3 text-xs text-slate-500">
            Se conecta un número que ya tienes. Las tarifas de mensajes de Meta se administran en la
            cuenta del negocio.
          </p>
          <WhatsAppLinkFlow flow={flow} />
        </section>
      )}
      <section className="mt-6 rounded-2xl border bg-white p-6">
        <h2>Números de este hospital</h2>
        <div className="mt-4 space-y-3">
          {channels
            ?.filter((c) => c.phoneNumberId !== 'demo')
            .map((c) => (
              <div
                key={c.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl bg-slate-50 p-4"
              >
                <div>
                  <strong className="text-sm">{c.name}</strong>
                  <p className="mt-1 text-xs text-slate-500">
                    {c.coexistence ? 'Coexistence · teléfono y Recepción' : 'Conexión dedicada'} ·{' '}
                    {c.doctorId ? 'Doctor' : 'Atención general'}
                  </p>
                </div>
                <Link
                  href={`/inbox?phoneNumberId=${encodeURIComponent(c.phoneNumberId)}`}
                  className="text-sm text-primary"
                >
                  Consultar historial →
                </Link>
              </div>
            ))}
          {channels && !channels.some((c) => c.phoneNumberId !== 'demo') && (
            <p className="text-sm text-slate-500">Aún no tienes números conectados.</p>
          )}
        </div>
      </section>
      <p className="mt-5 flex items-center gap-2 text-xs text-slate-500">
        <ShieldCheck size={14} /> El número se verifica con Kapso y queda asociado únicamente a tu
        hospital. El alta no activa respuestas automáticas.
      </p>
    </main>
  );
}
