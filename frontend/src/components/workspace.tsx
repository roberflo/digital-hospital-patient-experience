'use client';
import { GoogleCalendarConnection } from './google-calendar-connection';
import {
  useEffect,
  useState,
  useRef,
  useSyncExternalStore,
  type FormEvent,
  type ReactNode,
} from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { endSession } from '@/lib/session-client';
import {
  HeartPulse,
  LayoutDashboard,
  Inbox,
  Users,
  Building2,
  Columns3,
  CalendarDays,
  Activity as ActivityIcon,
  Settings,
  Search,
  Plus,
  ArrowUpRight,
  ChevronLeft,
  ChevronRight,
  Send,
  Paperclip,
  Sparkles,
  UserRound,
  Check,
  CheckCheck,
  ShieldCheck,
  Link2,
  LogOut,
  X,
  MessageCircle,
  FileText,
  RefreshCw,
  Pause,
  Play,
  ArrowLeft,
  AlertCircle,
  ExternalLink,
  Stethoscope,
} from 'lucide-react';
import { toast } from 'sonner';
import { Alert, AlertDescription } from './ui/alert';
import { Avatar, AvatarFallback } from './ui/avatar';
import { Badge } from './ui/badge';
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from './ui/breadcrumb';
import { Button } from './ui/button';
import { ButtonGroup } from './ui/button-group';
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from './ui/card';
import { Checkbox } from './ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from './ui/empty';
import {
  Field as UiField,
  FieldContent,
  FieldDescription,
  FieldGroup,
  FieldLabel,
} from './ui/field';
import { Input } from './ui/input';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import { Item, ItemActions, ItemContent, ItemDescription, ItemMedia, ItemTitle } from './ui/item';
import { Label } from './ui/label';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Separator } from './ui/separator';
import { Sheet, SheetClose, SheetContent, SheetHeader, SheetTitle } from './ui/sheet';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuAction,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarSeparator,
  SidebarTrigger,
  useSidebar,
} from './ui/sidebar';
import { Spinner } from './ui/spinner';
import { Switch } from './ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';
import { Textarea } from './ui/textarea';
import {
  api,
  fetcher,
  type Me,
  type Contact,
  type Chat,
  type Message,
  type Activity,
  type Channel,
  type Opportunity,
} from '@/lib/api';
import { cn } from '@/lib/utils';
import { AppointmentReminders, ReminderConsent } from './appointment-reminders';
import { AttentionSettings } from './attention-settings';
import {
  HospitalCompanies,
  CustomerCommercial,
  OpportunityCommercial,
} from './commercial-workspace';
import { ActivityWorkspace } from './activity-workspace';
import { PatientAppointments } from './patient-appointments';
import { HospitalConnection, PatientLink } from './hospital-connection';
import { PatientClinical } from './patient-clinical';
import { SavedInboxViews, ConversationMacros } from './inbox-productivity';
import { DashboardView } from './dashboard-view';
import { FirstSteps } from './first-steps';
import { ThemeToggle } from './theme-toggle';
import {
  AssignmentControl,
  TeamWorkspace,
  managesTeam,
  memberLabel,
  type Workload,
} from './team-workspace';
import {
  WorkflowControls,
  CustomerCrm,
  SavedReplies,
  ChannelConnection,
  conversationStates,
  priorities,
} from './conversation-workspace';
const nav = [
  ['dashboard', 'Dashboard', LayoutDashboard],
  ['inbox', 'Bandeja de entrada', Inbox],
  ['team', 'Equipo', Users],
  ['contacts', 'Contactos', Users],
  ['companies', 'Empresas', Building2],
  ['opportunities', 'Oportunidades', Columns3],
  ['calendar', 'Agenda', CalendarDays],
  ['hospital', 'Mi hospital', HeartPulse],
  ['activity', 'Actividad', ActivityIcon],
  ['agent', 'Agente de atención', Sparkles],
] as const;
const stages = [
  ['new', 'Nuevo'],
  ['contacted', 'En seguimiento'],
  ['scheduled', 'Agendado'],
  ['won', 'Completado'],
  ['lost', 'Cerrado'],
] as const;
const labels: Record<string, string> = {
  agent: 'Agente IA',
  human: 'Atención humana',
  closed: 'Cerrada',
  admin: 'Administrador',
  doctor: 'Doctor',
  patient: 'Paciente',
  sent: 'Enviado',
  delivered: 'Entregado',
  read: 'Leído',
  received: 'Recibido',
  sending: 'Enviando',
  uncertain: 'Sin confirmar',
  failed: 'Falló',
  done: 'Completado',
  pending: 'Pendiente',
  running: 'En curso',
};
function initials(name: string) {
  return name
    .split(' ')
    .map((x) => x[0])
    .slice(0, 2)
    .join('')
    .toUpperCase();
}
function time(value: string, tz = 'America/El_Salvador') {
  return new Date(value).toLocaleTimeString('es-SV', {
    hour: '2-digit',
    minute: '2-digit',
    timeZone: tz,
  });
}
function date(value: string) {
  return new Date(value).toLocaleDateString('es-SV', { day: 'numeric', month: 'short' });
}
function PersonAvatar({ name, large = false }: { name: string; large?: boolean }) {
  return (
    <Avatar className={cn(large && 'size-16')}>
      <AvatarFallback className={cn(large && 'text-lg')}>{initials(name)}</AvatarFallback>
    </Avatar>
  );
}
function StatusBadge({ value }: { value: string }) {
  return (
    <Badge
      variant={
        value === 'failed'
          ? 'destructive'
          : value === 'human' || value === 'agent'
            ? 'secondary'
            : 'outline'
      }
    >
      {value === 'agent' && <Sparkles />}
      {labels[value] ?? value}
    </Badge>
  );
}
function EmptyState({
  icon: Icon = Inbox,
  title,
  children,
}: {
  icon?: typeof Inbox;
  title: string;
  children?: ReactNode;
}) {
  return (
    <Empty>
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <Icon />
        </EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        {children && <EmptyDescription>{children}</EmptyDescription>}
      </EmptyHeader>
    </Empty>
  );
}
function ErrorBox({ error }: { error?: Error }) {
  return error ? (
    <Alert variant="destructive">
      <AlertCircle />
      <AlertDescription>{error.message}</AlertDescription>
    </Alert>
  ) : null;
}
function Loading() {
  return (
    <div className="flex items-center justify-center gap-2 p-10 text-sm text-muted-foreground">
      <Spinner /> Cargando tu espacio…
    </div>
  );
}
type Field = {
  name: string;
  label: string;
  type?: string;
  required?: boolean;
  options?: { value: string; label: string }[];
  value?: string;
  placeholder?: string;
};
function FormDialog({
  title,
  description,
  fields,
  open,
  onClose,
  onSubmit,
}: {
  title: string;
  description?: string;
  fields: Field[];
  open: boolean;
  onClose: () => void;
  onSubmit: (values: Record<string, string>) => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        if (!v) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            {description ?? 'Completa los datos para continuar.'}
          </DialogDescription>
        </DialogHeader>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            try {
              await onSubmit(
                Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>,
              );
              onClose();
              toast.success('Cambios guardados');
            } catch (err) {
              toast.error((err as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          <FieldGroup>
            {fields.map((f) => (
              <UiField key={f.name}>
                <FieldLabel htmlFor={'field-' + f.name}>{f.label}</FieldLabel>
                {f.options ? (
                  <NativeSelect
                    id={'field-' + f.name}
                    className="w-full"
                    name={f.name}
                    defaultValue={f.value}
                    required={f.required}
                  >
                    <NativeSelectOption value="">Selecciona una opción</NativeSelectOption>
                    {f.options.map((o) => (
                      <NativeSelectOption key={o.value} value={o.value}>
                        {o.label}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                ) : f.type === 'textarea' ? (
                  <Textarea
                    id={'field-' + f.name}
                    rows={4}
                    name={f.name}
                    defaultValue={f.value}
                    required={f.required}
                    maxLength={10000}
                  />
                ) : (
                  <Input
                    id={'field-' + f.name}
                    name={f.name}
                    type={f.type ?? 'text'}
                    defaultValue={f.value}
                    required={f.required}
                    placeholder={f.placeholder}
                    maxLength={f.type === 'text' || !f.type ? 500 : undefined}
                  />
                )}
              </UiField>
            ))}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose}>
                Cancelar
              </Button>
              <Button disabled={busy}>{busy ? <Spinner /> : <Check />}Guardar</Button>
            </DialogFooter>
          </FieldGroup>
        </form>
      </DialogContent>
    </Dialog>
  );
}
const descriptions: Record<string, string> = {
  dashboard: 'El resumen de la atención de tu hospital.',
  team: 'Organiza responsables y reparte la atención de tu hospital.',
  contacts: 'Conoce a tus pacientes. Acompaña cada paso.',
  companies: 'Relaciones y convenios que conectan tu hospital.',
  opportunities: 'Del primer contacto al seguimiento de la atención.',
  calendar: 'Una agenda compartida para todo tu hospital.',
  hospital: 'Tu cuenta, tus pacientes y tu equipo, conectados al mismo hospital.',
  activity: 'Quién atendió a cada paciente, qué hizo y cómo continuó la atención.',
  agent: 'Un compañero para tu equipo. Disponible para tus pacientes.',
  settings: 'Personaliza cómo trabaja y se conecta tu hospital.',
};
function AppSidebar({
  me,
  view,
  waiting,
  onNavigate,
}: {
  me?: Me;
  view: string;
  waiting?: number;
  onNavigate: (view: string) => void;
}) {
  const { setOpenMobile } = useSidebar();
  const go = (v: string) => {
    setOpenMobile(false);
    onNavigate(v);
  };
  return (
    <Sidebar>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild>
              <a href="/" aria-label="Recepción inicio">
                <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground">
                  <HeartPulse className="size-4" />
                </div>
                <div className="grid flex-1 text-left text-sm leading-tight">
                  <span className="truncate font-medium">Recepción</span>
                  <span className="truncate text-xs">CRM del hospital</span>
                </div>
              </a>
            </SidebarMenuButton>
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              size="lg"
              isActive={view === 'hospital'}
              onClick={() => go('hospital')}
              aria-label="Ver conexión con mi hospital"
            >
              <div className="flex aspect-square size-8 items-center justify-center rounded-lg border bg-background">
                <Building2 className="size-4" />
              </div>
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="truncate font-medium">{me?.tenant.name ?? 'Hospital'}</span>
                <span className="truncate text-xs">Ver conexión y cuenta</span>
              </div>
              <ChevronRight className="ml-auto" />
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Espacio de trabajo</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {nav.map(([id, label, Icon]) => (
                <SidebarMenuItem key={id}>
                  <SidebarMenuButton
                    aria-label={label}
                    isActive={view === id}
                    onClick={() => go(id)}
                  >
                    <Icon />
                    <span>{label}</span>
                  </SidebarMenuButton>
                  {id === 'inbox' && !!waiting && <SidebarMenuBadge>{waiting}</SidebarMenuBadge>}
                  {id === 'agent' && <SidebarMenuBadge>IA</SidebarMenuBadge>}
                </SidebarMenuItem>
              ))}
              <SidebarMenuItem>
                <SidebarMenuButton asChild>
                  <a href="/whatsapp">
                    <MessageCircle />
                    <span>WhatsApp · números</span>
                  </a>
                </SidebarMenuButton>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>
      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton
              size="lg"
              onClick={() => go('agent')}
              aria-label="Ver actividad del agente"
            >
              <div className="flex aspect-square size-8 items-center justify-center rounded-lg border bg-background">
                {me?.tenant.agentEnabled ? (
                  <Sparkles className="size-4" />
                ) : (
                  <Pause className="size-4" />
                )}
              </div>
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="truncate font-medium">
                  {me?.tenant.agentEnabled ? 'Agente disponible' : 'Agente en pausa'}
                </span>
                <span className="truncate text-xs">
                  {me?.tenant.agentEnabled
                    ? 'Conectado con tu equipo'
                    : 'Actívalo cuando esté configurado'}
                </span>
              </div>
            </SidebarMenuButton>
          </SidebarMenuItem>
          {me?.role === 'admin' && (
            <SidebarMenuItem>
              <SidebarMenuButton isActive={view === 'settings'} onClick={() => go('settings')}>
                <Settings />
                <span>Configuración</span>
              </SidebarMenuButton>
            </SidebarMenuItem>
          )}
        </SidebarMenu>
        <SidebarSeparator />
        <SidebarMenu>
          <SidebarMenuItem className="profile">
            <SidebarMenuButton size="lg" asChild>
              <div>
                <PersonAvatar name={me?.name ?? 'Usuario'} />
                <div className="grid flex-1 text-left text-sm leading-tight">
                  <span className="truncate font-medium">{me?.name ?? 'Conectando…'}</span>
                  <span className="truncate text-xs">
                    {me?.role === 'agent' ? 'Recepcionista' : (labels[me?.role ?? ''] ?? '')}
                  </span>
                </div>
              </div>
            </SidebarMenuButton>
            <SidebarMenuAction aria-label="Cerrar sesión" onClick={endSession}>
              <LogOut />
            </SidebarMenuAction>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
    </Sidebar>
  );
}
export default function Workspace() {
  const [view, setView] = useState('dashboard');
  const [search, setSearch] = useState('');
  const [teamAssignment, setTeamAssignment] = useState<string | null>(null);
  const [todayLabel, setTodayLabel] = useState('');
  const { data: me, error } = useSWR<Me>('/me', fetcher);
  const { data: stats, error: statsError } = useSWR<Record<string, number>>('/overview', fetcher, {
    refreshInterval: 10000,
  });
  useEffect(() => {
    if (me)
      setTodayLabel(
        new Date().toLocaleDateString('es-SV', {
          weekday: 'short',
          day: 'numeric',
          month: 'long',
          timeZone: me.tenant.timeZone,
        }),
      );
  }, [me?.tenant.timeZone]);
  useEffect(() => {
    const v = new URLSearchParams(location.search).get('view');
    if (v && (v === 'settings' || nav.some(([id]) => id === v))) setView(v);
  }, []);
  const navigate = (v: string) => {
    setView(v);
    setSearch('');
    history.replaceState(null, '', '/?view=' + v);
  };
  const title =
    view === 'settings' ? 'Configuración' : (nav.find((n) => n[0] === view)?.[1] ?? 'Recepción');
  const searchable = !['hospital', 'settings', 'agent', 'dashboard', 'activity'].includes(view);
  return (
    <SidebarProvider>
      <AppSidebar me={me} view={view} waiting={stats?.human} onNavigate={navigate} />
      <SidebarInset className={cn('min-w-0', view === 'inbox' && 'h-svh overflow-hidden')}>
        <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
          <SidebarTrigger className="-ml-1 md:hidden" disabled={!me} aria-label="Abrir menú" />
          <Separator
            orientation="vertical"
            className="mr-2 data-[orientation=vertical]:h-4 md:hidden"
          />
          <Breadcrumb>
            <BreadcrumbList>
              <BreadcrumbItem className="hidden md:block">Espacio de trabajo</BreadcrumbItem>
              <BreadcrumbSeparator className="hidden md:block" />
              <BreadcrumbItem>
                <BreadcrumbPage>{title}</BreadcrumbPage>
              </BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
          <div className="ml-auto flex items-center gap-3 text-sm text-muted-foreground">
            <span className="hidden sm:inline">{todayLabel}</span>
            <Badge variant="outline">
              <ShieldCheck /> Sesión protegida
            </Badge>
            <ThemeToggle />
          </div>
        </header>
        <div
          className={cn(
            'flex min-h-0 flex-1 flex-col p-4 md:p-6',
            view === 'inbox' ? 'gap-3 md:py-4' : 'gap-6',
          )}
        >
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex flex-col gap-1">
              <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
              {descriptions[view] && <p className="text-muted-foreground">{descriptions[view]}</p>}
            </div>
            {searchable && (
              <InputGroup className="w-full sm:w-72">
                <InputGroupInput
                  aria-label="Buscar"
                  placeholder="Buscar en esta vista…"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
                <InputGroupAddon>
                  <Search />
                </InputGroupAddon>
              </InputGroup>
            )}
          </div>
          <ErrorBox error={error} />
          {!me && !error ? (
            <Loading />
          ) : (
            me && (
              <div className="flex min-h-0 flex-1 flex-col gap-6">
                {view === 'dashboard' ? (
                  <>
                    <ErrorBox error={statsError} />
                    <FirstSteps me={me} onNavigate={navigate} />
                    <DashboardView stats={stats} onNavigate={navigate} />
                  </>
                ) : view === 'inbox' ? (
                  <InboxView me={me} search={search} initialAssignment={teamAssignment} />
                ) : view === 'team' ? (
                  <TeamWorkspace
                    me={me}
                    search={search}
                    onOpenInbox={(assignment) => {
                      setTeamAssignment(assignment);
                      navigate('inbox');
                    }}
                  />
                ) : view === 'contacts' ? (
                  <ContactsView search={search} />
                ) : view === 'companies' ? (
                  <HospitalCompanies search={search} />
                ) : view === 'opportunities' ? (
                  <OpportunitiesView search={search} />
                ) : view === 'hospital' ? (
                  <HospitalConnection />
                ) : view === 'calendar' ? (
                  <CalendarView search={search} me={me} />
                ) : view === 'activity' ? (
                  <ActivityWorkspace search={search} onSearch={setSearch} me={me} />
                ) : view === 'agent' ? (
                  <AgentView me={me} />
                ) : view === 'settings' ? (
                  <SettingsView me={me} />
                ) : null}
              </div>
            )
          )}
        </div>
      </SidebarInset>
    </SidebarProvider>
  );
}
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
function ActivityTimeline({ items, proposal }: { items?: Activity[]; proposal: string }) {
  if (!items?.length) return null;
  return (
    <div className="flex flex-col gap-4">
      {items.map((a) => (
        <div key={a.id} className="flex flex-col gap-1 border-l pl-3 text-sm">
          <strong className="font-medium">{a.actor}</strong>
          <p className="break-words whitespace-pre-wrap text-muted-foreground">
            {a.kind.startsWith('proposal') ? proposal : a.body}
          </p>
          <small className="text-xs text-muted-foreground">
            {date(a.createdAt)} · {time(a.createdAt)}
          </small>
        </div>
      ))}
    </div>
  );
}
function InboxView({
  me,
  search,
  initialAssignment,
}: {
  me: Me;
  search: string;
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
  }, []);
  const [channelId, setChannelId] = useState('');
  const [priority, setPriority] = useState('');
  const [label, setLabel] = useState('');
  const [page, setPage] = useState(1);
  const { data: channels } = useSWR<Channel[]>('/channels', fetcher);
  useEffect(() => setPage(1), [state, assignment, channelId, priority, label]);
  useEffect(() => setChecked({}), [state, assignment, channelId, priority, label, page, search]);
  const query = new URLSearchParams({ assignment, page: String(page) });
  if (linkedConversation) query.set('conversationId', linkedConversation);
  if (linkedPhone) query.set('phone', linkedPhone);
  if (linkedNumber) query.set('phoneNumberId', linkedNumber);
  if (state) query.set('state', state);
  if (channelId) query.set('channelId', channelId);
  if (priority) query.set('priority', priority);
  if (label.trim()) query.set('label', label.trim());
  const {
    data: chats,
    error,
    mutate,
  } = useSWR<Chat[]>('/conversations?' + query, fetcher, { refreshInterval: 5000 });
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState('all');
  const filtered = chats?.filter(
    (c) =>
      (filter === 'all' || c.conversation.status === filter) &&
      (c.contact.name.toLowerCase().includes(search.toLowerCase()) ||
        c.contact.phone.includes(search)),
  );
  const active =
    chats?.find((c) => c.conversation.id === selected) ??
    (linkedPhone && chats?.length === 1 ? chats[0] : undefined);
  async function assignSelection() {
    setAssigning(true);
    try {
      await api('/conversations/assign', 'POST', {
        conversations: Object.entries(checked).map(([id, expectedRevision]) => ({
          id,
          expectedRevision,
        })),
        assignedTo: bulkTarget || null,
      });
      setChecked({});
      toast.success('Conversaciones asignadas');
    } catch (e) {
      toast.error((e as Error).message);
      setChecked({});
    } finally {
      setAssigning(false);
      mutate();
      refreshTeam('/members/workload');
    }
  }
  return (
    <>
      <ErrorBox error={error} />
      <Card className="inbox-layout min-h-0 flex-1 flex-row gap-0 overflow-hidden py-0">
        <section
          className={cn(
            'w-full shrink-0 flex-col overflow-y-auto md:flex md:w-72 md:overflow-hidden md:border-r 2xl:w-80',
            active ? 'hidden' : 'flex',
          )}
        >
          <div className="flex flex-col gap-3 border-b p-3">
            <div className="flex items-center gap-1">
              <h2 className="flex items-center gap-2 text-sm font-semibold">
                Conversaciones <Badge variant="secondary">{filtered?.length ?? 0}</Badge>
              </h2>
              <Button asChild variant="link" size="sm" className="ml-auto">
                <a href="/whatsapp" title="Gestionar números de WhatsApp">
                  Números ↗
                </a>
              </Button>
              <Button
                variant="ghost"
                size="icon-sm"
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
                setSelected(null);
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
              <Input
                className="col-span-2"
                aria-label="Filtrar por etiqueta"
                placeholder="Etiqueta de conversación"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                maxLength={40}
              />
            </div>
            <ButtonGroup className="w-full *:flex-1">
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
            {(linkedPhone || linkedConversation) && (
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
                {linkedConversation
                  ? 'Conversación desde Actividad'
                  : 'Conversación desde WhatsApp'}{' '}
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
                      <NativeSelectOption value="">Sin asignar</NativeSelectOption>
                      {workload?.members
                        .filter((m) => !m.disabled)
                        .map((m) => (
                          <NativeSelectOption key={m.subject} value={m.subject}>
                            {memberLabel(m)}
                          </NativeSelectOption>
                        ))}
                    </NativeSelect>
                    <Button size="sm" disabled={assigning} onClick={assignSelection}>
                      Asignar selección
                    </Button>
                  </>
                )}
              </div>
            )}
          </div>
          <div className="flex flex-col md:min-h-0 md:flex-1 md:overflow-y-auto">
            {!chats ? (
              <Loading />
            ) : !filtered?.length ? (
              <EmptyState title="Todo al día">
                Las conversaciones entrantes aparecerán aquí. Revisa los filtros si buscas otra
                atención.
              </EmptyState>
            ) : (
              filtered.map(({ conversation: c, contact, channel, unreadCount }) => (
                <div
                  key={c.id}
                  className={cn(
                    'flex items-center border-b last:border-b-0',
                    managesTeam(me) && 'pl-3',
                  )}
                >
                  {managesTeam(me) && (
                    <Checkbox
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
                    variant={active?.conversation.id === c.id ? 'muted' : 'default'}
                    className="conversation-card min-w-0 flex-1 flex-nowrap text-left hover:bg-accent/50"
                  >
                    <button onClick={() => setSelected(c.id)}>
                      <ItemMedia>
                        <PersonAvatar name={contact.name} />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle className="w-full justify-between">
                          <strong className="flex min-w-0 items-center gap-2 font-medium">
                            <span className="truncate">{contact.name}</span>
                            {unreadCount > 0 && (
                              <Badge aria-label={`${unreadCount} mensajes sin leer`}>
                                {unreadCount}
                              </Badge>
                            )}
                          </strong>
                          <time className="shrink-0 text-xs font-normal text-muted-foreground">
                            {time(c.updatedAt, me.tenant.timeZone)}
                          </time>
                        </ItemTitle>
                        <ItemDescription className="line-clamp-1">
                          {c.lastMessage || c.summary || 'Nueva conversación de WhatsApp'}
                        </ItemDescription>
                        <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                          <Badge variant="outline">
                            {conversationStates.find(([v]) => v === c.state)?.[1]}
                          </Badge>
                          <StatusBadge value={c.status} />
                          {c.priority !== 'normal' && (
                            <Badge variant={c.priority === 'urgent' ? 'destructive' : 'secondary'}>
                              {priorities.find(([v]) => v === c.priority)?.[1]}
                            </Badge>
                          )}
                          <small className="text-xs">{channel.name}</small>
                          <span className="flex items-center gap-1">
                            <UserRound className="size-3" />
                            {workload?.members.find((m) => m.subject === c.assignedTo)?.name ??
                              (c.assignedTo ? 'Responsable asignado' : 'Sin asignar')}
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
              setSelected(null);
              clearLinkedConversation();
            }}
            refresh={() => mutate()}
          />
        ) : (
          <section className="hidden min-w-0 flex-1 items-center justify-center md:flex">
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
function ChatPanel({
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
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'nearest' });
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
      if (sent.status !== 'sent')
        toast.error('Envío ' + (labels[sent.status] ?? sent.status) + '. Revisa antes de repetir.');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const context = (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col items-center gap-2 text-center">
        <PersonAvatar name={chat.contact.name} large />
        <h3 className="font-semibold">{chat.contact.name}</h3>
        <p className="text-sm text-muted-foreground">+{chat.contact.phone}</p>
        {chat.contact.tags && <Badge variant="secondary">{chat.contact.tags}</Badge>}
      </div>
      <Separator />
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
        />
      </div>
    </div>
  );
  return (
    <>
      <section className="flex min-w-0 flex-1 flex-col overflow-y-auto">
        <div className="flex items-center gap-3 border-b p-3">
          <Button
            variant="ghost"
            size="icon-sm"
            className="md:hidden"
            onClick={onClose}
            aria-label="Volver a conversaciones"
          >
            <ArrowLeft />
          </Button>
          <PersonAvatar name={chat.contact.name} />
          <div className="flex min-w-0 flex-col">
            <h2 className="truncate text-sm font-semibold">{chat.contact.name}</h2>
            <small className="truncate text-xs text-muted-foreground">+{chat.contact.phone}</small>
          </div>
          <div className="ml-auto flex shrink-0 items-center gap-1">
            <span className="hidden sm:inline-flex">
              <StatusBadge value={c.status} />
            </span>
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
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Ver información del paciente"
              onClick={() => setDetails(!details)}
            >
              <UserRound />
            </Button>
          </div>
        </div>
        <div className="flex flex-col gap-3 border-b p-3">
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
                {c.status === 'agent'
                  ? 'El agente está atendiendo esta conversación.'
                  : c.status === 'closed'
                    ? 'La conversación está cerrada.'
                    : 'Tu equipo está a cargo. El agente está en pausa.'}
              </span>
              <Button
                variant="outline"
                size="sm"
                disabled={!managesTeam(me) && !!c.assignedTo && c.assignedTo !== me.subject}
                onClick={() =>
                  action(
                    c.status === 'agent' ? 'human' : 'agent',
                    c.status === 'agent' ? me.subject : undefined,
                  )
                }
              >
                {c.status === 'agent' ? 'Tomar conversación' : 'Activar agente'}
                <ArrowUpRight />
              </Button>
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
            {(findMessages || onlyFiles) && (
              <span className="text-xs text-muted-foreground">
                {visibleMessages?.length ?? 0} resultados · últimos 200 mensajes
              </span>
            )}
          </div>
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
        <div className="flex min-h-48 flex-1 flex-col gap-3 overflow-y-auto p-4">
          <div className="flex items-center gap-3 text-xs text-muted-foreground">
            <Separator className="flex-1" />
            <span>Historial de atención</span>
            <Separator className="flex-1" />
          </div>
          {visibleMessages?.length === 0 && (
            <p className="text-center text-sm text-muted-foreground">
              {onlyFiles
                ? 'No hay archivos en los mensajes cargados.'
                : 'No hay mensajes que coincidan.'}
            </p>
          )}
          {visibleMessages?.map((m) => (
            <div
              className={cn('flex items-end gap-2', m.sender !== 'patient' && 'justify-end')}
              key={m.id}
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
                    <a href={`/api/crm/messages/${m.id}/media`} target="_blank" rel="noreferrer">
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
          ))}
          <div ref={bottom} />
        </div>
        <form className="flex flex-col gap-2 border-t p-3" onSubmit={send}>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <UserRound className="size-3" /> Respuesta del equipo
            </span>
            <Button type="button" variant="ghost" size="sm" onClick={() => setNote(true)}>
              <FileText /> Nota interna
            </Button>
          </div>
          <SavedReplies me={me} onInsert={(text) => setBody(text)} />
          <Textarea
            aria-label="Mensaje al paciente"
            rows={2}
            placeholder="Escribe un mensaje para el paciente…"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            maxLength={4000}
          />
          <div className="flex items-center gap-2">
            <Button asChild variant="ghost" size="icon-sm">
              <label title="Adjuntar archivo">
                <Paperclip />
                <span className="sr-only">Adjuntar archivo</span>
                <input
                  type="file"
                  accept=".pdf,.jpg,.jpeg,.png,.ogg,.mp3,.mp4"
                  className="sr-only"
                  disabled={busy || !canReply}
                  onChange={async (e) => {
                    const file = e.target.files?.[0];
                    if (!file) return;
                    const form = new FormData();
                    form.append('file', file);
                    setBusy(true);
                    try {
                      const sent = await api<Message>(`/conversations/${c.id}/media`, 'POST', form);
                      mutate();
                      refresh();
                      toast[sent.status === 'sent' ? 'success' : 'error'](
                        labels[sent.status] ?? sent.status,
                      );
                    } catch (err) {
                      toast.error((err as Error).message);
                    } finally {
                      setBusy(false);
                      e.target.value = '';
                    }
                  }}
                />
              </label>
            </Button>
            <small className="min-w-0 flex-1 text-xs text-muted-foreground">
              {canReply
                ? 'Al responder, tomarás la conversación.'
                : 'Solicita la transferencia al responsable para responder.'}
            </small>
            <Button type="submit" size="sm" disabled={busy || !body.trim() || !canReply}>
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
function ContactsView({ search }: { search: string }) {
  const [page, setPage] = useState(1);
  useEffect(() => setPage(1), [search]);
  const { data, error, mutate } = useSWR<Contact[]>(
    '/contacts?q=' + encodeURIComponent(search) + '&page=' + page,
    fetcher,
  );
  const [create, setCreate] = useState(false);
  const [edit, setEdit] = useState<Contact | null>(null);
  const [patientContact, setPatientContact] = useState<Contact | null>(null);
  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle>
            <h2 className="flex items-center gap-2">
              Contactos <Badge variant="secondary">{data?.length ?? 0}</Badge>
            </h2>
          </CardTitle>
          <CardAction>
            <Button size="sm" onClick={() => setCreate(true)}>
              <Plus />
              Nuevo contacto
            </Button>
          </CardAction>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <ErrorBox error={error} />
          {!data && !error ? (
            <Loading />
          ) : data?.length ? (
            <div className="w-0 min-w-full">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Paciente / contacto</TableHead>
                    <TableHead>Teléfono</TableHead>
                    <TableHead>Correo</TableHead>
                    <TableHead>Etiqueta</TableHead>
                    <TableHead>Relación / expediente</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.map((c) => (
                    <TableRow key={c.id}>
                      <TableCell>
                        <div className="flex items-center gap-2">
                          <PersonAvatar name={c.name} />
                          <strong className="font-medium">{c.name}</strong>
                        </div>
                      </TableCell>
                      <TableCell>+{c.phone}</TableCell>
                      <TableCell>{c.email || '—'}</TableCell>
                      <TableCell>
                        {c.tags ? <Badge variant="secondary">{c.tags}</Badge> : '—'}
                      </TableCell>
                      <TableCell>
                        <Badge variant={c.patientId ? 'secondary' : 'outline'}>
                          {c.isCustomer ? 'Cliente' : 'Contacto'} ·{' '}
                          {c.patientId ? 'Vinculado' : 'Por vincular'}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center justify-end gap-2">
                          <Button variant="outline" size="sm" onClick={() => setPatientContact(c)}>
                            {c.patientId ? 'Ver vínculo' : 'Vincular paciente'}
                          </Button>
                          <CustomerCommercial
                            contact={c}
                            onChange={() => {
                              mutate();
                            }}
                          />
                          <Button variant="ghost" size="sm" onClick={() => setEdit(c)}>
                            Editar
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          ) : (
            <EmptyState icon={Users} title="Tu próxima relación empieza aquí">
              Agrega un contacto o recibe su primer mensaje por WhatsApp.
            </EmptyState>
          )}
        </CardContent>
        <CardFooter className="flex-wrap justify-between gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={page === 1}
            onClick={() => setPage(page - 1)}
          >
            Anterior
          </Button>
          <span className="text-sm text-muted-foreground">Página {page} · hasta 100 contactos</span>
          <Button
            variant="outline"
            size="sm"
            disabled={!data || data.length < 100}
            onClick={() => setPage(page + 1)}
          >
            Siguiente
          </Button>
        </CardFooter>
      </Card>
      {patientContact && (
        <PatientLink
          key={patientContact.id}
          contact={patientContact}
          open
          onClose={() => setPatientContact(null)}
          onChange={() => {
            mutate();
          }}
        />
      )}
      <FormDialog
        title={edit ? 'Editar contacto' : 'Nuevo contacto'}
        open={create || !!edit}
        onClose={() => {
          setCreate(false);
          setEdit(null);
        }}
        fields={[
          { name: 'name', label: 'Nombre completo', required: true, value: edit?.name },
          {
            name: 'phone',
            label: 'Teléfono con código de país',
            required: true,
            value: edit?.phone,
            placeholder: '50370000000',
          },
          { name: 'email', label: 'Correo electrónico', type: 'email', value: edit?.email },
          { name: 'tags', label: 'Etiquetas', value: edit?.tags },
        ]}
        onSubmit={async (v) => {
          await api('/contacts' + (edit ? '/' + edit.id : ''), edit ? 'PUT' : 'POST', v);
          mutate();
        }}
      />
    </>
  );
}
function OpportunitiesView({ search }: { search: string }) {
  const { data, error, mutate } = useSWR<Opportunity[]>('/opportunities', fetcher);
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const [create, setCreate] = useState(false);
  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span className="text-sm text-muted-foreground">
          Seguimientos de la atención y oportunidades del hospital
        </span>
        <Button size="sm" onClick={() => setCreate(true)}>
          <Plus />
          Nuevo seguimiento
        </Button>
      </div>
      <ErrorBox error={error} />
      <div className="flex w-0 min-w-full items-start gap-4 overflow-x-auto pb-2">
        {stages.map(([id, label]) => {
          const rows = data?.filter(
            (o) => o.stage === id && o.title.toLowerCase().includes(search.toLowerCase()),
          );
          return (
            <section
              className="kanban-column flex max-w-80 min-w-64 flex-1 flex-col gap-3 rounded-xl bg-muted/50 p-3"
              key={id}
            >
              <div className="flex items-center gap-2">
                <h3 className="text-sm font-medium">{label}</h3>
                <Badge variant="secondary">{rows?.length ?? 0}</Badge>
              </div>
              {rows?.map((o) => (
                <Card key={o.id}>
                  <CardHeader>
                    <CardDescription>ATENCIÓN AL PACIENTE</CardDescription>
                    <CardTitle>
                      <h4 className="break-words">{o.title}</h4>
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="flex flex-col gap-4">
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <UserRound className="size-4 shrink-0" />
                      {contacts?.find((c) => c.id === o.contactId)?.name ?? 'Contacto'}
                    </div>
                    {o.value > 0 && (
                      <strong className="flex flex-wrap items-baseline gap-2 font-semibold">
                        ${o.value.toFixed(2)}{' '}
                        <small className="text-xs font-normal text-muted-foreground">
                          {o.hospitalPurchaseId
                            ? 'Pagado'
                            : o.hospitalQuote
                              ? 'Cotizado por Hospital'
                              : 'Estimado'}
                        </small>
                      </strong>
                    )}
                    <OpportunityCommercial
                      opportunity={o}
                      onChange={() => {
                        mutate();
                      }}
                    />
                    <UiField>
                      <NativeSelect
                        aria-label={'Etapa de ' + o.title}
                        value={o.stage}
                        onChange={async (e) => {
                          try {
                            await api(`/opportunities/${o.id}`, 'PATCH', { stage: e.target.value });
                            mutate();
                          } catch (err) {
                            toast.error((err as Error).message);
                          }
                        }}
                      >
                        {stages.map(([key, value]) => (
                          <NativeSelectOption value={key} key={key}>
                            {value}
                          </NativeSelectOption>
                        ))}
                      </NativeSelect>
                    </UiField>
                  </CardContent>
                </Card>
              ))}
              <Button
                variant="ghost"
                size="sm"
                className="justify-start"
                onClick={() => setCreate(true)}
              >
                <Plus />
                Agregar seguimiento
              </Button>
            </section>
          );
        })}
      </div>
      <FormDialog
        title="Nuevo seguimiento"
        open={create}
        onClose={() => setCreate(false)}
        fields={[
          { name: 'title', label: 'Título', required: true },
          {
            name: 'contactId',
            label: 'Contacto',
            required: true,
            options: contacts?.map((c) => ({ value: c.id, label: c.name })) ?? [],
          },
          { name: 'value', label: 'Valor estimado (USD)', type: 'number', value: '0' },
          {
            name: 'stage',
            label: 'Etapa',
            required: true,
            value: 'new',
            options: stages.map(([value, label]) => ({ value, label })),
          },
        ]}
        onSubmit={async (v) => {
          await api('/opportunities', 'POST', { ...v, value: Number(v.value) });
          mutate();
        }}
      />
    </>
  );
}
type AgendaRow = {
  appointmentId: string;
  patientId: string;
  displayName: string;
  scheduledStart: string;
  durationMinutes: number;
  status: string;
  clinicianName: string;
  placeName: string;
  overlaps: boolean;
};
type Availability = {
  professionals: {
    clinicianId: string;
    clinicianName: string;
    defaultDurationMinutes: number;
    days: {
      nextOpenDay?: string | null;
      slots: { startsAt: string; durationMinutes: number; offered: boolean; takenBy: number }[];
    }[];
  }[];
};
/** A doctor whose printed name Hospital has not captured yet still has to be choosable. */
const doctorName = (name: string) => name.trim() || 'Profesional sin nombre';
const freeSlots = (p: Availability['professionals'][number]) =>
  p.days.flatMap((d) => d.slots).filter((s) => s.offered && s.takenBy === 0);
function CalendarView({ search, me }: { search: string; me: Me }) {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: me.tenant.timeZone }).format(
    new Date(),
  );
  const [day, setDay] = useState(today);
  const [create, setCreate] = useState(false);
  const [modify, setModify] = useState<AgendaRow | null>(null);
  const [doctor, setDoctor] = useState('');
  const [cancel, setCancel] = useState<AgendaRow | null>(null);
  const [cancelReason, setCancelReason] = useState('');
  const appointmentBusy = useRef(false);
  const appointmentRequest = useRef<{ body: string; key: string } | null>(null);
  async function saveAppointment(input: unknown) {
    const body = JSON.stringify(input);
    if (appointmentRequest.current?.body !== body)
      appointmentRequest.current = { body, key: crypto.randomUUID() };
    return api<{ overlaps?: boolean }>(
      '/hospital/appointments',
      'POST',
      input,
      appointmentRequest.current.key,
    );
  }
  const [savingAppointment, setSavingAppointment] = useState(false);
  const { data, error, mutate } = useSWR<{ rows: AgendaRow[] }>(
    `/hospital/agenda?date=${day}`,
    fetcher,
    { shouldRetryOnError: false },
  );
  const {
    data: availability,
    error: availabilityError,
    isLoading: availabilityLoading,
  } = useSWR<Availability>(
    create || modify ? `/hospital/availability?date=${day}` : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const options = availability?.professionals.find((p) => p.clinicianId === doctor);
  const slots = options ? freeSlots(options) : [];
  // A day nobody attends (a Sunday) used to look like a broken form: every doctor listed, no hour to pick.
  const closedDay =
    !!availability?.professionals.length &&
    !availability.professionals.some((p) => freeSlots(p).length);
  const nextOpenDay = closedDay
    ? availability?.professionals
        .flatMap((p) => p.days.map((d) => d.nextOpenDay))
        .filter((d): d is string => !!d)
        .sort()[0]
    : undefined;
  const rows = data?.rows.filter(
    (r) =>
      r.displayName.toLowerCase().includes(search.toLowerCase()) ||
      r.clinicianName.toLowerCase().includes(search.toLowerCase()),
  );
  const notConfigured =
    error instanceof Error &&
    ((error as Error & { code?: string }).code === 'hospital.not_configured' ||
      error.message.includes('hospital.not_configured'));
  const shift = (n: number) => {
    const d = new Date(day + 'T12:00:00');
    d.setDate(d.getDate() + n);
    setDay(d.toISOString().slice(0, 10));
  };
  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle className="flex flex-wrap items-center gap-2">
            <CalendarDays className="size-4" /> Agenda del hospital · {me.tenant.timeZone}
          </CardTitle>
          <CardDescription>Todos los doctores</CardDescription>
          <CardAction>
            <Button size="sm" disabled={!data || !!error} onClick={() => setCreate(true)}>
              <Plus />
              Nueva cita
            </Button>
          </CardAction>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outline" size="sm" onClick={() => setDay(today)}>
              Hoy
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Día anterior"
              onClick={() => shift(-1)}
            >
              <ChevronLeft />
            </Button>
            <Input
              className="w-auto"
              aria-label="Fecha de la agenda"
              type="date"
              value={day}
              onChange={(e) => setDay(e.target.value)}
            />
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Día siguiente"
              onClick={() => shift(1)}
            >
              <ChevronRight />
            </Button>
          </div>
          <ErrorBox error={notConfigured ? null : error} />
          {!data && !error ? (
            <Loading />
          ) : !rows?.length ? (
            <EmptyState
              icon={CalendarDays}
              title={error ? 'Revisa la conexión de tu hospital' : 'Un día por organizar'}
            >
              {notConfigured ? (
                <>
                  <span>Estás en «{me.tenant.name}», que aún no tiene una agenda conectada.</span>
                  <span className="mt-2 block">
                    Revisa la conexión o entra con tu cuenta del Hospital desde «Mi hospital».
                  </span>
                  <Button className="mt-4" variant="outline" asChild>
                    <a href="/?view=hospital">Revisar conexión con Hospital</a>
                  </Button>
                </>
              ) : error ? (
                'No pudimos consultar Hospital. Revisa la conexión y vuelve a intentar.'
              ) : (
                'No hay citas para esta fecha.'
              )}
            </EmptyState>
          ) : (
            <div className="flex flex-col gap-2">
              {rows.map((r) => (
                <Item variant="outline" key={r.appointmentId}>
                  <div className="flex w-16 shrink-0 flex-col">
                    <strong className="font-medium">
                      {time(r.scheduledStart, me.tenant.timeZone)}
                    </strong>
                    <span className="text-xs text-muted-foreground">{r.durationMinutes} min</span>
                  </div>
                  <ItemMedia>
                    <PersonAvatar name={r.displayName} />
                  </ItemMedia>
                  <ItemContent className="min-w-40">
                    <ItemTitle>
                      <h3>{r.displayName}</h3>
                    </ItemTitle>
                    <ItemDescription className="flex items-center gap-1">
                      <Stethoscope className="size-3.5 shrink-0" />
                      {doctorName(r.clinicianName)} · {r.placeName}
                    </ItemDescription>
                  </ItemContent>
                  <ItemActions className="flex-wrap">
                    <Badge variant="secondary">
                      {(
                        {
                          booked: 'Programada',
                          'cancelled-by-patient': 'Cancelada por paciente',
                          'cancelled-by-clinic': 'Cancelada por hospital',
                          arrived: 'Presente',
                          fulfilled: 'Atendida',
                          'no-show': 'No asistió',
                          'not-recorded': 'Sin registrar',
                          'entered-in-error': 'Registrada por error',
                        } as Record<string, string>
                      )[r.status] ?? r.status}
                    </Badge>
                    {r.overlaps && <Badge variant="destructive">Solapamiento</Badge>}
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={r.status !== 'booked' && r.status !== 'not-recorded'}
                      onClick={() => setModify(r)}
                    >
                      Reprogramar
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={r.status !== 'booked' && r.status !== 'not-recorded'}
                      onClick={() => {
                        setCancelReason('');
                        setCancel(r);
                      }}
                    >
                      Cancelar
                    </Button>
                  </ItemActions>
                </Item>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
      <Dialog
        open={create || !!modify}
        onOpenChange={(v) => {
          if (!v) {
            setCreate(false);
            setModify(null);
          }
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{modify ? 'Reprogramar cita' : 'Nueva cita'}</DialogTitle>
            <DialogDescription>Se registrará en la agenda del hospital.</DialogDescription>
          </DialogHeader>
          <form
            onSubmit={async (e) => {
              e.preventDefault();
              const f = new FormData(e.currentTarget);
              const slot = slots.find((s) => s.startsAt === f.get('startsAt'));
              if (!slot || appointmentBusy.current) return;
              appointmentBusy.current = true;
              setSavingAppointment(true);
              try {
                const result = await saveAppointment({
                  action: modify ? 'reschedule' : 'create',
                  appointmentId: modify?.appointmentId,
                  contactId: f.get('contactId'),
                  doctorId: doctor,
                  startsAt: slot.startsAt,
                  durationMinutes: slot.durationMinutes,
                });
                toast[result.overlaps ? 'warning' : 'success'](
                  result.overlaps
                    ? 'Cita registrada con solapamiento: verifica la agenda.'
                    : 'Agenda actualizada',
                );
                appointmentRequest.current = null;
                setCreate(false);
                setModify(null);
                mutate();
              } catch (err) {
                toast.error((err as Error).message);
              } finally {
                appointmentBusy.current = false;
                setSavingAppointment(false);
              }
            }}
          >
            <FieldGroup>
              <UiField>
                <FieldLabel htmlFor="appointment-contact">Paciente vinculado</FieldLabel>
                <NativeSelect
                  id="appointment-contact"
                  name="contactId"
                  required
                  defaultValue={contacts?.find((c) => c.patientId === modify?.patientId)?.id ?? ''}
                >
                  <NativeSelectOption value="">Selecciona un paciente</NativeSelectOption>
                  {contacts
                    ?.filter((c) => c.patientId && (!modify || c.patientId === modify.patientId))
                    .map((c) => (
                      <NativeSelectOption value={c.id} key={c.id}>
                        {c.name}
                      </NativeSelectOption>
                    ))}
                </NativeSelect>
                <FieldDescription>
                  Si un paciente no aparece, vincula primero su expediente desde la conversación.
                </FieldDescription>
              </UiField>
              <UiField>
                <FieldLabel htmlFor="appointment-date">Fecha</FieldLabel>
                <Input
                  id="appointment-date"
                  type="date"
                  value={day}
                  onChange={(e) => setDay(e.target.value)}
                  required
                />
              </UiField>
              <ErrorBox error={availabilityError} />
              {availabilityLoading && (
                <p className="flex items-center gap-2 text-sm text-muted-foreground">
                  <Spinner /> Consultando horarios del hospital…
                </p>
              )}
              {availability && !availability.professionals.length && (
                <p className="text-sm text-muted-foreground">
                  El hospital no tiene doctores disponibles en el padrón para esta consulta.
                </p>
              )}
              {closedDay && (
                <div
                  className="flex flex-col items-start gap-2 text-sm text-muted-foreground"
                  role="status"
                >
                  Ningún doctor tiene horario disponible en esta fecha. Elige otro día.{' '}
                  {nextOpenDay && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setDay(nextOpenDay)}
                    >
                      Ir al próximo día con horario
                    </Button>
                  )}
                </div>
              )}
              <UiField>
                <FieldLabel htmlFor="appointment-doctor">Doctor</FieldLabel>
                <NativeSelect
                  id="appointment-doctor"
                  value={doctor}
                  onChange={(e) => setDoctor(e.target.value)}
                  required
                >
                  <NativeSelectOption value="">Selecciona un doctor</NativeSelectOption>
                  {availability?.professionals.map((p) => (
                    <NativeSelectOption value={p.clinicianId} key={p.clinicianId}>
                      {doctorName(p.clinicianName)}
                      {freeSlots(p).length ? '' : ' · sin cupo este día'}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              </UiField>
              <UiField>
                <FieldLabel htmlFor="appointment-slot">Horario disponible</FieldLabel>
                <NativeSelect id="appointment-slot" name="startsAt" required>
                  <NativeSelectOption value="">Selecciona un horario</NativeSelectOption>
                  {slots.map((s) => (
                    <NativeSelectOption key={s.startsAt} value={s.startsAt}>
                      {time(s.startsAt, me.tenant.timeZone)} · {s.durationMinutes} min
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
                {doctor && availability && !slots.length && (
                  <FieldDescription>
                    No hay cupos disponibles para este doctor en la fecha seleccionada. Cambia el
                    día o el doctor.
                  </FieldDescription>
                )}
              </UiField>
              <DialogFooter>
                <Button type="submit" disabled={!slots.length || savingAppointment}>
                  Confirmar cita
                </Button>
              </DialogFooter>
            </FieldGroup>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog
        open={!!cancel}
        onOpenChange={(v) => {
          if (!v) setCancel(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cancelar cita</DialogTitle>
            <DialogDescription>
              Se cancelará la cita de {cancel?.displayName} en la agenda del hospital.
            </DialogDescription>
          </DialogHeader>
          {/* Hospital records who cancelled; staff say so instead of it being assumed. */}
          <UiField>
            <FieldLabel htmlFor="cancel-reason">Motivo de la cancelación</FieldLabel>
            <NativeSelect
              id="cancel-reason"
              aria-label="Motivo de la cancelación"
              value={cancelReason}
              onChange={(e) => setCancelReason(e.target.value)}
            >
              <NativeSelectOption value="">Selecciona un motivo</NativeSelectOption>
              <NativeSelectOption value="patient-requested">
                El paciente lo pidió
              </NativeSelectOption>
              <NativeSelectOption value="clinician-unavailable">
                El doctor no está disponible
              </NativeSelectOption>
              <NativeSelectOption value="clinic-closed">
                El hospital no atiende ese día
              </NativeSelectOption>
              <NativeSelectOption value="duplicate">La cita estaba duplicada</NativeSelectOption>
              <NativeSelectOption value="other">Otro motivo del hospital</NativeSelectOption>
            </NativeSelect>
          </UiField>
          {/* A destructive confirm offers its way out as a button, not only the X. */}
          <DialogFooter>
            <Button variant="outline" disabled={savingAppointment} onClick={() => setCancel(null)}>
              Conservar cita
            </Button>
            <Button
              variant="destructive"
              disabled={savingAppointment || !cancelReason}
              onClick={async () => {
                if (appointmentBusy.current) return;
                const c = contacts?.find((x) => x.patientId === cancel?.patientId);
                if (!c) {
                  toast.error('Vincula este paciente a un contacto antes de cancelar.');
                  return;
                }
                appointmentBusy.current = true;
                setSavingAppointment(true);
                try {
                  await saveAppointment({
                    action: 'cancel',
                    appointmentId: cancel?.appointmentId,
                    contactId: c.id,
                    doctorId: '00000000-0000-0000-0000-000000000000',
                    startsAt: cancel?.scheduledStart,
                    durationMinutes: cancel?.durationMinutes,
                    cancelReason,
                  });
                  appointmentRequest.current = null;
                  toast.success('Cita cancelada');
                  setCancel(null);
                  mutate();
                } catch (e) {
                  toast.error((e as Error).message);
                } finally {
                  appointmentBusy.current = false;
                  setSavingAppointment(false);
                }
              }}
            >
              Confirmar cancelación
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
function AgentView({ me }: { me: Me }) {
  const { data } = useSWR<Activity[]>('/activities', fetcher, { refreshInterval: 5000 });
  const admin = me.role === 'admin';
  const { data: contacts } = useSWR<Contact[]>('/contacts', fetcher);
  const [contactId, setContactId] = useState('');
  const [query, setQuery] = useState('');
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <>
      <Card>
        <CardHeader>
          <CardDescription>TU EQUIPO, CON MÁS CAPACIDAD</CardDescription>
          <CardTitle>
            <h2 className="flex items-center gap-2">
              <Sparkles className="size-4" /> Agente de atención
            </h2>
          </CardTitle>
          <CardDescription>
            Responde preguntas, acompaña la agenda y entrega la atención a la persona correcta.
          </CardDescription>
          <CardAction>
            <StatusBadge value={me.tenant.agentEnabled ? 'agent' : 'human'} />
          </CardAction>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Badge variant="outline">
            <ShieldCheck /> Contexto de tu hospital
          </Badge>
          <Badge variant="outline">
            <Users /> Transferencia a humanos
          </Badge>
          <Badge variant="outline">
            <ActivityIcon /> Historial de cada acción
          </Badge>
        </CardContent>
      </Card>
      <div className="grid items-start gap-6 lg:grid-cols-5">
        <Card className="min-w-0 lg:col-span-3">
          <CardHeader>
            <CardTitle>
              <h2 className="flex items-center gap-2">
                <Sparkles className="size-4" /> Asistente del equipo
              </h2>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <div className="flex flex-col gap-1">
              <h3 className="font-medium">¿En qué trabajamos hoy?</h3>
              <p className="text-sm text-muted-foreground">
                Consulta las métricas o selecciona un contacto para guardar una nota o crear un
                seguimiento.
              </p>
            </div>
            <UiField>
              <FieldLabel htmlFor="assistant-contact">Contacto para acciones</FieldLabel>
              <NativeSelect
                id="assistant-contact"
                aria-label="Contacto para acciones"
                value={contactId}
                onChange={(e) => setContactId(e.target.value)}
              >
                <NativeSelectOption value="">Solo consultar métricas</NativeSelectOption>
                {contacts?.map((c) => (
                  <NativeSelectOption value={c.id} key={c.id}>
                    {c.name}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
              <FieldDescription>
                La ficha del contacto seleccionado permanece en el CRM.
              </FieldDescription>
            </UiField>
            <div className="flex flex-col gap-2">
              {[
                'Resume las conversaciones pendientes',
                '¿Qué oportunidades necesitan seguimiento?',
                'Crea un seguimiento para el contacto seleccionado',
              ].map((s) => (
                <Item
                  asChild
                  variant="outline"
                  size="sm"
                  className="text-left hover:bg-accent/50"
                  key={s}
                >
                  <button onClick={() => setQuery(s)}>
                    <ItemContent>
                      <ItemTitle>{s}</ItemTitle>
                    </ItemContent>
                    <ItemActions>
                      <ArrowUpRight className="size-4" />
                    </ItemActions>
                  </button>
                </Item>
              ))}
            </div>
            {answer && (
              <Alert>
                <Sparkles />
                <AlertDescription className="break-words whitespace-pre-wrap">
                  {answer}
                </AlertDescription>
              </Alert>
            )}
            <form
              className="flex flex-col items-start gap-3"
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                try {
                  const r = await api<{ answer: string }>('/assistant', 'POST', {
                    message: query,
                    contactId: contactId || null,
                  });
                  setAnswer(r.answer);
                  setQuery('');
                } catch (e) {
                  toast.error((e as Error).message);
                } finally {
                  setBusy(false);
                }
              }}
            >
              <Textarea
                aria-label="Pregunta al asistente"
                rows={3}
                placeholder="Pregunta sobre la atención de tu hospital…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                maxLength={2000}
              />
              <Button disabled={busy || !query.trim()}>
                {busy ? <Spinner /> : <Send />}
                {busy ? 'Consultando…' : 'Consultar al agente'}
              </Button>
            </form>
          </CardContent>
        </Card>
        <Card className="min-w-0 lg:col-span-2">
          <CardHeader>
            <CardTitle>
              <h2>Acciones recientes</h2>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <ActivityTimeline
              items={data?.filter((a) => a.actor === 'Agente' || a.kind === 'handoff').slice(0, 10)}
              proposal="Propuesta de cita registrada"
            />
            {!data?.some((a) => a.actor === 'Agente' || a.kind === 'handoff') && (
              <EmptyState icon={Sparkles} title="Listo para acompañar">
                Las acciones aparecerán al habilitar la atención automática.
              </EmptyState>
            )}
          </CardContent>
        </Card>
      </div>
      {admin && <AppointmentReminders me={me} />}
      {admin && (
        <Alert>
          <Settings />
          <AlertDescription>
            Personaliza las instrucciones y habilita el agente desde Configuración.
          </AlertDescription>
        </Alert>
      )}
    </>
  );
}
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
function SettingsView({ me }: { me: Me }) {
  const { mutate: globalMutate } = useSWRConfig();
  const { data, error, mutate } = useSWR<SettingsData>('/settings', fetcher);
  const { data: channels, mutate: refreshChannels } = useSWR<Channel[]>('/channels', fetcher);
  const [saving, setSaving] = useState(false);
  if (me.role !== 'admin')
    return (
      <EmptyState icon={ShieldCheck} title="Solo administradores">
        Tu administrador gestiona las conexiones y la guía del negocio.
      </EmptyState>
    );
  return (
    <>
      <ErrorBox error={error} />
      {data ? (
        <div className="flex flex-col gap-6">
          <HospitalConnection />
          <div className="grid gap-6 lg:grid-cols-5">
            <Card className="min-w-0 lg:col-span-3">
              <CardHeader>
                <CardTitle>
                  <h2>Tu hospital y su guía de atención</h2>
                </CardTitle>
              </CardHeader>
              <CardContent>
                <form
                  key={data.name + data.timeZone + data.agentEnabled + data.emergencyWhatsApp}
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
                        agentEnabled: f.get('agentEnabled') === 'on',
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
                    <UiField orientation="horizontal">
                      <FieldContent>
                        <FieldLabel htmlFor="settings-agent-enabled">
                          Atención automática
                        </FieldLabel>
                        <FieldDescription>
                          Permite al agente atender conversaciones habilitadas.
                        </FieldDescription>
                      </FieldContent>
                      <Switch
                        id="settings-agent-enabled"
                        name="agentEnabled"
                        defaultChecked={data.agentEnabled}
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
                            <Button
                              variant="outline"
                              size="sm"
                              onClick={async () => {
                                try {
                                  await api('/channels/' + c.id, 'PATCH', { enabled: !c.enabled });
                                  refreshChannels();
                                  toast.success('Canal actualizado');
                                } catch (e) {
                                  toast.error((e as Error).message);
                                }
                              }}
                            >
                              {c.enabled ? <Pause /> : <Play />}
                              {c.enabled ? 'Pausar' : 'Habilitar'}
                            </Button>
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
