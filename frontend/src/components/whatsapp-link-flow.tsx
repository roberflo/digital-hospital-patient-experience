'use client';
// The number-linking flow shared by /whatsapp and the administrator's «Primeros pasos»:
// hosted Kapso link → automatic verification on return → sync result with its warnings.
import { sessionFetch } from '@/lib/session-client';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ExternalLink, MessageCircle, RefreshCw } from 'lucide-react';
import { Button } from '@/components/ui/button';
import styles from './whatsapp-connect.module.css';

export type Sync = { connected: number; added: number; webhooksReady: number; warnings: string[] };
export async function whatsappApi<T>(path: string, method = 'GET'): Promise<T> {
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

export function useWhatsAppConnect(admin: boolean, onSynced: () => unknown) {
  const [link, setLink] = useState<{ url: string; expiresAt?: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [checking, setChecking] = useState(false);
  const pending = useRef(false);
  const [result, setResult] = useState<Sync | null>(null);
  const [error, setError] = useState('');
  const [waiting, setWaiting] = useState(false);
  const [attempts, setAttempts] = useState(0);
  // A ref keeps `verify` stable whatever callback identity the caller passes.
  const synced = useRef(onSynced);
  useEffect(() => {
    synced.current = onSynced;
  });
  const verify = useCallback(async () => {
    if (!admin || pending.current) return;
    pending.current = true;
    setChecking(true);
    setError('');
    try {
      const data = await whatsappApi<Sync>('/channels/sync', 'POST');
      setResult(data);
      await synced.current();
      if (data.added > 0 && data.webhooksReady === data.connected) setWaiting(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      pending.current = false;
      setChecking(false);
      setAttempts((n) => n + 1);
    }
  }, [admin]);
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
      const data = await whatsappApi<{ url: string; expiresAt?: string }>(
        '/channels/onboarding',
        'POST',
      );
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
  const opened = () => {
    setWaiting(true);
    setAttempts(0);
  };
  return { link, busy, checking, result, error, waiting, attempts, start, verify, opened };
}

export function WhatsAppLinkFlow({ flow }: { flow: ReturnType<typeof useWhatsAppConnect> }) {
  const { link, busy, checking, result, error, waiting, attempts, start, verify, opened } = flow;
  return (
    <>
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
            onClick={opened}
            className={styles.launch}
          >
            Continuar conexión en Kapso <ExternalLink size={16} />
          </a>
          {link.expiresAt && (
            <p className="mt-3 text-xs text-slate-500">
              Válido hasta {new Date(link.expiresAt).toLocaleDateString('es')}. Generar otro enlace
              reemplaza el anterior.
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
    </>
  );
}
