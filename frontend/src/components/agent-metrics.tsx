'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { fetcher } from '@/lib/api';
import { Card, CardAction, CardContent, CardFooter, CardHeader, CardTitle } from './ui/card';
import { NativeSelect, NativeSelectOption } from './ui/native-select';

type Metrics = {
  days: number;
  attended: number;
  withoutPerson: number;
  handedOver: number;
  appointments: number;
  registrations: number;
  prescriptions: number;
  personOffers: number;
  withheld: number;
  providerFailures: number;
  waitingNotices: number;
  remindersSent: number;
  remindersConfirmed: number;
  changeRequests: number;
  firstReplyMinutes: number | null;
};

const duration = (minutes: number) =>
  minutes < 60
    ? `${minutes} min`
    : `${Math.floor(minutes / 60)} h` + (minutes % 60 ? ` ${minutes % 60} min` : '');

/** What Recepción resolved for the hospital, counted from its trail (docs/reception-agent.md, criterios 66–67 y 80–82). */
export function AgentMetrics() {
  const [days, setDays] = useState('7');
  const { data } = useSWR<Metrics>(`/agent-metrics?days=${days}`, fetcher, {
    refreshInterval: 60000,
  });
  if (!data) return null;
  const figures: [string, string, string?][] = [];
  if (data.attended > 0)
    figures.push(
      [String(data.attended), 'conversaciones atendidas por el agente'],
      [
        `${Math.round((data.withoutPerson / data.attended) * 100)}%`,
        'resueltas sin una persona',
        `${data.withoutPerson} de ${data.attended}`,
      ],
      [String(data.handedOver), 'pasaron a una persona'],
      [String(data.appointments), 'gestiones de cita'],
      [String(data.registrations), 'pacientes registrados'],
      [String(data.prescriptions), 'recetas entregadas'],
    );
  if (data.remindersSent > 0)
    figures.push(
      [
        String(data.remindersConfirmed),
        'citas confirmadas desde el recordatorio',
        `${data.remindersConfirmed} de ${data.remindersSent}`,
      ],
      [String(data.changeRequests), 'pidieron cambiar su cita a tiempo'],
    );
  if (data.firstReplyMinutes !== null)
    figures.push([duration(data.firstReplyMinutes), 'mediana de primera respuesta']);
  if (figures.length === 0) return null;
  const review = [
    [data.waitingNotices, 'esperaron sin respuesta del equipo'],
    [data.withheld, 'respuestas retenidas'],
    [data.providerFailures, 'fallos del proveedor de IA'],
  ].filter(([count]) => Number(count) > 0);
  return (
    <section aria-label={`Lo que Recepción resolvió en los últimos ${data.days} días`}>
      <Card>
        <CardHeader>
          <CardTitle>
            <h2>Lo que Recepción resolvió · últimos {data.days} días</h2>
          </CardTitle>
          <CardAction>
            <NativeSelect
              aria-label="Periodo"
              size="sm"
              value={days}
              onChange={(e) => setDays(e.target.value)}
            >
              <NativeSelectOption value="7">7 días</NativeSelectOption>
              <NativeSelectOption value="30">30 días</NativeSelectOption>
              <NativeSelectOption value="90">90 días</NativeSelectOption>
            </NativeSelect>
          </CardAction>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-4 sm:grid-cols-3 xl:grid-cols-6">
            {figures.map(([value, label, detail]) => (
              <div key={label} className="flex min-w-0 flex-col-reverse justify-end gap-1">
                <dt className="text-sm text-muted-foreground">{label}</dt>
                <dd className="flex flex-wrap items-baseline gap-2 text-2xl font-semibold tabular-nums">
                  {value}
                  {detail && (
                    <small className="text-xs font-normal text-muted-foreground">{detail}</small>
                  )}
                </dd>
              </div>
            ))}
          </dl>
        </CardContent>
        {review.length > 0 && (
          <CardFooter className="text-sm text-muted-foreground">
            <p>Para revisar: {review.map(([count, label]) => `${count} ${label}`).join(' · ')}</p>
          </CardFooter>
        )}
      </Card>
    </section>
  );
}
