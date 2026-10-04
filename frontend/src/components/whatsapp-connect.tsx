'use client';
import { sessionFetch } from '@/lib/session-client';
import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import useSWR from 'swr';
import { ArrowLeft, ExternalLink, MessageCircle, RefreshCw, ShieldCheck } from 'lucide-react';
import { Button } from '@/components/ui/button';
import styles from './whatsapp-connect.module.css';
type Channel = {
  id: string;
  name: string;
  phoneNumberId: string;
  coexistence: boolean;
  doctorId?: string;
};
type Sync = { connected: number; added: number; webhooksReady: number; warnings: string[] };
async function api<T>(path: string, method = 'GET'): Promise<T> {
  const response = await sessionFetch('/api/crm' + path, {
    method,
    cache: 'no-store',
    ...(method === 'POST' ? { headers: { 'Content-Type': 'application/json' }, body: '{}' } : {}),
  });
  const value = await response.json();
  if (!response.ok)
    throw new Error(value.title || 'No se pudo completar la conexión. Inténtalo de nuevo.');
  return value;
}
export default function WhatsAppConnect() {
  const { data: me, error: authError } = useSWR<{ role: string; name: string }>('/me', api);
  const { data: channels, mutate } = useSWR<Channel[]>('/channels', api);
  const admin = !!me && me.role === 'admin';
  const [link, setLink] = useState<{ url: string; expiresAt?: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [checking, setChecking] = useState(false);
  const pending = useRef(false);
  const [result, setResult] = useState<Sync | null>(null);
  const [error, setError] = useState('');
  const [waiting, setWaiting] = useState(false);
  const [attempts, setAttempts] = useState(0);
  const verify = useCallback(async () => {
    if (!admin || pending.current) return;
    pending.current = true;
    setChecking(true);
    setError('');
    try {
      const data = await api<Sync>('/channels/sync', 'POST');
      setResult(data);
      await mutate();
      if (data.added > 0 && data.webhooksReady === data.connected) setWaiting(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      pending.current = false;
      setChecking(false);
      setAttempts((n) => n + 1);
    }
  }, [admin, mutate]);
  useEffect(() => {
    if (!admin) return;
    // Return values are only UX hints. Sync verifies provider ownership, ignoring all IDs in the URL.
    const returned = new URLSearchParams(window.location.search).get('connection');
    if (returned === 'returned') {
      setWaiting(true);
      void verify();
    }
    if (returned === 'failed') setError('La conexión no se completó. Puedes volver a intentarlo.');
  }, [admin, verify]);
  useEffect(() => {
    if (!waiting || attempts >= 20) return;
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') void verify();
    }, 15000);
    const focus = () => {
      void verify();
    };
    window.addEventListener('focus', focus);
    return () => {
      clearInterval(timer);
      window.removeEventListener('focus', focus);
    };
  }, [waiting, attempts, verify]);
  async function start() {
    setBusy(true);
    setError('');
    try {
      const data = await api<{ url: string; expiresAt?: string }>('/channels/onboarding', 'POST');
      const url = new URL(data.url);
      if (
        url.protocol !== 'https:' ||
        !['setup.kapso.ai', 'app.kapso.ai'].includes(url.hostname) ||
        url.username ||
        url.password
      )
        throw new Error('El enlace de conexión no es válido.');
      setLink(data);
      setResult(null);
      setAttempts(0);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
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
          <div className="mt-6 flex flex-wrap gap-3">
            <Button disabled={busy} onClick={() => void start()}>
              <MessageCircle size={17} />
              {busy ? 'Preparando enlace…' : link ? 'Generar otro enlace' : 'Agregar mi número'}
            </Button>
            <Button variant="outline" disabled={checking} onClick={() => void verify()}>
              <RefreshCw size={16} />
              {checking ? 'Verificando…' : 'Verificar conexión'}
            </Button>
          </div>
          {link && (
            <div className="mt-5 rounded-xl border border-emerald-200 bg-emerald-50 p-5">
              <h2>Tu enlace está listo</h2>
              <p className="my-2 text-sm">
                Continúa en Kapso y regresa a esta pestaña cuando termines.
              </p>
              <a
                href={link.url}
                target="_blank"
                rel="noopener noreferrer"
                onClick={() => {
                  setWaiting(true);
                  setAttempts(0);
                }}
                className={styles.launch}
              >
                Continuar conexión en Kapso <ExternalLink size={16} />
              </a>
              {link.expiresAt && (
                <p className="mt-3 text-xs text-slate-500">
                  Válido hasta {new Date(link.expiresAt).toLocaleDateString('es')}. Generar otro
                  enlace reemplaza el anterior.
                </p>
              )}
            </div>
          )}
          {waiting && (
            <p className="mt-4 text-sm text-slate-500" role="status">
              {attempts < 20
                ? 'Esperando la conexión. Al volver, verificaremos tus números automáticamente.'
                : 'Puedes seguir en Kapso. Cuando termines, pulsa Verificar conexión.'}
            </p>
          )}
          {result && (
            <div role="status" className="mt-4 rounded-lg bg-slate-50 p-4 text-sm">
              <p>
                {result.added > 0
                  ? `${result.added} número(s) agregado(s) a tu hospital.`
                  : result.connected > 0
                    ? 'Tus números conectados están actualizados.'
                    : 'Todavía no encontramos un número conectado. Completa los pasos en Kapso y vuelve a verificar.'}
              </p>
              {result.warnings.map((w) => (
                <p key={w} className="mt-2 text-amber-800">
                  {w}
                </p>
              ))}
            </div>
          )}
          {error && (
            <p role="alert" className="mt-4 text-sm text-red-700">
              {error}
            </p>
          )}
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
