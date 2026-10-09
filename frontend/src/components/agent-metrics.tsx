'use client';
import useSWR from 'swr';
import { fetcher } from '@/lib/api';
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from './ui/card';

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
};

/** How the WhatsApp agent's attention went, counted from its trail (docs/reception-agent.md, criterios 66–67). */
export function AgentMetrics() {
  const { data } = useSWR<Metrics>('/agent-metrics?days=7', fetcher, { refreshInterval: 60000 });
  if (!data || data.attended === 0) return null;
  const figures: [string, string, string?][] = [
    [String(data.attended), 'conversaciones atendidas'],
    [
      `${Math.round((data.withoutPerson / data.attended) * 100)}%`,
      'resueltas sin una persona',
      `${data.withoutPerson} de ${data.attended}`,
    ],
    [String(data.handedOver), 'pasaron a una persona'],
    [String(data.appointments), 'gestiones de cita'],
    [String(data.registrations), 'pacientes registrados'],
    [String(data.prescriptions), 'recetas entregadas'],
  ];
  const review = [
    [data.waitingNotices, 'esperaron sin respuesta del equipo'],
    [data.withheld, 'respuestas retenidas'],
    [data.providerFailures, 'fallos del proveedor de IA'],
  ].filter(([count]) => Number(count) > 0);
  return (
    <section aria-label={`Atención del agente en los últimos ${data.days} días`}>
      <Card>
        <CardHeader>
          <CardTitle>
            <h2>Agente de WhatsApp · últimos {data.days} días</h2>
          </CardTitle>
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
