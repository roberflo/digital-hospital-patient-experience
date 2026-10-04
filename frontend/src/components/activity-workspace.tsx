'use client';
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import {
  Activity,
  ArrowUpRight,
  Bot,
  CalendarDays,
  FileText,
  RefreshCw,
  Search,
  Stethoscope,
  UserRound,
  Users,
  X,
} from 'lucide-react';
import { fetcher, type Me } from '@/lib/api';
import { Button } from './ui/button';

type Event = {
  id: string;
  kind: string;
  category: string;
  actor: string;
  actorRole: string;
  careType: string;
  body: string;
  createdAt: string;
  contactId?: string;
  patientName?: string;
  conversationId?: string;
  conversationState?: string;
  channelName?: string;
  deliveryStatus?: string;
};
type Feed = {
  items: Event[];
  total: number;
  page: number;
  pageSize: number;
  careCounts: Record<string, number>;
};
const careOptions = [
  ['', 'Toda la atención'],
  ['ai', 'Agente IA'],
  ['reception', 'Recepción humana'],
  ['doctor', 'Doctores'],
  ['team', 'Supervisión y administración'],
  ['external', 'WhatsApp externo'],
  ['system', 'Sistema'],
  ['unknown', 'Histórico sin rol'],
];
const categories = [
  ['', 'Todas las acciones'],
  ['response', 'Respuestas al paciente'],
  ['handoff', 'Transferencias y asignaciones'],
  ['note', 'Notas de seguimiento'],
  ['appointment', 'Gestión de citas'],
  ['clinical', 'Consulta de Hospital'],
  ['review', 'Requiere revisión'],
  ['ai_action', 'Acciones del agente'],
  ['crm', 'Actualizaciones CRM'],
];
const titles: Record<string, string> = {
  response: 'Respondió al paciente',
  handoff: 'Cambió la atención de la conversación',
  assignment: 'Asignó la conversación',
  note: 'Registró una nota de seguimiento',
  appointment: 'Gestionó una cita',
  appointment_reminder: 'Recordatorio de cita por WhatsApp',
  reminder_consent: 'Actualizó la autorización de recordatorios',
  proposal: 'Gestionó una confirmación de agenda',
  clinical_review: 'Consultó Hospital',
  error: 'La atención requiere revisión',
  delivery: 'Detectó un problema de entrega',
  agent_tool: 'Realizó una acción automática',
  handoff_offer: 'Ofreció pasar con una persona',
  guard: 'Retuvo una respuesta automática',
  agent_provider: 'El proveedor de IA no respondió',
  patient_registered: 'Registró al paciente en Hospital',
  prescription_delivered: 'Entregó una receta emitida',
  conversation_state: 'Actualizó el estado de la conversación',
  opportunity: 'Actualizó el seguimiento',
  purchase: 'Registró una compra pagada en Hospital',
  customer_converted: 'Contacto convertido en cliente',
  contact_updated: 'Actualizó la ficha del paciente',
  contact_created: 'Registró al paciente',
  conversation_created: 'Abrió una conversación',
  macro: 'Aplicó una acción rápida',
};
const statuses: Record<string, string> = {
  sending: 'En proceso',
  sent: 'Enviado a WhatsApp',
  delivered: 'Entregado',
  read: 'Leído',
  failed: 'Envío fallido',
  uncertain: 'Entrega sin confirmar',
};
const states: Record<string, string> = {
  open: 'Abierta',
  pending: 'Pendiente',
  snoozed: 'Pospuesta',
  resolved: 'Resuelta',
};
function ActorIcon({ type }: { type: string }) {
  return type === 'ai' ? (
    <Bot size={19} />
  ) : type === 'doctor' ? (
    <Stethoscope size={19} />
  ) : type === 'reception' ? (
    <UserRound size={19} />
  ) : (
    <Users size={19} />
  );
}
function EventCard({
  event,
  zone,
  onPatient,
}: {
  event: Event;
  zone: string;
  onPatient: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const deliveryProblem = ['failed', 'uncertain'].includes(event.deliveryStatus ?? '');
  return (
    <article className={`care-event care-${event.careType}`}>
      <div className="care-event-icon">
        <ActorIcon type={event.careType} />
      </div>
      <div className="care-event-content">
        <div className="care-event-top">
          <span className="care-role">
            {careOptions.find(([key]) => key === event.careType)?.[1] ?? 'Histórico sin rol'}
          </span>
          <time dateTime={event.createdAt}>
            {new Date(event.createdAt).toLocaleTimeString('es-SV', {
              timeZone: zone,
              hour: '2-digit',
              minute: '2-digit',
            })}
          </time>
        </div>
        <h3>{event.patientName ?? 'Actividad general del hospital'}</h3>
        <p className="care-event-action">
          <strong>{event.actor}</strong> ·{' '}
          {event.kind === 'response' && deliveryProblem
            ? 'Intentó responder al paciente'
            : event.kind === 'response' && event.deliveryStatus === 'sending'
              ? 'Está enviando una respuesta'
              : (titles[event.kind] ?? 'Registró una acción en el CRM')}
        </p>
        {event.careType === 'unknown' && (
          <small>Este registro anterior no guardó el rol de quien realizó la acción.</small>
        )}
        <p className="care-event-body">
          {!expanded && event.body.length > 300 ? event.body.slice(0, 300) + '…' : event.body}
        </p>
        {event.body.length > 300 && (
          <button
            className="care-text-button"
            aria-expanded={expanded}
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? 'Mostrar menos' : 'Leer nota completa'}
          </button>
        )}
        <div className="care-event-bottom">
          {event.deliveryStatus && (
            <span className={`care-delivery ${deliveryProblem ? 'care-delivery-error' : ''}`}>
              {statuses[event.deliveryStatus] ?? event.deliveryStatus}
            </span>
          )}
          {event.conversationState && (
            <span className="care-state">
              Ahora: {states[event.conversationState] ?? event.conversationState}
            </span>
          )}
          {event.channelName && <span className="care-channel">{event.channelName}</span>}
          <div className="care-event-links">
            {event.contactId && (
              <button
                className="care-text-button"
                onClick={onPatient}
                aria-label={`Ver historial de ${event.patientName}`}
              >
                Historial del paciente
              </button>
            )}
            {event.conversationId && (
              <a
                href={`/?view=inbox&conversationId=${event.conversationId}`}
                className="care-text-button"
              >
                Abrir conversación <ArrowUpRight size={14} />
              </a>
            )}
          </div>
        </div>
      </div>
    </article>
  );
}
export function ActivityWorkspace({
  me,
  search,
  onSearch,
}: {
  me: Me;
  search: string;
  onSearch: (value: string) => void;
}) {
  const [query, setQuery] = useState(search);
  const [care, setCare] = useState('');
  const [category, setCategory] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [patient, setPatient] = useState<{ id: string; name: string }>();
  const [page, setPage] = useState(1);
  useEffect(() => {
    const timer = setTimeout(() => setQuery(search), 300);
    return () => clearTimeout(timer);
  }, [search]);
  useEffect(() => setPage(1), [query, care, category, from, to, patient?.id]);
  const invalidDates = !!(from && to && from > to);
  const params = new URLSearchParams({ page: String(page) });
  if (query.trim()) params.set('q', query.trim());
  if (care) params.set('care', care);
  if (category) params.set('category', category);
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  if (patient) params.set('contactId', patient.id);
  const { data, error, isLoading, isValidating, mutate } = useSWR<Feed>(
    invalidDates ? null : '/activity-feed?' + params,
    fetcher,
    { refreshInterval: page === 1 ? 15000 : 0 },
  );
  const groups = new Map<string, Event[]>();
  for (const event of data?.items ?? []) {
    const day = new Intl.DateTimeFormat('en-CA', { timeZone: me.tenant.timeZone }).format(
      new Date(event.createdAt),
    );
    groups.set(day, [...(groups.get(day) ?? []), event]);
  }
  function clear() {
    onSearch('');
    setQuery('');
    setCare('');
    setCategory('');
    setFrom('');
    setTo('');
    setPatient(undefined);
    setPage(1);
  }
  const filtered = !!(search || care || category || from || to || patient);
  return (
    <section className="activity-workspace" aria-label="Historial de atención">
      <div className="care-filters">
        <div className="care-search-row">
          <label className="care-search">
            <Search size={18} />
            <input
              aria-label="Buscar en el historial"
              placeholder="Paciente, teléfono, profesional o contenido de una nota…"
              value={search}
              maxLength={150}
              onChange={(e) => onSearch(e.target.value)}
            />
            {search && (
              <button aria-label="Borrar búsqueda" onClick={() => onSearch('')}>
                <X size={16} />
              </button>
            )}
          </label>
          <Button
            variant="outline"
            onClick={() => mutate()}
            disabled={isValidating || invalidDates}
          >
            <RefreshCw className={isValidating ? 'animate-spin' : ''} />
            Actualizar
          </Button>
        </div>
        <div className="care-filter-row">
          <label>
            Quién realizó la acción
            <select value={care} onChange={(e) => setCare(e.target.value)}>
              {careOptions.map(([key, text]) => (
                <option key={key} value={key}>
                  {text}
                </option>
              ))}
            </select>
          </label>
          <label>
            Tipo de acción
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              {categories.map(([key, text]) => (
                <option key={key} value={key}>
                  {text}
                </option>
              ))}
            </select>
          </label>
          <label>
            Desde
            <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          </label>
          <label>
            Hasta
            <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
        </div>
        <div className="care-filter-summary">
          <span>
            <CalendarDays size={14} /> Fechas y horas de {me.tenant.timeZone}
          </span>
          {patient && (
            <span className="care-patient-filter">
              {patient.name}
              <button aria-label="Quitar filtro de paciente" onClick={() => setPatient(undefined)}>
                <X size={14} />
              </button>
            </span>
          )}
          {filtered && (
            <button className="care-text-button" onClick={clear}>
              Limpiar filtros
            </button>
          )}
        </div>
      </div>
      {invalidDates ? (
        <p role="alert" className="error">
          La fecha Desde debe ser anterior o igual a Hasta.
        </p>
      ) : (
        <>
          <div className="care-results-bar">
            <div>
              <h2>
                {data
                  ? `${data.total} ${data.total === 1 ? 'acción encontrada' : 'acciones encontradas'}`
                  : 'Historial de atención'}
              </h2>
              <p>
                Del registro más reciente al más antiguo · las notas internas no se envían al
                paciente.
              </p>
            </div>
            {data && (
              <div className="care-quick-filters" aria-label="Filtrar por tipo de atención">
                {[
                  ['ai', 'IA', Bot],
                  ['reception', 'Recepción', UserRound],
                  ['doctor', 'Doctores', Stethoscope],
                ].map(([id, title, Icon]) => {
                  const key = id as string;
                  const Symbol = Icon as typeof Bot;
                  return (
                    <button
                      key={key}
                      aria-pressed={care === key}
                      className={`care-quick care-${key}`}
                      onClick={() => setCare(care === key ? '' : key)}
                    >
                      <Symbol size={15} />
                      {title as string}
                      <strong>{data.careCounts[key] ?? 0}</strong>
                    </button>
                  );
                })}
              </div>
            )}
          </div>
          {error && (
            <div className="care-empty" role="alert">
              <p>{error.message}</p>
              <Button variant="outline" onClick={() => mutate()}>
                Reintentar
              </Button>
            </div>
          )}
          {isLoading && (
            <div className="care-empty" role="status">
              Buscando en el historial del hospital…
            </div>
          )}
          {!error && data?.items.length === 0 && (
            <div className="care-empty">
              <Activity size={30} />
              <h3>
                {filtered
                  ? 'No hay actividad que coincida'
                  : 'Aquí verás cómo se atendió a cada paciente'}
              </h3>
              <p>
                {filtered
                  ? 'Prueba otro paciente, tipo de atención o rango de fechas.'
                  : 'Aparecerán respuestas, transferencias, citas y notas del agente, recepción y doctores.'}
              </p>
              {filtered && (
                <Button variant="outline" onClick={clear}>
                  Mostrar toda la actividad
                </Button>
              )}
            </div>
          )}
          {!error &&
            [...groups.entries()].map(([day, events]) => (
              <section className="care-day" key={day} aria-label={`Actividad del ${day}`}>
                <h2 className="care-day-title">
                  {new Date(day + 'T12:00:00Z').toLocaleDateString('es-SV', {
                    timeZone: 'UTC',
                    weekday: 'long',
                    day: 'numeric',
                    month: 'long',
                    year: 'numeric',
                  })}
                </h2>
                {events.map((event) => (
                  <EventCard
                    key={event.id}
                    event={event}
                    zone={me.tenant.timeZone}
                    onPatient={() => {
                      setPatient({ id: event.contactId!, name: event.patientName ?? 'Paciente' });
                    }}
                  />
                ))}
              </section>
            ))}
          {data && data.total > 0 && (
            <nav className="care-pagination" aria-label="Páginas de actividad">
              <Button
                variant="outline"
                disabled={page <= 1 || isValidating}
                onClick={() => setPage(page - 1)}
              >
                Anterior
              </Button>
              <span>
                Página {page} de {Math.max(1, Math.ceil(data.total / data.pageSize))}
              </span>
              <Button
                variant="outline"
                disabled={page * data.pageSize >= data.total || isValidating}
                onClick={() => setPage(page + 1)}
              >
                Siguiente
              </Button>
            </nav>
          )}
          <p className="care-footnote">
            <FileText size={14} />
            El rol mostrado corresponde al momento de la acción. El estado de la conversación indica
            su situación actual.
          </p>
        </>
      )}
    </section>
  );
}
