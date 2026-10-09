'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { AlertCircle, Bell, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Channel, type Me } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { Field, FieldLabel } from './ui/field';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemSeparator,
  ItemTitle,
} from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Separator } from './ui/separator';

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
const tones: Record<string, 'secondary' | 'destructive'> = {
  sent: 'secondary',
  delivered: 'secondary',
  read: 'secondary',
  failed: 'destructive',
  uncertain: 'destructive',
};
const hint = 'text-sm text-muted-foreground';
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
    <Card>
      <CardHeader>
        <CardTitle>
          <h2 className="flex items-center gap-2">
            <Bell className="size-4" /> Recordatorios de citas
          </h2>
        </CardTitle>
        <CardDescription>
          A las <strong className="font-medium text-foreground">09:00 del día anterior</strong> y{' '}
          <strong className="font-medium text-foreground">una hora antes</strong> ·{' '}
          {me.tenant.timeZone}. Se confirma el horario con Hospital antes de cada aviso.
        </CardDescription>
        <CardAction>
          <Badge variant={data?.enabled ? 'default' : 'secondary'}>
            {data?.enabled ? 'Servicio activo' : 'Servicio en pausa'}
          </Badge>
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {error && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>{error.message}</AlertDescription>
          </Alert>
        )}
        {data && (
          <>
            <Field className="max-w-md">
              <FieldLabel htmlFor="reminder-channel">Número general para recordatorios</FieldLabel>
              <NativeSelect
                id="reminder-channel"
                aria-label="Número para recordatorios"
                value={data.channelId ?? ''}
                disabled={busy}
                onChange={(e) => {
                  setTemplates(undefined);
                  void save(false, e.target.value);
                }}
              >
                <NativeSelectOption value="">Seleccionar número</NativeSelectOption>
                {channels
                  ?.filter((c) => c.enabled && !c.doctorId)
                  .map((c) => (
                    <NativeSelectOption key={c.id} value={c.id}>
                      {c.name}
                    </NativeSelectOption>
                  ))}
              </NativeSelect>
            </Field>
            <div className="flex flex-wrap gap-2">
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
                <RefreshCw />
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
            <div className="flex flex-col gap-2">
              {!data.sendEnabled && (
                <p className={hint}>
                  Envío de notificaciones pausado en la instalación. La cola se puede consultar sin
                  enviar mensajes.
                </p>
              )}
              <p className={hint}>
                {data.consentingContacts} pacientes vinculados con autorización. Regístrala en la
                ficha de la conversación o pide al paciente escribir ACTIVAR RECORDATORIOS. BAJA
                desactiva los avisos.
              </p>
              <p className={hint}>
                Se usan dos plantillas de notificación aprobadas. No se incluyen recetas ni
                información clínica en el recordatorio.
              </p>
              {templates?.map((t, i) => (
                <p key={t.name} className={hint}>
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
                <p className={hint}>Última consulta a Hospital: {format(data.lastSyncAt)}</p>
              )}
            </div>
            {data.error && (
              <Alert variant="destructive">
                <AlertCircle />
                <AlertDescription>{data.error}</AlertDescription>
              </Alert>
            )}
            <Separator />
            <h3 className="text-sm font-medium">Últimos 100 recordatorios</h3>
            {!data.rows.length && (
              <p className={hint}>
                Todavía no hay avisos programados. Se generan para citas futuras de pacientes
                vinculados con autorización.
              </p>
            )}
            <ItemGroup>
              {data.rows.map((r, index) => (
                <div key={r.id} role="listitem" className="flex flex-col">
                  {index > 0 && <ItemSeparator />}
                  <Item size="sm" className="px-0">
                    <ItemContent className="min-w-0">
                      <ItemTitle>{r.patientName ?? 'Paciente'}</ItemTitle>
                      <ItemDescription className="line-clamp-none">
                        Cita: {format(r.startsAt)}
                      </ItemDescription>
                      <p className="text-xs text-muted-foreground">
                        Aviso: {format(r.dueAt)} ·{' '}
                        {r.window === 'day_before' ? 'Día anterior' : 'Una hora antes'}
                      </p>
                      {r.reason && (
                        <p className="text-xs wrap-anywhere text-muted-foreground">{r.reason}</p>
                      )}
                    </ItemContent>
                    <ItemActions>
                      <Badge variant={tones[r.deliveryStatus ?? r.status] ?? 'outline'}>
                        {statuses[r.deliveryStatus ?? r.status] ?? r.status}
                      </Badge>
                    </ItemActions>
                  </Item>
                </div>
              ))}
            </ItemGroup>
          </>
        )}
      </CardContent>
    </Card>
  );
}
export function ReminderConsent({ contactId }: { contactId: string }) {
  const { data, mutate, error } = useSWR<{ enabled: boolean; linked: boolean }>(
    '/appointment-reminders/contacts/' + contactId,
    fetcher,
  );
  const [busy, setBusy] = useState(false);
  return (
    <section className="flex flex-col gap-2">
      <div className="flex items-center justify-between gap-2">
        <h4 className="text-sm font-medium">Recordatorios de citas</h4>
        <Bell className="size-4 text-muted-foreground" />
      </div>
      {error && <p className="text-sm text-destructive">No se pudo consultar la autorización.</p>}
      {data && (
        <>
          <p className={hint}>
            {data.enabled
              ? 'El paciente autorizó recordatorios por WhatsApp.'
              : 'Registra la autorización sólo cuando el paciente haya aceptado recibir avisos.'}{' '}
            A las 09:00 del día anterior y una hora antes.
          </p>
          <Button
            variant="outline"
            size="sm"
            className="h-auto min-h-8 w-full whitespace-normal"
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
          {!data.linked && <p className={hint}>Vincula primero el expediente de Hospital.</p>}
        </>
      )}
    </section>
  );
}
