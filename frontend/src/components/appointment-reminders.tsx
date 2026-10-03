'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { Bell, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Channel, type Me } from '@/lib/api';
import { Button } from './ui/button';

type Reminder = {
  id: string;
  patientName?: string;
  startsAt: string;
  dueAt: string;
  window: string;
  status: string;
  reason?: string;
  deliveryStatus?: string;
};
type Settings = {
  enabled: boolean;
  channelId?: string;
  dayTemplate: string;
  hourTemplate: string;
  language: string;
  sendEnabled: boolean;
  consentingContacts: number;
  lastSyncAt?: string;
  error?: string;
  rows: Reminder[];
};
const statuses: Record<string, string> = {
  pending: 'Programado',
  sending: 'Enviando',
  sent: 'Enviado',
  delivered: 'Entregado',
  read: 'Leído',
  failed: 'Fallido',
  uncertain: 'Entrega sin confirmar',
  cancelled: 'Cancelado',
  skipped: 'Omitido',
};
export function AppointmentReminders({ me }: { me: Me }) {
  const { data, error, mutate } = useSWR<Settings>('/appointment-reminders', fetcher, {
    refreshInterval: 15000,
  });
  const { data: channels } = useSWR<Channel[]>('/channels', fetcher);
  const [busy, setBusy] = useState(false);
  const [templates, setTemplates] = useState<{ name: string; status: string; ready: boolean }[]>();
  async function save(enabled: boolean, channelId?: string) {
    setBusy(true);
    try {
      await api('/appointment-reminders/settings', 'PUT', {
        enabled,
        channelId: channelId || null,
      });
      await mutate();
      toast.success('Recordatorios actualizados');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const format = (value: string) =>
    new Date(value).toLocaleString('es-SV', {
      timeZone: me.tenant.timeZone,
      dateStyle: 'short',
      timeStyle: 'short',
    });
  return (
    <section className="content-card appointment-reminder-settings">
      <div className="card-toolbar">
        <h2>
          <Bell size={18} /> Recordatorios de citas
        </h2>
        <span className="tag">{data?.enabled ? 'Servicio activo' : 'Servicio en pausa'}</span>
      </div>
      <div className="reminder-settings-body">
        <p>
          A las <strong>09:00 del día anterior</strong> y <strong>una hora antes</strong> ·{' '}
          {me.tenant.timeZone}. Se confirma el horario con Hospital antes de cada aviso.
        </p>
        {error && (
          <p role="alert" className="error">
            {error.message}
          </p>
        )}
        {data && (
          <>
            <label className="field">
              Número general para recordatorios
              <select
                aria-label="Número para recordatorios"
                value={data.channelId ?? ''}
                disabled={busy}
                onChange={(e) => {
                  setTemplates(undefined);
                  void save(false, e.target.value);
                }}
              >
                <option value="">Seleccionar número</option>
                {channels
                  ?.filter((c) => c.enabled && !c.doctorId)
                  .map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.name}
                    </option>
                  ))}
              </select>
            </label>
            <div className="reminder-actions">
              <Button
                disabled={busy || !data.channelId}
                variant={data.enabled ? 'outline' : 'default'}
                onClick={() => save(!data.enabled, data.channelId)}
              >
                {data.enabled ? 'Pausar recordatorios' : 'Activar recordatorios'}
              </Button>
              <Button
                variant="outline"
                disabled={busy || !data.channelId}
                onClick={async () => {
                  setBusy(true);
                  try {
                    await api('/appointment-reminders/sync', 'POST');
                    await mutate();
                    toast.success('Agenda verificada; no se enviaron avisos desde esta acción');
                  } catch (e) {
                    toast.error((e as Error).message);
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                <RefreshCw size={14} />
                Consultar agenda ahora
              </Button>
              <Button
                variant="outline"
                disabled={busy || !data.channelId}
                onClick={async () => {
                  setBusy(true);
                  try {
                    setTemplates(await api('/appointment-reminders/templates'));
                  } catch (e) {
                    toast.error((e as Error).message);
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                Comprobar plantillas
              </Button>
            </div>
            {!data.sendEnabled && (
              <p className="hint">
                Envío de notificaciones pausado en la instalación. La cola se puede consultar sin
                enviar mensajes.
              </p>
            )}
            <p className="hint">
              {data.consentingContacts} pacientes vinculados con autorización. Regístrala en la
              ficha de la conversación o pide al paciente escribir ACTIVAR RECORDATORIOS. BAJA
              desactiva los avisos.
            </p>
            <p className="hint">
              Se usan dos plantillas de notificación aprobadas. No se incluyen recetas ni
              información clínica en el recordatorio.
            </p>
            {templates?.map((t, i) => (
              <p key={t.name} className="hint">
                {i === 0 ? 'Día anterior' : 'Una hora antes'}:{' '}
                {t.ready
                  ? 'Aprobada y lista'
                  : t.status === 'PENDING'
                    ? 'Pendiente de aprobación'
                    : t.status === 'NOT_FOUND'
                      ? 'No registrada'
                      : t.status === 'REJECTED'
                        ? 'Rechazada'
                        : t.status + ' · revisar compatibilidad'}
              </p>
            ))}
            {data.lastSyncAt && (
              <p className="hint">Última consulta a Hospital: {format(data.lastSyncAt)}</p>
            )}
            {data.error && (
              <p role="alert" className="error">
                {data.error}
              </p>
            )}
            <h3>Últimos 100 recordatorios</h3>
            {!data.rows.length && (
              <p className="hint">
                Todavía no hay avisos programados. Se generan para citas futuras de pacientes
                vinculados con autorización.
              </p>
            )}
            <div className="reminder-queue">
              {data.rows.map((r) => (
                <article key={r.id} className="reminder-row">
                  <div>
                    <strong>{r.patientName ?? 'Paciente'}</strong>
                    <p>Cita: {format(r.startsAt)}</p>
                    <small>
                      Aviso: {format(r.dueAt)} ·{' '}
                      {r.window === 'day_before' ? 'Día anterior' : 'Una hora antes'}
                    </small>
                  </div>
                  <div>
                    <span className="tag">
                      {statuses[r.deliveryStatus ?? r.status] ?? r.status}
                    </span>
                    {r.reason && <p className="hint">{r.reason}</p>}
                  </div>
                </article>
              ))}
            </div>
          </>
        )}
      </div>
    </section>
  );
}
export function ReminderConsent({ contactId }: { contactId: string }) {
  const { data, mutate, error } = useSWR<{ enabled: boolean; linked: boolean }>(
    '/appointment-reminders/contacts/' + contactId,
    fetcher,
  );
  const [busy, setBusy] = useState(false);
  return (
    <div className="detail-section">
      <div className="section-heading">
        <h4>Recordatorios de citas</h4>
        <Bell size={14} />
      </div>
      {error && <p className="hint">No se pudo consultar la autorización.</p>}
      {data && (
        <>
          <p className="hint">
            {data.enabled
              ? 'El paciente autorizó recordatorios por WhatsApp.'
              : 'Registra la autorización sólo cuando el paciente haya aceptado recibir avisos.'}{' '}
            A las 09:00 del día anterior y una hora antes.
          </p>
          <Button
            variant="outline"
            size="sm"
            disabled={busy || (!data.enabled && !data.linked)}
            onClick={async () => {
              setBusy(true);
              try {
                await api('/appointment-reminders/contacts/' + contactId, 'PUT', {
                  enabled: !data.enabled,
                });
                await mutate();
                toast.success('Autorización actualizada');
              } catch (e) {
                toast.error((e as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            {data.enabled
              ? 'Desactivar avisos del paciente'
              : 'Registrar autorización del paciente'}
          </Button>
          {!data.linked && <p className="hint">Vincula primero el expediente de Hospital.</p>}
        </>
      )}
    </div>
  );
}
