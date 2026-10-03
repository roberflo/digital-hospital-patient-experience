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
    },
    {
      icon: UserRound,
      label: 'Esperando a tu equipo',
      value: stats?.human,
      detail: 'Atención personal',
    },
    { icon: Sparkles, label: 'Con el agente', value: stats?.agent, detail: 'Atención automática' },
    { icon: Users, label: 'Contactos', value: stats?.contacts, detail: 'Relaciones que importan' },
  ];

  return (
    <>
      <div className="metrics">
        {metrics.map(({ icon: Icon, label, value, detail }) => (
          <div className="metric" key={label}>
            <div>
              <span>{label}</span>
              <Icon size={17} />
            </div>
            <strong>{value ?? '—'}</strong>
            <small>{detail}</small>
          </div>
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
