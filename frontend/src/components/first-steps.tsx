'use client';
import { useState, type ReactNode } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { AlertTriangle, CheckCircle2, Circle, RefreshCw, Wrench } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Me } from '@/lib/api';
import {
  firstSteps,
  receptionProbe,
  type Installation,
  type Reception,
  type Step,
  type StepChannel,
  type StepId,
  type StepState,
} from '@/lib/first-steps';
import { refreshHospitalIdentity } from '@/lib/hospital-identity';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import {
  Item,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemMedia,
  ItemSeparator,
  ItemTitle,
} from './ui/item';
import { Progress } from './ui/progress';
import { Spinner } from './ui/spinner';
import { CopyAccessLink, type Workload } from './team-workspace';
import { useWhatsAppConnect, WhatsAppLinkFlow } from './whatsapp-link-flow';

type Connection = { configured: boolean; hospital: Me['tenant'] };
type Settings = {
  name: string;
  timeZone: string;
  guide: string;
  emergencyPhone?: string | null;
  emergencyWhatsApp?: boolean;
};

const copy: Record<StepId, { title: string; detail: string }> = {
  hospital: {
    title: 'Conecta tu hospital',
    detail: 'Recepción lee la agenda y los pacientes de tu hospital.',
  },
  whatsapp: {
    title: 'Conecta WhatsApp',
    detail: 'Vincula el número donde escriben tus pacientes y actívalo para recibir mensajes.',
  },
  team: {
    title: 'Incorpora a tu equipo',
    detail: 'Al menos otra persona, además de ti, que atienda conversaciones.',
  },
  agent: {
    title: 'Activa el agente de recepción',
    detail: 'Responde a tus pacientes y entrega la conversación a tu equipo cuando hace falta.',
  },
};
const status: Record<StepState, string> = {
  done: 'Listo',
  pending: 'Pendiente',
  failed: 'No pudimos comunicarnos con Hospital',
  loading: 'Comprobando…',
  unknown: 'No pudimos comprobarlo',
  blocked: 'Falta configurar la instalación',
};
const icons = {
  done: CheckCircle2,
  pending: Circle,
  failed: AlertTriangle,
  loading: Spinner,
  unknown: AlertTriangle,
  blocked: Wrench,
};
const tone: Record<StepState, string> = {
  done: 'text-primary',
  pending: 'text-muted-foreground',
  failed: 'text-destructive',
  loading: 'text-muted-foreground',
  unknown: 'text-destructive',
  blocked: 'text-destructive',
};
const badge: Record<StepState, 'secondary' | 'outline' | 'destructive'> = {
  done: 'secondary',
  pending: 'outline',
  failed: 'destructive',
  loading: 'outline',
  unknown: 'destructive',
  blocked: 'destructive',
};
const note = 'text-sm text-muted-foreground';
// One Kapso/Hospital round trip per visit, never on focus or on a timer.
const once = { revalidateOnFocus: false, shouldRetryOnError: false, dedupingInterval: 30000 };
const post = <T,>([path]: [string, string]) => api<T>(path, 'POST', {});

export function FirstSteps({ me, onNavigate }: { me: Me; onNavigate: (view: string) => void }) {
  const admin = me.role === 'admin';
  const { mutate: globalMutate } = useSWRConfig();
  // Same keys as the views these steps belong to, so the cache and the verdict are shared with them.
  const connection = useSWR<Connection>(admin ? '/hospital/connection' : null, fetcher);
  const check = useSWR<{ connected: boolean }>(
    connection.data?.configured ? ['/hospital/connection/check', me.tenant.id] : null,
    post<{ connected: boolean }>,
    // The check syncs name and zone from Hospital: repaint every read that shows them, once.
    { ...once, onSuccess: refreshHospitalIdentity },
  );
  const installation = useSWR<Installation>(admin ? '/platform/installation' : null, fetcher);
  const channels = useSWR<StepChannel[]>(admin ? '/channels' : null, fetcher);
  const i = installation.data;
  // Kapso is only asked when the installation can receive and no signed event has proven it yet.
  const probe =
    i?.kapsoKey && i.webhookUrl && i.webhookSecret ? receptionProbe(channels.data) : null;
  const reception = useSWR<Reception>(
    probe ? [`/channels/${probe.id}/diagnostics`, probe.id] : null,
    post<Reception>,
    once,
  );
  const workload = useSWR<Workload>(admin ? '/members/workload' : null, fetcher);
  const flow = useWhatsAppConnect(admin, () =>
    Promise.all([channels.mutate(), reception.mutate()]),
  );
  const [busy, setBusy] = useState(false);

  const steps = firstSteps({
    me,
    connection,
    check: { data: check.data, error: check.error, validating: check.isValidating },
    installation,
    channels,
    reception: { data: reception.data, error: reception.error, validating: reception.isValidating },
    workload,
  });
  if (!steps) return null;

  async function act(run: () => Promise<unknown>, done: string) {
    setBusy(true);
    try {
      await run();
      toast.success(done);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const activateChannel = (channel: StepChannel) =>
    act(async () => {
      await api('/channels/' + channel.id, 'PATCH', { enabled: true });
      await channels.mutate();
    }, 'Número activado');
  const activateAgent = () =>
    act(async () => {
      // Same PUT as Configuración, with what is stored right now: only the switch changes.
      const s = await api<Settings>('/settings');
      await api('/settings', 'PUT', {
        name: s.name,
        timeZone: s.timeZone,
        guide: s.guide,
        emergencyPhone: s.emergencyPhone,
        emergencyWhatsApp: s.emergencyWhatsApp ?? false,
        agentEnabled: true,
      });
      await Promise.all([globalMutate('/me'), globalMutate('/settings')]);
    }, 'Agente activado');
  const retry: Record<StepId, () => unknown> = {
    hospital: () => (connection.error ? connection.mutate() : check.mutate()),
    whatsapp: () => Promise.all([installation.mutate(), channels.mutate(), reception.mutate()]),
    team: () => workload.mutate(),
    agent: () => installation.mutate(),
  };

  const body: Record<StepId, (step: Step) => ReactNode> = {
    hospital: ({ state }) => {
      const hospital = connection.data?.hospital;
      if (state === 'done')
        return (
          hospital && (
            <p className={note}>
              <span className="font-medium text-foreground">{hospital.name}</span> · zona horaria{' '}
              {hospital.timeZone}
            </p>
          )
        );
      return (
        (state === 'pending' || state === 'failed') && (
          <Button size="sm" onClick={() => onNavigate('hospital')}>
            Abrir Mi hospital
          </Button>
        )
      );
    },
    whatsapp: ({ state, need, channel }) => {
      if (state === 'done')
        return channel?.name && <p className={note}>{channel.name} · recibe mensajes</p>;
      if (state !== 'pending') return null;
      return (
        <>
          {need === 'activate' && channel && (
            <>
              <p className={note}>
                {channel.name ?? 'Tu número'} está vinculado pero inactivo: todavía no entra ningún
                mensaje.
              </p>
              <Button size="sm" disabled={busy} onClick={() => activateChannel(channel)}>
                Activar número
              </Button>
            </>
          )}
          {need === 'reception' && (
            <p className="text-sm text-destructive">
              {channel?.name ?? 'Tu número'} está activo, pero Kapso aún no entrega sus mensajes a
              Recepción. Pulsa Verificar conexión para registrar la recepción de nuevo.
            </p>
          )}
          <WhatsAppLinkFlow flow={flow} />
        </>
      );
    },
    team: ({ state }) =>
      state === 'pending' && (
        <>
          <p className={note}>
            Las cuentas se crean en Hospital; aquí no hay alta. Comparte el enlace: cada persona
            aparece en tu equipo al iniciar sesión con su cuenta del hospital.
          </p>
          <div className="flex flex-wrap gap-2">
            <CopyAccessLink size="sm" />
            <Button size="sm" variant="outline" onClick={() => onNavigate('team')}>
              Ir a Equipo
            </Button>
          </div>
        </>
      ),
    agent: ({ state }) =>
      state === 'pending' && (
        <>
          <p className={note}>
            Al activarlo, el agente atiende las conversaciones nuevas de tus números activos.
          </p>
          <Button size="sm" disabled={busy} onClick={activateAgent}>
            Activar agente
          </Button>
        </>
      ),
  };

  const ready = steps.filter((s) => s.state === 'done').length;

  return (
    <section aria-labelledby="first-steps-title">
      <Card>
        <CardHeader>
          <CardTitle>
            <h2 id="first-steps-title">Primeros pasos</h2>
          </CardTitle>
          <CardDescription>
            {ready} de {steps.length} listos
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <Progress value={(ready / steps.length) * 100} aria-label="Avance de primeros pasos" />
          <ItemGroup>
            {steps.map((step, index) => {
              const { id, state, missing } = step;
              const { title, detail } = copy[id];
              const Icon = icons[state];
              return (
                <div key={id} role="listitem" data-state={state} className="flex flex-col">
                  {index > 0 && <ItemSeparator />}
                  <Item className="px-0">
                    <ItemMedia className="self-start">
                      <Icon aria-hidden="true" className={`size-5 ${tone[state]}`} />
                    </ItemMedia>
                    <ItemContent className="min-w-0 items-start gap-2">
                      <ItemTitle>{title}</ItemTitle>
                      <ItemDescription className="line-clamp-none">{detail}</ItemDescription>
                      <Badge variant={badge[state]} className="max-w-full">
                        <span className="truncate">{status[state]}</span>
                      </Badge>
                      {state === 'blocked' && (
                        <p className="text-sm text-destructive">
                          Esto lo resuelve el operador de la plataforma, no tú. Falta en la
                          instalación:{' '}
                          {missing?.map((name, n) => (
                            <span key={name}>
                              {n > 0 && ', '}
                              <code>{name}</code>
                            </span>
                          ))}
                          .
                        </p>
                      )}
                      {body[id](step)}
                      {(state === 'unknown' || state === 'failed') && (
                        <Button
                          variant="outline"
                          size="sm"
                          aria-label={`Reintentar: ${title}`}
                          onClick={() => retry[id]()}
                        >
                          <RefreshCw /> Reintentar
                        </Button>
                      )}
                    </ItemContent>
                  </Item>
                </div>
              );
            })}
          </ItemGroup>
        </CardContent>
      </Card>
    </section>
  );
}
