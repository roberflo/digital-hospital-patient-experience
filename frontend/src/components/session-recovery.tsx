'use client';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { usePathname } from 'next/navigation';
import { SWRConfig, useSWRConfig } from 'swr';
import { LockKeyhole, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from './ui/dialog';
import { Button } from './ui/button';
import { loginUrl } from '@/lib/login-return';
import {
  expireSession,
  platformSubject,
  isSessionPaused,
  resumeSession,
  SESSION_CHANNEL,
  SESSION_EXPIRED,
  SESSION_IDENTITY,
} from '@/lib/session-client';

type Identity = { subject: string; tenant: { id: string } };
function identityKey(value: Identity | undefined) {
  return value?.subject && value?.tenant?.id ? value.tenant.id + ':' + value.subject : null;
}
function Recovery({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  const publicPage =
    pathname === '/login' || pathname === '/session-restored' || pathname.startsWith('/llamar/');
  const { mutate } = useSWRConfig();
  const [expired, setExpired] = useState(false);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState('');
  const identity = useRef<string | null>(null);
  const loginLink = useRef<HTMLAnchorElement>(null);
  const probe = useRef<() => Promise<void>>(async () => {});
  useEffect(() => {
    if (publicPage) return;
    let alive = true;
    let pending = false;
    const onExpired = () => {
      setExpired(true);
      setError('');
    };
    const onIdentity = (event: Event) => {
      const next = identityKey((event as CustomEvent<Identity>).detail);
      if (next && identity.current && next !== identity.current) {
        window.location.replace('/');
        return;
      }
      if (next) identity.current = next;
    };
    async function check() {
      if (pending || !alive) return;
      pending = true;
      setChecking(true);
      try {
        // Intentionally bypass the paused fetcher: this only validates identity, never replays work.
        const response = await fetch('/api/crm/me', {
          cache: 'no-store',
          signal: AbortSignal.timeout(8000),
        });
        if (!alive) return;
        if (response.status === 401) {
          expireSession();
          return;
        }
        // The platform owner is denied /me by design: its identity is its own `sub`. From here it
        // follows the same rules as anyone — same identity resumes in place (a link generated in
        // memory survives), a different one gets a clean page.
        const platform = response.status === 403 ? await platformSubject() : null;
        if (!alive) return;
        if (!response.ok && !platform) {
          const refusal = await response.json().catch(() => null);
          if (isSessionPaused())
            setError(
              refusal?.code === 'hospital_not_onboarded'
                ? refusal.title
                : response.status === 403
                  ? 'Esta cuenta no tiene acceso al hospital. Entra con tu cuenta anterior o consulta al administrador.'
                  : 'No pudimos comprobar la sesión. Revisa tu conexión e inténtalo de nuevo.',
            );
          return;
        }
        const next = platform ? 'platform:' + platform : identityKey(await response.json());
        if (!next) throw new Error('Identity missing');
        if (isSessionPaused() && !identity.current) {
          window.location.reload();
          return;
        }
        if (identity.current && next !== identity.current) {
          window.location.replace('/');
          return;
        }
        identity.current = next;
        if (isSessionPaused()) {
          resumeSession();
          setExpired(false);
          setError('');
          // SWR revalidates reads only. Failed sends and edits must be submitted by the user.
          await mutate(() => true);
          toast.success('Sesión recuperada. Puedes continuar.');
        }
      } catch {
        if (alive && isSessionPaused())
          setError('No pudimos conectar. Tu trabajo sigue abierto; vuelve a comprobar la sesión.');
      } finally {
        pending = false;
        if (alive) setChecking(false);
      }
    }
    probe.current = check;
    window.addEventListener(SESSION_EXPIRED, onExpired);
    window.addEventListener(SESSION_IDENTITY, onIdentity);
    const onFocus = () => {
      if (document.visibilityState === 'visible') void check();
    };
    window.addEventListener('focus', onFocus);
    window.addEventListener('online', onFocus);
    document.addEventListener('visibilitychange', onFocus);
    const channel =
      typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel(SESSION_CHANNEL) : null;
    if (channel)
      channel.onmessage = () => {
        expireSession();
        void check();
      };
    const interval = setInterval(onFocus, 60000);
    if (isSessionPaused()) onExpired();
    void check();
    return () => {
      alive = false;
      clearInterval(interval);
      channel?.close();
      window.removeEventListener(SESSION_EXPIRED, onExpired);
      window.removeEventListener(SESSION_IDENTITY, onIdentity);
      window.removeEventListener('focus', onFocus);
      window.removeEventListener('online', onFocus);
      document.removeEventListener('visibilitychange', onFocus);
    };
  }, [publicPage, mutate]);
  return (
    <>
      <div inert={expired && !publicPage} aria-hidden={expired && !publicPage ? true : undefined}>
        {children}
      </div>
      <Dialog open={expired && !publicPage}>
        <DialogContent
          showClose={false}
          onOpenAutoFocus={(event) => {
            event.preventDefault();
            loginLink.current?.focus();
          }}
          className="session-recovery"
          onEscapeKeyDown={(e) => e.preventDefault()}
          onPointerDownOutside={(e) => e.preventDefault()}
          onInteractOutside={(e) => e.preventDefault()}
        >
          <div className="session-recovery-icon">
            <LockKeyhole size={26} />
          </div>
          <DialogTitle className="text-xl font-semibold">Tu sesión terminó</DialogTitle>
          <DialogDescription className="mt-3 text-sm leading-6 text-slate-600">
            Vuelve a iniciar sesión para continuar. Esta pantalla y tus borradores siguen abiertos
            en esta pestaña.
          </DialogDescription>
          <a
            ref={loginLink}
            className="session-recovery-login"
            href={loginUrl('/session-restored')}
            target="_blank"
            rel="noopener noreferrer"
          >
            Iniciar sesión y continuar
          </a>
          <p className="text-xs leading-5 text-slate-500">
            El acceso se abre en otra pestaña. Al terminar, vuelve aquí: recuperaremos tu sesión sin
            recargar tu trabajo. No cierres esta pestaña.
          </p>
          <Button
            variant="outline"
            className="mt-4 w-full"
            disabled={checking}
            onClick={() => {
              setError('');
              void probe.current();
            }}
          >
            <RefreshCw className={checking ? 'animate-spin' : ''} />
            {checking ? 'Comprobando…' : 'Ya inicié sesión · continuar'}
          </Button>
          {error && (
            <p className="mt-3 text-sm text-red-700" role="alert">
              {error}
            </p>
          )}
          <p className="mt-4 text-xs text-slate-500">
            Los mensajes y cambios que no se completaron no se reenviarán automáticamente.
          </p>
        </DialogContent>
      </Dialog>
    </>
  );
}
export default function SessionRecovery({ children }: { children: ReactNode }) {
  return (
    <SWRConfig
      value={{ isPaused: isSessionPaused, shouldRetryOnError: (error) => error?.status !== 401 }}
    >
      <Recovery>{children}</Recovery>
    </SWRConfig>
  );
}
