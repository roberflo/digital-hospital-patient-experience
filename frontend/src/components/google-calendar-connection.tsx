'use client';
import { useId, useState } from 'react';
import useSWR from 'swr';
import { AlertCircle, Link2, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Button } from './ui/button';
import { Field, FieldDescription, FieldLabel } from './ui/field';
import { NativeSelect, NativeSelectOption } from './ui/native-select';

export function GoogleCalendarConnection({
  connected,
  selected,
  onChange,
}: {
  connected: boolean;
  selected?: string;
  onChange: () => void;
}) {
  const { data: calendars, error } = useSWR<{ id: string; name: string; primary: boolean }[]>(
    connected ? '/google/calendars' : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  const [busy, setBusy] = useState(false);
  const calendarId = useId();
  async function perform(action: () => Promise<void>) {
    setBusy(true);
    try {
      await action();
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="flex flex-col items-start gap-4">
      <p className="text-sm text-muted-foreground">
        Puedes copiar las citas a Google Calendar. La agenda del Hospital seguirá siendo la que
        utiliza tu equipo.
      </p>
      <Button
        variant="outline"
        disabled={busy}
        onClick={() =>
          perform(async () => {
            const result = await api<{ url: string }>('/google/connect', 'POST', {});
            location.href = result.url;
          })
        }
      >
        <Link2 />
        {connected ? 'Volver a conectar Google' : 'Conectar Google Calendar'}
      </Button>
      {connected && (
        <>
          {error ? (
            <Alert variant="destructive">
              <AlertCircle />
              <AlertDescription>
                No pudimos consultar tus calendarios. Vuelve a conectar Google para revisar el
                acceso.
              </AlertDescription>
            </Alert>
          ) : !calendars ? (
            <p className="text-sm text-muted-foreground" role="status">
              Buscando tus calendarios…
            </p>
          ) : (
            <Field>
              <FieldLabel htmlFor={calendarId}>Calendario para las citas</FieldLabel>
              <NativeSelect
                id={calendarId}
                aria-label="Calendario para las citas"
                disabled={busy}
                value={calendars.some((c) => c.id === selected) ? selected : ''}
                onChange={(e) => {
                  const id = e.target.value;
                  if (id)
                    void perform(async () => {
                      await api('/google/calendar', 'PUT', { id });
                      onChange();
                      toast.success('Calendario seleccionado');
                    });
                }}
              >
                <NativeSelectOption value="" disabled>
                  {selected ? 'Revisa y selecciona el calendario' : 'Elige un calendario'}
                </NativeSelectOption>
                {calendars.map((c) => (
                  <NativeSelectOption key={c.id} value={c.id}>
                    {c.name}
                    {c.primary ? ' · Principal' : ''}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
              {!calendars.length && (
                <FieldDescription>
                  No hay calendarios con permiso para guardar citas. Revisa los permisos de tu
                  cuenta de Google.
                </FieldDescription>
              )}
            </Field>
          )}
          {selected && (
            <Button
              variant="outline"
              disabled={busy}
              onClick={() =>
                perform(async () => {
                  const result = await api<{ synced: number }>('/google/sync', 'POST', {
                    from: new Date().toISOString().slice(0, 10),
                    days: 7,
                  });
                  toast.success(`${result.synced} citas sincronizadas`);
                })
              }
            >
              <RefreshCw />
              Sincronizar próximos 7 días
            </Button>
          )}
          <Button
            variant="ghost"
            size="sm"
            disabled={busy}
            onClick={() =>
              perform(async () => {
                await api('/google', 'DELETE');
                onChange();
              })
            }
          >
            Desconectar Google
          </Button>
        </>
      )}
    </div>
  );
}
