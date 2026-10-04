'use client';
import { useState, type ReactNode } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { AlertTriangle, CheckCircle2, Circle, Loader2, RefreshCw, Wrench } from 'lucide-react';
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
import { Button } from './ui/button';
import { CopyAccessLink, type Workload } from './team-workspace';
import { useWhatsAppConnect, WhatsAppLinkFlow } from './whatsapp-link-flow';
import styles from './first-steps.module.css';

type Connection = { configured: boolean; hospital: Me['tenant'] };
type Settings = { name: string; timeZone: string; guide: string; emergencyPhone?: string | null };

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
  loading: Loader2,
  unknown: AlertTriangle,
  blocked: Wrench,
};
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
            <p>
              <strong>{hospital.name}</strong> · zona horaria {hospital.timeZone}
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
      if (state === 'done') return channel?.name && <p>{channel.name} · recibe mensajes</p>;
      if (state !== 'pending') return null;
      return (
        <>
          {need === 'activate' && channel && (
            <>
              <p>
                {channel.name ?? 'Tu número'} está vinculado pero inactivo: todavía no entra ningún
                mensaje.
              </p>
              <Button size="sm" disabled={busy} onClick={() => activateChannel(channel)}>
                Activar número
              </Button>
            </>
          )}
          {need === 'reception' && (
            <p className={styles.warning}>
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
          <p>
            Las cuentas se crean en Hospital; aquí no hay alta. Comparte el enlace: cada persona
            aparece en tu equipo al iniciar sesión con su cuenta del hospital.
          </p>
          <div className={styles.actions}>
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
          <p>Al activarlo, el agente atiende las conversaciones nuevas de tus números activos.</p>
          <Button size="sm" disabled={busy} onClick={activateAgent}>
            Activar agente
          </Button>
        </>
      ),
  };

  return (
    <section className={`content-card ${styles.guide}`} aria-labelledby="first-steps-title">
      <div className="card-toolbar">
        <h2 id="first-steps-title">Primeros pasos</h2>
        <span className={styles.progress}>
          {steps.filter((s) => s.state === 'done').length} de {steps.length} listos
        </span>
      </div>
      <ol className={styles.steps}>
        {steps.map((step) => {
          const { id, state, missing } = step;
          const { title, detail } = copy[id];
          const Icon = icons[state];
          return (
            <li key={id} className={styles.step} data-state={state}>
              <Icon
                aria-hidden="true"
                size={20}
                className={state === 'loading' ? 'animate-spin' : undefined}
              />
              <div className={styles.text}>
                <strong>{title}</strong>
                <span>{detail}</span>
                <span className={styles.status}>{status[state]}</span>
                {state === 'blocked' && (
                  <p>
                    Esto lo resuelve el operador de la plataforma, no tú. Falta en la instalación:{' '}
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
                  <div className={styles.actions}>
                    <Button
                      variant="outline"
                      size="sm"
                      aria-label={`Reintentar: ${title}`}
                      onClick={() => retry[id]()}
                    >
                      <RefreshCw /> Reintentar
                    </Button>
                  </div>
                )}
              </div>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
