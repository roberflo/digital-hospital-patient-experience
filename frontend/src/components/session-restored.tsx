'use client';
import { useEffect, useState } from 'react';
import { CheckCircle2, HeartPulse } from 'lucide-react';
import { platformSubject, SESSION_CHANNEL } from '@/lib/session-client';
import { loginUrl } from '@/lib/login-return';
import { Button } from './ui/button';
export default function SessionRestored() {
  const [state, setState] = useState<'checking' | 'ready' | 'failed'>('checking');
  useEffect(() => {
    let alive = true;
    fetch('/api/crm/me', { cache: 'no-store', signal: AbortSignal.timeout(8000) })
      .then(async (response) => {
        // The platform owner is denied /me by design: its live session is the proof.
        if (response.status === 403 ? !(await platformSubject()) : !response.ok) throw new Error();
        if (response.ok) {
          const me = await response.json();
          if (!me.subject || !me.tenant?.id) throw new Error();
        }
        if (!alive) return;
        setState('ready');
        if (typeof BroadcastChannel !== 'undefined') {
          const channel = new BroadcastChannel(SESSION_CHANNEL);
          channel.postMessage('session-ready');
          channel.close();
        }
      })
      .catch(() => {
        if (alive) setState('failed');
      });
    return () => {
      alive = false;
    };
  }, []);
  return (
    <main className="session-restored">
      <div>
        {state === 'ready' ? <CheckCircle2 size={40} /> : <HeartPulse size={40} />}
        <h1>
          {state === 'ready'
            ? 'Ya puedes continuar'
            : state === 'failed'
              ? 'No pudimos recuperar el acceso'
              : 'Comprobando tu sesión…'}
        </h1>
        <p>
          {state === 'ready'
            ? 'Tu sesión está activa. Vuelve a la pestaña donde estabas trabajando; tus borradores siguen allí.'
            : state === 'failed'
              ? 'Comprueba tu conexión e inicia sesión con tu cuenta del hospital.'
              : 'Un momento, estamos validando tu acceso al hospital.'}
        </p>
        {state === 'ready' && (
          <Button onClick={() => window.close()}>Volver a mi pestaña de trabajo</Button>
        )}
        {state === 'failed' && (
          <a href={loginUrl('/session-restored')}>Intentar iniciar sesión de nuevo</a>
        )}
        <small>Puedes cerrar esta pestaña y volver manualmente si tu navegador no la cierra.</small>
      </div>
    </main>
  );
}
