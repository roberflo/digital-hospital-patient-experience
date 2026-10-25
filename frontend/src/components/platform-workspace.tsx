'use client';
// Vista del dueño de plataforma (docs/platform-owner.md). Solo configura: elige una recepción y,
// en su nombre, revisa la conexión con Hospital y conecta WhatsApp. Sin bandeja, contactos ni
// ajustes. La elección vive en una cookie del servidor: aquí nunca se guarda ni viaja en la URL.
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import {
  AlertCircle,
  ArrowLeftRight,
  HeartPulse,
  LogOut,
  RefreshCw,
  ShieldCheck,
} from 'lucide-react';
import { toast } from 'sonner';
import { fetcher } from '@/lib/api';
import { ACTING_LOST, endSession, expectActing, type ActingLost } from '@/lib/session-client';
import { Alert, AlertDescription, AlertTitle } from './ui/alert';
import { Badge } from './ui/badge';
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from './ui/breadcrumb';
import { Button } from './ui/button';
import { Separator } from './ui/separator';
import { HospitalConnection } from './hospital-connection';
import { ThemeToggle } from './theme-toggle';
import { setActing, ReceptionOpener, ReceptionPicker } from './platform-receptions';
import { WhatsAppSection } from './platform-whatsapp';

type ActingFor = { tenantId: string; name: string };

export default function PlatformWorkspace({
  actingFor,
  name,
}: {
  actingFor: ActingFor | null;
  name: string;
}) {
  // Before any child fetches: every request of this view names the reception it painted.
  if (typeof window !== 'undefined') expectActing(actingFor?.tenantId ?? null);
  const [changing, setChanging] = useState(false);
  // The choice is a browser-wide cookie. Once the proxy says it is no longer the one painted
  // here, this tab stops operating: nothing below is rendered, so nothing can be sent.
  const [lost, setLost] = useState<ActingLost | null>(null);
  useEffect(() => {
    const onLost = (event: Event) => setLost((event as CustomEvent<ActingLost>).detail);
    window.addEventListener(ACTING_LOST, onLost);
    return () => window.removeEventListener(ACTING_LOST, onLost);
  }, []);
  // Same key as «Conexión con Hospital»: one read. The API answering 403/404 to it means the
  // reception no longer exists or the choice is no longer honoured — not «could not read».
  const probe = useSWR(actingFor && !lost ? '/hospital/connection' : null, fetcher);
  const state: ActingLost | null =
    lost ?? ([403, 404].includes(probe.error?.status) ? 'acting_expired' : null);
  // Once the choice is lost, nothing on screen may still claim to act for that reception.
  const shown = state ? null : actingFor;
  const change = async () => {
    setChanging(true);
    try {
      await setActing();
    } catch (e) {
      toast.error((e as Error).message);
      setChanging(false);
    }
  };
  return (
    <div className="flex min-h-svh flex-col">
      <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4 md:px-6">
        <a
          className="flex shrink-0 items-center gap-2 font-medium"
          href="/"
          aria-label="Recepción inicio"
        >
          <span className="flex size-6 items-center justify-center rounded-md bg-primary text-primary-foreground">
            <HeartPulse className="size-4" />
          </span>
          <span className="hidden sm:inline">Recepción</span>
        </a>
        <Separator orientation="vertical" className="mx-2 data-[orientation=vertical]:h-4" />
        <Breadcrumb className="min-w-0">
          <BreadcrumbList className="flex-nowrap">
            <BreadcrumbItem className="hidden md:block">Plataforma</BreadcrumbItem>
            <BreadcrumbSeparator className="hidden md:block" />
            <BreadcrumbItem className="min-w-0">
              <BreadcrumbPage className="truncate">
                {shown ? shown.name : 'Recepciones'}
              </BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
        <div className="ml-auto flex shrink-0 items-center gap-3">
          <Badge variant="outline" className="hidden lg:inline-flex">
            <ShieldCheck /> Sesión protegida
          </Badge>
          <ThemeToggle />
          <div className="hidden max-w-48 text-right text-sm leading-tight sm:grid">
            <span className="truncate font-medium">{name}</span>
            <span className="truncate text-xs text-muted-foreground">Dueño de plataforma</span>
          </div>
          <Button
            variant="ghost"
            size="icon"
            aria-label="Cerrar sesión"
            onClick={async () => {
              // Signing out also drops the chosen reception (origin-checked DELETE).
              await fetch('/api/platform/acting', { method: 'DELETE' }).catch(() => {});
              await endSession();
            }}
          >
            <LogOut />
          </Button>
        </div>
      </header>
      {shown && (
        <div
          className="sticky top-0 z-20 flex items-center gap-3 border-b bg-muted px-4 py-2 text-sm md:px-6"
          role="status"
        >
          <ArrowLeftRight className="size-4 shrink-0" aria-hidden="true" />
          <span className="min-w-0 flex-1 break-words">
            Actuando en nombre de <strong className="font-semibold">{shown.name}</strong>
          </span>
          <Button variant="outline" size="sm" disabled={changing} onClick={change}>
            Cambiar
          </Button>
        </div>
      )}
      <main className="mx-auto flex w-full max-w-5xl flex-1 flex-col gap-6 p-4 md:p-6">
        <div className="flex flex-col gap-1">
          <h1 className="text-2xl font-semibold tracking-tight">
            {actingFor ? 'Configurar recepción' : 'Recepciones'}
          </h1>
          <p className="text-muted-foreground">
            {actingFor
              ? 'Conexión con Hospital y WhatsApp. La atención, los contactos y los ajustes son del hospital.'
              : 'Elige la recepción que vas a configurar.'}
          </p>
        </div>
        {state === 'acting_changed' ? (
          <Alert>
            <ArrowLeftRight />
            <AlertTitle className="line-clamp-none">
              Cambió de recepción en otra pestaña.
            </AlertTitle>
            <AlertDescription className="gap-3">
              <p>
                Esta pestaña mostraba {actingFor?.name ?? 'otra recepción'} y ya no actúa en su
                nombre. No se envió nada. Recarga para ver la recepción actual.
              </p>
              <Button size="sm" onClick={() => window.location.assign('/')}>
                <RefreshCw /> Recargar
              </Button>
            </AlertDescription>
          </Alert>
        ) : state === 'acting_expired' ? (
          <Alert>
            <AlertCircle />
            <AlertTitle className="line-clamp-none">
              Tu elección caducó; elige la recepción de nuevo.
            </AlertTitle>
            <AlertDescription className="gap-3">
              <p>
                La elección dura doce horas, o la recepción ya no existe. No se envió nada en su
                nombre.
              </p>
              <Button size="sm" disabled={changing} onClick={change}>
                Elegir recepción
              </Button>
            </AlertDescription>
          </Alert>
        ) : actingFor ? (
          <>
            <HospitalConnection platform />
            <WhatsAppSection />
          </>
        ) : (
          <>
            <ReceptionPicker />
            <ReceptionOpener />
          </>
        )}
      </main>
    </div>
  );
}
