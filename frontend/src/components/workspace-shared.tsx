'use client';
import { type ReactNode } from 'react';
import {
  HeartPulse,
  LayoutDashboard,
  Inbox,
  Users,
  BookUser,
  Building2,
  Columns3,
  CalendarDays,
  Activity as ActivityIcon,
  Sparkles,
  AlertCircle,
} from 'lucide-react';
import { Alert, AlertDescription } from './ui/alert';
import { Avatar, AvatarFallback } from './ui/avatar';
import { Badge } from './ui/badge';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from './ui/empty';
import { Spinner } from './ui/spinner';
import { type Activity } from '@/lib/api';
import { cn } from '@/lib/utils';
import { dayInZone, inZone } from '@/lib/format';
export const nav = [
  ['dashboard', 'Inicio', LayoutDashboard],
  ['inbox', 'Bandeja de entrada', Inbox],
  ['team', 'Equipo', Users],
  ['contacts', 'Contactos', BookUser],
  ['companies', 'Empresas', Building2],
  ['opportunities', 'Seguimientos', Columns3],
  ['calendar', 'Agenda', CalendarDays],
  ['hospital', 'Mi hospital', HeartPulse],
  ['activity', 'Actividad', ActivityIcon],
  ['agent', 'Agente de atención', Sparkles],
] as const;
export const views = new Set<string>(['settings', ...nav.map(([id]) => id)]);
/** The view a `/?view=` URL names, or null when it is not one of ours. */
export function viewOf(href: string) {
  const url = new URL(href, location.href);
  const v = url.searchParams.get('view');
  return url.origin === location.origin &&
    url.pathname === '/' &&
    [...url.searchParams.keys()].join() === 'view' &&
    v &&
    views.has(v)
    ? v
    : null;
}
export const labels: Record<string, string> = {
  agent: 'Agente IA',
  unattended: 'Sin atender',
  human: 'Atención humana',
  closed: 'Cerrada',
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
export const time = (value: string, tz: string) =>
  inZone(value, tz, { hour: '2-digit', minute: '2-digit' });
const date = (value: string, tz: string) => inZone(value, tz, { day: 'numeric', month: 'short' });
/** «Hoy», «Ayer» or «17 oct» in the hospital's zone; another year carries it. */
export function dayLabel(value: string, tz: string) {
  const d = dayInZone(value, tz);
  const today = dayInZone(Date.now(), tz);
  if (d === today) return 'Hoy';
  if (d === dayInZone(Date.now() - 864e5, tz)) return 'Ayer';
  return d.slice(0, 4) === today.slice(0, 4)
    ? date(value, tz)
    : inZone(value, tz, { day: 'numeric', month: 'short', year: 'numeric' });
}
/** What the team reads when WhatsApp did not confirm a message. */
export const notSent = (status: string) =>
  status === 'failed'
    ? 'El mensaje no se envió. Inténtalo de nuevo.'
    : 'No sabemos si el mensaje llegó. Revisa el chat antes de reenviar.';
export function PersonAvatar({ name, large = false }: { name: string; large?: boolean }) {
  return (
    <Avatar className={cn(large && 'size-16')}>
      <AvatarFallback className={cn(large && 'text-lg')}>{initials(name)}</AvatarFallback>
    </Avatar>
  );
}
/** Who attends, as the team should read it: with the hospital's agent off, an «agent» conversation has nobody (MR-46). */
export const attention = (status: string, agentEnabled: boolean) =>
  status === 'agent' && !agentEnabled ? 'unattended' : status;
export function StatusBadge({ value }: { value: string }) {
  return (
    <Badge
      variant={
        value === 'failed'
          ? 'destructive'
          : value === 'unattended'
            ? 'warning'
            : value === 'human' || value === 'agent'
              ? 'secondary'
              : 'outline'
      }
    >
      {value === 'agent' && <Sparkles />}
      {labels[value] ?? value}
    </Badge>
  );
}
export function EmptyState({
  icon: Icon = Inbox,
  title,
  children,
}: {
  icon?: typeof Inbox;
  title: string;
  children?: ReactNode;
}) {
  return (
    <Empty>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Icon />
        </EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        {children && <EmptyDescription>{children}</EmptyDescription>}
      </EmptyHeader>
    </Empty>
  );
}
export function ErrorBox({ error }: { error?: Error }) {
  return error ? (
    <Alert variant="destructive">
      <AlertCircle />
      <AlertDescription>{error.message}</AlertDescription>
    </Alert>
  ) : null;
}
export function Loading() {
  return (
    <div className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground">
      <Spinner /> Cargando tu espacio…
    </div>
  );
}
export function ActivityTimeline({
  items,
  proposal,
  zone,
}: {
  items?: Activity[];
  proposal: string;
  zone: string;
}) {
  if (!items?.length) return null;
  return (
    <div className="flex flex-col gap-4">
      {items.map((a) => (
        <div key={a.id} className="flex flex-col gap-1 border-l pl-3 text-sm">
          <strong className="font-medium">{a.actor}</strong>
          <p className="break-words whitespace-pre-wrap text-muted-foreground">
            {a.kind.startsWith('proposal') ? proposal : a.body}
          </p>
          <small className="text-xs text-muted-foreground">
            {date(a.createdAt, zone)} · {time(a.createdAt, zone)}
          </small>
        </div>
      ))}
    </div>
  );
}
