'use client';

import {
  CalendarDays,
  Columns3,
  Inbox,
  MessageCircle,
  Sparkles,
  UserRound,
  Users,
} from 'lucide-react';
import { Button } from './ui/button';

export function DashboardView({
  stats,
  onNavigate,
}: {
  stats?: Record<string, number>;
  onNavigate: (view: string) => void;
}) {
  const metrics = [
    {
      icon: MessageCircle,
      label: 'Conversaciones abiertas',
      value: stats?.conversations,
      detail: 'Tu bandeja de atención',
      view: 'inbox',
    },
    {
      icon: UserRound,
      label: 'Esperando a tu equipo',
      value: stats?.human,
      detail: 'Atención personal',
      view: 'inbox',
      urgent: true,
    },
    {
      icon: Sparkles,
      label: 'Con el agente',
      value: stats?.agent,
      detail: 'Atención automática',
      view: 'agent',
    },
    {
      icon: Users,
      label: 'Contactos',
      value: stats?.contacts,
      detail: 'Relaciones que importan',
      view: 'contacts',
    },
  ];

  return (
    <>
      <div className="metrics">
        {/* Each figure leads where it is acted on; the one waiting on people stands out. */}
        {metrics.map(({ icon: Icon, label, value, detail, view, urgent }) => (
          <button
            type="button"
            className={'metric' + (urgent && value ? ' metric-urgent' : '')}
            key={label}
            onClick={() => onNavigate(view)}
          >
            <div>
              <span>{label}</span>
              <Icon size={17} />
            </div>
            <strong>{value ?? '—'}</strong>
            <small>{detail}</small>
          </button>
        ))}
      </div>
      <section className="content-card">
        <div className="card-toolbar">
          <h2>Continúa la atención</h2>
        </div>
        <div className="dashboard-actions">
          <Button onClick={() => onNavigate('inbox')}>
            <Inbox />
            Abrir bandeja
          </Button>
          <Button variant="outline" onClick={() => onNavigate('calendar')}>
            <CalendarDays />
            Ver agenda
          </Button>
          <Button variant="outline" onClick={() => onNavigate('opportunities')}>
            <Columns3 />
            Ver seguimientos
          </Button>
        </div>
      </section>
    </>
  );
}
