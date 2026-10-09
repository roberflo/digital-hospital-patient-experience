'use client';
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import {
  Activity,
  AlertCircle,
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
import { AgentMetrics } from './agent-metrics';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent } from './ui/card';
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from './ui/empty';
import { Field, FieldLabel } from './ui/field';
import { Input } from './ui/input';
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from './ui/input-group';
import { Item, ItemContent, ItemMedia, ItemTitle } from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Spinner } from './ui/spinner';
import { Toggle } from './ui/toggle';

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
  waiting_notice: 'Avisó al paciente que sigue en espera',
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
const roleTone: Record<string, 'default' | 'secondary' | 'outline'> = {
  ai: 'secondary',
  reception: 'default',
  doctor: 'outline',
};
function ActorIcon({ type }: { type: string }) {
  return type === 'ai' ? (
    <Bot />
  ) : type === 'doctor' ? (
    <Stethoscope />
  ) : type === 'reception' ? (
    <UserRound />
  ) : (
    <Users />
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
    <Item variant="outline" asChild>
      <article>
        <ItemMedia variant="icon" className="self-start">
          <ActorIcon type={event.careType} />
        </ItemMedia>
        <ItemContent className="min-w-0 gap-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            {/* `care-role` carries no style: the Playwright suite finds the role chip by it. */}
            <Badge variant={roleTone[event.careType] ?? 'outline'} className="care-role">
              {careOptions.find(([key]) => key === event.careType)?.[1] ?? 'Histórico sin rol'}
            </Badge>
            <time dateTime={event.createdAt} className="text-xs text-muted-foreground">
              {new Date(event.createdAt).toLocaleTimeString('es-SV', {
                timeZone: zone,
                hour: '2-digit',
                minute: '2-digit',
              })}
            </time>
          </div>
          <ItemTitle>
            <h3>{event.patientName ?? 'Actividad general del hospital'}</h3>
          </ItemTitle>
          <p className="text-sm text-muted-foreground">
            <strong className="font-medium text-foreground">{event.actor}</strong> ·{' '}
            {event.kind === 'response' && deliveryProblem
              ? 'Intentó responder al paciente'
              : event.kind === 'response' && event.deliveryStatus === 'sending'
                ? 'Está enviando una respuesta'
                : (titles[event.kind] ?? 'Registró una acción en el CRM')}
          </p>
          {event.careType === 'unknown' && (
            <small className="text-xs text-muted-foreground">
              Este registro anterior no guardó el rol de quien realizó la acción.
            </small>
          )}
          <p className="text-sm wrap-anywhere whitespace-pre-wrap">
            {!expanded && event.body.length > 300 ? event.body.slice(0, 300) + '…' : event.body}
          </p>
          {event.body.length > 300 && (
            <Button
              variant="outline"
              size="sm"
              className="self-start"
              aria-expanded={expanded}
              onClick={() => setExpanded(!expanded)}
            >
              {expanded ? 'Mostrar menos' : 'Leer nota completa'}
            </Button>
          )}
          <div className="flex flex-wrap items-center gap-2">
            {event.deliveryStatus && (
              <Badge variant={deliveryProblem ? 'destructive' : 'outline'}>
                {statuses[event.deliveryStatus] ?? event.deliveryStatus}
              </Badge>
            )}
            {event.conversationState && (
              <Badge variant="outline">
                Ahora: {states[event.conversationState] ?? event.conversationState}
              </Badge>
            )}
            {event.channelName && (
              <span className="min-w-0 truncate text-xs text-muted-foreground">
                {event.channelName}
              </span>
            )}
            <div className="flex flex-wrap items-center gap-2 sm:ml-auto">
              {event.contactId && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={onPatient}
                  aria-label={`Ver historial de ${event.patientName}`}
                >
                  Historial del paciente
                </Button>
              )}
              {event.conversationId && (
                <Button variant="ghost" size="sm" asChild>
                  <a href={`/?view=inbox&conversationId=${event.conversationId}`}>
                    Abrir conversación <ArrowUpRight />
                  </a>
                </Button>
              )}
            </div>
          </div>
        </ItemContent>
      </article>
    </Item>
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
    <section
      className="mx-auto flex w-full max-w-6xl flex-col gap-6"
      aria-label="Historial de atención"
    >
      <Card>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
            <InputGroup className="sm:flex-1">
              <InputGroupInput
                aria-label="Buscar en el historial"
                placeholder="Paciente, teléfono, profesional o contenido de una nota…"
                value={search}
                maxLength={150}
                onChange={(e) => onSearch(e.target.value)}
              />
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
              {search && (
                <InputGroupAddon align="inline-end">
                  <InputGroupButton
                    size="icon-xs"
                    aria-label="Borrar búsqueda"
                    onClick={() => onSearch('')}
                  >
                    <X />
                  </InputGroupButton>
                </InputGroupAddon>
              )}
            </InputGroup>
            <Button
              variant="outline"
              onClick={() => mutate()}
              disabled={isValidating || invalidDates}
            >
              {isValidating ? <Spinner /> : <RefreshCw />}
              Actualizar
            </Button>
          </div>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Field className="min-w-0 gap-2">
              <FieldLabel htmlFor="activity-care">Quién realizó la acción</FieldLabel>
              <NativeSelect
                id="activity-care"
                value={care}
                onChange={(e) => setCare(e.target.value)}
              >
                {careOptions.map(([key, text]) => (
                  <NativeSelectOption key={key} value={key}>
                    {text}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </Field>
            <Field className="min-w-0 gap-2">
              <FieldLabel htmlFor="activity-category">Tipo de acción</FieldLabel>
              <NativeSelect
                id="activity-category"
                value={category}
                onChange={(e) => setCategory(e.target.value)}
              >
                {categories.map(([key, text]) => (
                  <NativeSelectOption key={key} value={key}>
                    {text}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </Field>
            <Field className="min-w-0 gap-2">
              <FieldLabel htmlFor="activity-from">Desde</FieldLabel>
              <Input
                id="activity-from"
                type="date"
                value={from}
                onChange={(e) => setFrom(e.target.value)}
              />
            </Field>
            <Field className="min-w-0 gap-2">
              <FieldLabel htmlFor="activity-to">Hasta</FieldLabel>
              <Input
                id="activity-to"
                type="date"
                value={to}
                onChange={(e) => setTo(e.target.value)}
              />
            </Field>
          </div>
          <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
            <span className="flex items-center gap-2">
              <CalendarDays className="size-4 shrink-0" /> Fechas y horas de {me.tenant.timeZone}
            </span>
            {patient && (
              <span className="flex min-w-0 items-center gap-1">
                <Badge variant="secondary" className="max-w-48">
                  <span className="truncate">{patient.name}</span>
                </Badge>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Quitar filtro de paciente"
                  onClick={() => setPatient(undefined)}
                >
                  <X />
                </Button>
              </span>
            )}
            {filtered && (
              <Button variant="ghost" size="sm" onClick={clear}>
                Limpiar filtros
              </Button>
            )}
          </div>
        </CardContent>
      </Card>
      {invalidDates ? (
        <Alert variant="destructive">
          <AlertCircle />
          <AlertDescription>La fecha Desde debe ser anterior o igual a Hasta.</AlertDescription>
        </Alert>
      ) : (
        <>
          <AgentMetrics />
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div className="flex min-w-0 flex-col gap-1">
              <h2 className="text-lg font-semibold tracking-tight">
                {data
                  ? `${data.total} ${data.total === 1 ? 'acción encontrada' : 'acciones encontradas'}`
                  : 'Historial de atención'}
              </h2>
              <p className="text-sm text-muted-foreground">
                Del registro más reciente al más antiguo · las notas internas no se envían al
                paciente.
              </p>
            </div>
            {data && (
              <div
                role="group"
                className="flex flex-wrap gap-2"
                aria-label="Filtrar por tipo de atención"
              >
                {[
                  ['ai', 'IA', Bot],
                  ['reception', 'Recepción', UserRound],
                  ['doctor', 'Doctores', Stethoscope],
                ].map(([id, title, Icon]) => {
                  const key = id as string;
                  const Symbol = Icon as typeof Bot;
                  return (
                    <Toggle
                      key={key}
                      variant="outline"
                      size="sm"
                      pressed={care === key}
                      onPressedChange={() => setCare(care === key ? '' : key)}
                    >
                      <Symbol />
                      {title as string}
                      <strong className="font-semibold">{data.careCounts[key] ?? 0}</strong>
                    </Toggle>
                  );
                })}
              </div>
            )}
          </div>
          {error && (
            <Alert variant="destructive">
              <AlertCircle />
              <AlertDescription>
                <p>{error.message}</p>
                <Button variant="outline" size="sm" onClick={() => mutate()}>
                  Reintentar
                </Button>
              </AlertDescription>
            </Alert>
          )}
          {isLoading && (
            <div
              className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground"
              role="status"
            >
              <Spinner aria-hidden="true" role="presentation" />
              Buscando en el historial del hospital…
            </div>
          )}
          {!error && data?.items.length === 0 && (
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <Activity />
                </EmptyMedia>
                <EmptyTitle>
                  <h3>
                    {filtered
                      ? 'No hay actividad que coincida'
                      : 'Aquí verás cómo se atendió a cada paciente'}
                  </h3>
                </EmptyTitle>
                <EmptyDescription>
                  {filtered
                    ? 'Prueba otro paciente, tipo de atención o rango de fechas.'
                    : 'Aparecerán respuestas, transferencias, citas y notas del agente, recepción y doctores.'}
                </EmptyDescription>
              </EmptyHeader>
              {filtered && (
                <EmptyContent>
                  <Button variant="outline" onClick={clear}>
                    Mostrar toda la actividad
                  </Button>
                </EmptyContent>
              )}
            </Empty>
          )}
          {!error &&
            [...groups.entries()].map(([day, events]) => (
              <section
                className="flex flex-col gap-2"
                key={day}
                aria-label={`Actividad del ${day}`}
              >
                <h2 className="text-sm font-medium text-muted-foreground first-letter:uppercase">
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
            <nav
              className="flex flex-wrap items-center justify-center gap-4"
              aria-label="Páginas de actividad"
            >
              <Button
                variant="outline"
                size="sm"
                disabled={page <= 1 || isValidating}
                onClick={() => setPage(page - 1)}
              >
                Anterior
              </Button>
              <span className="text-sm text-muted-foreground">
                Página {page} de {Math.max(1, Math.ceil(data.total / data.pageSize))}
              </span>
              <Button
                variant="outline"
                size="sm"
                disabled={page * data.pageSize >= data.total || isValidating}
                onClick={() => setPage(page + 1)}
              >
                Siguiente
              </Button>
            </nav>
          )}
          <p className="flex items-start gap-2 text-xs text-muted-foreground">
            <FileText className="size-4 shrink-0" />
            El rol mostrado corresponde al momento de la acción. El estado de la conversación indica
            su situación actual.
          </p>
        </>
      )}
    </section>
  );
}
