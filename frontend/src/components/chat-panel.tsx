'use client';
import {
  useEffect,
  useId,
  useState,
  useRef,
  useSyncExternalStore,
  type FormEvent,
  type UIEvent,
} from 'react';
import useSWR from 'swr';
import {
  Search,
  Plus,
  ArrowUpRight,
  Send,
  Paperclip,
  Sparkles,
  UserRound,
  Check,
  CheckCheck,
  Link2,
  X,
  FileText,
  ArrowLeft,
  ExternalLink,
  Forward,
  MessageSquareOff,
} from 'lucide-react';
import { toast } from 'sonner';
import { Alert, AlertDescription, AlertTitle } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import { Separator } from './ui/separator';
import { Sheet, SheetClose, SheetContent, SheetHeader, SheetTitle } from './ui/sheet';
import { Spinner } from './ui/spinner';
import { Textarea } from './ui/textarea';
import { api, fetcher, type Me, type Chat, type Message, type Activity } from '@/lib/api';
import { cn } from '@/lib/utils';
import { dayInZone, whatsAppPhone } from '@/lib/format';
import { ConfirmAction } from './confirm-action';
import { ReminderConsent } from './appointment-reminders';
import { PatientAppointments } from './patient-appointments';
import { PatientLink } from './hospital-connection';
import { PatientClinical } from './patient-clinical';
import { ConversationMacros } from './inbox-productivity';
import { AssignmentControl, managesTeam } from './team-workspace';
import { WorkflowControls, CustomerCrm, SavedReplies } from './conversation-workspace';
import {
  labels,
  time,
  dayLabel,
  notSent,
  PersonAvatar,
  StatusBadge,
  attention,
  ErrorBox,
  ActivityTimeline,
} from './workspace-shared';
import { FormDialog } from './form-dialog';
/** Tracks a CSS media query; reads the real value on the first client render. */
function useMediaQuery(query: string) {
  return useSyncExternalStore(
    (notify) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', notify);
      return () => list.removeEventListener('change', notify);
    },
    () => window.matchMedia(query).matches,
    () => false,
  );
}
export function ChatPanel({
  chat,
  me,
  onClose,
  refresh,
}: {
  chat: Chat;
  me: Me;
  onClose: () => void;
  refresh: () => void;
}) {
  const c = chat.conversation;
  const canReply = managesTeam(me) || !c.assignedTo || c.assignedTo === me.subject;
  const {
    data: messages,
    error,
    mutate,
  } = useSWR<Message[]>(`/conversations/${c.id}/messages`, fetcher, { refreshInterval: 4000 });
  const { data: activities, mutate: refreshActivities } = useSWR<Activity[]>(
    `/activities?contactId=${chat.contact.id}`,
    fetcher,
    { refreshInterval: 8000 },
  );
  const [body, setBody] = useState('');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState(false);
  const [patient, setPatient] = useState(false);
  const [details, setDetails] = useState(false);
  // A chosen file waits for the person to confirm whom it goes to.
  // One key per chosen file: retrying after a lost answer never sends it twice.
  const [file, setFile] = useState<{ file: File; key: string } | null>(null);
  const agentOff = !me.tenant.agentEnabled && c.status !== 'closed';
  // MR-48/49: what the server would refuse, said before anyone types or confirms.
  const blocked = chat.sendBlock;
  const agentBlocked = c.status !== 'agent' && !agentOff ? chat.agentBlock : null;
  const blockId = useId();
  const draft = useRef<HTMLTextAreaElement>(null);
  /** MR-59: a saved reply goes where the cursor is; what was written stays. */
  function insert(text: string) {
    const el = draft.current;
    const start = el?.selectionStart ?? body.length;
    const end = el?.selectionEnd ?? body.length;
    setBody(body.slice(0, start) + text + body.slice(end));
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + text.length, start + text.length);
    });
  }
  // Wide screens keep the patient context as a third column; narrower ones open it on demand.
  const wide = useMediaQuery('(min-width: 1280px)');
  const [findMessages, setFindMessages] = useState(false);
  const [messageSearch, setMessageSearch] = useState('');
  const [onlyFiles, setOnlyFiles] = useState(false);
  const visibleMessages = messages?.filter(
    (m) =>
      (!onlyFiles || !!m.mediaId) &&
      (!findMessages ||
        (m.body + ' ' + (m.mediaName ?? ''))
          .toLocaleLowerCase('es')
          .includes(messageSearch.toLocaleLowerCase('es'))),
  );
  const bottom = useRef<HTMLDivElement>(null);
  // Follow new messages only while the person is at the end; reading further up stays put (MR-26).
  const following = useRef(true);
  const follow = (e: UIEvent<HTMLElement>) => {
    const el = e.currentTarget;
    following.current = el.scrollHeight - el.scrollTop - el.clientHeight <= 80;
  };
  useEffect(() => {
    if (following.current) bottom.current?.scrollIntoView({ block: 'nearest' });
  }, [messages?.length, c.id]);
  useEffect(() => setBody(''), [c.id]);
  const lastRead = useRef('');
  const newest = messages?.reduce<Message | undefined>(
    (latest, m) => (!latest || m.receivedAt > latest.receivedAt ? m : latest),
    undefined,
  );
  useEffect(() => {
    if (!newest || lastRead.current === newest.id || document.visibilityState !== 'visible') return;
    lastRead.current = newest.id;
    api(`/conversations/${c.id}/read`, 'POST', { throughMessageId: newest.id })
      .then(refresh)
      .catch(() => {
        lastRead.current = '';
      });
  }, [c.id, newest?.id]);
  async function action(status: string, assignedTo?: string) {
    try {
      await api(`/conversations/${c.id}`, 'PATCH', {
        status,
        assignedTo: assignedTo ?? null,
        expectedRevision: c.revision,
      });
      refresh();
      refreshActivities();
      toast.success('Atención actualizada');
    } catch (e) {
      toast.error((e as Error).message);
    }
  }
  async function send(e: FormEvent) {
    e.preventDefault();
    if (!body.trim()) return;
    setBusy(true);
    try {
      const sent = await api<Message>(`/conversations/${c.id}/messages`, 'POST', { body });
      setBody('');
      mutate();
      refresh();
      if (sent.status !== 'sent') toast.error(notSent(sent.status));
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function sendFile() {
    if (!file) return;
    const form = new FormData();
    form.append('file', file.file);
    setBusy(true);
    try {
      const sent = await api<Message>(`/conversations/${c.id}/media`, 'POST', form, file.key);
      setFile(null);
      mutate();
      refresh();
      if (sent.status === 'sent') toast.success('Archivo enviado');
      else toast.error(notSent(sent.status));
    } catch (err) {
      toast.error((err as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const context = (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col items-center gap-2 text-center">
        <PersonAvatar name={chat.contact.name} large />
        <h3 className="font-semibold">{chat.contact.name}</h3>
        <p className="text-sm text-muted-foreground">{whatsAppPhone(chat.contact.phone)}</p>
        {chat.contact.tags && <Badge variant="secondary">{chat.contact.tags}</Badge>}
      </div>
      <Separator />
      <div className="flex flex-col gap-2">
        <div className="flex items-center justify-between gap-2">
          <h4 className="text-sm font-medium">Expediente del hospital</h4>
          <Link2 className="size-4 text-muted-foreground" />
        </div>
        <p className="text-sm text-muted-foreground">
          {chat.contact.patientId
            ? 'Paciente vinculado. El teléfono se verifica en cada consulta.'
            : 'Vincula el expediente para consultar agenda y recetas.'}
        </p>
        <Button variant="outline" size="sm" className="w-full" onClick={() => setPatient(true)}>
          {chat.contact.patientId ? 'Ver paciente vinculado' : 'Vincular paciente'}
        </Button>
      </div>
      <WorkflowControls
        chat={chat}
        onChange={() => {
          refresh();
          refreshActivities();
        }}
      />
      <PatientAppointments contact={chat.contact} me={me} />
      <ReminderConsent contactId={chat.contact.id} />
      <CustomerCrm
        chat={chat}
        onChange={() => {
          refresh();
          refreshActivities();
        }}
      />
      <div className="flex flex-col gap-2">
        <div className="flex items-center justify-between gap-2">
          <h4 className="text-sm font-medium">Historial del cliente</h4>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label="Agregar nota"
            onClick={() => setNote(true)}
          >
            <Plus />
          </Button>
        </div>
        <ActivityTimeline
          items={activities?.slice(0, 16)}
          proposal="Propuesta de agenda registrada"
          zone={me.tenant.timeZone}
        />
      </div>
    </div>
  );
  return (
    <>
      <section data-chat-open className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <div className="flex items-center gap-3 border-b p-3">
          <Button
            variant="ghost"
            size="icon-sm"
            className="lg:hidden"
            onClick={onClose}
            aria-label="Volver a conversaciones"
          >
            <ArrowLeft />
          </Button>
          <PersonAvatar name={chat.contact.name} />
          <div className="flex min-w-0 flex-col gap-0.5">
            <h2 className="truncate text-sm font-semibold">{chat.contact.name}</h2>
            {/* MR-56: who attends stays visible on a phone too. */}
            <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
              <small className="text-xs whitespace-nowrap text-muted-foreground">
                {whatsAppPhone(chat.contact.phone)}
              </small>
              <StatusBadge value={attention(c.status, me.tenant.agentEnabled)} />
            </div>
          </div>
          <div className="ml-auto flex shrink-0 items-center gap-1">
            <Button
              variant={findMessages ? 'secondary' : 'ghost'}
              size="icon-sm"
              aria-label="Buscar mensajes"
              aria-pressed={findMessages}
              onClick={() => setFindMessages(!findMessages)}
            >
              <Search />
            </Button>
            <Button
              variant={onlyFiles ? 'secondary' : 'ghost'}
              size="icon-sm"
              aria-label="Mostrar archivos"
              aria-pressed={onlyFiles}
              onClick={() => setOnlyFiles(!onlyFiles)}
            >
              <Paperclip />
            </Button>
            {/* MR-58: from 1280 px the patient context is the third column, always in view. */}
            {!wide && (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Ver información del paciente"
                onClick={() => setDetails(!details)}
              >
                <UserRound />
              </Button>
            )}
          </div>
        </div>
        {/* A phone scrolls the assignment, actions and thread as one; the reply box stays below. */}
        <div
          className="flex min-h-0 flex-1 flex-col overflow-y-auto md:overflow-hidden"
          onScroll={follow}
        >
          <div className="flex shrink-0 flex-col gap-3 border-b p-3">
            {chat.handoff && (
              <Alert>
                <Forward />
                <AlertTitle>Transferida por el agente</AlertTitle>
                <AlertDescription>{chat.handoff.reason}</AlertDescription>
              </Alert>
            )}
            <AssignmentControl
              conversation={c}
              me={me}
              onChange={() => {
                refresh();
                refreshActivities();
              }}
            />
            <Alert>
              <Sparkles />
              <AlertDescription className="flex flex-wrap items-center justify-between gap-2">
                <span>
                  {agentBlocked
                    ? `El agente no podría responder: ${agentBlocked.reason}`
                    : agentOff
                      ? me.role === 'admin'
                        ? 'El agente está apagado para todo el hospital. Actívalo en Configuración.'
                        : 'El agente está apagado para todo el hospital. Pide al administrador que lo active.'
                      : c.status === 'agent'
                        ? 'El agente está atendiendo esta conversación.'
                        : c.status === 'closed'
                          ? 'La conversación está cerrada.'
                          : 'Tu equipo está a cargo. El agente está en pausa.'}
                </span>
                <ConfirmAction
                  ask={c.status !== 'agent'}
                  title="¿Activar el agente en esta conversación?"
                  description="La conversación quedará sin responsable y el agente responderá los próximos mensajes del paciente por WhatsApp. Tu equipo puede retomarla en cualquier momento."
                  action="Activar agente"
                  onConfirm={() =>
                    action(
                      c.status === 'agent' ? 'human' : 'agent',
                      c.status === 'agent' ? me.subject : undefined,
                    )
                  }
                >
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={
                      (!managesTeam(me) && !!c.assignedTo && c.assignedTo !== me.subject) ||
                      (agentOff && c.status !== 'agent') ||
                      !!agentBlocked
                    }
                  >
                    {c.status === 'agent' ? 'Tomar conversación' : 'Activar agente'}
                    <ArrowUpRight />
                  </Button>
                </ConfirmAction>
              </AlertDescription>
            </Alert>
            <div className="flex flex-wrap items-center gap-2">
              <PatientClinical
                key={`${c.id}:${c.assignedTo}:${c.status}:${chat.contact.patientId}`}
                chat={chat}
                me={me}
              />
              <ConversationMacros
                chat={chat}
                me={me}
                onChange={() => {
                  refresh();
                  refreshActivities();
                }}
              />
            </div>
          </div>
          <div className="flex shrink-0 flex-col gap-2 border-b p-3 empty:hidden">
            {(findMessages || onlyFiles) && (
              <span className="text-xs text-muted-foreground">
                {visibleMessages?.length ?? 0} resultados · últimos 200 mensajes
              </span>
            )}
            {findMessages && (
              <InputGroup>
                <InputGroupInput
                  aria-label="Buscar en esta conversación"
                  placeholder="Buscar texto o nombre de archivo…"
                  value={messageSearch}
                  onChange={(e) => setMessageSearch(e.target.value)}
                />
                <InputGroupAddon>
                  <Search />
                </InputGroupAddon>
              </InputGroup>
            )}
            <ErrorBox error={error} />
          </div>
          <div
            className="message-list flex flex-col gap-3 p-4 md:min-h-0 md:flex-1 md:overflow-y-auto"
            onScroll={follow}
          >
            <div className="flex items-center gap-3 text-xs text-muted-foreground">
              <Separator className="flex-1" />
              <span>Historial de atención</span>
              <Separator className="flex-1" />
            </div>
            {(messages?.length ?? 0) >= 200 && (
              <p className="text-center text-xs text-muted-foreground" role="status">
                Se muestran los últimos 200 mensajes.
              </p>
            )}
            {visibleMessages?.length === 0 && (
              <p className="text-center text-sm text-muted-foreground">
                {onlyFiles
                  ? 'No hay archivos en los mensajes cargados.'
                  : 'No hay mensajes que coincidan.'}
              </p>
            )}
            {visibleMessages?.map((m, i) => (
              <div className="flex flex-col gap-3" key={m.id}>
                {(i === 0 ||
                  dayInZone(visibleMessages[i - 1].createdAt, me.tenant.timeZone) !==
                    dayInZone(m.createdAt, me.tenant.timeZone)) && (
                  <div className="flex items-center gap-3 text-xs text-muted-foreground">
                    <Separator className="flex-1" />
                    <span>{dayLabel(m.createdAt, me.tenant.timeZone)}</span>
                    <Separator className="flex-1" />
                  </div>
                )}
                <div
                  className={cn('flex items-end gap-2', m.sender !== 'patient' && 'justify-end')}
                >
                  {m.sender === 'patient' && <PersonAvatar name={chat.contact.name} />}
                  <div
                    className={cn(
                      'bubble flex max-w-4/5 min-w-0 flex-col gap-1 rounded-lg border px-3 py-2 text-sm',
                      m.sender !== 'patient' && 'bg-muted',
                    )}
                  >
                    <div className="flex items-center gap-1 text-xs font-medium text-muted-foreground">
                      {m.sender === 'agent' ? (
                        <>
                          <Sparkles className="size-3" />
                          Agente de atención
                        </>
                      ) : m.sender === 'human' ? (
                        'Equipo de recepción'
                      ) : (
                        chat.contact.name
                      )}
                    </div>
                    <p className="break-words whitespace-pre-wrap">{m.body}</p>
                    {m.mediaId && (
                      <Button asChild variant="outline" size="sm" className="self-start">
                        <a
                          href={`/api/crm/messages/${m.id}/media`}
                          target="_blank"
                          rel="noreferrer"
                        >
                          <Paperclip />{' '}
                          {m.type === 'audio' ? 'Escuchar / descargar audio' : 'Abrir archivo'}
                          <ExternalLink />
                        </a>
                      </Button>
                    )}
                    <div className="flex items-center justify-end gap-1 text-xs text-muted-foreground">
                      <time>{time(m.createdAt, me.tenant.timeZone)}</time>
                      {m.sender !== 'patient' && (
                        <span title={labels[m.status]}>
                          {m.status === 'read' ? (
                            <CheckCheck className="size-3" />
                          ) : m.status === 'sent' || m.status === 'delivered' ? (
                            <Check className="size-3" />
                          ) : (
                            labels[m.status]
                          )}
                        </span>
                      )}
                    </div>
                  </div>
                </div>
              </div>
            ))}
            <div ref={bottom} />
          </div>
        </div>
        <form className="flex flex-col gap-2 border-t p-3" onSubmit={send}>
          {blocked && (
            <Alert id={blockId} role="status">
              <MessageSquareOff />
              <AlertTitle>
                {blocked.code === 'window'
                  ? 'Solo se pueden enviar plantillas aprobadas'
                  : 'No se pueden enviar mensajes'}
              </AlertTitle>
              <AlertDescription>{blocked.reason}</AlertDescription>
            </Alert>
          )}
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <UserRound className="size-3" /> Respuesta del equipo
            </span>
            <Button type="button" variant="ghost" size="sm" onClick={() => setNote(true)}>
              <FileText /> Nota interna
            </Button>
          </div>
          <SavedReplies me={me} onInsert={insert} />
          {file && (
            <div className="flex flex-wrap items-center gap-2 rounded-md border p-2 text-sm">
              <Paperclip className="size-4 shrink-0 text-muted-foreground" />
              <span className="min-w-0 flex-1 truncate" title={file.file.name}>
                {file.file.name}
              </span>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={busy}
                onClick={() => setFile(null)}
              >
                Quitar
              </Button>
              <Button
                type="button"
                size="sm"
                className="max-w-full"
                disabled={busy || !canReply || !!blocked}
                aria-describedby={blocked ? blockId : undefined}
                onClick={sendFile}
              >
                {busy ? <Spinner /> : <Send />}
                <span className="truncate">Enviar archivo a {chat.contact.name}</span>
              </Button>
            </div>
          )}
          <Textarea
            ref={draft}
            aria-label="Mensaje al paciente"
            rows={2}
            placeholder="Escribe un mensaje para el paciente…"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            maxLength={4000}
            disabled={!canReply}
          />
          <div className="flex items-center gap-2">
            <Button
              asChild
              variant="ghost"
              size="icon-sm"
              className={cn((busy || !canReply || !!blocked) && 'pointer-events-none opacity-50')}
            >
              <label title="Adjuntar archivo" aria-disabled={busy || !canReply || !!blocked}>
                <Paperclip />
                <span className="sr-only">Adjuntar archivo</span>
                <input
                  type="file"
                  accept=".pdf,.jpg,.jpeg,.png,.ogg,.mp3,.mp4"
                  className="sr-only"
                  disabled={busy || !canReply || !!blocked}
                  onChange={(e) => {
                    const chosen = e.target.files?.[0];
                    setFile(chosen ? { file: chosen, key: crypto.randomUUID() } : null);
                    e.target.value = '';
                  }}
                />
              </label>
            </Button>
            <small className="min-w-0 flex-1 text-xs text-muted-foreground">
              {!canReply
                ? 'Solicita la transferencia al responsable para responder.'
                : blocked
                  ? 'Enviar no está disponible por ahora.'
                  : 'Al responder, tomarás la conversación.'}
            </small>
            <Button
              type="submit"
              size="sm"
              disabled={busy || !body.trim() || !canReply || !!blocked}
              aria-describedby={blocked ? blockId : undefined}
            >
              {busy ? <Spinner /> : <Send />}Enviar
            </Button>
          </div>
        </form>
      </section>
      {wide ? (
        <aside className="flex w-72 shrink-0 flex-col gap-4 overflow-y-auto border-l p-4 2xl:w-80">
          <div className="text-xs font-medium text-muted-foreground">CONTEXTO DEL PACIENTE</div>
          {context}
        </aside>
      ) : (
        <Sheet open={details} onOpenChange={setDetails}>
          <SheetContent showCloseButton={false} aria-describedby={undefined} className="gap-0">
            <SheetHeader className="flex-row items-center justify-between gap-2">
              <SheetTitle>CONTEXTO DEL PACIENTE</SheetTitle>
              <SheetClose asChild>
                <Button variant="ghost" size="icon-sm" aria-label="Cerrar detalles">
                  <X />
                </Button>
              </SheetClose>
            </SheetHeader>
            <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-4">{context}</div>
          </SheetContent>
        </Sheet>
      )}
      <FormDialog
        title="Nota interna"
        description="Visible para tu equipo; no se envía por WhatsApp."
        open={note}
        onClose={() => setNote(false)}
        fields={[{ name: 'body', label: 'Nota de seguimiento', type: 'textarea', required: true }]}
        onSubmit={async (v) => {
          await api('/activities', 'POST', {
            ...v,
            contactId: chat.contact.id,
            conversationId: c.id,
          });
          refreshActivities();
        }}
      />
      <PatientLink
        key={chat.contact.id}
        contact={chat.contact}
        open={patient}
        onClose={() => setPatient(false)}
        onChange={refresh}
      />
    </>
  );
}
