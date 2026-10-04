'use client';
import useSWR from 'swr';
import { fetcher } from '@/lib/api';
import styles from './agent-metrics.module.css';

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
    <section
      className={styles.metrics}
      aria-label={`Atención del agente en los últimos ${data.days} días`}
    >
      <h2 className={styles.title}>Agente de WhatsApp · últimos {data.days} días</h2>
      <dl className={styles.figures}>
        {figures.map(([value, label, detail]) => (
          <div key={label} className={styles.figure}>
            <dt>{label}</dt>
            <dd>
              {value}
              {detail && <small>{detail}</small>}
            </dd>
          </div>
        ))}
      </dl>
      {review.length > 0 && (
        <p className={styles.review}>
          Para revisar: {review.map(([count, label]) => `${count} ${label}`).join(' · ')}
        </p>
      )}
    </section>
  );
}
