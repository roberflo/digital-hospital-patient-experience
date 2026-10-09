'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { AlertCircle, CalendarDays, RefreshCw } from 'lucide-react';
import { fetcher, type Contact, type Me } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Item, ItemActions, ItemContent, ItemTitle } from './ui/item';
import { Spinner } from './ui/spinner';

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
    <section className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <h4 className="text-sm font-medium">Citas en Hospital</h4>
        <CalendarDays className="size-4 text-muted-foreground" />
      </div>
      {!contact.patientId ? (
        <p className="text-xs text-muted-foreground">
          Vincula el expediente para consultar las citas del paciente.
        </p>
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
              <p className="text-xs text-muted-foreground">
                Próximos 31 días · {me.tenant.timeZone}
              </p>
              {isLoading && (
                <p className="flex items-center gap-2 text-xs text-muted-foreground">
                  <Spinner role={undefined} aria-label={undefined} aria-hidden />
                  Consultando Hospital…
                </p>
              )}
              {error && (
                <Alert variant="destructive">
                  <AlertCircle />
                  <AlertDescription>{error.message}</AlertDescription>
                </Alert>
              )}
              {data?.rows.length === 0 && (
                <p className="text-xs text-muted-foreground">No hay citas en este período.</p>
              )}
              {data?.rows.map((a) => (
                <Item variant="outline" size="sm" key={a.appointmentId}>
                  <ItemContent className="min-w-0">
                    <ItemTitle>{a.clinicianName}</ItemTitle>
                    <p className="text-xs text-muted-foreground">
                      {new Date(a.scheduledStart).toLocaleString('es-SV', {
                        timeZone: me.tenant.timeZone,
                        dateStyle: 'short',
                        timeStyle: 'short',
                      })}{' '}
                      · {a.durationMinutes} min
                    </p>
                  </ItemContent>
                  <ItemActions>
                    <Badge variant="secondary">
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
                    </Badge>
                  </ItemActions>
                </Item>
              ))}
              <div className="flex flex-wrap items-center justify-between gap-2">
                <Button variant="ghost" size="sm" onClick={() => mutate()}>
                  <RefreshCw />
                  Actualizar citas
                </Button>
                <Button asChild variant="link" size="sm">
                  <a href="/?view=calendar">Gestionar en Agenda</a>
                </Button>
              </div>
            </>
          )}
        </>
      )}
    </section>
  );
}
