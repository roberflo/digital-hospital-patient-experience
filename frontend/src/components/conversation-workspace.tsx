'use client';
import { useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { toast } from 'sonner';
import { Check, Plus, FileText, RefreshCw } from 'lucide-react';
import {
  CustomerCommercial,
  OpportunityCommercial,
  HospitalCommercialLink,
} from './commercial-workspace';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogTitle, DialogDescription } from './ui/dialog';
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
    <div className="detail-section workflow-controls">
      <h4>Gestión de la conversación</h4>
      <label>
        Estado de conversación
        <select
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
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
      </label>
      <label>
        Posponer por
        <select value={delay} onChange={(e) => setDelay(e.target.value)}>
          <option value="60">Una hora</option>
          <option value="1440">Un día</option>
          <option value="10080">Una semana</option>
        </select>
      </label>
      {c.snoozedUntil && (
        <p className="hint">Reapertura: {new Date(c.snoozedUntil).toLocaleString('es-SV')}</p>
      )}
      <label>
        Prioridad
        <select
          aria-label="Prioridad"
          disabled={busy}
          value={c.priority}
          onChange={(e) => update({ priority: e.target.value })}
        >
          {priorities.map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
      </label>
      <form
        key={c.id + ':' + c.labels}
        onSubmit={(e) => {
          e.preventDefault();
          update({ labels: new FormData(e.currentTarget).get('labels') });
        }}
      >
        <label>
          Etiquetas de conversación
          <input
            name="labels"
            aria-label="Etiquetas de conversación"
            defaultValue={c.labels}
            placeholder="agenda, receta, seguimiento"
            maxLength={410}
          />
        </label>
        <Button disabled={busy} size="sm" variant="outline" className="w-full mt-2">
          <Check />
          Guardar etiquetas
        </Button>
      </form>
      <p className="hint">
        Pendiente, pospuesta y resuelta pausan al agente. Un nuevo mensaje del paciente reabre la
        atención.
      </p>
    </div>
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
      <div className="detail-section crm-context">
        <div className="section-heading">
          <h4>Ficha del cliente · CRM</h4>
          <button onClick={() => setEdit(true)}>Editar ficha</button>
        </div>
        {error && <p className="error">No se pudo cargar el contexto CRM.</p>}
        <p>
          {contact.email || 'Sin correo'}
          <br />
          {contact.hospitalCompanyName ?? 'Sin empresa asignada en Hospital'}
        </p>
        <span className="tag">
          {contact.isCustomer ? 'Cliente Hospital' : 'Contacto'}
          {contact.lifecycleStage === 'inactive' ? ' · Inactivo' : ''}
        </span>
        <CustomerCommercial
          contact={contact}
          onChange={() => {
            refresh();
          }}
        />
        <p className="hint">
          {data?.conversations.length ?? '—'} conversaciones vinculadas a esta ficha.
        </p>
        <div className="section-heading">
          <h4>Seguimientos</h4>
          <button aria-label="Crear seguimiento desde conversación" onClick={() => setCreate(true)}>
            <Plus size={15} />
          </button>
        </div>
        {data?.opportunities.length === 0 && (
          <p className="hint">Crea un seguimiento para continuar la atención en el CRM.</p>
        )}
        {data?.opportunities.map((o) => (
          <div className="conversation-followup" key={o.id}>
            <strong>{o.title}</strong>
            <OpportunityCommercial
              opportunity={o}
              onChange={() => {
                refresh();
              }}
            />
            <select
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
                <option key={v} value={v}>
                  {l}
                </option>
              ))}
            </select>
          </div>
        ))}
      </div>
      <Dialog open={edit} onOpenChange={setEdit}>
        <DialogContent>
          <DialogTitle>Ficha CRM del cliente</DialogTitle>
          <DialogDescription>
            Los cambios se reflejan en Contactos y en todas sus conversaciones. El teléfono conserva
            su historial.
          </DialogDescription>
          <form
            className="dialog-form"
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
            <label>
              Nombre del cliente
              <input name="name" defaultValue={contact.name} required maxLength={200} />
            </label>
            <label>
              Correo del cliente
              <input type="email" name="email" defaultValue={contact.email} maxLength={320} />
            </label>
            <label>
              Etiquetas del cliente
              <input name="tags" defaultValue={contact.tags} maxLength={410} />
            </label>
            <label>
              Estado del cliente
              <select
                name="lifecycleStage"
                defaultValue={
                  contact.isCustomer
                    ? contact.lifecycleStage
                    : contact.lifecycleStage === 'inactive'
                      ? 'inactive'
                      : 'lead'
                }
              >
                <option value="lead">Contacto nuevo</option>
                {contact.isCustomer && <option value="active">Cliente activo</option>}
                <option value="inactive">Inactivo</option>
              </select>
            </label>
            <p className="hint">La condición de cliente y su empresa provienen de Hospital.</p>
            <HospitalCommercialLink />
            <Button disabled={busy}>Guardar ficha CRM</Button>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog open={create} onOpenChange={setCreate}>
        <DialogContent>
          <DialogTitle>Seguimiento de esta conversación</DialogTitle>
          <DialogDescription>
            Quedará vinculado al cliente, al hilo de WhatsApp y al pipeline del CRM.
          </DialogDescription>
          <form
            className="dialog-form"
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
            <label>
              Título del seguimiento
              <input name="title" required maxLength={200} />
            </label>
            <label>
              Valor estimado
              <input type="number" name="value" min="0" step="0.01" defaultValue="0" />
            </label>
            <label>
              Etapa inicial
              <select name="stage">
                {stages.map(([v, l]) => (
                  <option key={v} value={v}>
                    {l}
                  </option>
                ))}
              </select>
            </label>
            <Button disabled={busy}>Crear seguimiento CRM</Button>
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
  const manager = ['admin', 'platform_admin', 'supervisor'].includes(me.role);
  return (
    <>
      <div className="saved-replies">
        <select
          aria-label="Respuesta guardada"
          value=""
          onChange={(e) => {
            const reply = data?.find((r) => r.id === e.target.value);
            if (reply) onInsert(reply.body);
          }}
        >
          <option value="">Insertar respuesta guardada…</option>
          {data?.map((r) => (
            <option key={r.id} value={r.id}>
              {r.title}
            </option>
          ))}
        </select>
        {manager && (
          <button
            type="button"
            onClick={() => setOpen(true)}
            aria-label="Administrar respuestas guardadas"
          >
            <FileText size={15} />
          </button>
        )}
      </div>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogTitle>Respuestas guardadas</DialogTitle>
          <DialogDescription>
            Textos compartidos por tu equipo. Insertar un texto no lo envía al paciente.
          </DialogDescription>
          <div className="saved-reply-list">
            {data?.map((r) => (
              <div key={r.id}>
                <span>{r.title}</span>
                <button
                  type="button"
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
                </button>
              </div>
            ))}
          </div>
          <form
            className="dialog-form"
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
            <label>
              Título de respuesta
              <input name="title" required maxLength={80} />
            </label>
            <label>
              Texto de respuesta
              <textarea name="body" required maxLength={4000} rows={3} />
            </label>
            <Button disabled={busy}>Guardar respuesta</Button>
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
    <div className="channel-diagnostics">
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
        <RefreshCw size={13} />
        {busy ? 'Verificando…' : 'Verificar conexión'}
      </Button>
      {error && <p className="error">{error}</p>}
      {data && (
        <div role="status">
          <strong>{data.ready ? 'Conexión configurada' : 'Conexión pendiente'}</strong>
          <p>
            {data.kind === 'sandbox' ? 'Sandbox' : 'Número de negocio'} · Proveedor:{' '}
            {data.providerStatus}
          </p>
          <p>
            Webhook CRM:{' '}
            {data.crmWebhookFound
              ? 'encontrado'
              : data.webhookUrlConfigured
                ? 'no registrado'
                : 'falta URL pública'}
            <br />
            Eventos entrantes: {data.receivesMessages ? 'sí' : 'no'}
            <br />
            Firma: {data.signatureMatches ? 'coincide' : 'sin verificar'}
            <br />
            Envío: {data.enabled && data.sendEnabled ? 'habilitado' : 'pausado'}
            <br />
            Último evento:{' '}
            {data.lastWebhookAt
              ? new Date(data.lastWebhookAt).toLocaleString('es-SV')
              : 'sin eventos recibidos'}
          </p>
        </div>
      )}
    </div>
  );
}
