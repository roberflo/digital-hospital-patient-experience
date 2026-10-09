'use client';
import { useId, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { toast } from 'sonner';
import { AlertCircle, Check, Plus, FileText, RefreshCw } from 'lucide-react';
import {
  CustomerCommercial,
  OpportunityCommercial,
  HospitalCommercialLink,
} from './commercial-workspace';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field, FieldDescription, FieldGroup, FieldLabel } from './ui/field';
import { Input } from './ui/input';
import { Item, ItemActions, ItemContent, ItemGroup, ItemTitle } from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Textarea } from './ui/textarea';
import { api, fetcher, type Chat, type Me, type Opportunity, type Contact } from '@/lib/api';

export const conversationStates = [
  ['open', 'Abierta'],
  ['pending', 'Pendiente'],
  ['snoozed', 'Pospuesta'],
  ['resolved', 'Resuelta'],
] as const;
export const priorities = [
  ['low', 'Baja'],
  ['normal', 'Normal'],
  ['high', 'Alta'],
  ['urgent', 'Urgente'],
] as const;
const stages = [
  ['new', 'Nuevo'],
  ['contacted', 'En seguimiento'],
  ['scheduled', 'Agendado'],
  ['won', 'Completado'],
  ['lost', 'Cerrado'],
] as const;

export function WorkflowControls({ chat, onChange }: { chat: Chat; onChange: () => void }) {
  const c = chat.conversation;
  const [busy, setBusy] = useState(false);
  const [delay, setDelay] = useState('60');
  const id = useId();
  async function update(values: Record<string, unknown>) {
    setBusy(true);
    try {
      await api(`/conversations/${c.id}/workflow`, 'PATCH', {
        ...values,
        expectedRevision: c.revision,
      });
      onChange();
      toast.success('Conversación actualizada');
    } catch (e) {
      onChange();
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="flex flex-col gap-3">
      <h4 className="text-sm font-medium">Gestión de la conversación</h4>
      <FieldGroup className="gap-3">
        <Field className="gap-1.5">
          <FieldLabel htmlFor={id + '-state'}>Estado de conversación</FieldLabel>
          <NativeSelect
            id={id + '-state'}
            size="sm"
            aria-label="Estado de conversación"
            disabled={busy}
            value={c.state}
            onChange={(e) =>
              update({
                state: e.target.value,
                snoozedUntil:
                  e.target.value === 'snoozed'
                    ? new Date(Date.now() + Number(delay) * 60000).toISOString()
                    : null,
              })
            }
          >
            {conversationStates.map(([v, l]) => (
              <NativeSelectOption key={v} value={v}>
                {l}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
        <Field className="gap-1.5">
          <FieldLabel htmlFor={id + '-delay'}>Posponer por</FieldLabel>
          <NativeSelect
            id={id + '-delay'}
            size="sm"
            value={delay}
            onChange={(e) => setDelay(e.target.value)}
          >
            <NativeSelectOption value="60">Una hora</NativeSelectOption>
            <NativeSelectOption value="1440">Un día</NativeSelectOption>
            <NativeSelectOption value="10080">Una semana</NativeSelectOption>
          </NativeSelect>
          {c.snoozedUntil && (
            <FieldDescription>
              Reapertura: {new Date(c.snoozedUntil).toLocaleString('es-SV')}
            </FieldDescription>
          )}
        </Field>
        <Field className="gap-1.5">
          <FieldLabel htmlFor={id + '-priority'}>Prioridad</FieldLabel>
          <NativeSelect
            id={id + '-priority'}
            size="sm"
            aria-label="Prioridad"
            disabled={busy}
            value={c.priority}
            onChange={(e) => update({ priority: e.target.value })}
          >
            {priorities.map(([v, l]) => (
              <NativeSelectOption key={v} value={v}>
                {l}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
      </FieldGroup>
      <form
        key={c.id + ':' + c.labels}
        className="flex flex-col gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          update({ labels: new FormData(e.currentTarget).get('labels') });
        }}
      >
        <Field className="gap-1.5">
          <FieldLabel htmlFor={id + '-labels'}>Etiquetas de conversación</FieldLabel>
          <Input
            id={id + '-labels'}
            name="labels"
            aria-label="Etiquetas de conversación"
            defaultValue={c.labels}
            placeholder="agenda, receta, seguimiento"
            maxLength={410}
          />
        </Field>
        <Button disabled={busy} size="sm" variant="outline" className="w-full">
          <Check />
          Guardar etiquetas
        </Button>
      </form>
      <p className="text-xs text-muted-foreground">
        Pendiente, pospuesta y resuelta pausan al agente. Un nuevo mensaje del paciente reabre la
        atención.
      </p>
    </section>
  );
}

export function CustomerCrm({ chat, onChange }: { chat: Chat; onChange: () => void }) {
  const { mutate: globalMutate } = useSWRConfig();
  const key = `/contacts/${chat.contact.id}/context`;
  const { data, mutate, error } = useSWR<{
    contact: Contact;
    opportunities: Opportunity[];
    conversations: unknown[];
  }>(key, fetcher, { refreshInterval: 10000 });
  const [edit, setEdit] = useState(false);
  const [create, setCreate] = useState(false);
  const [busy, setBusy] = useState(false);
  const contact = data?.contact ?? chat.contact;
  const id = useId();
  async function refresh() {
    await mutate();
    onChange();
    await globalMutate(
      (k) =>
        typeof k === 'string' &&
        (k.startsWith('/contacts') ||
          k.startsWith('/opportunities') ||
          k.startsWith('/activities') ||
          k === '/overview'),
    );
  }
  return (
    <>
      <section className="flex flex-col gap-3">
        <div className="flex items-center justify-between gap-2">
          <h4 className="text-sm font-medium">Ficha del cliente · CRM</h4>
          <Button variant="ghost" size="sm" onClick={() => setEdit(true)}>
            Editar ficha
          </Button>
        </div>
        {error && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>No se pudo cargar el contexto CRM.</AlertDescription>
          </Alert>
        )}
        <p className="text-sm break-words text-muted-foreground">
          {contact.email || 'Sin correo'}
          <br />
          {contact.hospitalCompanyName ?? 'Sin empresa asignada en Hospital'}
        </p>
        <Badge variant="secondary">
          {contact.isCustomer ? 'Cliente Hospital' : 'Contacto'}
          {contact.lifecycleStage === 'inactive' ? ' · Inactivo' : ''}
        </Badge>
        <CustomerCommercial
          contact={contact}
          onChange={() => {
            refresh();
          }}
        />
        <p className="text-xs text-muted-foreground">
          {data?.conversations.length ?? '—'} conversaciones vinculadas a esta ficha.
        </p>
        <div className="flex items-center justify-between gap-2">
          <h4 className="text-sm font-medium">Seguimientos</h4>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label="Crear seguimiento desde conversación"
            onClick={() => setCreate(true)}
          >
            <Plus />
          </Button>
        </div>
        {data?.opportunities.length === 0 && (
          <p className="text-xs text-muted-foreground">
            Crea un seguimiento para continuar la atención en el CRM.
          </p>
        )}
        {data?.opportunities.map((o) => (
          <Item variant="outline" size="sm" key={o.id}>
            <ItemContent className="min-w-0 gap-2">
              <ItemTitle>{o.title}</ItemTitle>
              <OpportunityCommercial
                opportunity={o}
                onChange={() => {
                  refresh();
                }}
              />
              <Field>
                <NativeSelect
                  size="sm"
                  aria-label={'Etapa de ' + o.title}
                  value={o.stage}
                  onChange={async (e) => {
                    try {
                      await api('/opportunities/' + o.id, 'PATCH', { stage: e.target.value });
                      await refresh();
                    } catch (err) {
                      toast.error((err as Error).message);
                    }
                  }}
                >
                  {stages.map(([v, l]) => (
                    <NativeSelectOption key={v} value={v}>
                      {l}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              </Field>
            </ItemContent>
          </Item>
        ))}
      </section>
      <Dialog open={edit} onOpenChange={setEdit}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Ficha CRM del cliente</DialogTitle>
            <DialogDescription>
              Los cambios se reflejan en Contactos y en todas sus conversaciones. El teléfono
              conserva su historial.
            </DialogDescription>
          </DialogHeader>
          <form
            className="flex flex-col gap-4"
            onSubmit={async (e) => {
              e.preventDefault();
              const f = new FormData(e.currentTarget);
              setBusy(true);
              try {
                await api(`/contacts/${contact.id}/profile`, 'PATCH', {
                  name: f.get('name'),
                  email: f.get('email'),
                  tags: f.get('tags'),
                  lifecycleStage: f.get('lifecycleStage'),
                  companyId: contact.companyId ?? null,
                });
                await refresh();
                setEdit(false);
                toast.success('Ficha CRM actualizada');
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            <FieldGroup className="gap-4">
              <Field>
                <FieldLabel htmlFor={id + '-name'}>Nombre del cliente</FieldLabel>
                <Input
                  id={id + '-name'}
                  name="name"
                  defaultValue={contact.name}
                  required
                  maxLength={200}
                />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-email'}>Correo del cliente</FieldLabel>
                <Input
                  id={id + '-email'}
                  type="email"
                  name="email"
                  defaultValue={contact.email}
                  maxLength={320}
                />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-tags'}>Etiquetas del cliente</FieldLabel>
                <Input id={id + '-tags'} name="tags" defaultValue={contact.tags} maxLength={410} />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-lifecycle'}>Estado del cliente</FieldLabel>
                <NativeSelect
                  id={id + '-lifecycle'}
                  name="lifecycleStage"
                  defaultValue={
                    contact.isCustomer
                      ? contact.lifecycleStage
                      : contact.lifecycleStage === 'inactive'
                        ? 'inactive'
                        : 'lead'
                  }
                >
                  <NativeSelectOption value="lead">Contacto nuevo</NativeSelectOption>
                  {contact.isCustomer && (
                    <NativeSelectOption value="active">Cliente activo</NativeSelectOption>
                  )}
                  <NativeSelectOption value="inactive">Inactivo</NativeSelectOption>
                </NativeSelect>
                <FieldDescription>
                  La condición de cliente y su empresa provienen de Hospital.
                </FieldDescription>
              </Field>
            </FieldGroup>
            <HospitalCommercialLink />
            <DialogFooter>
              <Button disabled={busy}>Guardar ficha CRM</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog open={create} onOpenChange={setCreate}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Seguimiento de esta conversación</DialogTitle>
            <DialogDescription>
              Quedará vinculado al cliente, al hilo de WhatsApp y al pipeline del CRM.
            </DialogDescription>
          </DialogHeader>
          <form
            className="flex flex-col gap-4"
            onSubmit={async (e) => {
              e.preventDefault();
              const f = new FormData(e.currentTarget);
              setBusy(true);
              try {
                await api('/opportunities', 'POST', {
                  title: f.get('title'),
                  value: Number(f.get('value')),
                  stage: f.get('stage'),
                  contactId: contact.id,
                  conversationId: chat.conversation.id,
                });
                await refresh();
                setCreate(false);
                toast.success('Seguimiento creado en CRM');
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            <FieldGroup className="gap-4">
              <Field>
                <FieldLabel htmlFor={id + '-title'}>Título del seguimiento</FieldLabel>
                <Input id={id + '-title'} name="title" required maxLength={200} />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-value'}>Valor estimado</FieldLabel>
                <Input
                  id={id + '-value'}
                  type="number"
                  name="value"
                  min="0"
                  step="0.01"
                  defaultValue="0"
                />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-stage'}>Etapa inicial</FieldLabel>
                <NativeSelect id={id + '-stage'} name="stage">
                  {stages.map(([v, l]) => (
                    <NativeSelectOption key={v} value={v}>
                      {l}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              </Field>
            </FieldGroup>
            <DialogFooter>
              <Button disabled={busy}>Crear seguimiento CRM</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}

type SavedReply = { id: string; title: string; body: string };
export function SavedReplies({ me, onInsert }: { me: Me; onInsert: (text: string) => void }) {
  const { data, mutate } = useSWR<SavedReply[]>('/saved-replies', fetcher);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const manager = me.role === 'admin';
  const id = useId();
  return (
    <>
      <div className="flex items-center gap-2">
        <Field className="min-w-0 flex-1">
          <NativeSelect
            size="sm"
            aria-label="Respuesta guardada"
            value=""
            onChange={(e) => {
              const reply = data?.find((r) => r.id === e.target.value);
              if (reply) onInsert(reply.body);
            }}
          >
            <NativeSelectOption value="">Insertar respuesta guardada…</NativeSelectOption>
            {data?.map((r) => (
              <NativeSelectOption key={r.id} value={r.id}>
                {r.title}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
        {manager && (
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            onClick={() => setOpen(true)}
            aria-label="Administrar respuestas guardadas"
          >
            <FileText />
          </Button>
        )}
      </div>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Respuestas guardadas</DialogTitle>
            <DialogDescription>
              Textos compartidos por tu equipo. Insertar un texto no lo envía al paciente.
            </DialogDescription>
          </DialogHeader>
          {!!data?.length && (
            <ItemGroup className="max-h-48 gap-2 overflow-y-auto">
              {data.map((r) => (
                <Item variant="outline" size="sm" key={r.id}>
                  <ItemContent className="min-w-0">
                    <ItemTitle>{r.title}</ItemTitle>
                  </ItemContent>
                  <ItemActions>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={async () => {
                        try {
                          await api('/saved-replies/' + r.id, 'DELETE');
                          mutate();
                        } catch (e) {
                          toast.error((e as Error).message);
                        }
                      }}
                    >
                      Eliminar
                    </Button>
                  </ItemActions>
                </Item>
              ))}
            </ItemGroup>
          )}
          <form
            className="flex flex-col gap-4"
            onSubmit={async (e) => {
              e.preventDefault();
              const form = e.currentTarget;
              const f = new FormData(form);
              setBusy(true);
              try {
                await api('/saved-replies', 'POST', { title: f.get('title'), body: f.get('body') });
                await mutate();
                form.reset();
                toast.success('Respuesta guardada');
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            <FieldGroup className="gap-4">
              <Field>
                <FieldLabel htmlFor={id + '-title'}>Título de respuesta</FieldLabel>
                <Input id={id + '-title'} name="title" required maxLength={80} />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-body'}>Texto de respuesta</FieldLabel>
                <Textarea id={id + '-body'} name="body" required maxLength={4000} rows={3} />
              </Field>
            </FieldGroup>
            <DialogFooter>
              <Button disabled={busy}>Guardar respuesta</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}

type Diagnostics = {
  kind: string;
  providerStatus: string;
  coexistence: boolean;
  activeWebhooks: number;
  webhookUrlConfigured: boolean;
  crmWebhookFound: boolean;
  receivesMessages: boolean;
  signatureMatches: boolean;
  lastWebhookAt?: string;
  enabled: boolean;
  sendEnabled: boolean;
  ready: boolean;
  checks: { name: string; passed: boolean }[];
};
export function ChannelConnection({ id }: { id: string }) {
  const [data, setData] = useState<Diagnostics>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  return (
    <div className="flex max-w-60 min-w-48 flex-col items-start gap-2 whitespace-normal">
      <Button
        variant="outline"
        size="sm"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError('');
          try {
            setData(await api<Diagnostics>(`/channels/${id}/diagnostics`, 'POST', {}));
          } catch (e) {
            setData(undefined);
            setError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <RefreshCw />
        {busy ? 'Verificando…' : 'Verificar conexión'}
      </Button>
      {error && (
        <Alert variant="destructive">
          <AlertCircle />
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}
      {data && (
        <div role="status" className="flex flex-col items-start gap-2 text-sm">
          <Badge variant={data.ready ? 'default' : 'outline'}>
            {data.ready ? 'Conexión configurada' : 'Conexión pendiente'}
          </Badge>
          <p className="font-medium">
            {data.kind === 'sandbox' ? 'Número de prueba' : 'Número del negocio'}
          </p>
          <p className="text-muted-foreground">
            Recepción de mensajes:{' '}
            {data.crmWebhookFound && data.receivesMessages && data.signatureMatches
              ? 'lista'
              : 'pendiente de preparación'}
            <br />
            Envío: {data.enabled && data.sendEnabled ? 'habilitado' : 'pausado'}
            <br />
            Última actualización recibida:{' '}
            {data.lastWebhookAt
              ? new Date(data.lastWebhookAt).toLocaleString('es-SV')
              : 'aún no hay mensajes'}
          </p>
          {!data.ready && (
            <p className="text-muted-foreground">
              Si acabas de conectar el número, espera un momento y vuelve a revisar. Si continúa
              pendiente, pide ayuda al administrador.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
