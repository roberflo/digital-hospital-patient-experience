'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { Link2, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher } from '@/lib/api';
import { Button } from './ui/button';

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
    <div className="settings-form">
      <p className="hint">
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
            <p role="alert">
              No pudimos consultar tus calendarios. Vuelve a conectar Google para revisar el acceso.
            </p>
          ) : !calendars ? (
            <p role="status">Buscando tus calendarios…</p>
          ) : (
            <label>
              Calendario para las citas
              <select
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
                <option value="" disabled>
                  {selected ? 'Revisa y selecciona el calendario' : 'Elige un calendario'}
                </option>
                {calendars.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                    {c.primary ? ' · Principal' : ''}
                  </option>
                ))}
              </select>
              {!calendars.length && (
                <small>
                  No hay calendarios con permiso para guardar citas. Revisa los permisos de tu
                  cuenta de Google.
                </small>
              )}
            </label>
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
          <button
            className="text-button"
            disabled={busy}
            onClick={() =>
              perform(async () => {
                await api('/google', 'DELETE');
                onChange();
              })
            }
          >
            Desconectar Google
          </button>
        </>
      )}
    </div>
  );
}
