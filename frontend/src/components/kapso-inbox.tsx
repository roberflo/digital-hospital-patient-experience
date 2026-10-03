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
  MessageCircle,
  Search,
  Send,
  ShieldCheck,
  TriangleAlert,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { mergeMessages, windowOpen } from '@/lib/kapso-types';
import type { Conversation, InboxEvent, Message, MessageStatus, Page } from '@/lib/kapso-types';
import styles from './kapso-inbox.module.css';
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
        size={15}
        aria-label={status === 'read' ? 'Leído' : 'Entregado'}
        className={status === 'read' ? styles.read : ''}
      />
    );
  if (status === 'sent') return <Check size={15} aria-label="Enviado" />;
  if (status === 'failed')
    return <TriangleAlert size={14} aria-label="Envío fallido o sin confirmar" />;
  return <Clock3 size={13} aria-label="Pendiente" />;
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
      <main className="p-8">
        <p role="alert">{error.message}</p>
        <Link href="/login">Iniciar sesión</Link>
      </main>
    );
  if (!channels) return <main className="p-8">Cargando números de tu hospital…</main>;
  if (!current)
    return (
      <main className="mx-auto max-w-xl p-10">
        <MessageCircle size={36} />
        <h1 className="mt-4 text-2xl font-semibold">Conecta tu WhatsApp</h1>
        <p className="my-4">
          Agrega el número de tu hospital para comenzar a recibir conversaciones.
        </p>
        <Link href="/whatsapp" className="text-primary">
          Agregar mi número →
        </Link>
        <p className="mt-5">
          <Link href="/?view=inbox">Bandeja de atención</Link>
        </p>
      </main>
    );
  return (
    <InboxForNumber
      key={current.phoneNumberId}
      number={current.phoneNumberId}
      selector={
        <select
          aria-label="Número de WhatsApp"
          value={current.phoneNumberId}
          onChange={(event) => setChosen(event.target.value)}
        >
          {numbers.map((c) => (
            <option key={c.id} value={c.phoneNumberId}>
              {c.name}
            </option>
          ))}
        </select>
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
    <main className={styles.shell}>
      <header className={styles.top}>
        <Link href="/?view=inbox" className={styles.backLink}>
          <ArrowLeft size={16} /> Bandeja de atención
        </Link>
        <Link href="/whatsapp" className={styles.backLink}>
          Agregar mi número
        </Link>
      </header>
      <div className={`${styles.layout} ${selected ? styles.selected : ''}`}>
        <aside className={styles.sidebar} aria-label="Conversaciones de WhatsApp">
          <div className={styles.heading}>
            <div className={styles.brand}>
              <MessageCircle size={21} />
            </div>
            <div>
              <h1>Historial de WhatsApp</h1>
              <p>
                <i className={live ? styles.online : styles.offline} />
                {live ? 'Conectado en vivo' : 'Actualización cada 15 s'}
              </p>
            </div>
            <span className={styles.count}>{conversations.length}</span>
          </div>
          <div className="px-4 pb-3">{selector}</div>
          <label className={styles.search}>
            <Search size={17} />
            <input
              aria-label="Buscar conversación"
              placeholder="Buscar nombre o número"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </label>
          <div className={styles.filters} aria-label="Estado de conversación">
            {[
              ['all', 'Todas'],
              ['active', 'Activas'],
              ['ended', 'Finalizadas'],
            ].map(([value, name]) => (
              <button key={value} aria-pressed={filter === value} onClick={() => setFilter(value)}>
                {name}
              </button>
            ))}
          </div>
          <div className={styles.list}>
            {loading ? (
              <p className={styles.emptySmall}>Cargando conversaciones…</p>
            ) : visible.length === 0 ? (
              <div className={styles.emptySmall}>
                <MessageCircle size={28} />
                <p>
                  {query || filter !== 'all'
                    ? 'No hay conversaciones con este filtro.'
                    : 'Aún no hay conversaciones.'}
                </p>
                <small>Los mensajes recibidos aparecerán aquí.</small>
              </div>
            ) : (
              visible.map((c) => (
                <button
                  key={c.id}
                  className={`${styles.row} ${selected?.id === c.id ? styles.current : ''}`}
                  onClick={() => void choose(c)}
                  aria-label={`Abrir conversación con ${label(c)}`}
                >
                  <span className={styles.avatar}>{label(c).slice(0, 2).toUpperCase()}</span>
                  <span className={styles.preview}>
                    <strong>{label(c)}</strong>
                    <span>
                      {c.kapso?.last_message_text ||
                        placeholders[c.kapso?.last_message_type || ''] ||
                        'Sin mensajes'}
                    </span>
                  </span>
                  <span className={styles.rowMeta}>
                    <time>{relative(c.last_active_at)}</time>
                    {unread[c.id] > 0 && (
                      <b aria-label={`${unread[c.id]} no leídos`}>{unread[c.id]}</b>
                    )}
                  </span>
                </button>
              ))
            )}
            {listCursor && (
              <Button variant="ghost" onClick={() => void refreshList(listCursor).catch(fail)}>
                Cargar más conversaciones
              </Button>
            )}
          </div>
          <footer className={styles.listFooter}>
            Consulta de Kapso · responsables y seguimiento en Bandeja
          </footer>
        </aside>
        <section className={styles.chat} aria-label="Chat">
          {selected ? (
            <>
              <header className={styles.chatHeader}>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label="Volver a conversaciones"
                  className={styles.mobileBack}
                  onClick={() => {
                    selectedRef.current = null;
                    setSelected(null);
                  }}
                >
                  <ArrowLeft size={20} />
                </Button>
                <span className={styles.avatar}>{label(selected).slice(0, 2).toUpperCase()}</span>
                <div>
                  <h2>{label(selected)}</h2>
                  <p>
                    {selected.phone_number || selected.username || 'Sin teléfono disponible'} ·{' '}
                    {selected.status === 'active' ? 'Activa' : 'Finalizada'}
                  </p>
                </div>
                <Link
                  href={
                    selected.phone_number
                      ? `/?view=inbox&phone=${encodeURIComponent(selected.phone_number)}&phoneNumberId=${encodeURIComponent(number)}`
                      : '/?view=inbox'
                  }
                  className={styles.crmLink}
                >
                  Asignar y gestionar en bandeja ↗
                </Link>
              </header>
              <div
                ref={scroller}
                className={styles.messages}
                onScroll={(e) => {
                  const el = e.currentTarget;
                  shouldScroll.current = el.scrollHeight - el.scrollTop - el.clientHeight < 100;
                }}
              >
                {messageCursor && (
                  <Button
                    variant="outline"
                    className={styles.older}
                    disabled={loadingOlder}
                    onClick={() => void older()}
                  >
                    {loadingOlder ? 'Cargando…' : 'Cargar anteriores'}
                  </Button>
                )}
                {loadingChat && <p className={styles.emptySmall}>Cargando mensajes…</p>}
                {!loadingChat && !messages.length && (
                  <p className={styles.emptySmall}>Esta conversación aún no tiene mensajes.</p>
                )}
                {messages.map((m, index) => (
                  <div key={m.id}>
                    {(index === 0 ||
                      new Date(Number(messages[index - 1].timestamp) * 1000).toDateString() !==
                        new Date(Number(m.timestamp) * 1000).toDateString()) && (
                      <div className={styles.date}>
                        {new Date(Number(m.timestamp) * 1000).toLocaleDateString('es', {
                          day: 'numeric',
                          month: 'long',
                        })}
                      </div>
                    )}
                    <div
                      className={`${styles.bubble} ${m.kapso?.direction === 'outbound' ? styles.outbound : styles.inbound}`}
                    >
                      <p>{content(m)}</p>
                      <span className={styles.messageMeta}>
                        <time>{time(m)}</time>
                        {m.kapso?.direction === 'outbound' && <Status status={m.kapso?.status} />}
                      </span>
                    </div>
                  </div>
                ))}
              </div>
              <div className={styles.composer}>
                {disabledReason && <p className={styles.notice}>{disabledReason}</p>}
                <form
                  onSubmit={(e) => {
                    e.preventDefault();
                    void send();
                  }}
                >
                  <textarea
                    aria-label="Mensaje"
                    placeholder={disabledReason ? 'Envío no disponible' : 'Escribe un mensaje…'}
                    value={text}
                    maxLength={4096}
                    rows={2}
                    disabled={!!disabledReason || sending || loadingChat}
                    onChange={(e) => setText(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                        e.preventDefault();
                        void send();
                      }
                    }}
                  />
                  <Button
                    type="submit"
                    aria-label="Enviar mensaje"
                    disabled={!!disabledReason || sending || loadingChat || !text.trim()}
                  >
                    <Send size={18} />
                  </Button>
                </form>
                <small>Enter para enviar · Shift + Enter para un salto de línea</small>
              </div>
            </>
          ) : (
            <div className={styles.welcome}>
              <div className={styles.welcomeIcon}>
                <MessageCircle size={42} />
              </div>
              <h2>Una conversación, toda la atención</h2>
              <p>
                Selecciona un chat para consultar su historial
                <br />y continuar la conversación con tu paciente.
              </p>
              <span>
                <ShieldCheck size={14} /> Acceso exclusivo a tu hospital
              </span>
            </div>
          )}
        </section>
      </div>
      {error && (
        <div className={styles.error} role="alert">
          <TriangleAlert size={18} />
          <span>{error}</span>
          {/sesión/i.test(error) ? (
            <Link href="/login">Iniciar sesión</Link>
          ) : (
            <button
              onClick={() => {
                setError('');
                void refreshList().catch(fail);
              }}
            >
              Reintentar consulta
            </button>
          )}
        </div>
      )}
    </main>
  );
}
