'use client';
import {
  sessionFetch,
  isSessionPaused,
  SessionExpiredError,
  SESSION_EXPIRED,
  SESSION_RESUMED,
} from '@/lib/session-client';
import Link from 'next/link';
import useSWR from 'swr';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ArrowLeft,
  Check,
  CheckCheck,
  Clock3,
  Info,
  MessageCircle,
  Search,
  Send,
  ShieldCheck,
  TriangleAlert,
} from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Avatar, AvatarFallback } from '@/components/ui/avatar';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty';
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
  InputGroupTextarea,
} from '@/components/ui/input-group';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemMedia,
  ItemTitle,
} from '@/components/ui/item';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Spinner } from '@/components/ui/spinner';
import { cn } from '@/lib/utils';
import { mergeMessages, windowOpen } from '@/lib/kapso-types';
import type { Conversation, InboxEvent, Message, MessageStatus, Page } from '@/lib/kapso-types';
const label = (c: Conversation) =>
  c.kapso?.contact_name || c.phone_number || c.username || 'Conversación sin teléfono';
const placeholders: Record<string, string> = {
  image: '[imagen]',
  audio: '[audio]',
  video: '[video]',
  document: '[documento]',
  template: '[plantilla]',
  reaction: '[reacción]',
  sticker: '[sticker]',
  location: '[ubicación]',
  contacts: '[contacto]',
};
const content = (m: Message) =>
  m.type === 'text'
    ? m.text?.body || m.kapso?.content || ''
    : placeholders[m.type] || `[${m.type || 'mensaje'}]`;
const time = (m: Message) =>
  new Date(Number(m.timestamp) * 1000).toLocaleTimeString('es', {
    hour: '2-digit',
    minute: '2-digit',
  });
function relative(value?: string) {
  if (!value) return '';
  const delta = Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 60000));
  return !Number.isFinite(delta)
    ? ''
    : delta < 1
      ? 'Ahora'
      : delta < 60
        ? `${delta} min`
        : delta < 1440
          ? `${Math.floor(delta / 60)} h`
          : `${Math.floor(delta / 1440)} d`;
}
function Status({ status }: { status?: MessageStatus }) {
  if (status === 'read' || status === 'delivered')
    return (
      <CheckCheck
        aria-label={status === 'read' ? 'Leído' : 'Entregado'}
        className={cn('size-3.5', status === 'read' && 'text-primary-foreground')}
      />
    );
  if (status === 'sent') return <Check className="size-3.5" aria-label="Enviado" />;
  if (status === 'failed')
    return <TriangleAlert className="size-3.5" aria-label="Envío fallido o sin confirmar" />;
  return <Clock3 className="size-3.5" aria-label="Pendiente" />;
}
class RequestError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}
async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await sessionFetch(url, { ...init, cache: 'no-store' });
  const body = await response.json();
  if (!response.ok)
    throw new RequestError(
      body.error || body.title || 'No se pudo cargar WhatsApp.',
      response.status,
    );
  return body;
}
export default function KapsoInbox() {
  const { data: channels, error } = useSWR<{ id: string; name: string; phoneNumberId: string }[]>(
    '/api/crm/channels',
    api,
  );
  const [chosen, setChosen] = useState('');
  useEffect(() => {
    setChosen(new URLSearchParams(window.location.search).get('phoneNumberId') || '');
  }, []);
  const numbers = channels?.filter((c) => /^\d+$/.test(c.phoneNumberId)) || [];
  const current = numbers.find((c) => c.phoneNumberId === chosen) || numbers[0];
  if (error && !channels)
    return (
      <main className="flex min-h-dvh items-center justify-center bg-background p-4">
        <div className="flex w-full max-w-md flex-col items-start gap-4">
          <Alert variant="destructive">
            <TriangleAlert />
            <AlertDescription>{error.message}</AlertDescription>
          </Alert>
          <Button variant="outline" asChild>
            <Link href="/login">Iniciar sesión</Link>
          </Button>
        </div>
      </main>
    );
  if (!channels)
    return (
      <main className="flex min-h-dvh items-center justify-center gap-2 bg-background p-4 text-sm text-muted-foreground">
        <Spinner /> Cargando números de tu hospital…
      </main>
    );
  if (!current)
    return (
      <main className="flex min-h-dvh items-center justify-center bg-background p-4">
        <Empty>
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <MessageCircle />
            </EmptyMedia>
            <EmptyTitle>
              <h1>Conecta tu WhatsApp</h1>
            </EmptyTitle>
            <EmptyDescription>
              Agrega el número de tu hospital para comenzar a recibir conversaciones.
            </EmptyDescription>
          </EmptyHeader>
          <EmptyContent>
            <Button asChild>
              <Link href="/whatsapp">Agregar mi número →</Link>
            </Button>
            <Button variant="ghost" asChild>
              <Link href="/?view=inbox">Bandeja de atención</Link>
            </Button>
          </EmptyContent>
        </Empty>
      </main>
    );
  return (
    <InboxForNumber
      key={current.phoneNumberId}
      number={current.phoneNumberId}
      selector={
        <NativeSelect
          aria-label="Número de WhatsApp"
          value={current.phoneNumberId}
          onChange={(event) => setChosen(event.target.value)}
        >
          {numbers.map((c) => (
            <NativeSelectOption key={c.id} value={c.phoneNumberId}>
              {c.name}
            </NativeSelectOption>
          ))}
        </NativeSelect>
      }
    />
  );
}
function InboxForNumber({ number, selector }: { number: string; selector: React.ReactNode }) {
  const numberQuery = `phoneNumberId=${encodeURIComponent(number)}`;
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [selected, setSelected] = useState<Conversation | null>(null);
  const selectedRef = useRef<Conversation | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [listCursor, setListCursor] = useState<string | null>(null);
  const hasLoadedMore = useRef(false);
  const [messageCursor, setMessageCursor] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const [text, setText] = useState('');
  const [unread, setUnread] = useState<Record<string, number>>({});
  const seen = useRef(new Set<string>());
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadingChat, setLoadingChat] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [sending, setSending] = useState(false);
  const sendingRef = useRef(false);
  const [manualSend, setManualSend] = useState(false);
  const [live, setLive] = useState(false);
  const [sessionPaused, setSessionPaused] = useState(isSessionPaused);
  useEffect(() => {
    const pause = () => {
      setSessionPaused(true);
      setLive(false);
    };
    const resume = () => {
      setSessionPaused(false);
      setError('');
    };
    window.addEventListener(SESSION_EXPIRED, pause);
    window.addEventListener(SESSION_RESUMED, resume);
    return () => {
      window.removeEventListener(SESSION_EXPIRED, pause);
      window.removeEventListener(SESSION_RESUMED, resume);
    };
  }, []);
  const [now, setNow] = useState(Date.now());
  const scroller = useRef<HTMLDivElement>(null);
  const shouldScroll = useRef(true);
  const revoked = useRef(false);
  const fail = useCallback((e: unknown) => {
    if (e instanceof SessionExpiredError || (e instanceof RequestError && e.status === 401)) return;
    if (e instanceof RequestError && e.status === 403) {
      revoked.current = true;
      selectedRef.current = null;
      setSelected(null);
      setMessages([]);
      setConversations([]);
    }
    setError(e instanceof Error ? e.message : 'No se pudo conectar.');
  }, []);
  const refreshList = useCallback(
    async (after?: string) => {
      const result = await api<Page<Conversation> & { manualSendEnabled: boolean }>(
        `/api/conversations?${numberQuery}${after ? `&after=${encodeURIComponent(after)}` : ''}`,
      );
      revoked.current = false;
      setConversations((old) => {
        const merged = new Map(
          (after ? old : old.filter((c) => !result.data.some((n) => n.id === c.id))).map((c) => [
            c.id,
            c,
          ]),
        );
        for (const c of result.data) merged.set(c.id, c);
        return [...merged.values()].sort((a, b) =>
          (b.last_active_at || '').localeCompare(a.last_active_at || ''),
        );
      });
      if (after) hasLoadedMore.current = true;
      if (after || !hasLoadedMore.current)
        setListCursor(result.paging?.next ? result.paging.cursors?.after || null : null);
      setManualSend(result.manualSendEnabled);
      const current = result.data.find((c) => c.id === selectedRef.current?.id);
      if (current) {
        selectedRef.current = current;
        setSelected(current);
      }
      setLoading(false);
    },
    [numberQuery],
  );
  const refreshChat = useCallback(
    async (id: string, older?: string, initial = false) => {
      const result = await api<Page<Message> & { conversation: Conversation }>(
        `/api/conversations/${encodeURIComponent(id)}/messages?${numberQuery}${older ? `&cursor=${encodeURIComponent(older)}` : ''}`,
      );
      if (selectedRef.current?.id !== id || revoked.current) return;
      for (const message of result.data) seen.current.add(message.id);
      setMessages((old) => mergeMessages(initial ? [] : old, result.data));
      if (older || initial)
        setMessageCursor(result.paging?.next ? result.paging.cursors?.after || null : null);
      selectedRef.current = result.conversation;
      setSelected(result.conversation);
    },
    [numberQuery],
  );
  useEffect(() => {
    if (sessionPaused) return;
    let alive = true;
    const update = async () => {
      try {
        await refreshList();
        const id = selectedRef.current?.id;
        if (id) await refreshChat(id);
      } catch (e) {
        if (alive) fail(e);
      } finally {
        if (alive) {
          setLoading(false);
          setNow(Date.now());
        }
      }
    };
    void update();
    const poll = setInterval(() => {
      void update();
    }, 15000);
    const source = new EventSource(`/api/kapso/stream?${numberQuery}`);
    source.onopen = () => {
      setLive(true);
      void update();
    };
    source.onerror = () => setLive(false);
    source.onmessage = ({ data }) => {
      if (revoked.current) return;
      let event: InboxEvent;
      try {
        event = JSON.parse(data);
      } catch {
        return;
      }
      const message = event.payload?.message;
      const id = event.conversationId;
      if (message?.id) {
        const status = event.event.split('.').at(-1) as MessageStatus;
        const normalized = {
          ...message,
          kapso: {
            ...message.kapso,
            ...(['sent', 'delivered', 'read', 'failed'].includes(status) ? { status } : {}),
          },
        };
        if (id === selectedRef.current?.id) {
          setMessages((old) =>
            message.timestamp
              ? mergeMessages(old, [normalized])
              : old.map((m) =>
                  m.id === message.id
                    ? mergeMessages([m], [{ ...m, kapso: { ...m.kapso, ...normalized.kapso } }])[0]
                    : m,
                ),
          );
        } else if (
          event.event === 'whatsapp.message.received' &&
          id &&
          !seen.current.has(message.id)
        ) {
          setUnread((old) => ({ ...old, [id]: (old[id] || 0) + 1 }));
        }
        seen.current.add(message.id);
        if (seen.current.size > 10000) seen.current.delete(seen.current.values().next().value!);
      }
      void update();
    };
    return () => {
      alive = false;
      clearInterval(poll);
      source.close();
    };
  }, [refreshList, refreshChat, fail, numberQuery, sessionPaused]);
  useEffect(() => {
    if (shouldScroll.current && scroller.current)
      scroller.current.scrollTop = scroller.current.scrollHeight;
  }, [messages]);
  async function choose(conversation: Conversation) {
    selectedRef.current = conversation;
    setSelected(conversation);
    setMessages([]);
    setMessageCursor(null);
    setText('');
    setError('');
    setLoadingChat(true);
    shouldScroll.current = true;
    setUnread((old) => ({ ...old, [conversation.id]: 0 }));
    try {
      await refreshChat(conversation.id, undefined, true);
    } catch (e) {
      fail(e);
    } finally {
      if (selectedRef.current?.id === conversation.id) setLoadingChat(false);
    }
  }
  async function older() {
    if (!selected || !messageCursor || loadingOlder) return;
    const element = scroller.current;
    const height = element?.scrollHeight || 0;
    const top = element?.scrollTop || 0;
    setLoadingOlder(true);
    shouldScroll.current = false;
    try {
      await refreshChat(selected.id, messageCursor);
      requestAnimationFrame(() => {
        if (element) element.scrollTop = element.scrollHeight - height + top;
      });
    } catch (e) {
      fail(e);
    } finally {
      setLoadingOlder(false);
    }
  }
  const closed = !selected || !windowOpen(selected, now);
  const disabledReason = !selected
    ? ''
    : !selected.phone_number
      ? 'Este contacto no tiene teléfono disponible para enviar texto.'
      : closed
        ? 'Ventana de 24h cerrada: usa una plantilla'
        : !manualSend
          ? 'Recepción activa. El envío manual aún no está habilitado.'
          : '';
  async function send() {
    if (!selected || disabledReason || sendingRef.current || !text.trim()) return;
    const current = selected;
    const value = text.trim();
    const pending = `pending:${crypto.randomUUID()}`;
    const optimistic: Message = {
      id: pending,
      timestamp: String(Math.floor(Date.now() / 1000)),
      type: 'text',
      text: { body: value },
      kapso: { direction: 'outbound', status: 'pending' },
    };
    sendingRef.current = true;
    setSending(true);
    setText('');
    setError('');
    shouldScroll.current = true;
    setMessages((old) => mergeMessages(old, [optimistic]));
    try {
      const result = await api<{ message: Message }>(`/api/messages?${numberQuery}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ conversationId: current.id, to: current.phone_number, text: value }),
      });
      if (selectedRef.current?.id === current.id)
        setMessages((old) =>
          mergeMessages(
            old.filter((m) => m.id !== pending),
            [result.message],
          ),
        );
      await refreshList();
    } catch (e) {
      if (
        selectedRef.current?.id === current.id &&
        (e instanceof SessionExpiredError || (e instanceof RequestError && e.status === 401))
      ) {
        setText((draft) => draft || value);
        setMessages((old) => old.filter((m) => m.id !== pending));
      }
      if (selectedRef.current?.id === current.id)
        setMessages((old) =>
          old.map((m) =>
            m.id === pending ? { ...m, kapso: { ...m.kapso, status: 'failed' } } : m,
          ),
        );
      fail(e);
    } finally {
      sendingRef.current = false;
      setSending(false);
    }
  }
  const visible = conversations.filter(
    (c) =>
      (filter === 'all' || c.status === filter) &&
      `${label(c)} ${c.phone_number || ''} ${c.username || ''}`
        .toLowerCase()
        .includes(query.toLowerCase()),
  );
  return (
    <main className="flex h-dvh flex-col bg-background">
      <header className="flex shrink-0 flex-wrap items-center justify-between gap-2 px-4 py-3 md:px-6">
        <Button variant="ghost" asChild>
          <Link href="/?view=inbox">
            <ArrowLeft /> Bandeja de atención
          </Link>
        </Button>
        <Button variant="outline" asChild>
          <Link href="/whatsapp">Agregar mi número</Link>
        </Button>
      </header>
      <div className="flex min-h-0 flex-1 md:px-6 md:pb-6">
        <div className="flex min-h-0 min-w-0 flex-1 overflow-hidden border-y bg-card text-card-foreground md:rounded-xl md:border">
          <aside
            className={cn(
              'min-h-0 w-full min-w-0 flex-col md:flex md:w-88 md:shrink-0 md:border-r',
              selected ? 'hidden' : 'flex',
            )}
            aria-label="Conversaciones de WhatsApp"
          >
            <div className="flex flex-col gap-4 border-b p-4">
              <div className="flex items-center gap-3">
                <div className="flex min-w-0 flex-1 flex-col gap-1">
                  <h1 className="text-lg font-semibold tracking-tight">Historial de WhatsApp</h1>
                  <p className="text-sm text-muted-foreground">
                    {live ? 'Conectado en vivo' : 'Actualización cada 15 s'}
                  </p>
                </div>
                <Badge variant="outline">{conversations.length}</Badge>
              </div>
              {selector}
              <InputGroup>
                <InputGroupInput
                  aria-label="Buscar conversación"
                  placeholder="Buscar nombre o número"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                />
                <InputGroupAddon>
                  <Search />
                </InputGroupAddon>
              </InputGroup>
              <div className="flex flex-wrap gap-2" aria-label="Estado de conversación">
                {[
                  ['all', 'Todas'],
                  ['active', 'Activas'],
                  ['ended', 'Finalizadas'],
                ].map(([value, name]) => (
                  <Button
                    key={value}
                    size="sm"
                    variant={filter === value ? 'secondary' : 'ghost'}
                    aria-pressed={filter === value}
                    onClick={() => setFilter(value)}
                  >
                    {name}
                  </Button>
                ))}
              </div>
            </div>
            <div className="flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto p-2">
              {loading ? (
                <p className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground">
                  <Spinner /> Cargando conversaciones…
                </p>
              ) : visible.length === 0 ? (
                <Empty>
                  <EmptyHeader>
                    <EmptyMedia variant="icon">
                      <MessageCircle />
                    </EmptyMedia>
                    <EmptyTitle>
                      {query || filter !== 'all'
                        ? 'No hay conversaciones con este filtro.'
                        : 'Aún no hay conversaciones.'}
                    </EmptyTitle>
                    <EmptyDescription>Los mensajes recibidos aparecerán aquí.</EmptyDescription>
                  </EmptyHeader>
                </Empty>
              ) : (
                visible.map((c) => (
                  <Item
                    key={c.id}
                    asChild
                    size="sm"
                    variant={selected?.id === c.id ? 'muted' : 'default'}
                    className="w-full shrink-0 flex-nowrap text-left"
                  >
                    <button
                      type="button"
                      onClick={() => void choose(c)}
                      aria-label={`Abrir conversación con ${label(c)}`}
                    >
                      <ItemMedia>
                        <Avatar size="lg">
                          <AvatarFallback>{label(c).slice(0, 2).toUpperCase()}</AvatarFallback>
                        </Avatar>
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle className="max-w-full">
                          <span className="truncate">{label(c)}</span>
                        </ItemTitle>
                        <ItemDescription className="line-clamp-1">
                          {c.kapso?.last_message_text ||
                            placeholders[c.kapso?.last_message_type || ''] ||
                            'Sin mensajes'}
                        </ItemDescription>
                      </ItemContent>
                      <ItemActions className="flex-col items-end">
                        <time className="text-xs text-muted-foreground">
                          {relative(c.last_active_at)}
                        </time>
                        {unread[c.id] > 0 && (
                          <Badge aria-label={`${unread[c.id]} no leídos`}>{unread[c.id]}</Badge>
                        )}
                      </ItemActions>
                    </button>
                  </Item>
                ))
              )}
              {listCursor && (
                <Button
                  variant="ghost"
                  className="shrink-0"
                  onClick={() => void refreshList(listCursor).catch(fail)}
                >
                  Cargar más conversaciones
                </Button>
              )}
            </div>
            <footer className="border-t p-3 text-center text-xs text-muted-foreground">
              Historial de WhatsApp · responsables y seguimiento en Bandeja
            </footer>
          </aside>
          <section
            className={cn('min-h-0 min-w-0 flex-1 flex-col md:flex', selected ? 'flex' : 'hidden')}
            aria-label="Chat"
          >
            {selected ? (
              <>
                <header className="flex flex-wrap items-center gap-3 border-b p-4">
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Volver a conversaciones"
                    className="md:hidden"
                    onClick={() => {
                      selectedRef.current = null;
                      setSelected(null);
                    }}
                  >
                    <ArrowLeft />
                  </Button>
                  <Avatar size="lg">
                    <AvatarFallback>{label(selected).slice(0, 2).toUpperCase()}</AvatarFallback>
                  </Avatar>
                  <div className="flex min-w-0 flex-1 flex-col gap-1">
                    <h2 className="truncate text-sm font-semibold">{label(selected)}</h2>
                    <p className="truncate text-xs text-muted-foreground">
                      {selected.phone_number || selected.username || 'Sin teléfono disponible'} ·{' '}
                      {selected.status === 'active' ? 'Activa' : 'Finalizada'}
                    </p>
                  </div>
                  <Button variant="outline" size="sm" className="w-full md:w-auto" asChild>
                    <Link
                      href={
                        selected.phone_number
                          ? `/?view=inbox&phone=${encodeURIComponent(selected.phone_number)}&phoneNumberId=${encodeURIComponent(number)}`
                          : '/?view=inbox'
                      }
                    >
                      Asignar y gestionar en bandeja ↗
                    </Link>
                  </Button>
                </header>
                <div
                  ref={scroller}
                  className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-4 md:p-6"
                  onScroll={(e) => {
                    const el = e.currentTarget;
                    shouldScroll.current = el.scrollHeight - el.scrollTop - el.clientHeight < 100;
                  }}
                >
                  {messageCursor && (
                    <Button
                      variant="outline"
                      size="sm"
                      className="shrink-0 self-center"
                      disabled={loadingOlder}
                      onClick={() => void older()}
                    >
                      {loadingOlder ? 'Cargando…' : 'Cargar anteriores'}
                    </Button>
                  )}
                  {loadingChat && (
                    <p className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground">
                      <Spinner /> Cargando mensajes…
                    </p>
                  )}
                  {!loadingChat && !messages.length && (
                    <p className="p-10 text-center text-sm text-muted-foreground">
                      Esta conversación aún no tiene mensajes.
                    </p>
                  )}
                  {messages.map((m, index) => (
                    <div key={m.id} className="flex shrink-0 flex-col gap-2">
                      {(index === 0 ||
                        new Date(Number(messages[index - 1].timestamp) * 1000).toDateString() !==
                          new Date(Number(m.timestamp) * 1000).toDateString()) && (
                        <Badge variant="secondary" className="self-center">
                          {new Date(Number(m.timestamp) * 1000).toLocaleDateString('es', {
                            day: 'numeric',
                            month: 'long',
                          })}
                        </Badge>
                      )}
                      <div
                        className={cn(
                          'flex w-fit max-w-[80%] flex-col gap-1 rounded-lg px-3 py-2 text-sm',
                          m.kapso?.direction === 'outbound'
                            ? 'self-end bg-primary text-primary-foreground'
                            : 'self-start bg-muted',
                        )}
                      >
                        <p className="wrap-anywhere whitespace-pre-wrap">{content(m)}</p>
                        <span
                          className={cn(
                            'flex items-center justify-end gap-1 text-xs',
                            m.kapso?.direction === 'outbound'
                              ? 'text-primary-foreground/70'
                              : 'text-muted-foreground',
                          )}
                        >
                          <time>{time(m)}</time>
                          {m.kapso?.direction === 'outbound' && <Status status={m.kapso?.status} />}
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
                <div className="flex flex-col gap-2 border-t p-4">
                  {disabledReason && (
                    <Alert role="note">
                      <Info />
                      <AlertDescription>{disabledReason}</AlertDescription>
                    </Alert>
                  )}
                  <form
                    onSubmit={(e) => {
                      e.preventDefault();
                      void send();
                    }}
                  >
                    <InputGroup>
                      <InputGroupTextarea
                        aria-label="Mensaje"
                        placeholder={disabledReason ? 'Envío no disponible' : 'Escribe un mensaje…'}
                        value={text}
                        maxLength={4096}
                        rows={2}
                        className="max-h-40"
                        disabled={!!disabledReason || sending || loadingChat}
                        onChange={(e) => setText(e.target.value)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                            e.preventDefault();
                            void send();
                          }
                        }}
                      />
                      <InputGroupAddon align="block-end" className="justify-end">
                        <InputGroupButton
                          type="submit"
                          variant="default"
                          size="icon-sm"
                          aria-label="Enviar mensaje"
                          disabled={!!disabledReason || sending || loadingChat || !text.trim()}
                        >
                          <Send />
                        </InputGroupButton>
                      </InputGroupAddon>
                    </InputGroup>
                  </form>
                  <p className="text-xs text-muted-foreground">
                    Enter para enviar · Shift + Enter para un salto de línea
                  </p>
                </div>
              </>
            ) : (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <MessageCircle />
                  </EmptyMedia>
                  <EmptyTitle>
                    <h2>Una conversación, toda la atención</h2>
                  </EmptyTitle>
                  <EmptyDescription>
                    Selecciona un chat para consultar su historial
                    <br />y continuar la conversación con tu paciente.
                  </EmptyDescription>
                </EmptyHeader>
                <EmptyContent>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">
                    <ShieldCheck className="size-4" /> Acceso exclusivo a tu hospital
                  </span>
                </EmptyContent>
              </Empty>
            )}
          </section>
        </div>
      </div>
      {error && (
        <div className="fixed inset-x-4 bottom-4 z-20 mx-auto max-w-md">
          <Alert variant="destructive">
            <TriangleAlert />
            <AlertDescription>
              <span>{error}</span>
              {/sesión/i.test(error) ? (
                <Button variant="outline" size="sm" asChild>
                  <Link href="/login">Iniciar sesión</Link>
                </Button>
              ) : (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => {
                    setError('');
                    void refreshList().catch(fail);
                  }}
                >
                  Reintentar consulta
                </Button>
              )}
            </AlertDescription>
          </Alert>
        </div>
      )}
    </main>
  );
}
