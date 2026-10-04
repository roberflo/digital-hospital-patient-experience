'use client';
import { GoogleCalendarConnection } from './google-calendar-connection';
import { useEffect, useState, useRef, type FormEvent, type ReactNode } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { signOut } from 'next-auth/react';
import {
  HeartPulse,
  LayoutDashboard,
  Inbox,
  Users,
  Building2,
  Columns3,
  CalendarDays,
  Activity as ActivityIcon,
  Settings,
  Search,
  Plus,
  ArrowUpRight,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Send,
  Paperclip,
  Sparkles,
  UserRound,
  Check,
  CheckCheck,
  ShieldCheck,
  Link2,
  LogOut,
  Menu,
  X,
  MessageCircle,
  FileText,
  RefreshCw,
  Pause,
  Play,
  ArrowLeft,
  AlertCircle,
  Loader2,
  ExternalLink,
  Stethoscope,
} from 'lucide-react';
import { toast } from 'sonner';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogTitle, DialogDescription } from './ui/dialog';
import {
  api,
  fetcher,
  type Me,
  type Contact,
  type Chat,
  type Message,
  type Activity,
  type Channel,
  type Opportunity,
} from '@/lib/api';
import { cn } from '@/lib/utils';
import { AppointmentReminders, ReminderConsent } from './appointment-reminders';
import {
  HospitalCompanies,
  CustomerCommercial,
  OpportunityCommercial,
} from './commercial-workspace';
import { ActivityWorkspace } from './activity-workspace';
import { PatientAppointments } from './patient-appointments';
import { HospitalConnection, PatientLink } from './hospital-connection';
import { PatientClinical } from './patient-clinical';
import { SavedInboxViews, ConversationMacros } from './inbox-productivity';
import { DashboardView } from './dashboard-view';
import {
  AssignmentControl,
  TeamWorkspace,
  managesTeam,
  memberLabel,
  type Workload,
} from './team-workspace';
import {
  WorkflowControls,
  CustomerCrm,
  SavedReplies,
  ChannelConnection,
  conversationStates,
  priorities,
} from './conversation-workspace';
const nav = [
  ['dashboard', 'Dashboard', LayoutDashboard],
  ['inbox', 'Bandeja de entrada', Inbox],
  ['team', 'Equipo', Users],
  ['contacts', 'Contactos', Users],
  ['companies', 'Empresas', Building2],
  ['opportunities', 'Oportunidades', Columns3],
  ['calendar', 'Agenda', CalendarDays],
  ['hospital', 'Mi hospital', HeartPulse],
  ['activity', 'Actividad', ActivityIcon],
  ['agent', 'Agente de atención', Sparkles],
] as const;
const stages = [
  ['new', 'Nuevo'],
  ['contacted', 'En seguimiento'],
  ['scheduled', 'Agendado'],
  ['won', 'Completado'],
  ['lost', 'Cerrado'],
] as const;
const labels: Record<string, string> = {
  agent: 'Agente IA',
  human: 'Atención humana',
  closed: 'Cerrada',
  admin: 'Administrador',
  doctor: 'Doctor',
  patient: 'Paciente',
  sent: 'Enviado',
  delivered: 'Entregado',
  read: 'Leído',
  received: 'Recibido',
  sending: 'Enviando',
  uncertain: 'Sin confirmar',
  failed: 'Falló',
  done: 'Completado',
  pending: 'Pendiente',
  running: 'En curso',
};
function initials(name: string) {
  return name
    .split(' ')
    .map((x) => x[0])
    .slice(0, 2)
    .join('')
    .toUpperCase();
}
function time(value: string, tz = 'America/El_Salvador') {
  return new Date(value).toLocaleTimeString('es-SV', {
    hour: '2-digit',
    minute: '2-digit',
    timeZone: tz,
  });
}
function date(value: string) {
  return new Date(value).toLocaleDateString('es-SV', { day: 'numeric', month: 'short' });
}
function Avatar({ name, large = false }: { name: string; large?: boolean }) {
  return <span className={cn('avatar', large && 'large')}>{initials(name)}</span>;
}
function Badge({ value }: { value: string }) {
  return (
    <span className={'status status-' + value}>
      {value === 'agent' ? <Sparkles size={11} /> : <span />}
      {labels[value] ?? value}
    </span>
  );
}
function Empty({
  icon: Icon = Inbox,
  title,
  children,
}: {
  icon?: typeof Inbox;
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className="empty">
      <span>
        <Icon size={28} />
      </span>
      <h3>{title}</h3>
      <p>{children}</p>
    </div>
  );
}
function ErrorBox({ error }: { error?: Error }) {
  return error ? (
    <div className="error-box" role="alert">
      <AlertCircle size={17} />
      {error.message}
    </div>
  ) : null;
}
function Loading() {
  return (
    <div className="loading">
      <Loader2 className="animate-spin" size={20} /> Cargando tu espacio…
    </div>
  );
}
type Field = {
  name: string;
  label: string;
  type?: string;
  required?: boolean;
  options?: { value: string; label: string }[];
  value?: string;
  placeholder?: string;
};
function FormDialog({
  title,
  description,
  fields,
  open,
  onClose,
  onSubmit,
}: {
  title: string;
  description?: string;
  fields: Field[];
  open: boolean;
  onClose: () => void;
  onSubmit: (values: Record<string, string>) => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        if (!v) onClose();
      }}
    >
      <DialogContent>
        <DialogTitle className="dialog-title">{title}</DialogTitle>
        <DialogDescription className="dialog-description">
          {description ?? 'Completa los datos para continuar.'}
        </DialogDescription>
        <form
          className="dialog-form"
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            try {
              await onSubmit(
                Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>,
              );
              onClose();
              toast.success('Cambios guardados');
            } catch (err) {
              toast.error((err as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          {fields.map((f) => (
            <label key={f.name}>
              {f.label}
              {f.options ? (
                <select
                  aria-label={f.label}
                  name={f.name}
                  defaultValue={f.value}
                  required={f.required}
                >
                  <option value="">Selecciona una opción</option>
                  {f.options.map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </select>
              ) : f.type === 'textarea' ? (
                <textarea
                  rows={4}
                  name={f.name}
                  defaultValue={f.value}
                  required={f.required}
                  maxLength={10000}
                />
              ) : (
                <input
                  name={f.name}
                  type={f.type ?? 'text'}
                  defaultValue={f.value}
                  required={f.required}
                  placeholder={f.placeholder}
                  maxLength={f.type === 'text' || !f.type ? 500 : undefined}
                />
              )}
            </label>
          ))}
          <div className="dialog-actions">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancelar
            </Button>
            <Button disabled={busy}>
              {busy ? <Loader2 className="animate-spin" /> : <Check />}Guardar
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
export default function Workspace() {
  const [view, setView] = useState('dashboard');
  const [mobile, setMobile] = useState(false);
  const [search, setSearch] = useState('');
  const [teamAssignment, setTeamAssignment] = useState<string | null>(null);
  const [todayLabel, setTodayLabel] = useState('');
  const { data: me, error } = useSWR<Me>('/me', fetcher);
  const { data: stats, error: statsError } = useSWR<Record<string, number>>('/overview', fetcher, {
    refreshInterval: 10000,
  });
  useEffect(() => {
    if (me)
      setTodayLabel(
        new Date().toLocaleDateString('es-SV', {
          weekday: 'short',
          day: 'numeric',
          month: 'long',
          timeZone: me.tenant.timeZone,
        }),
      );
  }, [me?.tenant.timeZone]);
  useEffect(() => {
    const v = new URLSearchParams(location.search).get('view');
    if (v && (v === 'settings' || nav.some(([id]) => id === v))) setView(v);
  }, []);
  const navigate = (v: string) => {
    setView(v);
    setSearch('');
    setMobile(false);
    history.replaceState(null, '', '/?view=' + v);
  };
  const title =
    view === 'settings' ? 'Configuración' : (nav.find((n) => n[0] === view)?.[1] ?? 'Recepción');
  return (
    <div className="app-shell">
      <aside className={cn('sidebar', mobile && 'open')}>
        <a className="brand" href="/" aria-label="Recepción inicio">
          <span className="brand-icon">
            <HeartPulse />
          </span>
          recepción<span className="brand-dot">.</span>
        </a>
        <button
          className="hospital-switch"
          onClick={() => navigate('hospital')}
          aria-label="Ver conexión con mi hospital"
        >
          <div className="hospital-mark">
            <Building2 size={18} />
          </div>
          <div>
            <strong>{me?.tenant.name ?? 'Hospital'}</strong>
            <small>Ver conexión y cuenta</small>
          </div>
          <ChevronDown size={14} />
        </button>
        <div className="nav-caption">ESPACIO DE TRABAJO</div>
        <nav>
          {nav.map(([id, label, Icon]) => (
            <button
              key={id}
              aria-label={label}
              className={cn('nav-item', view === id && 'active')}
              onClick={() => navigate(id)}
            >
              <Icon size={18} />
              <span>{label}</span>
              {id === 'inbox' && !!stats?.human && <b>{stats.human}</b>}
              {id === 'agent' && <span className="nav-new">IA</span>}
            </button>
          ))}
          <a className="nav-item" href="/whatsapp">
            <MessageCircle size={18} />
            <span>WhatsApp · números</span>
          </a>
        </nav>
        <div className="sidebar-bottom">
          <div className="agent-mini">
            <div>
              <span className={cn('live-dot', !me?.tenant.agentEnabled && 'off')} />
              <strong>{me?.tenant.agentEnabled ? 'Agente disponible' : 'Agente en pausa'}</strong>
            </div>
            <p>
              {me?.tenant.agentEnabled
                ? 'Conectado con tu equipo'
                : 'Actívalo cuando esté configurado'}
            </p>
            <button onClick={() => navigate('agent')}>
              Ver actividad del agente <ArrowUpRight size={14} />
            </button>
          </div>
          {me?.role === 'admin' && (
            <button
              className={cn('nav-item', view === 'settings' && 'active')}
              onClick={() => navigate('settings')}
            >
              <Settings size={18} />
              Configuración
            </button>
          )}
          <div className="profile">
            <Avatar name={me?.name ?? 'Usuario'} />
            <div>
              <strong>{me?.name ?? 'Conectando…'}</strong>
              <small>
                {me?.role === 'agent' ? 'Recepcionista' : (labels[me?.role ?? ''] ?? '')}
              </small>
            </div>
            <button
              aria-label="Cerrar sesión"
              onClick={async () => {
                const end = await fetch('/api/session/end').then((r) => r.json());
                await signOut({ redirect: false });
                window.location.href = end.url;
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
      <main className={cn('main', view === 'inbox' && 'main-inbox')}>
        <header className="topbar">
          <div>
            <button
              className="mobile-menu"
              disabled={!me}
              onClick={() => setMobile(true)}
              aria-label="Abrir menú"
              aria-expanded={mobile}
            >
              <Menu />
            </button>
            <span className="breadcrumb">Espacio de trabajo</span>
            <ChevronRight size={13} />
            <strong>{title}</strong>
          </div>
          <div className="topbar-right">
            <span className="today">{todayLabel}</span>
            <span className="separator" />
            <span className="secure">
              <ShieldCheck size={15} /> Sesión protegida
            </span>
          </div>
        </header>
        <div className={cn('page-heading', view === 'inbox' && 'inbox-heading')}>
          <div>
            {view !== 'inbox' && <div className="eyebrow">ATENCIÓN CONECTADA</div>}
            <h1>
              {title}
              <span className="title-dot" />
            </h1>
            {view !== 'inbox' && (
              <p>
                {
                  (
                    {
                      dashboard: 'El resumen de la atención de tu hospital.',
                      inbox: 'Cada conversación, con el contexto que necesitas.',
                      team: 'Organiza responsables y reparte la atención de tu hospital.',
                      contacts: 'Conoce a tus pacientes. Acompaña cada paso.',
                      companies: 'Relaciones y convenios que conectan tu hospital.',
                      opportunities: 'Del primer contacto al seguimiento de la atención.',
                      calendar: 'Una agenda compartida para todo tu hospital.',
                      hospital:
                        'Tu cuenta, tus pacientes y tu equipo, conectados al mismo hospital.',
                      activity:
                        'Quién atendió a cada paciente, qué hizo y cómo continuó la atención.',
                      agent: 'Un compañero para tu equipo. Disponible para tus pacientes.',
                      settings: 'Personaliza cómo trabaja y se conecta tu hospital.',
                    } as Record<string, string>
                  )[view]
                }
              </p>
            )}
          </div>
          {view !== 'hospital' &&
            view !== 'settings' &&
            view !== 'agent' &&
            view !== 'dashboard' &&
            view !== 'activity' && (
              <div className="search-input">
                <Search size={16} />
                <input
                  aria-label="Buscar"
                  placeholder="Buscar en esta vista…"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
                <kbd>⌕</kbd>
              </div>
            )}
        </div>
        <ErrorBox error={error} />
        {!me && !error ? (
          <Loading />
        ) : (
          me && (
            <div className="page-content">
              {view === 'dashboard' ? (
                <>
                  <ErrorBox error={statsError} />
                  <DashboardView stats={stats} onNavigate={navigate} />
                </>
              ) : view === 'inbox' ? (
                <InboxView me={me} search={search} initialAssignment={teamAssignment} />
              ) : view === 'team' ? (
                <TeamWorkspace
                  me={me}
                  search={search}
                  onOpenInbox={(assignment) => {
                    setTeamAssignment(assignment);
                    navigate('inbox');
                  }}
                />
              ) : view === 'contacts' ? (
                <ContactsView search={search} />
              ) : view === 'companies' ? (
                <HospitalCompanies search={search} />
              ) : view === 'opportunities' ? (
                <OpportunitiesView search={search} />
              ) : view === 'hospital' ? (
                <HospitalConnection />
              ) : view === 'calendar' ? (
                <CalendarView search={search} me={me} />
              ) : view === 'activity' ? (
                <ActivityWorkspace search={search} onSearch={setSearch} me={me} />
              ) : view === 'agent' ? (
                <AgentView me={me} />
              ) : view === 'settings' ? (
                <SettingsView me={me} />
              ) : null}
            </div>
          )
        )}
      </main>
    </div>
  );
}
function InboxView({
  me,
  search,
  initialAssignment,
}: {
  me: Me;
  search: string;
  initialAssignment: string | null;
}) {
  const [state, setState] = useState(initialAssignment ? '' : 'open');
  const [assignment, setAssignment] = useState(initialAssignment ?? 'all');
  const { data: workload } = useSWR<Workload>('/members/workload', fetcher, {
    refreshInterval: 10000,
  });
  const { mutate: refreshTeam } = useSWRConfig();
  const [checked, setChecked] = useState<Record<string, number>>({});
  const [bulkTarget, setBulkTarget] = useState('');
  const [assigning, setAssigning] = useState(false);
  const [linkedPhone, setLinkedPhone] = useState('');
  const [linkedNumber, setLinkedNumber] = useState('');
  const [linkedConversation, setLinkedConversation] = useState('');
  function clearLinkedConversation() {
    setLinkedPhone('');
    setLinkedNumber('');
    setLinkedConversation('');
    const url = new URL(location.href);
    for (const key of ['conversationId', 'phone', 'phoneNumberId']) url.searchParams.delete(key);
    history.replaceState(null, '', url.pathname + url.search);
  }
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    if (params.get('conversationId')) {
      setLinkedConversation(params.get('conversationId')!);
      setSelected(params.get('conversationId')!);
      setState('');
    }
    if (params.get('phone')) {
      setLinkedPhone(params.get('phone')!);
      setLinkedNumber(params.get('phoneNumberId') ?? '');
      setState('');
    }
  }, []);
  const [channelId, setChannelId] = useState('');
  const [priority, setPriority] = useState('');
  const [label, setLabel] = useState('');
  const [page, setPage] = useState(1);
  const { data: channels } = useSWR<Channel[]>('/channels', fetcher);
  useEffect(() => setPage(1), [state, assignment, channelId, priority, label]);
  useEffect(() => setChecked({}), [state, assignment, channelId, priority, label, page, search]);
  const query = new URLSearchParams({ assignment, page: String(page) });
  if (linkedConversation) query.set('conversationId', linkedConversation);
  if (linkedPhone) query.set('phone', linkedPhone);
  if (linkedNumber) query.set('phoneNumberId', linkedNumber);
  if (state) query.set('state', state);
  if (channelId) query.set('channelId', channelId);
  if (priority) query.set('priority', priority);
  if (label.trim()) query.set('label', label.trim());
  const {
    data: chats,
    error,
    mutate,
  } = useSWR<Chat[]>('/conversations?' + query, fetcher, { refreshInterval: 5000 });
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState('all');
  const filtered = chats?.filter(
    (c) =>
      (filter === 'all' || c.conversation.status === filter) &&
      (c.contact.name.toLowerCase().includes(search.toLowerCase()) ||
        c.contact.phone.includes(search)),
  );
  const active =
    chats?.find((c) => c.conversation.id === selected) ??
    (linkedPhone && chats?.length === 1 ? chats[0] : undefined);
  async function assignSelection() {
    setAssigning(true);
    try {
      await api('/conversations/assign', 'POST', {
        conversations: Object.entries(checked).map(([id, expectedRevision]) => ({
          id,
          expectedRevision,
        })),
        assignedTo: bulkTarget || null,
      });
      setChecked({});
      toast.success('Conversaciones asignadas');
    } catch (e) {
      toast.error((e as Error).message);
      setChecked({});
    } finally {
      setAssigning(false);
      mutate();
      refreshTeam('/members/workload');
    }
  }
  return (
    <>
      <ErrorBox error={error} />
      <div className={cn('inbox-layout', active && 'has-selection')}>
        <section className="conversation-list">
          <div className="list-heading">
            <h2>
              Conversaciones <span>{filtered?.length ?? 0}</span>
            </h2>
            <a
              href="/whatsapp"
              className="text-xs text-primary"
              title="Gestionar números de WhatsApp"
            >
              Números ↗
            </a>
            <Button
              variant="ghost"
              size="icon"
              aria-label="Actualizar conversaciones"
              onClick={() => mutate()}
            >
              <RefreshCw size={15} />
            </Button>
          </div>
          <SavedInboxViews
            filters={{ state, assignment, channelId, priority, label, mode: filter }}
            onApply={(f) => {
              setState(f.state);
              setAssignment(f.assignment);
              setChannelId(f.channelId);
              setPriority(f.priority);
              setLabel(f.label);
              setFilter(f.mode);
              setPage(1);
              setSelected(null);
            }}
          />
          <div className="inbox-filters">
            <select
              aria-label="Filtrar por estado"
              value={state}
              onChange={(e) => setState(e.target.value)}
            >
              <option value="">Todos los estados</option>
              {conversationStates.map(([v, l]) => (
                <option value={v} key={v}>
                  {l}
                </option>
              ))}
            </select>
            <select
              aria-label="Filtrar por responsable"
              value={assignment}
              onChange={(e) => setAssignment(e.target.value)}
            >
              <option value="all">Todo el equipo</option>
              <option value="mine">Mis conversaciones</option>
              <option value="unassigned">Sin asignar</option>
              {workload?.members.map((m) => (
                <option key={m.subject} value={'member:' + m.subject}>
                  {memberLabel(m)}
                </option>
              ))}
            </select>
            <select
              aria-label="Filtrar por canal"
              value={channelId}
              onChange={(e) => setChannelId(e.target.value)}
            >
              <option value="">Todos los canales</option>
              {channels?.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
            <select
              aria-label="Filtrar por prioridad"
              value={priority}
              onChange={(e) => setPriority(e.target.value)}
            >
              <option value="">Todas las prioridades</option>
              {priorities.map(([v, l]) => (
                <option key={v} value={v}>
                  {l}
                </option>
              ))}
            </select>
            <input
              aria-label="Filtrar por etiqueta"
              placeholder="Etiqueta de conversación"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              maxLength={40}
            />
          </div>
          <div className="tabs">
            {[
              ['all', 'Todas'],
              ['human', 'Personas'],
              ['agent', 'Agente'],
            ].map(([id, name]) => (
              <button
                className={filter === id ? 'selected' : ''}
                key={id}
                onClick={() => setFilter(id)}
              >
                {name}
              </button>
            ))}
          </div>
          {(linkedPhone || linkedConversation) && (
            <div className="linked-conversation">
              {linkedConversation ? 'Conversación desde Actividad' : 'Conversación desde WhatsApp'}{' '}
              <button onClick={clearLinkedConversation}>Ver todas</button>
            </div>
          )}
          {managesTeam(me) && (
            <div className="bulk-assignment">
              <label>
                <input
                  type="checkbox"
                  aria-label="Seleccionar conversaciones visibles"
                  checked={
                    !!filtered?.length && filtered.every((c) => c.conversation.id in checked)
                  }
                  onChange={(e) =>
                    setChecked(
                      e.target.checked
                        ? Object.fromEntries(
                            (filtered ?? []).map((c) => [
                              c.conversation.id,
                              c.conversation.revision,
                            ]),
                          )
                        : {},
                    )
                  }
                />
                Seleccionar
              </label>
              {!!Object.keys(checked).length && (
                <>
                  <span>{Object.keys(checked).length}</span>
                  <select
                    aria-label="Responsable de la selección"
                    value={bulkTarget}
                    onChange={(e) => setBulkTarget(e.target.value)}
                  >
                    <option value="">Sin asignar</option>
                    {workload?.members
                      .filter((m) => !m.disabled)
                      .map((m) => (
                        <option key={m.subject} value={m.subject}>
                          {memberLabel(m)}
                        </option>
                      ))}
                  </select>
                  <Button size="sm" disabled={assigning} onClick={assignSelection}>
                    Asignar selección
                  </Button>
                </>
              )}
            </div>
          )}
          <div className="conversation-scroll">
            {!chats ? (
              <Loading />
            ) : !filtered?.length ? (
              <Empty title="Todo al día">
                Las conversaciones entrantes aparecerán aquí. Revisa los filtros si buscas otra
                atención.
              </Empty>
            ) : (
              filtered.map(({ conversation: c, contact, channel, unreadCount }) => (
                <div key={c.id} className="conversation-row">
                  {managesTeam(me) && (
                    <input
                      className="conversation-check"
                      type="checkbox"
                      aria-label={'Seleccionar conversación de ' + contact.name}
                      checked={c.id in checked}
                      onChange={(e) =>
                        setChecked((previous) => {
                          const next = { ...previous };
                          if (e.target.checked) next[c.id] = c.revision;
                          else delete next[c.id];
                          return next;
                        })
                      }
                    />
                  )}
                  <button
                    className={cn(
                      'conversation-card',
                      active?.conversation.id === c.id && 'selected',
                    )}
                    onClick={() => setSelected(c.id)}
                  >
                    <Avatar name={contact.name} />
                    <div>
                      <div className="conversation-title">
                        <strong>
                          <span>{contact.name}</span>
                          {unreadCount > 0 && (
                            <span
                              className="unread-count"
                              aria-label={`${unreadCount} mensajes sin leer`}
                            >
                              {unreadCount}
                            </span>
                          )}
                        </strong>
                        <time>{time(c.updatedAt, me.tenant.timeZone)}</time>
                      </div>
                      <p>{c.lastMessage || c.summary || 'Nueva conversación de WhatsApp'}</p>
                      <div className="conversation-meta">
                        <span className={'tag state-' + c.state}>
                          {conversationStates.find(([v]) => v === c.state)?.[1]}
                        </span>
                        <Badge value={c.status} />
                        {c.priority !== 'normal' && (
                          <span className={'tag priority-' + c.priority}>
                            {priorities.find(([v]) => v === c.priority)?.[1]}
                          </span>
                        )}
                        <small>{channel.name}</small>
                        <span className="conversation-owner">
                          <UserRound size={12} />
                          {workload?.members.find((m) => m.subject === c.assignedTo)?.name ??
                            (c.assignedTo ? 'Responsable asignado' : 'Sin asignar')}
                        </span>
                      </div>
                    </div>
                  </button>
                </div>
              ))
            )}
          </div>
          <div className="list-footer inbox-pagination">
            <button disabled={page === 1} onClick={() => setPage(page - 1)}>
              Anterior
            </button>
            <span>Página {page}</span>
            <button disabled={!chats || chats.length < 100} onClick={() => setPage(page + 1)}>
              Siguiente
            </button>
          </div>
        </section>
        {active ? (
          <ChatPanel
            key={active.conversation.id}
            chat={active}
            me={me}
            onClose={() => {
              setSelected(null);
              clearLinkedConversation();
            }}
            refresh={() => mutate()}
          />
        ) : (
          <section className="chat-placeholder">
            <div className="orbit">
              <MessageCircle size={36} />
              <span>
                <Sparkles size={16} />
              </span>
            </div>
            <h2>Todo comienza con una conversación</h2>
            <p>
              Selecciona un paciente para ver sus mensajes,
              <br />
              consultar el contexto y continuar la atención.
            </p>
            <div>
              <ShieldCheck size={14} /> Información disponible según tus permisos
            </div>
          </section>
        )}
      </div>
    </>
  );
}
function ChatPanel({
  chat,
  me,
  onClose,
  refresh,
}: {
  chat: Chat;
  me: Me;
  onClose: () => void;
  refresh: () => void;
}) {
  const c = chat.conversation;
  const canReply = managesTeam(me) || !c.assignedTo || c.assignedTo === me.subject;
  const {
    data: messages,
    error,
    mutate,
  } = useSWR<Message[]>(`/conversations/${c.id}/messages`, fetcher, { refreshInterval: 4000 });
  const { data: activities, mutate: refreshActivities } = useSWR<Activity[]>(
    `/activities?contactId=${chat.contact.id}`,
    fetcher,
    { refreshInterval: 8000 },
  );
  const [body, setBody] = useState('');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState(false);
  const [patient, setPatient] = useState(false);
  const [details, setDetails] = useState(false);
  const [findMessages, setFindMessages] = useState(false);
  const [messageSearch, setMessageSearch] = useState('');
  const [onlyFiles, setOnlyFiles] = useState(false);
  const visibleMessages = messages?.filter(
    (m) =>
      (!onlyFiles || !!m.mediaId) &&
      (!findMessages ||
        (m.body + ' ' + (m.mediaName ?? ''))
          .toLocaleLowerCase('es')
          .includes(messageSearch.toLocaleLowerCase('es'))),
  );
  const bottom = useRef<HTMLDivElement>(null);
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'nearest' });
  }, [messages?.length, c.id]);
  useEffect(() => setBody(''), [c.id]);
  const lastRead = useRef('');
  const newest = messages?.reduce<Message | undefined>(
    (latest, m) => (!latest || m.receivedAt > latest.receivedAt ? m : latest),
    undefined,
  );
  useEffect(() => {
    if (!newest || lastRead.current === newest.id || document.visibilityState !== 'visible') return;
    lastRead.current = newest.id;
    api(`/conversations/${c.id}/read`, 'POST', { throughMessageId: newest.id })
      .then(refresh)
      .catch(() => {
        lastRead.current = '';
      });
  }, [c.id, newest?.id]);
  async function action(status: string, assignedTo?: string) {
    try {
      await api(`/conversations/${c.id}`, 'PATCH', {
        status,
        assignedTo: assignedTo ?? null,
        expectedRevision: c.revision,
      });
      refresh();
      refreshActivities();
      toast.success('Atención actualizada');
    } catch (e) {
      toast.error((e as Error).message);
    }
  }
  async function send(e: FormEvent) {
    e.preventDefault();
    if (!body.trim()) return;
    setBusy(true);
    try {
      const sent = await api<Message>(`/conversations/${c.id}/messages`, 'POST', { body });
      setBody('');
      mutate();
      refresh();
      if (sent.status !== 'sent')
        toast.error('Envío ' + (labels[sent.status] ?? sent.status) + '. Revisa antes de repetir.');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <section className="chat-panel">
        <div className="chat-header">
          <button className="back-chat" onClick={onClose} aria-label="Volver a conversaciones">
            <ArrowLeft size={18} />
          </button>
          <Avatar name={chat.contact.name} />
          <div>
            <h2>{chat.contact.name}</h2>
            <small>
              <span className="whatsapp-dot" /> +{chat.contact.phone}
            </small>
          </div>
          <div className="chat-tools">
            <Badge value={c.status} />
            <Button
              variant="ghost"
              size="icon"
              aria-label="Buscar mensajes"
              aria-pressed={findMessages}
              onClick={() => setFindMessages(!findMessages)}
            >
              <Search />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label="Mostrar archivos"
              aria-pressed={onlyFiles}
              onClick={() => setOnlyFiles(!onlyFiles)}
            >
              <Paperclip />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label="Ver información del paciente"
              onClick={() => setDetails(!details)}
            >
              <UserRound />
            </Button>
          </div>
        </div>
        <AssignmentControl
          conversation={c}
          me={me}
          onChange={() => {
            refresh();
            refreshActivities();
          }}
        />
        <div className="attention-banner">
          <Sparkles size={16} />
          <span>
            {c.status === 'agent'
              ? 'El agente está atendiendo esta conversación.'
              : c.status === 'closed'
                ? 'La conversación está cerrada.'
                : 'Tu equipo está a cargo. El agente está en pausa.'}
          </span>
          <button
            disabled={!managesTeam(me) && !!c.assignedTo && c.assignedTo !== me.subject}
            onClick={() =>
              action(
                c.status === 'agent' ? 'human' : 'agent',
                c.status === 'agent' ? me.subject : undefined,
              )
            }
          >
            {c.status === 'agent' ? 'Tomar conversación' : 'Activar agente'}
            <ArrowUpRight size={13} />
          </button>
        </div>
        <div className="conversation-actions">
          <PatientClinical
            key={`${c.id}:${c.assignedTo}:${c.status}:${chat.contact.patientId}`}
            chat={chat}
            me={me}
          />
          <ConversationMacros
            chat={chat}
            me={me}
            onChange={() => {
              refresh();
              refreshActivities();
            }}
          />
          {(findMessages || onlyFiles) && (
            <span>{visibleMessages?.length ?? 0} resultados · últimos 200 mensajes</span>
          )}
        </div>
        {findMessages && (
          <div className="message-search">
            <Search size={14} />
            <input
              aria-label="Buscar en esta conversación"
              placeholder="Buscar texto o nombre de archivo…"
              value={messageSearch}
              onChange={(e) => setMessageSearch(e.target.value)}
            />
          </div>
        )}
        <ErrorBox error={error} />
        <div className="messages">
          <div className="day-divider">
            <span>Historial de atención</span>
          </div>
          {visibleMessages?.length === 0 && (
            <p className="hint">
              {onlyFiles
                ? 'No hay archivos en los mensajes cargados.'
                : 'No hay mensajes que coincidan.'}
            </p>
          )}
          {visibleMessages?.map((m) => (
            <div className={cn('message-row', m.sender !== 'patient' && 'outgoing')} key={m.id}>
              {m.sender === 'patient' && <Avatar name={chat.contact.name} />}
              <div className="bubble">
                <div className="bubble-author">
                  {m.sender === 'agent' ? (
                    <>
                      <Sparkles size={12} />
                      Agente de atención
                    </>
                  ) : m.sender === 'human' ? (
                    'Equipo de recepción'
                  ) : (
                    chat.contact.name
                  )}
                </div>
                <p>{m.body}</p>
                {m.mediaId && (
                  <a
                    className="media-link"
                    href={`/api/crm/messages/${m.id}/media`}
                    target="_blank"
                    rel="noreferrer"
                  >
                    <Paperclip size={14} />{' '}
                    {m.type === 'audio' ? 'Escuchar / descargar audio' : 'Abrir archivo'}
                    <ExternalLink size={12} />
                  </a>
                )}
                <div className="message-time">
                  <time>{time(m.createdAt, me.tenant.timeZone)}</time>
                  {m.sender !== 'patient' && (
                    <span title={labels[m.status]}>
                      {m.status === 'read' ? (
                        <CheckCheck size={13} />
                      ) : m.status === 'sent' || m.status === 'delivered' ? (
                        <Check size={13} />
                      ) : (
                        labels[m.status]
                      )}
                    </span>
                  )}
                </div>
              </div>
            </div>
          ))}
          <div ref={bottom} />
        </div>
        <form className="composer" onSubmit={send}>
          <div className="composer-top">
            <span>
              <UserRound size={13} /> Respuesta del equipo
            </span>
            <button type="button" onClick={() => setNote(true)}>
              <FileText size={13} /> Nota interna
            </button>
          </div>
          <SavedReplies me={me} onInsert={(text) => setBody(text)} />
          <textarea
            aria-label="Mensaje al paciente"
            rows={2}
            placeholder="Escribe un mensaje para el paciente…"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            maxLength={4000}
          />
          <div className="composer-bottom">
            <label className="attach-button" title="Adjuntar archivo">
              <Paperclip size={18} />
              <span className="sr-only">Adjuntar archivo</span>
              <input
                type="file"
                accept=".pdf,.jpg,.jpeg,.png,.ogg,.mp3,.mp4"
                className="sr-only"
                disabled={busy || !canReply}
                onChange={async (e) => {
                  const file = e.target.files?.[0];
                  if (!file) return;
                  const form = new FormData();
                  form.append('file', file);
                  setBusy(true);
                  try {
                    const sent = await api<Message>(`/conversations/${c.id}/media`, 'POST', form);
                    mutate();
                    refresh();
                    toast[sent.status === 'sent' ? 'success' : 'error'](
                      labels[sent.status] ?? sent.status,
                    );
                  } catch (err) {
                    toast.error((err as Error).message);
                  } finally {
                    setBusy(false);
                    e.target.value = '';
                  }
                }}
              />
            </label>
            <small>
              {canReply
                ? 'Al responder, tomarás la conversación.'
                : 'Solicita la transferencia al responsable para responder.'}
            </small>
            <Button type="submit" size="sm" disabled={busy || !body.trim() || !canReply}>
              {busy ? <Loader2 className="animate-spin" /> : <Send />}Enviar
            </Button>
          </div>
        </form>
      </section>
      <aside className={cn('contact-panel', details && 'mobile-visible')}>
        <div className="contact-panel-title">
          CONTEXTO DEL PACIENTE
          <button onClick={() => setDetails(false)} aria-label="Cerrar detalles">
            <X size={14} />
          </button>
        </div>
        <div className="contact-identity">
          <Avatar name={chat.contact.name} large />
          <h3>{chat.contact.name}</h3>
          <p>+{chat.contact.phone}</p>
          {chat.contact.tags && <span className="tag">{chat.contact.tags}</span>}
        </div>
        <WorkflowControls
          chat={chat}
          onChange={() => {
            refresh();
            refreshActivities();
          }}
        />
        <PatientAppointments contact={chat.contact} me={me} />
        <ReminderConsent contactId={chat.contact.id} />
        <CustomerCrm
          chat={chat}
          onChange={() => {
            refresh();
            refreshActivities();
          }}
        />
        <div className="detail-section">
          <div className="section-heading">
            <h4>Expediente del hospital</h4>
            <Link2 size={14} />
          </div>
          <p>
            {chat.contact.patientId
              ? 'Paciente vinculado. El teléfono se verifica en cada consulta.'
              : 'Vincula el expediente para consultar agenda y recetas.'}
          </p>
          <Button variant="outline" size="sm" className="w-full" onClick={() => setPatient(true)}>
            {chat.contact.patientId ? 'Ver paciente vinculado' : 'Vincular paciente'}
          </Button>
        </div>
        <div className="detail-section">
          <div className="section-heading">
            <h4>Historial del cliente</h4>
            <button aria-label="Agregar nota" onClick={() => setNote(true)}>
              <Plus size={16} />
            </button>
          </div>
          <div className="timeline small">
            {activities?.slice(0, 16).map((a) => (
              <div key={a.id}>
                <span className="timeline-dot" />
                <strong>{a.actor}</strong>
                <p>{a.kind.startsWith('proposal') ? 'Propuesta de agenda registrada' : a.body}</p>
                <small>
                  {date(a.createdAt)} · {time(a.createdAt)}
                </small>
              </div>
            ))}
          </div>
        </div>
      </aside>
      <FormDialog
        title="Nota interna"
        description="Visible para tu equipo; no se envía por WhatsApp."
        open={note}
        onClose={() => setNote(false)}
        fields={[{ name: 'body', label: 'Nota de seguimiento', type: 'textarea', required: true }]}
        onSubmit={async (v) => {
          await api('/activities', 'POST', {
            ...v,
            contactId: chat.contact.id,
            conversationId: c.id,
          });
          refreshActivities();
        }}
      />
      <PatientLink
        key={chat.contact.id}
        contact={chat.contact}
        open={patient}
        onClose={() => setPatient(false)}
        onChange={refresh}
      />
    </>
  );
}
function ContactsView({ search }: { search: string }) {
  const [page, setPage] = useState(1);
  useEffect(() => setPage(1), [search]);
  const { data, error, mutate } = useSWR<Contact[]>(
    '/contacts?q=' + encodeURIComponent(search) + '&page=' + page,
    fetcher,
  );
  const [create, setCreate] = useState(false);
  const [edit, setEdit] = useState<Contact | null>(null);
  const [patientContact, setPatientContact] = useState<Contact | null>(null);
  return (
    <section className="content-card">
      <div className="card-toolbar">
        <h2>
          Contactos <span>{data?.length ?? 0}</span>
        </h2>
        <Button size="sm" onClick={() => setCreate(true)}>
          <Plus />
          Nuevo contacto
        </Button>
      </div>
      <ErrorBox error={error} />
      {!data && !error ? (
        <Loading />
      ) : data?.length ? (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Paciente / contacto</th>
                <th>Teléfono</th>
                <th>Correo</th>
                <th>Etiqueta</th>
                <th>Relación / expediente</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.map((c) => (
                <tr key={c.id}>
                  <td>
                    <div className="name-cell">
                      <Avatar name={c.name} />
                      <strong>{c.name}</strong>
                    </div>
                  </td>
                  <td>+{c.phone}</td>
                  <td>{c.email || '—'}</td>
                  <td>{c.tags ? <span className="tag">{c.tags}</span> : '—'}</td>
                  <td>
                    <span className={c.patientId ? 'linked' : 'muted'}>
                      {c.isCustomer ? 'Cliente' : 'Contacto'} ·{' '}
                      {c.patientId ? 'Vinculado' : 'Por vincular'}
                    </span>
                  </td>
                  <td>
                    <Button variant="outline" size="sm" onClick={() => setPatientContact(c)}>
                      {c.patientId ? 'Ver vínculo' : 'Vincular paciente'}
                    </Button>
                    <CustomerCommercial
                      contact={c}
                      onChange={() => {
                        mutate();
                      }}
                    />
                    <Button variant="ghost" size="sm" onClick={() => setEdit(c)}>
                      Editar
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty icon={Users} title="Tu próxima relación empieza aquí">
          Agrega un contacto o recibe su primer mensaje por WhatsApp.
        </Empty>
      )}
      <div className="card-toolbar">
        <Button variant="outline" size="sm" disabled={page === 1} onClick={() => setPage(page - 1)}>
          Anterior
        </Button>
        <span className="hint">Página {page} · hasta 100 contactos</span>
        <Button
          variant="outline"
          size="sm"
          disabled={!data || data.length < 100}
          onClick={() => setPage(page + 1)}
        >
          Siguiente
        </Button>
      </div>
      {patientContact && (
        <PatientLink
          key={patientContact.id}
          contact={patientContact}
          open
          onClose={() => setPatientContact(null)}
          onChange={() => {
            mutate();
          }}
        />
      )}
      <FormDialog
        title={edit ? 'Editar contacto' : 'Nuevo contacto'}
        open={create || !!edit}
        onClose={() => {
          setCreate(false);
          setEdit(null);
        }}
        fields={[
          { name: 'name', label: 'Nombre completo', required: true, value: edit?.name },
          {
            name: 'phone',
            label: 'Teléfono con código de país',
            required: true,
            value: edit?.phone,
            placeholder: '50370000000',
          },
          { name: 'email', label: 'Correo electrónico', type: 'email', value: edit?.email },
          { name: 'tags', label: 'Etiquetas', value: edit?.tags },
        ]}
        onSubmit={async (v) => {
          await api('/contacts' + (edit ? '/' + edit.id : ''), edit ? 'PUT' : 'POST', v);
          mutate();
        }}
      />
    </section>
  );
}
function OpportunitiesView({ search }: { search: string }) {
  const { data, error, mutate } = useSWR<Opportunity[]>('/opportunities', fetcher);
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const [create, setCreate] = useState(false);
  return (
    <>
      <div className="section-toolbar">
        <span className="hint">Seguimientos de la atención y oportunidades del hospital</span>
        <Button size="sm" onClick={() => setCreate(true)}>
          <Plus />
          Nuevo seguimiento
        </Button>
      </div>
      <ErrorBox error={error} />
      <div className="kanban">
        {stages.map(([id, label], index) => {
          const rows = data?.filter(
            (o) => o.stage === id && o.title.toLowerCase().includes(search.toLowerCase()),
          );
          return (
            <section className="kanban-column" key={id}>
              <div className="kanban-heading">
                <span
                  style={{
                    background: ['#92a0b0', '#d9b567', '#7badbe', '#82b7a3', '#b8b8b8'][index],
                  }}
                />
                <h3>{label}</h3>
                <b>{rows?.length ?? 0}</b>
              </div>
              {rows?.map((o) => (
                <article className="opportunity-card" key={o.id}>
                  <span className="eyebrow">ATENCIÓN AL PACIENTE</span>
                  <h4>{o.title}</h4>
                  <div className="opportunity-person">
                    <UserRound size={13} />
                    {contacts?.find((c) => c.id === o.contactId)?.name ?? 'Contacto'}
                  </div>
                  {o.value > 0 && (
                    <strong className="amount">
                      ${o.value.toFixed(2)}{' '}
                      <small>
                        {o.hospitalPurchaseId
                          ? 'Pagado'
                          : o.hospitalQuote
                            ? 'Cotizado por Hospital'
                            : 'Estimado'}
                      </small>
                    </strong>
                  )}
                  <OpportunityCommercial
                    opportunity={o}
                    onChange={() => {
                      mutate();
                    }}
                  />
                  <select
                    aria-label={'Etapa de ' + o.title}
                    value={o.stage}
                    onChange={async (e) => {
                      try {
                        await api(`/opportunities/${o.id}`, 'PATCH', { stage: e.target.value });
                        mutate();
                      } catch (err) {
                        toast.error((err as Error).message);
                      }
                    }}
                  >
                    {stages.map(([key, value]) => (
                      <option value={key} key={key}>
                        {value}
                      </option>
                    ))}
                  </select>
                </article>
              ))}
              <button className="kanban-add" onClick={() => setCreate(true)}>
                <Plus size={14} />
                Agregar seguimiento
              </button>
            </section>
          );
        })}
      </div>
      <FormDialog
        title="Nuevo seguimiento"
        open={create}
        onClose={() => setCreate(false)}
        fields={[
          { name: 'title', label: 'Título', required: true },
          {
            name: 'contactId',
            label: 'Contacto',
            required: true,
            options: contacts?.map((c) => ({ value: c.id, label: c.name })) ?? [],
          },
          { name: 'value', label: 'Valor estimado (USD)', type: 'number', value: '0' },
          {
            name: 'stage',
            label: 'Etapa',
            required: true,
            value: 'new',
            options: stages.map(([value, label]) => ({ value, label })),
          },
        ]}
        onSubmit={async (v) => {
          await api('/opportunities', 'POST', { ...v, value: Number(v.value) });
          mutate();
        }}
      />
    </>
  );
}
type AgendaRow = {
  appointmentId: string;
  patientId: string;
  displayName: string;
  scheduledStart: string;
  durationMinutes: number;
  status: string;
  clinicianName: string;
  placeName: string;
  overlaps: boolean;
};
type Availability = {
  professionals: {
    clinicianId: string;
    clinicianName: string;
    defaultDurationMinutes: number;
    days: {
      slots: { startsAt: string; durationMinutes: number; offered: boolean; takenBy: number }[];
    }[];
  }[];
};
function CalendarView({ search, me }: { search: string; me: Me }) {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: me.tenant.timeZone }).format(
    new Date(),
  );
  const [day, setDay] = useState(today);
  const [create, setCreate] = useState(false);
  const [modify, setModify] = useState<AgendaRow | null>(null);
  const [doctor, setDoctor] = useState('');
  const [cancel, setCancel] = useState<AgendaRow | null>(null);
  const appointmentBusy = useRef(false);
  const appointmentRequest = useRef<{ body: string; key: string } | null>(null);
  async function saveAppointment(input: unknown) {
    const body = JSON.stringify(input);
    if (appointmentRequest.current?.body !== body)
      appointmentRequest.current = { body, key: crypto.randomUUID() };
    return api<{ overlaps?: boolean }>(
      '/hospital/appointments',
      'POST',
      input,
      appointmentRequest.current.key,
    );
  }
  const [savingAppointment, setSavingAppointment] = useState(false);
  const { data, error, mutate } = useSWR<{ rows: AgendaRow[] }>(
    `/hospital/agenda?date=${day}`,
    fetcher,
    { shouldRetryOnError: false },
  );
  const {
    data: availability,
    error: availabilityError,
    isLoading: availabilityLoading,
  } = useSWR<Availability>(
    create || modify ? `/hospital/availability?date=${day}` : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const options = availability?.professionals.find((p) => p.clinicianId === doctor);
  const slots =
    options?.days.flatMap((d) => d.slots).filter((s) => s.offered && s.takenBy === 0) ?? [];
  const rows = data?.rows.filter(
    (r) =>
      r.displayName.toLowerCase().includes(search.toLowerCase()) ||
      r.clinicianName.toLowerCase().includes(search.toLowerCase()),
  );
  const notConfigured =
    error instanceof Error &&
    ((error as Error & { code?: string }).code === 'hospital.not_configured' ||
      error.message.includes('hospital.not_configured'));
  const shift = (n: number) => {
    const d = new Date(day + 'T12:00:00');
    d.setDate(d.getDate() + n);
    setDay(d.toISOString().slice(0, 10));
  };
  return (
    <section className="content-card">
      <div className="card-toolbar">
        <div className="calendar-date">
          <Button variant="outline" size="sm" onClick={() => setDay(today)}>
            Hoy
          </Button>
          <Button variant="ghost" size="icon" aria-label="Día anterior" onClick={() => shift(-1)}>
            <ChevronLeft />
          </Button>
          <input
            aria-label="Fecha de la agenda"
            type="date"
            value={day}
            onChange={(e) => setDay(e.target.value)}
          />
          <Button variant="ghost" size="icon" aria-label="Día siguiente" onClick={() => shift(1)}>
            <ChevronRight />
          </Button>
        </div>
        <Button size="sm" disabled={!data || !!error} onClick={() => setCreate(true)}>
          <Plus />
          Nueva cita
        </Button>
      </div>
      <div className="calendar-note">
        <CalendarDays size={15} /> Agenda del hospital · {me.tenant.timeZone}
        <span>Todos los doctores</span>
      </div>
      <ErrorBox error={notConfigured ? null : error} />
      {!data && !error ? (
        <Loading />
      ) : !rows?.length ? (
        <Empty
          icon={CalendarDays}
          title={error ? 'Revisa la conexión de tu hospital' : 'Un día por organizar'}
        >
          {notConfigured ? (
            <>
              <span>Estás en «{me.tenant.name}», que aún no tiene una agenda conectada.</span>
              <span className="block mt-2">
                Revisa la conexión o entra con tu cuenta del Hospital desde «Mi hospital».
              </span>
              <Button className="mt-4" variant="outline" asChild>
                <a href="/?view=hospital">Revisar conexión con Hospital</a>
              </Button>
            </>
          ) : error ? (
            'No pudimos consultar Hospital. Revisa la conexión y vuelve a intentar.'
          ) : (
            'No hay citas para esta fecha.'
          )}
        </Empty>
      ) : (
        <div className="agenda-list">
          {rows.map((r) => (
            <article className="agenda-row" key={r.appointmentId}>
              <div className="agenda-time">
                <strong>{time(r.scheduledStart, me.tenant.timeZone)}</strong>
                <span>{r.durationMinutes} min</span>
              </div>
              <div className="agenda-accent" />
              <Avatar name={r.displayName} />
              <div className="agenda-person">
                <h3>{r.displayName}</h3>
                <p>
                  <Stethoscope size={13} />
                  {r.clinicianName} · {r.placeName}
                </p>
              </div>
              <span className="tag">
                {(
                  {
                    booked: 'Programada',
                    'cancelled-by-patient': 'Cancelada por paciente',
                    'cancelled-by-clinic': 'Cancelada por hospital',
                    arrived: 'Presente',
                    fulfilled: 'Atendida',
                    'no-show': 'No asistió',
                    'not-recorded': 'Sin registrar',
                    'entered-in-error': 'Registrada por error',
                  } as Record<string, string>
                )[r.status] ?? r.status}
              </span>
              {r.overlaps && <span className="error">Solapamiento</span>}
              <Button
                variant="ghost"
                size="sm"
                disabled={r.status !== 'booked' && r.status !== 'not-recorded'}
                onClick={() => setModify(r)}
              >
                Reprogramar
              </Button>
              <Button
                variant="ghost"
                size="sm"
                disabled={r.status !== 'booked' && r.status !== 'not-recorded'}
                onClick={() => setCancel(r)}
              >
                Cancelar
              </Button>
            </article>
          ))}
        </div>
      )}
      <Dialog
        open={create || !!modify}
        onOpenChange={(v) => {
          if (!v) {
            setCreate(false);
            setModify(null);
          }
        }}
      >
        <DialogContent>
          <DialogTitle className="dialog-title">
            {modify ? 'Reprogramar cita' : 'Nueva cita'}
          </DialogTitle>
          <DialogDescription className="dialog-description">
            Se registrará en la agenda del hospital.
          </DialogDescription>
          <form
            className="dialog-form"
            onSubmit={async (e) => {
              e.preventDefault();
              const f = new FormData(e.currentTarget);
              const slot = slots.find((s) => s.startsAt === f.get('startsAt'));
              if (!slot || appointmentBusy.current) return;
              appointmentBusy.current = true;
              setSavingAppointment(true);
              try {
                const result = await saveAppointment({
                  action: modify ? 'reschedule' : 'create',
                  appointmentId: modify?.appointmentId,
                  contactId: f.get('contactId'),
                  doctorId: doctor,
                  startsAt: slot.startsAt,
                  durationMinutes: slot.durationMinutes,
                });
                toast[result.overlaps ? 'warning' : 'success'](
                  result.overlaps
                    ? 'Cita registrada con solapamiento: verifica la agenda.'
                    : 'Agenda actualizada',
                );
                appointmentRequest.current = null;
                setCreate(false);
                setModify(null);
                mutate();
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                appointmentBusy.current = false;
                setSavingAppointment(false);
              }
            }}
          >
            <label>
              Paciente vinculado
              <select
                name="contactId"
                required
                defaultValue={contacts?.find((c) => c.patientId === modify?.patientId)?.id ?? ''}
              >
                <option value="">Selecciona un paciente</option>
                {contacts
                  ?.filter((c) => c.patientId && (!modify || c.patientId === modify.patientId))
                  .map((c) => (
                    <option value={c.id} key={c.id}>
                      {c.name}
                    </option>
                  ))}
              </select>
            </label>
            <label>
              Fecha
              <input type="date" value={day} onChange={(e) => setDay(e.target.value)} required />
            </label>
            <ErrorBox error={availabilityError} />
            {availabilityLoading && <p className="hint">Consultando horarios del hospital…</p>}
            {availability && !availability.professionals.length && (
              <p className="hint">
                El hospital no tiene doctores disponibles en el padrón para esta consulta.
              </p>
            )}
            <label>
              Doctor
              <select value={doctor} onChange={(e) => setDoctor(e.target.value)} required>
                <option value="">Selecciona un doctor</option>
                {availability?.professionals.map((p) => (
                  <option value={p.clinicianId} key={p.clinicianId}>
                    {p.clinicianName}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Horario disponible
              <select name="startsAt" required>
                <option value="">Selecciona un horario</option>
                {slots.map((s) => (
                  <option key={s.startsAt} value={s.startsAt}>
                    {time(s.startsAt, me.tenant.timeZone)} · {s.durationMinutes} min
                  </option>
                ))}
              </select>
            </label>
            {doctor && availability && !slots.length && (
              <p className="hint">
                No hay cupos disponibles para este doctor en la fecha seleccionada. Cambia el día o
                el doctor.
              </p>
            )}
            <p className="hint">
              Si un paciente no aparece, vincula primero su expediente desde la conversación.
            </p>
            <Button type="submit" disabled={!slots.length || savingAppointment}>
              Confirmar cita
            </Button>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog
        open={!!cancel}
        onOpenChange={(v) => {
          if (!v) setCancel(null);
        }}
      >
        <DialogContent>
          <DialogTitle className="dialog-title">Cancelar cita</DialogTitle>
          <DialogDescription className="dialog-description">
            Se cancelará la cita de {cancel?.displayName} en la agenda del hospital.
          </DialogDescription>
          <Button
            variant="destructive"
            disabled={savingAppointment}
            onClick={async () => {
              if (appointmentBusy.current) return;
              const c = contacts?.find((x) => x.patientId === cancel?.patientId);
              if (!c) {
                toast.error('Vincula este paciente a un contacto antes de cancelar.');
                return;
              }
              appointmentBusy.current = true;
              setSavingAppointment(true);
              try {
                await saveAppointment({
                  action: 'cancel',
                  appointmentId: cancel?.appointmentId,
                  contactId: c.id,
                  doctorId: '00000000-0000-0000-0000-000000000000',
                  startsAt: cancel?.scheduledStart,
                  durationMinutes: cancel?.durationMinutes,
                });
                appointmentRequest.current = null;
                toast.success('Cita cancelada');
                setCancel(null);
                mutate();
              } catch (e) {
                toast.error((e as Error).message);
              } finally {
                appointmentBusy.current = false;
                setSavingAppointment(false);
              }
            }}
          >
            Confirmar cancelación
          </Button>
        </DialogContent>
      </Dialog>
    </section>
  );
}
function AgentView({ me }: { me: Me }) {
  const { data } = useSWR<Activity[]>('/activities', fetcher, { refreshInterval: 5000 });
  const admin = me.role === 'admin';
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const [contactId, setContactId] = useState('');
  const [query, setQuery] = useState('');
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <>
      <section className="agent-hero">
        <div className="agent-hero-icon">
          <Sparkles size={34} />
        </div>
        <div>
          <span className="eyebrow">TU EQUIPO, CON MÁS CAPACIDAD</span>
          <h2>Agente de atención</h2>
          <p>Responde preguntas, acompaña la agenda y entrega la atención a la persona correcta.</p>
          <Badge value={me.tenant.agentEnabled ? 'agent' : 'human'} />
        </div>
        <div className="agent-hero-detail">
          <span>
            <ShieldCheck size={16} /> Contexto de tu hospital
          </span>
          <span>
            <Users size={16} /> Transferencia a humanos
          </span>
          <span>
            <ActivityIcon size={16} /> Historial de cada acción
          </span>
        </div>
      </section>
      <div className="agent-columns">
        <section className="content-card">
          <div className="card-toolbar">
            <h2>
              <Sparkles size={17} /> Asistente del equipo
            </h2>
          </div>
          <div className="assistant-intro">
            <h3>¿En qué trabajamos hoy?</h3>
            <p>
              Consulta las métricas o selecciona un contacto para guardar una nota o crear un
              seguimiento.
            </p>
            <label className="mt-5">
              Contacto para acciones
              <select
                aria-label="Contacto para acciones"
                value={contactId}
                onChange={(e) => setContactId(e.target.value)}
              >
                <option value="">Solo consultar métricas</option>
                {contacts?.map((c) => (
                  <option value={c.id} key={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
            <p className="hint mt-2">La ficha del contacto seleccionado permanece en el CRM.</p>
            <div className="suggestion-chips">
              {[
                'Resume las conversaciones pendientes',
                '¿Qué oportunidades necesitan seguimiento?',
                'Crea un seguimiento para el contacto seleccionado',
              ].map((s) => (
                <button key={s} onClick={() => setQuery(s)}>
                  {s}
                  <ArrowUpRight size={13} />
                </button>
              ))}
            </div>
            {answer && <div className="assistant-answer">{answer}</div>}
            <form
              className="assistant-form"
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                try {
                  const r = await api<{ answer: string }>('/assistant', 'POST', {
                    message: query,
                    contactId: contactId || null,
                  });
                  setAnswer(r.answer);
                  setQuery('');
                } catch (e) {
                  toast.error((e as Error).message);
                } finally {
                  setBusy(false);
                }
              }}
            >
              <textarea
                aria-label="Pregunta al asistente"
                rows={3}
                placeholder="Pregunta sobre la atención de tu hospital…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                maxLength={2000}
              />
              <Button disabled={busy || !query.trim()}>
                {busy ? <Loader2 className="animate-spin" /> : <Send />}
                {busy ? 'Consultando…' : 'Consultar al agente'}
              </Button>
            </form>
          </div>
        </section>
        <section className="content-card">
          <div className="card-toolbar">
            <h2>Acciones recientes</h2>
          </div>
          <div className="timeline small agent-timeline">
            {data
              ?.filter((a) => a.actor === 'Agente' || a.kind === 'handoff')
              .slice(0, 10)
              .map((a) => (
                <div key={a.id}>
                  <span className="timeline-dot" />
                  <strong>{a.actor}</strong>
                  <p>{a.kind.startsWith('proposal') ? 'Propuesta de cita registrada' : a.body}</p>
                  <small>
                    {date(a.createdAt)} · {time(a.createdAt)}
                  </small>
                </div>
              ))}
          </div>
          {!data?.some((a) => a.actor === 'Agente' || a.kind === 'handoff') && (
            <Empty icon={Sparkles} title="Listo para acompañar">
              Las acciones aparecerán al habilitar la atención automática.
            </Empty>
          )}
        </section>
      </div>
      {admin && <AppointmentReminders me={me} />}
      {admin && (
        <div className="inline-note">
          <Settings size={16} />
          Personaliza las instrucciones y habilita el agente desde Configuración.
        </div>
      )}
    </>
  );
}
type SettingsData = {
  name: string;
  guide: string;
  timeZone: string;
  agentEnabled: boolean;
  googleCalendarId?: string;
  googleConnected: boolean;
  hospitalConfigured: boolean;
  kapsoConfigured: boolean;
  aiConfigured: boolean;
  sendEnabled: boolean;
  manualSendEnabled?: boolean;
  aiModel: string;
};
function SettingsView({ me }: { me: Me }) {
  const { mutate: globalMutate } = useSWRConfig();
  const { data, error, mutate } = useSWR<SettingsData>('/settings', fetcher);
  const { data: channels, mutate: refreshChannels } = useSWR<Channel[]>('/channels', fetcher);
  const [saving, setSaving] = useState(false);
  if (me.role !== 'admin')
    return (
      <Empty icon={ShieldCheck} title="Solo administradores">
        Tu administrador gestiona las conexiones y la guía del negocio.
      </Empty>
    );
  return (
    <>
      <ErrorBox error={error} />
      {data ? (
        <div>
          <HospitalConnection />
          <div className="settings-grid">
            <section className="content-card">
              <div className="card-toolbar">
                <h2>Tu hospital y su guía de atención</h2>
              </div>
              <form
                key={data.name + data.agentEnabled}
                className="settings-form"
                onSubmit={async (e) => {
                  e.preventDefault();
                  const f = new FormData(e.currentTarget);
                  setSaving(true);
                  try {
                    await api('/settings', 'PUT', {
                      name: f.get('name'),
                      timeZone: f.get('timeZone'),
                      guide: f.get('guide'),
                      agentEnabled: f.get('agentEnabled') === 'on',
                    });
                    mutate();
                    globalMutate('/me');
                    toast.success('Configuración guardada');
                  } catch (err) {
                    toast.error((err as Error).message);
                  } finally {
                    setSaving(false);
                  }
                }}
              >
                <div className="form-grid">
                  <label>
                    Nombre del hospital
                    <input
                      name="name"
                      defaultValue={data.name}
                      required
                      readOnly={data.hospitalConfigured}
                    />
                  </label>
                  <label>
                    Zona horaria
                    <input
                      name="timeZone"
                      defaultValue={data.timeZone}
                      required
                      readOnly={data.hospitalConfigured}
                    />
                  </label>
                </div>
                {data.hospitalConfigured && (
                  <p className="hint">
                    El nombre y la zona horaria vienen de Hospital y se actualizan desde allí.
                  </p>
                )}
                <label>
                  Guía de atención
                  <textarea
                    rows={9}
                    name="guide"
                    defaultValue={data.guide}
                    placeholder="Horarios, servicios, instrucciones de atención y reglas de derivación…"
                    maxLength={30000}
                  />
                </label>
                <p className="hint">
                  El agente utiliza esta guía junto con las herramientas autorizadas del hospital.
                  Las decisiones clínicas se derivan al doctor.
                </p>
                <label className="toggle-row">
                  <div>
                    <strong>Atención automática</strong>
                    <small>Permite al agente atender conversaciones habilitadas.</small>
                  </div>
                  <input type="checkbox" name="agentEnabled" defaultChecked={data.agentEnabled} />
                </label>
                <div className="dialog-actions">
                  <Button disabled={saving}>
                    {saving ? <Loader2 className="animate-spin" /> : <Check />}Guardar cambios
                  </Button>
                </div>
              </form>
            </section>
            <section className="content-card">
              <div className="card-toolbar">
                <h2>Conexiones</h2>
              </div>
              <div className="integration-list">
                {[
                  [HeartPulse, 'Hospital', data.hospitalConfigured, 'Agenda y expediente'],
                  [
                    MessageCircle,
                    'WhatsApp',
                    data.kapsoConfigured,
                    data.sendEnabled
                      ? 'Envío habilitado'
                      : data.manualSendEnabled
                        ? 'Envío manual habilitado'
                        : 'Envío en pausa',
                  ],
                  [Sparkles, 'Agente de atención', data.aiConfigured, 'Respuestas y seguimiento'],
                  [CalendarDays, 'Google Calendar', data.googleConnected, 'Agenda compartida'],
                ].map(([Icon, title, ok, sub], i) => {
                  const I = Icon as typeof Inbox;
                  return (
                    <div className="integration-item" key={i}>
                      <span>
                        <I size={21} />
                      </span>
                      <div>
                        <strong>{title as string}</strong>
                        <small>{sub as string}</small>
                      </div>
                      <span
                        className={cn('connection-dot', ok && 'connected')}
                        title={ok ? 'Configurado' : 'Pendiente'}
                      />
                    </div>
                  );
                })}
                <p className="hint">
                  Tu cuenta del Hospital reúne a tu equipo, pacientes y agenda. Google y WhatsApp te
                  pedirán autorización al conectarlos.
                </p>
                <GoogleCalendarConnection
                  connected={data.googleConnected}
                  selected={data.googleCalendarId}
                  onChange={() => mutate()}
                />
              </div>
            </section>
            <section className="content-card wide">
              <div className="card-toolbar">
                <h2>Números de WhatsApp</h2>
                <div className="button-group">
                  <Button asChild size="sm">
                    <a href="/whatsapp">
                      <Plus />
                      Agregar mi número
                    </a>
                  </Button>
                </div>
              </div>
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
                    {channels?.map((c) => (
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
                          <ChannelConnection id={c.id} />
                        </td>
                        <td>
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={async () => {
                              try {
                                await api('/channels/' + c.id, 'PATCH', { enabled: !c.enabled });
                                refreshChannels();
                                toast.success('Canal actualizado');
                              } catch (e) {
                                toast.error((e as Error).message);
                              }
                            }}
                          >
                            {c.enabled ? <Pause /> : <Play />}
                            {c.enabled ? 'Pausar' : 'Habilitar'}
                          </Button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
            <section className="content-card wide">
              <div className="card-toolbar">
                <h2>Equipo de atención</h2>
                <a href="/?view=team">Administrar equipo y conversaciones ↗</a>
              </div>
            </section>
          </div>
        </div>
      ) : (
        !error && <Loading />
      )}
    </>
  );
}
