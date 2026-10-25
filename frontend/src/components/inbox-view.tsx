'use client';
import { useEffect, useRef, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import {
  UserRound,
  ShieldCheck,
  MessageCircle,
  RefreshCw,
  ListFilter,
  Forward,
} from 'lucide-react';
import { toast } from 'sonner';
import { Badge } from './ui/badge';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from './ui/collapsible';
import { Button } from './ui/button';
import { ButtonGroup } from './ui/button-group';
import { Card } from './ui/card';
import { Checkbox } from './ui/checkbox';
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from './ui/empty';
import { Item, ItemContent, ItemDescription, ItemMedia, ItemTitle } from './ui/item';
import { Label } from './ui/label';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { api, fetcher, type Me, type Chat, type Channel } from '@/lib/api';
import { cn } from '@/lib/utils';
import { defaultInboxFilters, filtersActive, inboxQuery } from '@/lib/inbox-filters';
import { ConfirmAction } from './confirm-action';
import { SavedInboxViews } from './inbox-productivity';
import { managesTeam, memberLabel, type Workload } from './team-workspace';
import { conversationStates, priorities } from './conversation-workspace';
import {
  time,
  dayLabel,
  PersonAvatar,
  StatusBadge,
  attention,
  EmptyState,
  ErrorBox,
  Loading,
} from './workspace-shared';
import { ChatPanel } from './chat-panel';
export function InboxView({
  me,
  search,
  onSearch,
  initialAssignment,
}: {
  me: Me;
  search: string;
  onSearch: (search: string) => void;
  initialAssignment: string | null;
}) {
  const [state, setState] = useState(initialAssignment ? '' : 'open');
  const [assignment, setAssignment] = useState(initialAssignment ?? 'all');
  const { data: workload } = useSWR<Workload>('/members/workload', fetcher, {
    refreshInterval: 10000,
  });
  const { mutate: refreshTeam } = useSWRConfig();
  const [checked, setChecked] = useState<Record<string, number>>({});
  const [bulkTarget, setBulkTarget] = useState('');
  const [assigning, setAssigning] = useState(false);
  const [linkedPhone, setLinkedPhone] = useState('');
  const [linkedNumber, setLinkedNumber] = useState('');
  const [linkedConversation, setLinkedConversation] = useState('');
  const [channelId, setChannelId] = useState('');
  // MR-45: a doctor with their own number starts on it, unless a link or team view says otherwise.
  const ownNumberPending = useRef(!initialAssignment);
  function clearLinkedConversation() {
    setLinkedPhone('');
    setLinkedNumber('');
    setLinkedConversation('');
    const url = new URL(location.href);
    for (const key of ['conversationId', 'phone', 'phoneNumberId']) url.searchParams.delete(key);
    history.replaceState(null, '', url.pathname + url.search);
  }
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    if (['conversationId', 'phone', 'channelId'].some((k) => params.get(k)))
      ownNumberPending.current = false;
    if (params.get('conversationId')) {
      setLinkedConversation(params.get('conversationId')!);
      setSelected(params.get('conversationId')!);
      setState('');
    }
    if (params.get('phone')) {
      setLinkedPhone(params.get('phone')!);
      setLinkedNumber(params.get('phoneNumberId') ?? '');
      setState('');
    }
    // A number's history (WhatsApp settings): every conversation on that channel, in any state.
    if (params.get('channelId')) {
      setChannelId(params.get('channelId')!);
      setState('');
    }
  }, []);
  const [priority, setPriority] = useState('');
  const [label, setLabel] = useState('');
  const [filter, setFilter] = useState('all');
  const [moreFilters, setMoreFilters] = useState(false);
  const [page, setPage] = useState(1);
  const { data: channels } = useSWR<Channel[]>('/channels', fetcher);
  useEffect(() => {
    if (!channels || !ownNumberPending.current) return;
    ownNumberPending.current = false;
    const own = channels.find((c) => c.doctorId && c.doctorId === me.subject);
    if (!own) return;
    setChannelId(own.id);
    setMoreFilters(true); // the filter shows where it is cleared: «Todos los canales»
  }, [channels, me.subject]);
  useEffect(() => setPage(1), [state, assignment, channelId, priority, label, filter]);
  useEffect(
    () => setChecked({}),
    [state, assignment, channelId, priority, label, filter, page, search],
  );
  // MR-53: the search runs on the server, once the person pauses typing.
  const [q, setQ] = useState('');
  useEffect(() => {
    const wait = setTimeout(() => setQ(search.trim()), 300);
    return () => clearTimeout(wait);
  }, [search]);
  useEffect(() => setPage(1), [q]);
  const filters = { state, assignment, channelId, priority, label, mode: filter };
  const query = inboxQuery(filters, page);
  if (q) query.set('q', q);
  if (linkedConversation) query.set('conversationId', linkedConversation);
  if (linkedPhone) query.set('phone', linkedPhone);
  if (linkedNumber) query.set('phoneNumberId', linkedNumber);
  const {
    data: chats,
    error,
    mutate,
  } = useSWR<Chat[]>('/conversations?' + query, fetcher, { refreshInterval: 5000 });
  const [selected, setSelected] = useState<string | null>(null);
  const filtered = chats;
  const { data: labelOptions } = useSWR<string[]>('/conversations/labels', fetcher);
  // MR-54: the open conversation is in the address; a reload opens it again (read above, as a link).
  function select(id: string | null) {
    setSelected(id);
    const url = new URL(location.href);
    if (id) url.searchParams.set('conversationId', id);
    else url.searchParams.delete('conversationId');
    history.replaceState(null, '', url.pathname + url.search);
  }
  // MR-50: a conversation that leaves the filtered list (its state changed) stays open, read on its own.
  const inList = chats?.find((c) => c.conversation.id === selected);
  const { data: selectedRows } = useSWR<Chat[]>(
    selected && chats && !inList
      ? '/conversations?' + new URLSearchParams({ assignment: 'all', conversationId: selected })
      : null,
    fetcher,
    { refreshInterval: 5000 },
  );
  function clearFilters() {
    setState(defaultInboxFilters.state);
    setAssignment(defaultInboxFilters.assignment);
    setChannelId('');
    setPriority('');
    setLabel('');
    setFilter(defaultInboxFilters.mode);
    onSearch('');
  }
  // While that read is on its way the chat keeps what it last showed, instead of closing and reopening.
  const shown = useRef<Chat | undefined>(undefined);
  const active =
    inList ??
    selectedRows?.find((c) => c.conversation.id === selected) ??
    (shown.current?.conversation.id === selected ? shown.current : undefined) ??
    (linkedPhone && chats?.length === 1 ? chats[0] : undefined);
  useEffect(() => {
    shown.current = active;
  });
  async function assignSelection() {
    setAssigning(true);
    try {
      await api('/conversations/assign', 'POST', {
        conversations: Object.entries(checked).map(([id, expectedRevision]) => ({
          id,
          expectedRevision,
        })),
        assignedTo: bulkTarget === 'none' ? null : bulkTarget,
      });
      setChecked({});
      setBulkTarget('');
      toast.success(bulkTarget === 'none' ? 'Responsable quitado' : 'Conversaciones asignadas');
    } catch (e) {
      // The selection stays; its revisions are re-read so the person can decide again on what they now see.
      toast.error((e as Error).message);
      const fresh = await mutate();
      setChecked((previous) =>
        Object.fromEntries(
          Object.entries(previous).map(([id, revision]) => [
            id,
            fresh?.find((c) => c.conversation.id === id)?.conversation.revision ?? revision,
          ]),
        ),
      );
    } finally {
      setAssigning(false);
      mutate();
      refreshTeam('/members/workload');
    }
  }
  const hiddenFilters = [channelId, priority, label].filter(Boolean).length;
  return (
    <>
      <ErrorBox error={error} />
      <Card className="inbox-layout min-h-0 flex-1 flex-row gap-0 overflow-hidden py-0">
        <section
          className={cn(
            'w-full shrink-0 flex-col overflow-y-auto lg:flex lg:w-80 lg:overflow-hidden lg:border-r 2xl:w-96',
            active ? 'hidden' : 'flex',
          )}
        >
          <div className="flex flex-col gap-3 border-b p-3">
            <div className="flex items-center gap-1">
              <h2 className="flex items-center gap-2 text-sm font-semibold">
                Conversaciones <Badge variant="secondary">{filtered?.length ?? 0}</Badge>
              </h2>
              {me.role === 'admin' && (
                <Button asChild variant="link" size="sm" className="ml-auto">
                  <a href="/whatsapp" title="Gestionar números de WhatsApp">
                    Números ↗
                  </a>
                </Button>
              )}
              <Button
                variant="ghost"
                size="icon-sm"
                className="ml-auto"
                aria-label="Actualizar conversaciones"
                onClick={() => mutate()}
              >
                <RefreshCw />
              </Button>
            </div>
            <SavedInboxViews
              filters={{ state, assignment, channelId, priority, label, mode: filter }}
              onApply={(f) => {
                setState(f.state);
                setAssignment(f.assignment);
                setChannelId(f.channelId);
                setPriority(f.priority);
                setLabel(f.label);
                setFilter(f.mode);
                setPage(1);
                select(null);
                // MR-57: filters a view applies are shown, not hidden behind «Más filtros».
                if (f.channelId || f.priority || f.label) setMoreFilters(true);
              }}
            />
            <div className="grid grid-cols-2 gap-2 *:w-full *:min-w-0">
              <NativeSelect
                aria-label="Filtrar por estado"
                value={state}
                onChange={(e) => setState(e.target.value)}
              >
                <NativeSelectOption value="">Todos los estados</NativeSelectOption>
                {conversationStates.map(([v, l]) => (
                  <NativeSelectOption value={v} key={v}>
                    {l}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
              <NativeSelect
                aria-label="Filtrar por responsable"
                value={assignment}
                onChange={(e) => setAssignment(e.target.value)}
              >
                <NativeSelectOption value="all">Todo el equipo</NativeSelectOption>
                <NativeSelectOption value="mine">Mis conversaciones</NativeSelectOption>
                <NativeSelectOption value="unassigned">Sin asignar</NativeSelectOption>
                {workload?.members.map((m) => (
                  <NativeSelectOption key={m.subject} value={'member:' + m.subject}>
                    {memberLabel(m)}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </div>
            <Collapsible
              open={moreFilters}
              onOpenChange={setMoreFilters}
              className="flex flex-col gap-2"
            >
              {/* MR-55: who attends has its own row; nothing covers «Agente». */}
              <ButtonGroup className="w-full *:flex-1" aria-label="Quién atiende">
                {[
                  ['all', 'Todas'],
                  ['human', 'Personas'],
                  ['agent', 'Agente'],
                ].map(([id, name]) => (
                  <Button
                    variant={filter === id ? 'secondary' : 'outline'}
                    size="sm"
                    aria-pressed={filter === id}
                    key={id}
                    onClick={() => setFilter(id)}
                  >
                    {name}
                  </Button>
                ))}
              </ButtonGroup>
              <CollapsibleTrigger asChild>
                <Button
                  variant={moreFilters ? 'secondary' : 'ghost'}
                  size="sm"
                  className="self-start"
                  aria-label={
                    hiddenFilters ? `Más filtros, ${hiddenFilters} aplicados` : 'Más filtros'
                  }
                >
                  <ListFilter />
                  Más filtros
                  {!!hiddenFilters && <Badge variant="secondary">{hiddenFilters}</Badge>}
                </Button>
              </CollapsibleTrigger>
              <CollapsibleContent className="grid grid-cols-2 gap-2 *:w-full *:min-w-0">
                <NativeSelect
                  aria-label="Filtrar por canal"
                  value={channelId}
                  onChange={(e) => setChannelId(e.target.value)}
                >
                  <NativeSelectOption value="">Todos los canales</NativeSelectOption>
                  {channels?.map((c) => (
                    <NativeSelectOption key={c.id} value={c.id}>
                      {c.name}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
                <NativeSelect
                  aria-label="Filtrar por prioridad"
                  value={priority}
                  onChange={(e) => setPriority(e.target.value)}
                >
                  <NativeSelectOption value="">Todas las prioridades</NativeSelectOption>
                  {priorities.map(([v, l]) => (
                    <NativeSelectOption key={v} value={v}>
                      {l}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
                <NativeSelect
                  className="col-span-2"
                  aria-label="Filtrar por etiqueta"
                  value={label}
                  onChange={(e) => setLabel(e.target.value)}
                >
                  <NativeSelectOption value="">Todas las etiquetas</NativeSelectOption>
                  {/* A saved view can name a label no conversation carries any more: it stays choosable. */}
                  {[...new Set([...(labelOptions ?? []), ...(label ? [label] : [])])].map((l) => (
                    <NativeSelectOption key={l} value={l}>
                      {l}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              </CollapsibleContent>
            </Collapsible>
            {(linkedPhone || linkedConversation) && (
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
                {linkedConversation ? 'Solo esta conversación' : 'Conversación desde WhatsApp'}{' '}
                <Button variant="link" size="sm" onClick={clearLinkedConversation}>
                  Ver todas
                </Button>
              </div>
            )}
            {managesTeam(me) && (
              <div className="flex flex-wrap items-center gap-2 *:max-w-full">
                <Checkbox
                  id="inbox-select-visible"
                  aria-label="Seleccionar conversaciones visibles"
                  checked={
                    !!filtered?.length && filtered.every((c) => c.conversation.id in checked)
                  }
                  onCheckedChange={(value) =>
                    setChecked(
                      value === true
                        ? Object.fromEntries(
                            (filtered ?? []).map((c) => [
                              c.conversation.id,
                              c.conversation.revision,
                            ]),
                          )
                        : {},
                    )
                  }
                />
                <Label htmlFor="inbox-select-visible">Seleccionar</Label>
                {!!Object.keys(checked).length && (
                  <>
                    <Badge variant="secondary">{Object.keys(checked).length}</Badge>
                    <NativeSelect
                      size="sm"
                      aria-label="Responsable de la selección"
                      value={bulkTarget}
                      onChange={(e) => setBulkTarget(e.target.value)}
                    >
                      <NativeSelectOption value="" disabled>
                        Elige un responsable
                      </NativeSelectOption>
                      <NativeSelectOption value="none">Quitar responsable</NativeSelectOption>
                      {workload?.members
                        .filter((m) => !m.disabled)
                        .map((m) => (
                          <NativeSelectOption key={m.subject} value={m.subject}>
                            {memberLabel(m)}
                          </NativeSelectOption>
                        ))}
                    </NativeSelect>
                    <ConfirmAction
                      ask={bulkTarget === 'none'}
                      title="¿Quitar el responsable?"
                      description={
                        Object.keys(checked).length === 1
                          ? 'La conversación seleccionada quedará sin responsable.'
                          : `Las ${Object.keys(checked).length} conversaciones seleccionadas quedarán sin responsable.`
                      }
                      action="Quitar responsable"
                      destructive
                      onConfirm={assignSelection}
                    >
                      <Button size="sm" disabled={assigning || !bulkTarget}>
                        Asignar selección
                      </Button>
                    </ConfirmAction>
                  </>
                )}
              </div>
            )}
          </div>
          <div className="flex flex-col lg:min-h-0 lg:flex-1 lg:overflow-y-auto">
            {!chats ? (
              <Loading />
            ) : !filtered?.length && filtersActive(filters, search) ? (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <ListFilter />
                  </EmptyMedia>
                  <EmptyTitle>Sin resultados con estos filtros</EmptyTitle>
                  <EmptyDescription>
                    Ninguna conversación coincide con estos filtros.
                  </EmptyDescription>
                </EmptyHeader>
                <EmptyContent>
                  <Button variant="outline" size="sm" onClick={clearFilters}>
                    Quitar filtros
                  </Button>
                </EmptyContent>
              </Empty>
            ) : !filtered?.length ? (
              <EmptyState title="Todo al día">
                Las conversaciones entrantes aparecerán aquí.
              </EmptyState>
            ) : (
              filtered.map(({ conversation: c, contact, channel, unreadCount, handoff }) => (
                <div
                  key={c.id}
                  className={cn(
                    'relative flex items-start gap-3 border-b px-3 py-3 last:border-b-0',
                    active?.conversation.id === c.id ? 'bg-accent' : 'hover:bg-accent/50',
                  )}
                >
                  {managesTeam(me) && (
                    <Checkbox
                      className="relative z-10 mt-2"
                      aria-label={'Seleccionar conversación de ' + contact.name}
                      checked={c.id in checked}
                      onCheckedChange={(value) =>
                        setChecked((previous) => {
                          const next = { ...previous };
                          if (value === true) next[c.id] = c.revision;
                          else delete next[c.id];
                          return next;
                        })
                      }
                    />
                  )}
                  <Item
                    asChild
                    size="sm"
                    className="conversation-card min-w-0 flex-1 flex-nowrap items-start p-0 text-left after:absolute after:inset-0"
                  >
                    <button
                      aria-current={active?.conversation.id === c.id ? 'true' : undefined}
                      onClick={() => select(c.id)}
                    >
                      <ItemMedia>
                        <PersonAvatar name={contact.name} />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle className="w-full justify-between">
                          <strong className="flex min-w-0 items-center gap-2 font-medium">
                            <span className="truncate">{contact.name}</span>
                            {unreadCount > 0 && (
                              <Badge
                                aria-label={`${unreadCount} ${unreadCount === 1 ? 'mensaje sin leer' : 'mensajes sin leer'}`}
                              >
                                {unreadCount}
                              </Badge>
                            )}
                          </strong>
                          <time
                            // When the patient last wrote: who has waited longest, not when someone last touched it.
                            dateTime={c.lastInboundAt ?? c.updatedAt}
                            className="shrink-0 text-xs font-normal text-muted-foreground"
                          >
                            {dayLabel(c.lastInboundAt ?? c.updatedAt, me.tenant.timeZone) === 'Hoy'
                              ? time(c.lastInboundAt ?? c.updatedAt, me.tenant.timeZone)
                              : dayLabel(c.lastInboundAt ?? c.updatedAt, me.tenant.timeZone)}
                          </time>
                        </ItemTitle>
                        {handoff ? (
                          // MR-47: why the agent passed it on, until a person answers.
                          <ItemDescription className="line-clamp-2" title={handoff.reason}>
                            Motivo: {handoff.reason}
                          </ItemDescription>
                        ) : (
                          <ItemDescription className="line-clamp-1">
                            {c.lastMessage || c.summary || 'Nueva conversación de WhatsApp'}
                          </ItemDescription>
                        )}
                        <div className="flex flex-wrap items-center gap-1.5">
                          {/* MR-55: priority leads the badges, so the name keeps the title row. */}
                          {c.priority !== 'normal' && (
                            <Badge variant={c.priority === 'urgent' ? 'destructive' : 'secondary'}>
                              {priorities.find(([v]) => v === c.priority)?.[1]}
                            </Badge>
                          )}
                          <Badge variant="outline">
                            {conversationStates.find(([v]) => v === c.state)?.[1]}
                          </Badge>
                          {handoff && (
                            <Badge variant="warning">
                              <Forward />
                              Transferida
                            </Badge>
                          )}
                          <StatusBadge value={attention(c.status, me.tenant.agentEnabled)} />
                          <CardLabels labels={c.labels} />
                        </div>
                        <div className="flex min-w-0 items-center gap-1 text-xs text-muted-foreground">
                          <UserRound className="size-3 shrink-0" />
                          <span className="truncate">
                            {workload?.members.find((m) => m.subject === c.assignedTo)?.name ??
                              (c.assignedTo ? 'Responsable asignado' : 'Sin asignar')}
                            {' · '}
                            <small className="text-xs">{channel.name}</small>
                          </span>
                        </div>
                      </ItemContent>
                    </button>
                  </Item>
                </div>
              ))
            )}
          </div>
          <div className="flex items-center justify-between gap-2 border-t p-3">
            <Button
              variant="outline"
              size="sm"
              disabled={page === 1}
              onClick={() => setPage(page - 1)}
            >
              Anterior
            </Button>
            <span className="text-sm text-muted-foreground">Página {page}</span>
            <Button
              variant="outline"
              size="sm"
              disabled={!chats || chats.length < 100}
              onClick={() => setPage(page + 1)}
            >
              Siguiente
            </Button>
          </div>
        </section>
        {active ? (
          <ChatPanel
            key={active.conversation.id}
            chat={active}
            me={me}
            onClose={() => {
              select(null);
              clearLinkedConversation();
            }}
            refresh={() => mutate()}
          />
        ) : (
          <section className="hidden min-w-0 flex-1 items-center justify-center lg:flex">
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <MessageCircle />
                </EmptyMedia>
                <EmptyTitle>
                  <h2>Todo comienza con una conversación</h2>
                </EmptyTitle>
                <EmptyDescription>
                  Selecciona un paciente para ver sus mensajes,
                  <br />
                  consultar el contexto y continuar la atención.
                </EmptyDescription>
              </EmptyHeader>
              <EmptyContent>
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <ShieldCheck className="size-4" /> Información disponible según tus permisos
                </div>
              </EmptyContent>
            </Empty>
          </section>
        )}
      </Card>
    </>
  );
}
/** MR-52: a card names up to two labels; the rest are counted. */
function CardLabels({ labels }: { labels: string }) {
  const all = labels
    .split(',')
    .map((l) => l.trim())
    .filter(Boolean);
  if (!all.length) return null;
  return (
    <>
      {all.slice(0, 2).map((l) => (
        <Badge key={l} variant="outline" className="max-w-24 font-normal text-muted-foreground">
          <span className="truncate">{l}</span>
        </Badge>
      ))}
      {all.length > 2 && (
        <Badge
          variant="outline"
          className="font-normal text-muted-foreground"
          title={all.slice(2).join(', ')}
          aria-label={`${all.length - 2} etiquetas más: ${all.slice(2).join(', ')}`}
        >
          +{all.length - 2}
        </Badge>
      )}
    </>
  );
}
