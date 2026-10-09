'use client';
import { useId, useState } from 'react';
import useSWR from 'swr';
import { toast } from 'sonner';
import { AlertCircle, Bookmark, ChevronDown, Zap, Plus, Trash2, Settings2 } from 'lucide-react';
import { api, fetcher, type Chat, type Me } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Button } from './ui/button';
import { Checkbox } from './ui/checkbox';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from './ui/collapsible';
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
  const id = useId();
  return (
    <>
      <div className="flex items-center gap-2">
        <Bookmark className="size-4 shrink-0 text-muted-foreground" />
        <Field className="min-w-0 flex-1">
          <NativeSelect
            size="sm"
            aria-label="Vistas guardadas"
            value={selected}
            onChange={(e) => {
              const row = data?.find((x) => x.id === e.target.value);
              setSelected(e.target.value);
              if (row) onApply(JSON.parse(row.filters));
            }}
          >
            <NativeSelectOption value="">Mis vistas guardadas</NativeSelectOption>
            {data?.map((v) => (
              <NativeSelectOption value={v.id} key={v.id}>
                {v.name}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
        <Button
          size="icon-sm"
          variant="ghost"
          aria-label="Administrar vistas guardadas"
          onClick={() => setOpen(true)}
        >
          <Settings2 />
        </Button>
      </div>
      {error && <p className="text-xs text-muted-foreground">No se pudieron cargar tus vistas.</p>}
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Mis vistas de bandeja</DialogTitle>
            <DialogDescription>
              Guarda los filtros actuales para volver a usarlos. Las vistas son personales y se
              conservan entre sesiones.
            </DialogDescription>
          </DialogHeader>
          <form
            className="flex flex-col gap-4"
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
            <Field>
              <FieldLabel htmlFor={id + '-name'}>Nombre de la vista</FieldLabel>
              <Input
                id={id + '-name'}
                name="name"
                required
                maxLength={60}
                placeholder="Mis consultas pendientes"
              />
            </Field>
            <DialogFooter>
              <Button disabled={busy}>
                <Plus />
                Guardar filtros actuales
              </Button>
            </DialogFooter>
          </form>
          {!!data?.length && (
            <ItemGroup className="max-h-52 gap-2 overflow-y-auto">
              {data.map((v) => (
                <Item variant="outline" size="sm" key={v.id}>
                  <ItemContent className="min-w-0">
                    <ItemTitle>{v.name}</ItemTitle>
                  </ItemContent>
                  <ItemActions>
                    <Button
                      size="icon-sm"
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
                      <Trash2 />
                    </Button>
                  </ItemActions>
                </Item>
              ))}
            </ItemGroup>
          )}
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
    <ul className="flex list-disc flex-col gap-1 pl-5 text-sm break-words text-muted-foreground">
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
  const supervisor = me.role === 'admin';
  const id = useId();
  return (
    <>
      <Button size="sm" variant="ghost" aria-label="Acciones rápidas" onClick={() => setOpen(true)}>
        <Zap />
        Acciones
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Acciones rápidas</DialogTitle>
            <DialogDescription>
              Aplica un procedimiento del hospital a esta conversación. Los cambios quedan en su
              historial; las notas son internas.
            </DialogDescription>
          </DialogHeader>
          {error && (
            <Alert variant="destructive">
              <AlertCircle />
              <AlertDescription>No se pudieron cargar las macros.</AlertDescription>
            </Alert>
          )}
          <Field>
            <FieldLabel htmlFor={id + '-macro'}>Procedimiento</FieldLabel>
            <NativeSelect
              id={id + '-macro'}
              aria-label="Procedimiento"
              value={selected}
              onChange={(e) => setSelected(e.target.value)}
            >
              <NativeSelectOption value="">Seleccionar macro…</NativeSelectOption>
              {data?.map((m) => (
                <NativeSelectOption key={m.id} value={m.id}>
                  {m.name}
                </NativeSelectOption>
              ))}
            </NativeSelect>
            {data?.length === 0 && (
              <FieldDescription>
                Un administrador puede crear procedimientos como «Revisar consulta con doctor».
              </FieldDescription>
            )}
          </Field>
          {macro && <MacroPreview macro={macro} />}
          <DialogFooter>
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
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <Dialog open={manage} onOpenChange={setManage}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Macros del hospital</DialogTitle>
            <DialogDescription>
              Define acciones repetibles para recepción. Disponibles para todos los usuarios de este
              hospital.
            </DialogDescription>
          </DialogHeader>
          <form
            className="flex flex-col gap-4"
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
            <FieldGroup className="gap-4">
              <Field>
                <FieldLabel htmlFor={id + '-name'}>Nombre de macro</FieldLabel>
                <Input
                  id={id + '-name'}
                  name="name"
                  required
                  maxLength={80}
                  placeholder="Revisar consulta con doctor"
                />
              </Field>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <Field>
                  <FieldLabel htmlFor={id + '-state'}>Cambiar estado</FieldLabel>
                  <NativeSelect id={id + '-state'} name="state" aria-label="Cambiar estado">
                    <NativeSelectOption value="">Conservar estado</NativeSelectOption>
                    {conversationStates
                      .filter(([v]) => v !== 'snoozed')
                      .map(([v, l]) => (
                        <NativeSelectOption value={v} key={v}>
                          {l}
                        </NativeSelectOption>
                      ))}
                  </NativeSelect>
                </Field>
                <Field>
                  <FieldLabel htmlFor={id + '-priority'}>Cambiar prioridad</FieldLabel>
                  <NativeSelect
                    id={id + '-priority'}
                    name="priority"
                    aria-label="Cambiar prioridad"
                  >
                    <NativeSelectOption value="">Conservar prioridad</NativeSelectOption>
                    {priorities.map(([v, l]) => (
                      <NativeSelectOption value={v} key={v}>
                        {l}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                </Field>
              </div>
              <Field>
                <FieldLabel htmlFor={id + '-labels'}>Añadir etiquetas</FieldLabel>
                <Input
                  id={id + '-labels'}
                  name="labels"
                  maxLength={410}
                  placeholder="revision-doctor"
                />
              </Field>
              <Field>
                <FieldLabel htmlFor={id + '-note'}>Nota interna de la macro</FieldLabel>
                <Textarea
                  id={id + '-note'}
                  name="note"
                  maxLength={4000}
                  placeholder="Recepción solicita revisión del doctor."
                />
              </Field>
              <Field orientation="horizontal">
                <Checkbox id={id + '-own'} name="takeOwnership" />
                <FieldLabel htmlFor={id + '-own'}>Asignar a quien aplica la macro</FieldLabel>
              </Field>
            </FieldGroup>
            <DialogFooter>
              <Button disabled={busy}>Crear macro</Button>
            </DialogFooter>
          </form>
          {!!data?.length && (
            <ItemGroup className="max-h-52 gap-2 overflow-y-auto">
              {data.map((m) => (
                <Item variant="outline" size="sm" key={m.id}>
                  <Collapsible className="flex w-full flex-col gap-2">
                    <div className="flex items-center justify-between gap-2">
                      <ItemContent className="min-w-0">
                        <CollapsibleTrigger asChild>
                          <Button variant="ghost" size="sm" className="w-full justify-start">
                            <ChevronDown />
                            <span className="truncate">{m.name}</span>
                          </Button>
                        </CollapsibleTrigger>
                      </ItemContent>
                      <ItemActions>
                        <Button
                          size="icon-sm"
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
                          <Trash2 />
                        </Button>
                      </ItemActions>
                    </div>
                    <CollapsibleContent>
                      <MacroPreview macro={m} />
                    </CollapsibleContent>
                  </Collapsible>
                </Item>
              ))}
            </ItemGroup>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}
