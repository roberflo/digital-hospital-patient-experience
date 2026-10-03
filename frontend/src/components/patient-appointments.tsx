'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { CalendarDays, RefreshCw } from 'lucide-react';
import { fetcher, type Contact, type Me } from '@/lib/api';
import { Button } from './ui/button';

type Appointment = {
  appointmentId: string;
  clinicianName: string;
  scheduledStart: string;
  durationMinutes: number;
  status: string;
};
export function PatientAppointments({ contact, me }: { contact: Contact; me: Me }) {
  const [expanded, setExpanded] = useState(false);
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: me.tenant.timeZone }).format(
    new Date(),
  );
  const end = new Date(today + 'T12:00:00Z');
  end.setUTCDate(end.getUTCDate() + 30);
  const { data, error, isLoading, mutate } = useSWR<{ rows: Appointment[] }>(
    expanded && contact.patientId
      ? `/hospital/contacts/${contact.id}/appointments?from=${today}&to=${end.toISOString().slice(0, 10)}`
      : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  return (
    <div className="detail-section">
      <div className="section-heading">
        <h4>Citas en Hospital</h4>
        <CalendarDays size={14} />
      </div>
      {!contact.patientId ? (
        <p className="hint">Vincula el expediente para consultar las citas del paciente.</p>
      ) : (
        <>
          <Button
            variant="outline"
            size="sm"
            className="w-full"
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? 'Ocultar citas' : 'Consultar próximas citas'}
          </Button>
          {expanded && (
            <>
              <p className="hint">Próximos 31 días · {me.tenant.timeZone}</p>
              {isLoading && <p className="hint">Consultando Hospital…</p>}
              {error && (
                <p role="alert" className="error">
                  {error.message}
                </p>
              )}
              {data?.rows.length === 0 && <p className="hint">No hay citas en este período.</p>}
              {data?.rows.map((a) => (
                <div className="conversation-followup" key={a.appointmentId}>
                  <strong>{a.clinicianName}</strong>
                  <p>
                    {new Date(a.scheduledStart).toLocaleString('es-SV', {
                      timeZone: me.tenant.timeZone,
                      dateStyle: 'short',
                      timeStyle: 'short',
                    })}{' '}
                    · {a.durationMinutes} min
                  </p>
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
                    )[a.status] ?? a.status}
                  </span>
                </div>
              ))}
              <Button variant="ghost" size="sm" onClick={() => mutate()}>
                <RefreshCw size={13} />
                Actualizar citas
              </Button>
              <a className="media-link" href="/?view=calendar">
                Gestionar en Agenda
              </a>
            </>
          )}
        </>
      )}
    </div>
  );
}
