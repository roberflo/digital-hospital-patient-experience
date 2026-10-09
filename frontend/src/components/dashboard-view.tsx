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
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from './ui/card';

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
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {/* Each figure leads where it is acted on. */}
        {metrics.map(({ icon: Icon, label, value, detail, view }) => (
          <button
            type="button"
            className="min-w-0 text-left"
            key={label}
            onClick={() => onNavigate(view)}
          >
            <Card className="h-full">
              <CardHeader>
                <CardDescription>{label}</CardDescription>
                <CardTitle className="text-2xl tabular-nums">{value ?? '—'}</CardTitle>
                <CardAction>
                  <Icon className="size-4 text-muted-foreground" />
                </CardAction>
              </CardHeader>
              <CardFooter className="text-sm text-muted-foreground">{detail}</CardFooter>
            </Card>
          </button>
        ))}
      </div>
      <Card>
        <CardHeader>
          <CardTitle>
            <h2>Continúa la atención</h2>
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
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
        </CardContent>
      </Card>
    </>
  );
}
