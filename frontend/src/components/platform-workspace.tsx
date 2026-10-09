'use client';
// Vista del dueño de plataforma (docs/platform-owner.md). Solo configura: elige una recepción y,
// en su nombre, revisa la conexión con Hospital y conecta WhatsApp. Sin bandeja, contactos ni
// ajustes. La elección vive en una cookie del servidor: aquí nunca se guarda ni viaja en la URL.
import { Fragment, useEffect, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import {
  AlertCircle,
  ArrowLeftRight,
  Building2,
  Copy,
  HeartPulse,
  LogOut,
  MessageCircle,
  Pause,
  Plus,
  Play,
  RefreshCw,
  Search,
  ShieldCheck,
  Wrench,
} from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Channel } from '@/lib/api';
import { whatsappStep, type Installation } from '@/lib/first-steps';
import {
  ACTING_LOST,
  endSession,
  expectActing,
  sessionFetch,
  type ActingLost,
} from '@/lib/session-client';
import { cn } from '@/lib/utils';
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
import { Card, CardContent, CardHeader, CardTitle } from './ui/card';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from './ui/empty';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemGroup,
  ItemMedia,
  ItemSeparator,
  ItemTitle,
} from './ui/item';
import { Separator } from './ui/separator';
import { Spinner } from './ui/spinner';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';
import { HospitalConnection } from './hospital-connection';
import { ChannelConnection } from './conversation-workspace';
import { useWhatsAppConnect, WhatsAppLinkFlow } from './whatsapp-link-flow';
import { ThemeToggle } from './theme-toggle';

type ActingFor = { tenantId: string; name: string };
type Tenant = { id: string; name: string; hospitalConfigured: boolean; whatsAppConnected: boolean };
type Hospital = { id: string; name: string; hasReception: boolean };

// The choice is sealed by the server. A full load afterwards drops every cached read and any
// half-finished connection link, so nothing from one reception can be shown under another.
async function setActing(tenantId?: string) {
  const response = await sessionFetch('/api/platform/acting', {
    method: tenantId ? 'POST' : 'DELETE',
    ...(tenantId
      ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tenantId }) }
      : {}),
  });
  if (!response.ok) {
    const refusal = await response.json().catch(() => null);
    throw new Error(refusal?.title ?? 'No se pudo completar la operación.');
  }
  window.location.assign('/');
}
const plain = (text: string) =>
  text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase();
// ponytail: filtrado en cliente con tope de filas pintadas; paginar en la API si la lista pasa de miles.
const SHOWN = 100;

function ReceptionPicker() {
  const { data, error, mutate, isValidating } = useSWR<{ tenants: Tenant[] }>(
    '/platform/tenants',
    fetcher,
  );
  const [query, setQuery] = useState('');
  const [choosing, setChoosing] = useState<string | null>(null);
  const [failure, setFailure] = useState('');
  // SWR keeps the last list when a revalidation fails: the failure wins, a stale list is not shown.
  const unreadable = !!error || (!!data && !Array.isArray(data.tenants));
  const tenants = unreadable ? undefined : data?.tenants;
  const matches = tenants?.filter((t) => plain(t.name).includes(plain(query.trim()))) ?? [];
  return (
    <Card role="region" aria-labelledby="receptions-title">
      <CardHeader>
        <CardTitle>
          <h2 id="receptions-title" className="flex items-center gap-2">
            <Building2 className="size-4" /> Recepciones
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {tenants && tenants.length > 0 && (
          <InputGroup className="sm:max-w-xs">
            <InputGroupInput
              aria-label="Buscar recepción por nombre"
              placeholder="Buscar por nombre…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              autoComplete="off"
            />
            <InputGroupAddon>
              <Search />
            </InputGroupAddon>
          </InputGroup>
        )}
        {unreadable ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>
                No pudimos leer la lista de recepciones. La lectura falló: no significa que no haya
                ninguna.
              </p>
              <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !tenants ? (
          <div className="flex items-center justify-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner /> Leyendo las recepciones…
          </div>
        ) : tenants.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Building2 />
              </EmptyMedia>
              <EmptyTitle>Aún no hay recepciones</EmptyTitle>
              <EmptyDescription>
                Una recepción aparece aquí cuando la abres para un hospital, más abajo, o cuando su
                Administrador entra por primera vez.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <>
            <p className="text-sm text-muted-foreground" role="status">
              {matches.length === tenants.length
                ? `${tenants.length} recepciones`
                : `${matches.length} de ${tenants.length} recepciones`}
              {matches.length > SHOWN && ` · se muestran ${SHOWN}; escribe el nombre para afinar`}
            </p>
            {failure && (
              <Alert variant="destructive">
                <AlertCircle />
                <AlertDescription>{failure}</AlertDescription>
              </Alert>
            )}
            {matches.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Ninguna recepción coincide con «{query.trim()}».
              </p>
            ) : (
              <ItemGroup className="max-h-144 overflow-y-auto">
                {matches.slice(0, SHOWN).map((t, index) => (
                  <Fragment key={t.id}>
                    {index > 0 && <ItemSeparator />}
                    <Item size="sm">
                      <ItemMedia variant="icon">
                        <Building2 />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle>{t.name}</ItemTitle>
                        <div className="flex flex-wrap gap-2">
                          <Badge variant={t.hospitalConfigured ? 'secondary' : 'outline'}>
                            <HeartPulse />
                            {t.hospitalConfigured
                              ? 'Hospital configurado'
                              : 'Hospital sin configurar'}
                          </Badge>
                          <Badge variant={t.whatsAppConnected ? 'secondary' : 'outline'}>
                            <MessageCircle />
                            {t.whatsAppConnected ? 'WhatsApp conectado' : 'Sin número de WhatsApp'}
                          </Badge>
                        </div>
                      </ItemContent>
                      <ItemActions>
                        <Button
                          variant="outline"
                          size="sm"
                          disabled={!!choosing}
                          aria-label={`Elegir: ${t.name}`}
                          onClick={async () => {
                            setChoosing(t.id);
                            setFailure('');
                            try {
                              await setActing(t.id);
                            } catch (e) {
                              setFailure((e as Error).message);
                              setChoosing(null);
                            }
                          }}
                        >
                          {choosing === t.id && <Spinner />}
                          Elegir
                        </Button>
                      </ItemActions>
                    </Item>
                  </Fragment>
                ))}
              </ItemGroup>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}

// REC-3: abrir la recepción de un hospital que aún no la tiene. Al abrirla queda elegida.
// Lo que la API contó de un hospital al intentar abrirlo; el texto libre es un fallo sin código.
type OpenNote = 'pending' | 'already' | 'unknown' | (string & {});
const openNotes: Record<string, string> = {
  pending:
    'La cuenta de servicio de este hospital se está creando; vuelve a intentarlo en un minuto.',
  already: 'Esta recepción ya está abierta.',
  unknown: 'Hospital no reconocido.',
};
function ReceptionOpener() {
  const { mutate: refresh } = useSWRConfig();
  const [open, setOpen] = useState(false);
  // Lazy: Hospital is only asked once this section is opened, and not again on every focus.
  const { data, error, mutate, isValidating } = useSWR<{ hospitals: Hospital[] }>(
    open ? '/platform/hospitals' : null,
    fetcher,
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, OpenNote>>({});
  const unreadable = !!error || (!!data && !Array.isArray(data.hospitals));
  // A hospital found already open stays listed so it can be chosen from its own row.
  const pending = unreadable
    ? undefined
    : data?.hospitals.filter((h) => !h.hasReception || notes[h.id] === 'already');
  const matches = pending?.filter((h) => plain(h.name).includes(plain(query.trim()))) ?? [];
  // Homonyms (dev data has hundreds of «Hospital sin nombre») are told apart by the start of
  // their id, so the owner opens the one they mean.
  const repeated = new Set(
    (pending ?? []).map((h) => h.name).filter((name, i, all) => all.indexOf(name) !== i),
  );
  const note = (id: string, value: OpenNote = '') => setNotes((all) => ({ ...all, [id]: value }));
  async function act(hospital: Hospital) {
    setBusy(hospital.id);
    try {
      // A reception carries its hospital's id; a fresh one is chosen by the id the API returned.
      let reception = hospital.id;
      if (notes[hospital.id] !== 'already') {
        note(hospital.id);
        let failure: OpenNote = '';
        try {
          reception = (
            await api<{ id: string }>('/platform/tenants', 'POST', { hospitalId: hospital.id })
          ).id;
        } catch (e) {
          const { status, code, message } = e as { status?: number; code?: string } & Error;
          failure =
            code === 'already_open'
              ? 'already'
              : code === 'service_account_pending'
                ? 'pending'
                : status === 404
                  ? 'unknown'
                  : message || 'No se pudo abrir la recepción.';
        }
        // Open now, or someone else opened it: it belongs in the list above either way, and if
        // choosing it fails below it can still be chosen from this row.
        if (!failure || failure === 'already') {
          note(hospital.id, 'already');
          void refresh('/platform/tenants');
        } else note(hospital.id, failure);
        // Found already open: offered, not chosen on the owner's behalf.
        if (failure) return;
      }
      // Leaves the page on success.
      await setActing(reception);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return (
    <Card role="region" aria-labelledby="open-reception-title">
      <CardHeader>
        <CardTitle>
          <h2 id="open-reception-title" className="flex items-center gap-2">
            <Plus className="size-4" /> Abrir la recepción de un hospital
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          {open && pending && pending.length > 0 && (
            <InputGroup className="sm:max-w-xs">
              <InputGroupInput
                aria-label="Buscar hospital por nombre"
                placeholder="Buscar por nombre…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                autoComplete="off"
              />
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
            </InputGroup>
          )}
          <Button
            variant="outline"
            size="sm"
            aria-expanded={open}
            onClick={() => setOpen((value) => !value)}
          >
            {open ? 'Ocultar' : 'Ver hospitales sin recepción'}
          </Button>
        </div>
        {!open ? (
          <p className="text-sm text-muted-foreground">
            Para un hospital que ya existe en Hospital y todavía no tiene Recepción. Al abrirla
            queda elegida para configurarla.
          </p>
        ) : unreadable ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>
                No pudimos leer los hospitales desde Hospital. La lectura falló: no significa que
                todos tengan ya recepción.
              </p>
              <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !pending ? (
          <div className="flex items-center justify-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner /> Leyendo los hospitales…
          </div>
        ) : pending.length === 0 ? (
          <p className="text-sm text-muted-foreground">Todos los hospitales ya tienen recepción.</p>
        ) : matches.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Ningún hospital coincide con «{query.trim()}».
          </p>
        ) : (
          <>
            <p className="text-sm text-muted-foreground" role="status">
              {matches.length === pending.length
                ? `${pending.length} sin recepción`
                : `${matches.length} de ${pending.length} sin recepción`}
              {matches.length > SHOWN && ` · se muestran ${SHOWN}; escribe el nombre para afinar`}
            </p>
            <ItemGroup className="max-h-144 overflow-y-auto">
              {matches.slice(0, SHOWN).map((h, index) => {
                const said = notes[h.id] || undefined;
                const label = repeated.has(h.name) ? `${h.name} (${h.id.slice(0, 8)})` : h.name;
                return (
                  <Fragment key={h.id}>
                    {index > 0 && <ItemSeparator />}
                    <Item size="sm">
                      <ItemMedia variant="icon">
                        <Building2 />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle>
                          {h.name}
                          {repeated.has(h.name) && (
                            <span className="font-normal text-muted-foreground">
                              · {h.id.slice(0, 8)}
                            </span>
                          )}
                        </ItemTitle>
                        {said && (
                          <p
                            role={said === 'already' ? 'status' : 'alert'}
                            className={cn(
                              'text-sm',
                              said === 'already' ? 'text-muted-foreground' : 'text-destructive',
                            )}
                          >
                            {openNotes[said] ?? said}
                          </p>
                        )}
                      </ItemContent>
                      {said !== 'unknown' && (
                        <ItemActions>
                          <Button
                            variant={said ? 'outline' : 'default'}
                            size="sm"
                            disabled={!!busy}
                            aria-label={`${said === 'already' ? 'Elegir' : said ? 'Reintentar' : 'Abrir'}: ${label}`}
                            onClick={() => act(h)}
                          >
                            {busy === h.id ? (
                              <Spinner />
                            ) : said && said !== 'already' ? (
                              <RefreshCw />
                            ) : null}
                            {said === 'already' ? 'Elegir' : said ? 'Reintentar' : 'Abrir'}
                          </Button>
                        </ItemActions>
                      )}
                    </Item>
                  </Fragment>
                );
              })}
            </ItemGroup>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function WhatsAppSection() {
  const installation = useSWR<Installation>('/platform/installation', fetcher);
  const channels = useSWR<Channel[]>('/channels', fetcher);
  // Same key as «Conexión con Hospital»: one read.
  const connection = useSWR<{ hospital?: { agentEnabled?: boolean } }>(
    '/hospital/connection',
    fetcher,
  );
  const flow = useWhatsAppConnect(true, () => channels.mutate());
  // With the agent on, no number may be enabled from here: «Habilitar» is not offered at all.
  // Reminders only block the one number they go out through, which the API does not name, so
  // that is learned per number from its 409.
  const [agentRefused, setAgentRefused] = useState(false);
  const [reminderNumbers, setReminderNumbers] = useState<string[]>([]);
  const agentActive = agentRefused || connection.data?.hospital?.agentEnabled === true;
  // Reuses the administrator guide's rule for what the installation lacks, by setting name.
  const install = whatsappStep(installation, { data: [] }, {});
  const rows = channels.error
    ? undefined
    : channels.data?.filter((c) => !c.phoneNumberId.startsWith('demo'));
  async function toggle(channel: Channel) {
    try {
      await api('/channels/' + channel.id, 'PATCH', { enabled: !channel.enabled });
      await channels.mutate();
      toast.success(channel.enabled ? 'Número pausado' : 'Número habilitado');
    } catch (e) {
      const { status, code } = e as { status?: number; code?: string };
      // The page-level notice already says the reception changed or the choice expired.
      if (code === 'acting_changed' || code === 'acting_expired') return;
      if (!channel.enabled && status === 409 && code === 'reminders_enabled')
        setReminderNumbers((ids) => [...ids, channel.id]);
      else if (!channel.enabled && status === 409) setAgentRefused(true);
      else toast.error((e as Error).message);
    }
  }
  return (
    <Card role="region" aria-labelledby="platform-whatsapp-title">
      <CardHeader>
        <CardTitle>
          <h2 id="platform-whatsapp-title" className="flex items-center gap-2">
            <MessageCircle className="size-4" /> WhatsApp
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {install.state === 'loading' && (
          <p className="text-sm text-muted-foreground">Comprobando la instalación…</p>
        )}
        {install.state === 'unknown' && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>No pudimos leer el estado de la instalación. No sabemos si se puede conectar.</p>
              <Button variant="outline" size="sm" onClick={() => installation.mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        )}
        {install.state === 'blocked' && (
          <Alert role="status">
            <Wrench aria-hidden="true" />
            <AlertTitle className="line-clamp-none">
              Tarea del operador de la instalación.
            </AlertTitle>
            <AlertDescription>
              <p>
                Falta configurar{' '}
                {install.missing?.map((name, n) => (
                  <span key={name}>
                    {n > 0 && ', '}
                    <code className="break-all">{name}</code>
                  </span>
                ))}
                . Hasta entonces no se puede conectar ni verificar un número desde aquí.
              </p>
            </AlertDescription>
          </Alert>
        )}
        {install.state === 'pending' && (
          <>
            <p className="max-w-prose text-sm text-muted-foreground">
              Genera el enlace y envíalo a quien administra el número en Meta, en el hospital: es
              esa persona quien lo abre. Cuando termine, pulsa Verificar conexión. Conectar un
              número no activa respuestas automáticas.
            </p>
            <WhatsAppLinkFlow flow={flow} />
            {flow.link && (
              <Button
                variant="outline"
                size="sm"
                className="self-start"
                onClick={async () => {
                  try {
                    await navigator.clipboard.writeText(flow.link!.url);
                    toast.success('Enlace copiado');
                  } catch {
                    toast.error('No se pudo copiar. Copia el enlace desde el botón de Kapso.');
                  }
                }}
              >
                <Copy /> Copiar enlace para el hospital
              </Button>
            )}
          </>
        )}
        {agentActive && (
          <Alert role="status">
            <ShieldCheck aria-hidden="true" />
            <AlertDescription>
              El agente de esta recepción está activo; habilitar el número lo pondría a contestar.
              Lo habilita el Administrador del hospital. Desde aquí puedes pausarlo.
            </AlertDescription>
          </Alert>
        )}
        {reminderNumbers.length > 0 && (
          <Alert role="status">
            <ShieldCheck aria-hidden="true" />
            <AlertDescription>
              Los recordatorios automáticos de esta recepción están activos; habilitar el número
              volvería a enviarlos. Lo habilita el Administrador del hospital.
            </AlertDescription>
          </Alert>
        )}
        <Separator />
        <h3 className="text-sm font-medium">Números de esta recepción</h3>
        {channels.error ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>No pudimos leer los números de esta recepción.</p>
              <Button variant="outline" size="sm" onClick={() => channels.mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !rows ? (
          <p className="text-sm text-muted-foreground">Leyendo los números…</p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Esta recepción aún no tiene números conectados.
          </p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Canal</TableHead>
                <TableHead>Atención</TableHead>
                <TableHead>Uso en el celular</TableHead>
                <TableHead>Conexión</TableHead>
                <TableHead>Estado</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((c) => (
                <TableRow key={c.id}>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <MessageCircle className="size-4 text-muted-foreground" />
                      <span className="font-medium">{c.name}</span>
                    </div>
                  </TableCell>
                  <TableCell>{c.doctorId ? 'Doctor' : 'General'}</TableCell>
                  <TableCell>
                    {c.coexistence ? 'WhatsApp Business y Recepción' : 'Recepción'}
                  </TableCell>
                  <TableCell>
                    {install.state === 'pending' ? (
                      <ChannelConnection id={c.id} />
                    ) : (
                      <span className="text-muted-foreground">
                        {install.state === 'blocked' ? 'Pendiente del operador' : '—'}
                      </span>
                    )}
                  </TableCell>
                  <TableCell>
                    {c.enabled ? (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Pause /> Pausar
                      </Button>
                    ) : agentActive || reminderNumbers.includes(c.id) ? (
                      <span className="text-muted-foreground">
                        Pausado · lo habilita el Administrador
                      </span>
                    ) : (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Play /> Habilitar
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}

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
