'use client';
// Vista del dueño de plataforma (docs/platform-owner.md). Solo configura: elige una recepción y,
// en su nombre, revisa la conexión con Hospital y conecta WhatsApp. Sin bandeja, contactos ni
// ajustes. La elección vive en una cookie del servidor: aquí nunca se guarda ni viaja en la URL.
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import {
  ArrowLeftRight,
  Building2,
  ChevronRight,
  Copy,
  HeartPulse,
  Loader2,
  LogOut,
  Menu,
  MessageCircle,
  Pause,
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
import { Button } from './ui/button';
import { HospitalConnection } from './hospital-connection';
import { ChannelConnection } from './conversation-workspace';
import { useWhatsAppConnect, WhatsAppLinkFlow } from './whatsapp-link-flow';
import styles from './platform-workspace.module.css';

type ActingFor = { tenantId: string; name: string };
type Tenant = { id: string; name: string; hospitalConfigured: boolean; whatsAppConnected: boolean };

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
    <section className="content-card" aria-labelledby="receptions-title">
      <div className="card-toolbar">
        <h2 id="receptions-title">
          <Building2 size={18} /> Recepciones
        </h2>
        {tenants && tenants.length > 0 && (
          <div className="search-input">
            <Search size={16} />
            <input
              aria-label="Buscar recepción por nombre"
              placeholder="Buscar por nombre…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              autoComplete="off"
            />
          </div>
        )}
      </div>
      {unreadable ? (
        <div className={styles.state} role="alert">
          <p>
            No pudimos leer la lista de recepciones. La lectura falló: no significa que no haya
            ninguna.
          </p>
          <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
            <RefreshCw /> Reintentar
          </Button>
        </div>
      ) : !tenants ? (
        <div className="loading">
          <Loader2 className="animate-spin" size={20} /> Leyendo las recepciones…
        </div>
      ) : tenants.length === 0 ? (
        <div className="empty">
          <span>
            <Building2 size={28} />
          </span>
          <h3>Aún no hay recepciones</h3>
          <p>
            Una recepción aparece aquí cuando el Administrador de un hospital abre su espacio por
            primera vez.
          </p>
        </div>
      ) : (
        <>
          <p className={styles.count} role="status">
            {matches.length === tenants.length
              ? `${tenants.length} recepciones`
              : `${matches.length} de ${tenants.length} recepciones`}
            {matches.length > SHOWN && ` · se muestran ${SHOWN}; escribe el nombre para afinar`}
          </p>
          {failure && (
            <p className={cn('error', styles.failure)} role="alert">
              {failure}
            </p>
          )}
          {matches.length === 0 ? (
            <p className={styles.state}>Ninguna recepción coincide con «{query.trim()}».</p>
          ) : (
            <ul className={styles.list}>
              {matches.slice(0, SHOWN).map((t) => (
                <li key={t.id}>
                  <button
                    className={styles.row}
                    disabled={!!choosing}
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
                    <span className="hospital-mark">
                      <Building2 size={18} />
                    </span>
                    <strong>{t.name}</strong>
                    <span className={styles.facts}>
                      <span data-ok={t.hospitalConfigured}>
                        <HeartPulse size={14} />
                        {t.hospitalConfigured ? 'Hospital configurado' : 'Hospital sin configurar'}
                      </span>
                      <span data-ok={t.whatsAppConnected}>
                        <MessageCircle size={14} />
                        {t.whatsAppConnected ? 'WhatsApp conectado' : 'Sin número de WhatsApp'}
                      </span>
                    </span>
                    {choosing === t.id ? (
                      <Loader2 className="animate-spin" size={16} />
                    ) : (
                      <ChevronRight size={16} aria-hidden="true" />
                    )}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
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
    : channels.data?.filter((c) => c.phoneNumberId !== 'demo');
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
    <section className="content-card" aria-labelledby="platform-whatsapp-title">
      <div className="card-toolbar">
        <h2 id="platform-whatsapp-title">
          <MessageCircle size={18} /> WhatsApp
        </h2>
      </div>
      <div className={styles.body}>
        {install.state === 'loading' && <p>Comprobando la instalación…</p>}
        {install.state === 'unknown' && (
          <div role="alert">
            <p>No pudimos leer el estado de la instalación. No sabemos si se puede conectar.</p>
            <Button variant="outline" size="sm" onClick={() => installation.mutate()}>
              <RefreshCw /> Reintentar
            </Button>
          </div>
        )}
        {install.state === 'blocked' && (
          <p className={styles.operator} role="status">
            <Wrench size={16} aria-hidden="true" />
            <span>
              <strong>Tarea del operador de la instalación.</strong> Falta configurar{' '}
              {install.missing?.map((name, n) => (
                <span key={name}>
                  {n > 0 && ', '}
                  <code>{name}</code>
                </span>
              ))}
              . Hasta entonces no se puede conectar ni verificar un número desde aquí.
            </span>
          </p>
        )}
        {install.state === 'pending' && (
          <>
            <p className="hint">
              Genera el enlace y envíalo a quien administra el número en Meta, en el hospital: es
              esa persona quien lo abre. Cuando termine, pulsa Verificar conexión. Conectar un
              número no activa respuestas automáticas.
            </p>
            <WhatsAppLinkFlow flow={flow} />
            {flow.link && (
              <Button
                variant="outline"
                size="sm"
                className="mt-3"
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
          <p className={styles.operator} role="status">
            <ShieldCheck size={16} aria-hidden="true" />
            <span>
              El agente de esta recepción está activo; habilitar el número lo pondría a contestar.
              Lo habilita el Administrador del hospital. Desde aquí puedes pausarlo.
            </span>
          </p>
        )}
        {reminderNumbers.length > 0 && (
          <p className={styles.operator} role="status">
            <ShieldCheck size={16} aria-hidden="true" />
            <span>
              Los recordatorios automáticos de esta recepción están activos; habilitar el número
              volvería a enviarlos. Lo habilita el Administrador del hospital.
            </span>
          </p>
        )}
      </div>
      <h3 className={styles.subtitle}>Números de esta recepción</h3>
      {channels.error ? (
        <div className={styles.state} role="alert">
          <p>No pudimos leer los números de esta recepción.</p>
          <Button variant="outline" size="sm" onClick={() => channels.mutate()}>
            <RefreshCw /> Reintentar
          </Button>
        </div>
      ) : !rows ? (
        <p className={styles.state}>Leyendo los números…</p>
      ) : rows.length === 0 ? (
        <p className={styles.state}>Esta recepción aún no tiene números conectados.</p>
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Canal</th>
                <th>Atención</th>
                <th>Uso en el celular</th>
                <th>Conexión</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((c) => (
                <tr key={c.id}>
                  <td>
                    <div className="name-cell">
                      <span className="channel-icon">
                        <MessageCircle size={17} />
                      </span>
                      <strong>{c.name}</strong>
                    </div>
                  </td>
                  <td>{c.doctorId ? 'Doctor' : 'General'}</td>
                  <td>{c.coexistence ? 'WhatsApp Business y Recepción' : 'Recepción'}</td>
                  <td>
                    {install.state === 'pending' ? (
                      <ChannelConnection id={c.id} />
                    ) : (
                      <span className="hint">
                        {install.state === 'blocked' ? 'Pendiente del operador' : '—'}
                      </span>
                    )}
                  </td>
                  <td>
                    {c.enabled ? (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Pause /> Pausar
                      </Button>
                    ) : agentActive || reminderNumbers.includes(c.id) ? (
                      <span className="hint">Pausado · lo habilita el Administrador</span>
                    ) : (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Play /> Habilitar
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
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
  const [mobile, setMobile] = useState(false);
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
    <div className="app-shell">
      <aside className={cn('sidebar', mobile && 'open')}>
        <a className="brand" href="/" aria-label="Recepción inicio">
          <span className="brand-icon">
            <HeartPulse />
          </span>
          recepción<span className="brand-dot">.</span>
        </a>
        <div className="hospital-switch">
          <div className="hospital-mark">
            <Building2 size={18} />
          </div>
          <div>
            <strong>{shown?.name ?? 'Sin recepción elegida'}</strong>
            <small>{shown ? 'Actuando en su nombre' : 'Elige una para configurarla'}</small>
          </div>
        </div>
        <div className="nav-caption">PLATAFORMA</div>
        <nav>
          <a className="nav-item active" href="/" aria-current="page">
            <Building2 size={18} />
            <span>Recepciones</span>
          </a>
        </nav>
        <div className="sidebar-bottom">
          <div className="profile">
            <div>
              <strong>{name}</strong>
              <small>Dueño de plataforma</small>
            </div>
            <button
              aria-label="Cerrar sesión"
              onClick={async () => {
                // Signing out also drops the chosen reception (origin-checked DELETE).
                await fetch('/api/platform/acting', { method: 'DELETE' }).catch(() => {});
                await endSession();
              }}
            >
              <LogOut size={17} />
            </button>
          </div>
        </div>
      </aside>
      {mobile && (
        <button
          className="mobile-shade"
          aria-label="Cerrar menú"
          onClick={() => setMobile(false)}
        />
      )}
      <main className="main">
        <header className="topbar">
          <div>
            <button
              className="mobile-menu"
              onClick={() => setMobile(true)}
              aria-label="Abrir menú"
              aria-expanded={mobile}
            >
              <Menu />
            </button>
            <span className="breadcrumb">Plataforma</span>
            <ChevronRight size={13} />
            <strong>{shown ? shown.name : 'Recepciones'}</strong>
          </div>
          <div className="topbar-right">
            <span className="secure">
              <ShieldCheck size={15} /> Sesión protegida
            </span>
          </div>
        </header>
        {shown && (
          <div className={styles.banner} role="status">
            <ArrowLeftRight size={17} aria-hidden="true" />
            <span>
              Actuando en nombre de <strong>{shown.name}</strong>
            </span>
            <Button variant="outline" size="sm" disabled={changing} onClick={change}>
              Cambiar
            </Button>
          </div>
        )}
        <div className="page-heading">
          <div>
            <div className="eyebrow">PLATAFORMA</div>
            <h1>
              {actingFor ? 'Configurar recepción' : 'Recepciones'}
              <span className="title-dot" />
            </h1>
            <p>
              {actingFor
                ? 'Conexión con Hospital y WhatsApp. La atención, los contactos y los ajustes son del hospital.'
                : 'Elige la recepción que vas a configurar.'}
            </p>
          </div>
        </div>
        <div className={cn('page-content', styles.sections)}>
          {state === 'acting_changed' ? (
            <section className="content-card" role="alert">
              <div className={styles.state}>
                <strong>Cambió de recepción en otra pestaña.</strong>
                <p>
                  Esta pestaña mostraba {actingFor?.name ?? 'otra recepción'} y ya no actúa en su
                  nombre. No se envió nada. Recarga para ver la recepción actual.
                </p>
                <Button size="sm" onClick={() => window.location.assign('/')}>
                  <RefreshCw /> Recargar
                </Button>
              </div>
            </section>
          ) : state === 'acting_expired' ? (
            <section className="content-card" role="alert">
              <div className={styles.state}>
                <strong>Tu elección caducó; elige la recepción de nuevo.</strong>
                <p>
                  La elección dura doce horas, o la recepción ya no existe. No se envió nada en su
                  nombre.
                </p>
                <Button size="sm" disabled={changing} onClick={change}>
                  Elegir recepción
                </Button>
              </div>
            </section>
          ) : actingFor ? (
            <>
              <HospitalConnection platform />
              <WhatsAppSection />
            </>
          ) : (
            <ReceptionPicker />
          )}
        </div>
      </main>
    </div>
  );
}
