'use client';
import { useEffect, useState } from 'react';
import { CheckCircle2, HeartPulse } from 'lucide-react';
import { platformSubject, SESSION_CHANNEL } from '@/lib/session-client';
import { loginUrl } from '@/lib/login-return';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { FieldDescription } from './ui/field';
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
    <main className="flex min-h-svh flex-col items-center justify-center bg-muted p-6 md:p-10">
      <Card className="w-full max-w-sm">
        <CardHeader className="justify-items-center text-center">
          <div className="flex size-10 items-center justify-center rounded-md bg-primary text-primary-foreground">
            {state === 'ready' ? (
              <CheckCircle2 className="size-5" />
            ) : (
              <HeartPulse className="size-5" />
            )}
          </div>
          <CardTitle className="text-xl">
            <h1>
              {state === 'ready'
                ? 'Ya puedes continuar'
                : state === 'failed'
                  ? 'No pudimos recuperar el acceso'
                  : 'Comprobando tu sesión…'}
            </h1>
          </CardTitle>
          <CardDescription>
            {state === 'ready'
              ? 'Tu sesión está activa. Vuelve a la pestaña donde estabas trabajando; tus borradores siguen allí.'
              : state === 'failed'
                ? 'Comprueba tu conexión e inicia sesión con tu cuenta del hospital.'
                : 'Un momento, estamos validando tu acceso al hospital.'}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {state === 'ready' && (
            <Button onClick={() => window.close()}>Volver a mi pestaña de trabajo</Button>
          )}
          {state === 'failed' && (
            <Button asChild>
              <a href={loginUrl('/session-restored')}>Intentar iniciar sesión de nuevo</a>
            </Button>
          )}
          <FieldDescription className="text-center">
            Puedes cerrar esta pestaña y volver manualmente si tu navegador no la cierra.
          </FieldDescription>
        </CardContent>
      </Card>
    </main>
  );
}
