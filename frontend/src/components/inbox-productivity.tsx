'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { toast } from 'sonner';
import { Bookmark, Zap, Plus, Trash2, Settings2 } from 'lucide-react';
import { api, fetcher, type Chat, type Me } from '@/lib/api';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogTitle, DialogDescription } from './ui/dialog';
import { conversationStates, priorities } from './conversation-workspace';

export type InboxFilters = {
  state: string;
  assignment: string;
  channelId: string;
  priority: string;
  label: string;
  mode: string;
};
type SavedView = { id: string; name: string; filters: string };
export function SavedInboxViews({
  filters,
  onApply,
}: {
  filters: InboxFilters;
  onApply: (filters: InboxFilters) => void;
}) {
  const { data, mutate, error } = useSWR<SavedView[]>('/inbox-views', fetcher);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState('');
  return (
    <>
      <div className="saved-view-bar">
        <Bookmark size={14} />
        <select
          aria-label="Vistas guardadas"
          value={selected}
          onChange={(e) => {
            const row = data?.find((x) => x.id === e.target.value);
            setSelected(e.target.value);
            if (row) onApply(JSON.parse(row.filters));
          }}
        >
          <option value="">Mis vistas guardadas</option>
          {data?.map((v) => (
            <option value={v.id} key={v.id}>
              {v.name}
            </option>
          ))}
        </select>
        <Button
          size="icon"
          variant="ghost"
          aria-label="Administrar vistas guardadas"
          onClick={() => setOpen(true)}
        >
          <Settings2 size={14} />
        </Button>
      </div>
      {error && <p className="hint">No se pudieron cargar tus vistas.</p>}
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogTitle>Mis vistas de bandeja</DialogTitle>
          <DialogDescription>
            Guarda los filtros actuales para volver a usarlos. Las vistas son personales y se
            conservan entre sesiones.
          </DialogDescription>
          <form
            className="dialog-form"
            onSubmit={async (e) => {
              e.preventDefault();
              const form = e.currentTarget;
              const f = new FormData(form);
              setBusy(true);
              try {
                await api('/inbox-views', 'POST', { name: f.get('name'), filters });
                await mutate();
                form.reset();
                toast.success('Vista guardada');
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            <label>
              Nombre de la vista
              <input name="name" required maxLength={60} placeholder="Mis consultas pendientes" />
            </label>
            <Button disabled={busy}>
              <Plus />
              Guardar filtros actuales
            </Button>
          </form>
          <div className="productivity-list">
            {data?.map((v) => (
              <div key={v.id}>
                <span>{v.name}</span>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={'Eliminar vista ' + v.name}
                  disabled={busy}
                  onClick={async () => {
                    setBusy(true);
                    try {
                      await api('/inbox-views/' + v.id, 'DELETE');
                      if (selected === v.id) setSelected('');
                      await mutate();
                    } catch (err) {
                      toast.error((err as Error).message);
                    } finally {
                      setBusy(false);
                    }
                  }}
                >
                  <Trash2 size={14} />
                </Button>
              </div>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
type Macro = {
  id: string;
  name: string;
  state: string | null;
  priority: string | null;
  labels: string;
  note: string;
  takeOwnership: boolean;
};
function MacroPreview({ macro }: { macro: Macro }) {
  return (
    <ul className="macro-preview">
      {macro.state && <li>Estado: {conversationStates.find(([v]) => v === macro.state)?.[1]}</li>}
      {macro.priority && <li>Prioridad: {priorities.find(([v]) => v === macro.priority)?.[1]}</li>}
      {macro.labels && <li>Añadir etiquetas: {macro.labels}</li>}
      {macro.takeOwnership && <li>Asignarme la conversación y pausar al agente.</li>}
      {macro.note && <li>Nota interna: {macro.note}</li>}
    </ul>
  );
}
export function ConversationMacros({
  chat,
  me,
  onChange,
}: {
  chat: Chat;
  me: Me;
  onChange: () => void;
}) {
  const { data, mutate, error } = useSWR<Macro[]>('/macros', fetcher);
  const [open, setOpen] = useState(false);
  const [manage, setManage] = useState(false);
  const [selected, setSelected] = useState('');
  const [busy, setBusy] = useState(false);
  const macro = data?.find((x) => x.id === selected);
  const supervisor = ['admin', 'platform_admin', 'supervisor'].includes(me.role);
  return (
    <>
      <Button size="sm" variant="ghost" aria-label="Acciones rápidas" onClick={() => setOpen(true)}>
        <Zap size={14} />
        Acciones
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogTitle>Acciones rápidas</DialogTitle>
          <DialogDescription>
            Aplica un procedimiento del hospital a esta conversación. Los cambios quedan en su
            historial; las notas son internas.
          </DialogDescription>
          {error && <p role="alert">No se pudieron cargar las macros.</p>}
          <div className="dialog-form">
            <label>
              Procedimiento
              <select
                aria-label="Procedimiento"
                value={selected}
                onChange={(e) => setSelected(e.target.value)}
              >
                <option value="">Seleccionar macro…</option>
                {data?.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
              </select>
            </label>
            {macro && <MacroPreview macro={macro} />}
            {data?.length === 0 && (
              <p className="hint">
                Un supervisor puede crear procedimientos como «Revisar consulta con doctor».
              </p>
            )}
            <Button
              disabled={!macro || busy}
              onClick={async () => {
                if (!macro) return;
                setBusy(true);
                try {
                  await api(`/conversations/${chat.conversation.id}/macros/${macro.id}`, 'POST', {
                    expectedRevision: chat.conversation.revision,
                  });
                  onChange();
                  setOpen(false);
                  toast.success('Macro aplicada');
                } catch (err) {
                  onChange();
                  toast.error((err as Error).message);
                } finally {
                  setBusy(false);
                }
              }}
            >
              Aplicar a esta conversación
            </Button>
            {supervisor && (
              <Button
                variant="outline"
                onClick={() => {
                  setOpen(false);
                  setManage(true);
                }}
              >
                Administrar macros
              </Button>
            )}
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={manage} onOpenChange={setManage}>
        <DialogContent>
          <DialogTitle>Macros del hospital</DialogTitle>
          <DialogDescription>
            Define acciones repetibles para recepción. Disponibles para todos los usuarios de este
            hospital.
          </DialogDescription>
          <form
            className="dialog-form"
            onSubmit={async (e) => {
              e.preventDefault();
              const form = e.currentTarget;
              const f = new FormData(form);
              setBusy(true);
              try {
                await api('/macros', 'POST', {
                  name: f.get('name'),
                  state: f.get('state') || null,
                  priority: f.get('priority') || null,
                  labels: f.get('labels'),
                  note: f.get('note'),
                  takeOwnership: f.get('takeOwnership') === 'on',
                });
                await mutate();
                form.reset();
                toast.success('Macro creada');
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            <label>
              Nombre de macro
              <input
                name="name"
                required
                maxLength={80}
                placeholder="Revisar consulta con doctor"
              />
            </label>
            <div className="macro-fields">
              <label>
                Cambiar estado
                <select name="state" aria-label="Cambiar estado">
                  <option value="">Conservar estado</option>
                  {conversationStates
                    .filter(([v]) => v !== 'snoozed')
                    .map(([v, l]) => (
                      <option value={v} key={v}>
                        {l}
                      </option>
                    ))}
                </select>
              </label>
              <label>
                Cambiar prioridad
                <select name="priority" aria-label="Cambiar prioridad">
                  <option value="">Conservar prioridad</option>
                  {priorities.map(([v, l]) => (
                    <option value={v} key={v}>
                      {l}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <label>
              Añadir etiquetas
              <input name="labels" maxLength={410} placeholder="revision-doctor" />
            </label>
            <label>
              Nota interna de la macro
              <textarea
                name="note"
                maxLength={4000}
                placeholder="Recepción solicita revisión del doctor."
              />
            </label>
            <label className="macro-checkbox">
              <input name="takeOwnership" type="checkbox" />
              Asignar a quien aplica la macro
            </label>
            <Button disabled={busy}>Crear macro</Button>
          </form>
          <div className="productivity-list">
            {data?.map((m) => (
              <div key={m.id}>
                <details>
                  <summary>{m.name}</summary>
                  <MacroPreview macro={m} />
                </details>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={'Eliminar macro ' + m.name}
                  disabled={busy}
                  onClick={async () => {
                    setBusy(true);
                    try {
                      await api('/macros/' + m.id, 'DELETE');
                      await mutate();
                    } catch (err) {
                      toast.error((err as Error).message);
                    } finally {
                      setBusy(false);
                    }
                  }}
                >
                  <Trash2 size={14} />
                </Button>
              </div>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
