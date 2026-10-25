'use client';
import { GoogleCalendarConnection } from './google-calendar-connection';
import { useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import {
  HeartPulse,
  Inbox,
  CalendarDays,
  Plus,
  Sparkles,
  Check,
  ShieldCheck,
  MessageCircle,
  RefreshCw,
  Pause,
  Play,
  AlertCircle,
} from 'lucide-react';
import { toast } from 'sonner';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from './ui/card';
import {
  Field as UiField,
  FieldContent,
  FieldDescription,
  FieldGroup,
  FieldLabel,
} from './ui/field';
import { Input } from './ui/input';
import { Item, ItemActions, ItemContent, ItemDescription, ItemMedia, ItemTitle } from './ui/item';
import { Separator } from './ui/separator';
import { Spinner } from './ui/spinner';
import { Switch } from './ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';
import { Textarea } from './ui/textarea';
import { api, fetcher, type Me, type Channel } from '@/lib/api';
import { ConfirmAction } from './confirm-action';
import { AttentionSettings } from './attention-settings';
import { ChannelConnection } from './conversation-workspace';
import { EmptyState, Loading } from './workspace-shared';
type SettingsData = {
  name: string;
  guide: string;
  emergencyPhone?: string;
  emergencyWhatsApp?: boolean;
  timeZone: string;
  agentEnabled: boolean;
  googleCalendarId?: string;
  googleConnected: boolean;
  hospitalConfigured: boolean;
  kapsoConfigured: boolean;
  aiConfigured: boolean;
  sendEnabled: boolean;
  manualSendEnabled?: boolean;
  aiModel: string;
};
export function SettingsView({ me }: { me: Me }) {
  const { mutate: globalMutate } = useSWRConfig();
  const { data, error, mutate, isValidating } = useSWR<SettingsData>('/settings', fetcher, {
    shouldRetryOnError: false,
  });
  const { data: channels, mutate: refreshChannels } = useSWR<Channel[]>('/channels', fetcher);
  const [saving, setSaving] = useState(false);
  async function setChannel(c: Channel, enabled: boolean) {
    try {
      await api('/channels/' + c.id, 'PATCH', { enabled });
      refreshChannels();
      toast.success('Canal actualizado');
    } catch (e) {
      toast.error((e as Error).message);
    }
  }
  // Switching the agent is its own action: saving the guide never changes it (MR-21).
  async function setAgent(agentEnabled: boolean) {
    if (!data) return;
    setSaving(true);
    try {
      await api('/settings', 'PUT', {
        name: data.name,
        timeZone: data.timeZone,
        guide: data.guide,
        emergencyPhone: data.emergencyPhone ?? '',
        emergencyWhatsApp: data.emergencyWhatsApp ?? false,
        agentEnabled,
      });
      mutate();
      globalMutate('/me');
      toast.success(agentEnabled ? 'Atención automática activada' : 'Atención automática en pausa');
    } catch (err) {
      toast.error((err as Error).message);
    } finally {
      setSaving(false);
    }
  }
  if (me.role !== 'admin')
    return (
      <EmptyState icon={ShieldCheck} title="Solo administradores">
        Tu administrador gestiona las conexiones y la guía del negocio.
      </EmptyState>
    );
  return (
    <>
      {error && (
        <Alert variant="destructive">
          <AlertCircle />
          <AlertDescription className="flex flex-wrap items-center justify-between gap-2">
            <span>No pudimos leer la configuración. {error.message}</span>
            <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
              <RefreshCw />
              Reintentar
            </Button>
          </AlertDescription>
        </Alert>
      )}
      {data ? (
        <div className="flex flex-col gap-6">
          <div className="grid gap-6 lg:grid-cols-5">
            <Card className="min-w-0 lg:col-span-3">
              <CardHeader>
                <CardTitle>
                  <h2>Tu hospital y su guía de atención</h2>
                </CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-6">
                <UiField orientation="horizontal">
                  <FieldContent>
                    <FieldLabel htmlFor="settings-agent-enabled">Atención automática</FieldLabel>
                    <FieldDescription>
                      Permite al agente atender conversaciones habilitadas. Se aplica al momento.
                    </FieldDescription>
                  </FieldContent>
                  <ConfirmAction
                    ask={!data.agentEnabled}
                    title="¿Activar la atención automática?"
                    description="El agente empezará a responder a los pacientes por WhatsApp en las conversaciones habilitadas."
                    action="Activar atención automática"
                    onConfirm={() => setAgent(!data.agentEnabled)}
                  >
                    <Switch
                      id="settings-agent-enabled"
                      checked={data.agentEnabled}
                      disabled={saving}
                    />
                  </ConfirmAction>
                </UiField>
                <Separator />
                <form
                  key={data.name + data.timeZone + data.emergencyWhatsApp}
                  onSubmit={async (e) => {
                    e.preventDefault();
                    const f = new FormData(e.currentTarget);
                    setSaving(true);
                    try {
                      await api('/settings', 'PUT', {
                        name: f.get('name'),
                        timeZone: f.get('timeZone'),
                        guide: f.get('guide'),
                        emergencyPhone: f.get('emergencyPhone'),
                        emergencyWhatsApp: f.get('emergencyWhatsApp') === 'on',
                        agentEnabled: data.agentEnabled,
                      });
                      mutate();
                      globalMutate('/me');
                      toast.success('Configuración guardada');
                    } catch (err) {
                      toast.error((err as Error).message);
                    } finally {
                      setSaving(false);
                    }
                  }}
                >
                  <FieldGroup>
                    <div className="grid gap-4 sm:grid-cols-2">
                      <UiField>
                        <FieldLabel htmlFor="settings-name">Nombre del hospital</FieldLabel>
                        <Input
                          id="settings-name"
                          name="name"
                          defaultValue={data.name}
                          required
                          readOnly={data.hospitalConfigured}
                        />
                      </UiField>
                      <UiField>
                        <FieldLabel htmlFor="settings-time-zone">Zona horaria</FieldLabel>
                        <Input
                          id="settings-time-zone"
                          name="timeZone"
                          defaultValue={data.timeZone}
                          required
                          readOnly={data.hospitalConfigured}
                        />
                      </UiField>
                      {data.hospitalConfigured && (
                        <p className="text-sm text-muted-foreground sm:col-span-2">
                          El nombre y la zona horaria vienen de Hospital y se actualizan desde allí.
                        </p>
                      )}
                    </div>
                    <UiField>
                      <FieldLabel htmlFor="settings-guide">Guía de atención</FieldLabel>
                      <Textarea
                        id="settings-guide"
                        rows={9}
                        name="guide"
                        defaultValue={data.guide}
                        placeholder="Horarios, servicios, instrucciones de atención y reglas de derivación…"
                        maxLength={30000}
                      />
                      <FieldDescription>
                        El agente utiliza esta guía junto con las herramientas autorizadas del
                        hospital. Las decisiones clínicas se derivan al doctor.
                      </FieldDescription>
                    </UiField>
                    <UiField>
                      <FieldLabel htmlFor="settings-emergency-phone">
                        Teléfono de urgencias
                      </FieldLabel>
                      <Input
                        id="settings-emergency-phone"
                        name="emergencyPhone"
                        type="tel"
                        defaultValue={data.emergencyPhone ?? ''}
                        placeholder="Ej. 132 o +503 2200 0000"
                        maxLength={20}
                      />
                      <FieldDescription>
                        El agente lo envía al paciente cada vez que deriva una conversación a tu
                        equipo, con un botón para llamar. Puede ser un número corto, como 132 o 911.
                      </FieldDescription>
                    </UiField>
                    <UiField orientation="horizontal">
                      <FieldContent>
                        <FieldLabel htmlFor="settings-emergency-whatsapp">
                          Este número tiene WhatsApp
                        </FieldLabel>
                        <FieldDescription>
                          En una emergencia el paciente recibe también un botón para escribirle.
                          Requiere el número con código de país, como +503 7000 0000.
                        </FieldDescription>
                      </FieldContent>
                      <Switch
                        id="settings-emergency-whatsapp"
                        name="emergencyWhatsApp"
                        defaultChecked={data.emergencyWhatsApp ?? false}
                      />
                    </UiField>
                    <div className="flex justify-end">
                      <Button disabled={saving}>
                        {saving ? <Spinner /> : <Check />}Guardar cambios
                      </Button>
                    </div>
                  </FieldGroup>
                </form>
              </CardContent>
            </Card>
            <Card className="min-w-0 lg:col-span-2">
              <CardHeader>
                <CardTitle>
                  <h2>Conexiones</h2>
                </CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-4">
                <div className="flex flex-col gap-2">
                  {[
                    [HeartPulse, 'Hospital', data.hospitalConfigured, 'Agenda y expediente'],
                    [
                      MessageCircle,
                      'WhatsApp',
                      data.kapsoConfigured,
                      data.sendEnabled
                        ? 'Envío habilitado'
                        : data.manualSendEnabled
                          ? 'Envío manual habilitado'
                          : 'Envío en pausa',
                    ],
                    [Sparkles, 'Agente de atención', data.aiConfigured, 'Respuestas y seguimiento'],
                    [CalendarDays, 'Google Calendar', data.googleConnected, 'Agenda compartida'],
                  ].map(([Icon, title, ok, sub], i) => {
                    const I = Icon as typeof Inbox;
                    return (
                      <Item variant="outline" size="sm" key={i}>
                        <ItemMedia variant="icon">
                          <I />
                        </ItemMedia>
                        <ItemContent>
                          <ItemTitle>{title as string}</ItemTitle>
                          <ItemDescription>{sub as string}</ItemDescription>
                        </ItemContent>
                        <ItemActions>
                          <Badge variant={ok ? 'secondary' : 'outline'}>
                            {ok ? 'Configurado' : 'Pendiente'}
                          </Badge>
                        </ItemActions>
                      </Item>
                    );
                  })}
                </div>
                <p className="text-sm text-muted-foreground">
                  Tu cuenta del Hospital reúne a tu equipo, pacientes y agenda. Google y WhatsApp te
                  pedirán autorización al conectarlos.
                </p>
                <a
                  className="text-sm font-medium underline underline-offset-4"
                  href="/?view=hospital"
                >
                  Revisar la conexión en «Mi hospital»
                </a>
                <GoogleCalendarConnection
                  connected={data.googleConnected}
                  selected={data.googleCalendarId}
                  onChange={() => mutate()}
                />
              </CardContent>
            </Card>
            <Card className="min-w-0 lg:col-span-5">
              <CardHeader>
                <CardTitle>
                  <h2>Números de WhatsApp</h2>
                </CardTitle>
                <CardAction>
                  <Button asChild size="sm">
                    <a href="/whatsapp">
                      <Plus />
                      Agregar mi número
                    </a>
                  </Button>
                </CardAction>
              </CardHeader>
              <CardContent>
                <div className="w-0 min-w-full">
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Canal</TableHead>
                        <TableHead>Atención</TableHead>
                        <TableHead>Uso en el celular</TableHead>
                        <TableHead>Conexión</TableHead>
                        <TableHead>Estado</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {channels?.map((c) => (
                        <TableRow key={c.id}>
                          <TableCell>
                            <div className="flex items-center gap-2">
                              <MessageCircle className="size-4 text-muted-foreground" />
                              <strong className="font-medium">{c.name}</strong>
                            </div>
                          </TableCell>
                          <TableCell>{c.doctorId ? 'Doctor' : 'General'}</TableCell>
                          <TableCell>
                            {c.coexistence ? 'WhatsApp Business y Recepción' : 'Recepción'}
                          </TableCell>
                          <TableCell>
                            <ChannelConnection id={c.id} />
                          </TableCell>
                          <TableCell>
                            <div className="flex flex-wrap items-center gap-2">
                              <Badge variant={c.enabled ? 'secondary' : 'outline'}>
                                {c.enabled ? 'Activo' : 'En pausa'}
                              </Badge>
                              <ConfirmAction
                                ask={c.enabled}
                                title={`¿Pausar ${c.name}?`}
                                description="Mientras esté en pausa, Recepción no atiende ni envía mensajes por este número."
                                action="Pausar canal"
                                destructive
                                onConfirm={() => setChannel(c, !c.enabled)}
                              >
                                <Button
                                  variant="outline"
                                  size="sm"
                                  aria-label={(c.enabled ? 'Pausar ' : 'Habilitar ') + c.name}
                                >
                                  {c.enabled ? <Pause /> : <Play />}
                                  {c.enabled ? 'Pausar' : 'Habilitar'}
                                </Button>
                              </ConfirmAction>
                            </div>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </div>
              </CardContent>
            </Card>
            <Card className="min-w-0 lg:col-span-5">
              <CardHeader>
                <CardTitle>
                  <h2>Equipo de atención</h2>
                </CardTitle>
              </CardHeader>
              <CardContent>
                <a className="text-sm font-medium underline underline-offset-4" href="/?view=team">
                  Administrar equipo y conversaciones ↗
                </a>
              </CardContent>
            </Card>
          </div>
          <AttentionSettings />
        </div>
      ) : (
        !error && <Loading />
      )}
    </>
  );
}
