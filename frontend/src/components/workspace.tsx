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
  BookUser,
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
  ListFilter,
} from 'lucide-react';
import { toast } from 'sonner';
import { Alert, AlertDescription } from './ui/alert';
import { Avatar, AvatarFallback } from './ui/avatar';
import { Badge } from './ui/badge';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from './ui/collapsible';
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
import { inZone } from '@/lib/format';
import { defaultInboxFilters, filtersActive, inboxQuery } from '@/lib/inbox-filters';
import { ConfirmAction } from './confirm-action';
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
  ['dashboard', 'Inicio', LayoutDashboard],
  ['inbox', 'Bandeja de entrada', Inbox],
  ['team', 'Equipo', Users],
  ['contacts', 'Contactos', BookUser],
  ['companies', 'Empresas', Building2],
  ['opportunities', 'Oportunidades', Columns3],
  ['calendar', 'Agenda', CalendarDays],
  ['hospital', 'Mi hospital', HeartPulse],
  ['activity', 'Actividad', ActivityIcon],
  ['agent', 'Agente de atención', Sparkles],
] as const;
const views = new Set<string>(['settings', ...nav.map(([id]) => id)]);
/** The view a `/?view=` URL names, or null when it is not one of ours. */
function viewOf(href: string) {
  const url = new URL(href, location.href);
  const v = url.searchParams.get('view');
  return url.origin === location.origin &&
    url.pathname === '/' &&
    [...url.searchParams.keys()].join() === 'view' &&
    v &&
    views.has(v)
    ? v
    : null;
}
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
const time = (value: string, tz: string) =>
  inZone(value, tz, { hour: '2-digit', minute: '2-digit' });
const date = (value: string, tz: string) => inZone(value, tz, { day: 'numeric', month: 'short' });
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
                {f.type === 'contact' ? (
                  <ContactPicker
                    id={'field-' + f.name}
                    name={f.name}
                    label={f.label}
                    required={f.required}
                  />
                ) : f.options ? (
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
/** Finds a contact on the server, not in the first page of 100 (MR-17). With `patientId` it
 * resolves the one contact linked to that Hospital patient. */
function ContactPicker({
  id,
  name = 'contactId',
  label,
  patientId,
  linkedOnly = false,
  empty = 'Selecciona un contacto',
  required = false,
  onChange,
}: {
  id: string;
  name?: string;
  label: string;
  patientId?: string;
  linkedOnly?: boolean;
  empty?: string;
  required?: boolean;
  onChange?: (contactId: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [term, setTerm] = useState('');
  useEffect(() => {
    const wait = setTimeout(() => setTerm(query.trim()), 250);
    return () => clearTimeout(wait);
  }, [query]);
  const [picked, setPicked] = useState<Contact>();
  const { data, error } = useSWR<Contact[]>(
    patientId
      ? '/contacts?patientId=' + encodeURIComponent(patientId)
      : '/contacts?q=' + encodeURIComponent(term) + (linkedOnly ? '&linked=true' : ''),
    fetcher,
  );
  const found = data ?? [];
  // The chosen contact stays choosable while the search moves on.
  const options = picked && !found.some((c) => c.id === picked.id) ? [picked, ...found] : found;
  const value = picked?.id ?? (patientId ? (found[0]?.id ?? '') : '');
  return (
    <div className="flex flex-col gap-2">
      {!patientId && (
        <InputGroup>
          <InputGroupInput
            type="search"
            aria-label={'Buscar ' + label.toLocaleLowerCase('es')}
            placeholder="Busca por nombre o teléfono…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            maxLength={100}
          />
          <InputGroupAddon>
            <Search />
          </InputGroupAddon>
        </InputGroup>
      )}
      <NativeSelect
        id={id}
        name={name}
        className="w-full"
        required={required}
        value={value}
        onChange={(e) => {
          setPicked(options.find((c) => c.id === e.target.value));
          onChange?.(e.target.value);
        }}
      >
        <NativeSelectOption value="">{data ? empty : 'Buscando…'}</NativeSelectOption>
        {options.map((c) => (
          <NativeSelectOption value={c.id} key={c.id}>
            {c.name}
          </NativeSelectOption>
        ))}
      </NativeSelect>
      <ErrorBox error={error} />
      {data?.length === 100 && (
        <FieldDescription>
          Hay más resultados: escribe más del nombre o del teléfono.
        </FieldDescription>
      )}
      {data && !found.length && (
        <FieldDescription>
          {patientId
            ? 'Este paciente no tiene un contacto vinculado en Recepción.'
            : term
              ? `Nadie coincide con «${term}».`
              : linkedOnly
                ? 'Aún no hay pacientes vinculados.'
                : 'Aún no hay contactos.'}
        </FieldDescription>
      )}
    </div>
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
              <a
                href="/"
                aria-label="Recepción inicio"
                onClick={(e) => {
                  // A new tab or window keeps the browser's own behaviour, as the global interceptor does.
                  if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                  e.preventDefault();
                  go('dashboard');
                }}
              >
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
              aria-current={view === 'hospital' ? 'page' : undefined}
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
                    aria-current={view === id ? 'page' : undefined}
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
              <SidebarMenuButton
                isActive={view === 'settings'}
                aria-current={view === 'settings' ? 'page' : undefined}
                onClick={() => go('settings')}
              >
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
  // Each view is a history entry: «Atrás» returns to the previous one (MR-24).
  const navigate = (v: string) => {
    setView(v);
    setSearch('');
    if (new URLSearchParams(location.search).get('view') !== v)
      history.pushState(null, '', '/?view=' + v);
  };
  useEffect(() => {
    const fromUrl = () => {
      const v = new URLSearchParams(location.search).get('view');
      setView(v && views.has(v) ? v : 'dashboard');
      setSearch('');
    };
    fromUrl();
    // An internal `/?view=` link changes the view without reloading the page.
    const onClick = (e: MouseEvent) => {
      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey)
        return;
      const link = (e.target as Element | null)?.closest?.('a[href]');
      if (!(link instanceof HTMLAnchorElement) || link.target) return;
      const v = viewOf(link.href);
      if (!v) return;
      e.preventDefault();
      navigate(v);
    };
    window.addEventListener('popstate', fromUrl);
    document.addEventListener('click', onClick);
    return () => {
      window.removeEventListener('popstate', fromUrl);
      document.removeEventListener('click', onClick);
    };
  }, []);
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
                  <InboxView
                    me={me}
                    search={search}
                    onSearch={setSearch}
                    initialAssignment={teamAssignment}
                  />
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
function ActivityTimeline({
  items,
  proposal,
  zone,
}: {
  items?: Activity[];
  proposal: string;
  zone: string;
}) {
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
            {date(a.createdAt, zone)} · {time(a.createdAt, zone)}
          </small>
        </div>
      ))}
    </div>
  );
}
function InboxView({
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
  const [filter, setFilter] = useState('all');
  const [moreFilters, setMoreFilters] = useState(false);
  const [page, setPage] = useState(1);
  const { data: channels } = useSWR<Channel[]>('/channels', fetcher);
  useEffect(() => setPage(1), [state, assignment, channelId, priority, label, filter]);
  useEffect(
    () => setChecked({}),
    [state, assignment, channelId, priority, label, filter, page, search],
  );
  const filters = { state, assignment, channelId, priority, label, mode: filter };
  const query = inboxQuery(filters, page);
  if (linkedConversation) query.set('conversationId', linkedConversation);
  if (linkedPhone) query.set('phone', linkedPhone);
  if (linkedNumber) query.set('phoneNumberId', linkedNumber);
  const {
    data: chats,
    error,
    mutate,
  } = useSWR<Chat[]>('/conversations?' + query, fetcher, { refreshInterval: 5000 });
  const [selected, setSelected] = useState<string | null>(null);
  const filtered = chats?.filter(
    (c) =>
      c.contact.name.toLowerCase().includes(search.toLowerCase()) ||
      c.contact.phone.includes(search),
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
            </div>
            <Collapsible
              open={moreFilters}
              onOpenChange={setMoreFilters}
              className="flex flex-col gap-2"
            >
              <div className="flex items-center gap-2">
                <ButtonGroup className="min-w-0 flex-1 *:flex-1">
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
                    variant={moreFilters ? 'secondary' : 'outline'}
                    size="sm"
                    aria-label="Más filtros"
                  >
                    <ListFilter />
                    {!!hiddenFilters && <Badge variant="secondary">{hiddenFilters}</Badge>}
                  </Button>
                </CollapsibleTrigger>
              </div>
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
                <Input
                  className="col-span-2"
                  aria-label="Filtrar por etiqueta"
                  placeholder="Etiqueta de conversación"
                  value={label}
                  onChange={(e) => setLabel(e.target.value)}
                  maxLength={40}
                />
              </CollapsibleContent>
            </Collapsible>
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
          <div className="flex flex-col md:min-h-0 md:flex-1 md:overflow-y-auto">
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
              filtered.map(({ conversation: c, contact, channel, unreadCount }) => (
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
                      onClick={() => setSelected(c.id)}
                    >
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
                            {c.priority !== 'normal' && (
                              <Badge
                                variant={c.priority === 'urgent' ? 'destructive' : 'secondary'}
                              >
                                {priorities.find(([v]) => v === c.priority)?.[1]}
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
                        <div className="flex flex-wrap items-center gap-1.5">
                          <Badge variant="outline">
                            {conversationStates.find(([v]) => v === c.state)?.[1]}
                          </Badge>
                          <StatusBadge value={c.status} />
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
          {!!search.trim() && chats?.length === 100 && (
            <p className="border-t px-3 pt-2 text-xs text-muted-foreground" role="status">
              La búsqueda por nombre solo revisa las 100 conversaciones de esta página.
            </p>
          )}
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
  // Follow new messages only while the person is at the end; reading further up stays put (MR-26).
  const following = useRef(true);
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
          zone={me.tenant.timeZone}
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
              <ConfirmAction
                ask={c.status !== 'agent'}
                title="¿Activar el agente en esta conversación?"
                description="El agente responderá al paciente por WhatsApp. Tu equipo puede retomarla en cualquier momento."
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
                  disabled={!managesTeam(me) && !!c.assignedTo && c.assignedTo !== me.subject}
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
        <div
          className="message-list flex min-h-48 flex-1 flex-col gap-3 overflow-y-auto p-4"
          onScroll={(e) => {
            const el = e.currentTarget;
            following.current = el.scrollHeight - el.scrollTop - el.clientHeight <= 80;
          }}
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
            disabled={!canReply}
          />
          <div className="flex items-center gap-2">
            <Button
              asChild
              variant="ghost"
              size="icon-sm"
              className={cn((busy || !canReply) && 'pointer-events-none opacity-50')}
            >
              <label title="Adjuntar archivo" aria-disabled={busy || !canReply}>
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
/** Names for the cards, by id: the contact may sit past the first page of 100 (MR-17). */
async function contactNames(ids: string[]) {
  const pages = [];
  for (let i = 0; i < ids.length; i += 100)
    pages.push(
      api<Contact[]>(
        '/contacts?' +
          ids
            .slice(i, i + 100)
            .map((id) => 'id=' + id)
            .join('&'),
      ),
    );
  return new Map((await Promise.all(pages)).flat().map((c) => [c.id, c.name]));
}
function OpportunitiesView({ search }: { search: string }) {
  const { data, error, mutate } = useSWR<Opportunity[]>('/opportunities', fetcher);
  const ids = [...new Set(data?.map((o) => o.contactId))].sort();
  const { data: names } = useSWR(ids.length ? 'contact-names:' + ids.join(',') : null, () =>
    contactNames(ids),
  );
  // The stage the new follow-up starts in; null while the dialog is closed.
  const [create, setCreate] = useState<string | null>(null);
  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span className="text-sm text-muted-foreground">
          Seguimientos de la atención y oportunidades del hospital
        </span>
        <Button size="sm" onClick={() => setCreate('new')}>
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
                      {names?.get(o.contactId) ?? 'Contacto'}
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
              {rows && !rows.length && (
                <p className="px-1 text-sm text-muted-foreground">Sin seguimientos</p>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="justify-start"
                aria-label={'Agregar seguimiento en ' + label}
                onClick={() => setCreate(id)}
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
        open={!!create}
        onClose={() => setCreate(null)}
        fields={[
          { name: 'title', label: 'Título', required: true },
          { name: 'contactId', label: 'Contacto', type: 'contact', required: true },
          { name: 'value', label: 'Valor estimado (USD)', type: 'number', value: '0' },
          {
            name: 'stage',
            label: 'Etapa',
            required: true,
            value: create ?? 'new',
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
/** The visit kinds Hospital publishes for a new appointment (MR-42). */
const VISIT_KINDS = [
  ['first-visit', 'Primera vez'],
  ['follow-up', 'Subsecuente'],
  ['procedure', 'Procedimiento'],
  ['results', 'Entrega de resultados'],
  ['paperwork', 'Trámite'],
  ['same-day-urgent', 'Urgencia del día'],
] as const;
/** A doctor whose printed name Hospital has not captured yet still has to be choosable. */
const doctorName = (name: string) => name.trim() || 'Profesional sin nombre';
const freeSlots = (p: Availability['professionals'][number]) =>
  p.days.flatMap((d) => d.slots).filter((s) => s.offered && s.takenBy === 0);
function CalendarView({ search, me }: { search: string; me: Me }) {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: me.tenant.timeZone }).format(
    new Date(),
  );
  const [day, setDay] = useState(today);
  // The dialog picks its own day; the agenda behind it stays on the one being viewed (MR-27).
  const [dialogDay, setDialogDay] = useState(today);
  const [create, setCreate] = useState(false);
  const [modify, setModify] = useState<AgendaRow | null>(null);
  const [doctor, setDoctor] = useState('');
  const [patient, setPatient] = useState('');
  const [slotStart, setSlotStart] = useState('');
  // What the team picked; null until they touch the select, so the suggestion only preselects.
  const [visitKind, setVisitKind] = useState<string | null>(null);
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
    create || modify ? `/hospital/availability?date=${dialogDay}` : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  // First visit or follow-up by whether Hospital has an earlier appointment of the patient; the team may choose another kind.
  const { data: suggested } = useSWR<{ visitKind: string }>(
    create && patient
      ? `/hospital/contacts/${patient}/visit-kind` +
          (slotStart ? `?before=${encodeURIComponent(slotStart)}` : '')
      : null,
    fetcher,
    { shouldRetryOnError: false },
  );
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
            <Button
              size="sm"
              disabled={!data || !!error}
              onClick={() => {
                setDialogDay(day);
                setCreate(true);
              }}
            >
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
                      onClick={() => {
                        setDialogDay(day);
                        setModify(r);
                      }}
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
            setPatient('');
            setSlotStart('');
            setVisitKind(null);
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
                  visitKind: modify ? undefined : f.get('visitKind') || undefined,
                });
                toast[result.overlaps ? 'warning' : 'success'](
                  result.overlaps
                    ? 'Cita registrada con solapamiento: verifica la agenda.'
                    : 'Agenda actualizada',
                );
                appointmentRequest.current = null;
                setCreate(false);
                setModify(null);
                setPatient('');
                setSlotStart('');
                setVisitKind(null);
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
                <ContactPicker
                  id="appointment-contact"
                  label="Paciente vinculado"
                  patientId={modify?.patientId}
                  linkedOnly
                  empty="Selecciona un paciente"
                  required
                  onChange={setPatient}
                />
                <FieldDescription>
                  Si un paciente no aparece, vincula primero su expediente desde la conversación.
                </FieldDescription>
              </UiField>
              <UiField>
                <FieldLabel htmlFor="appointment-date">Fecha</FieldLabel>
                <Input
                  id="appointment-date"
                  type="date"
                  value={dialogDay}
                  onChange={(e) => setDialogDay(e.target.value)}
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
                      onClick={() => setDialogDay(nextOpenDay)}
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
                <NativeSelect
                  id="appointment-slot"
                  name="startsAt"
                  required
                  onChange={(e) => setSlotStart(e.target.value)}
                >
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
              {!modify && (
                <UiField>
                  <FieldLabel htmlFor="appointment-visit-kind">Tipo de visita</FieldLabel>
                  <NativeSelect
                    id="appointment-visit-kind"
                    name="visitKind"
                    value={visitKind ?? suggested?.visitKind ?? ''}
                    onChange={(e) => setVisitKind(e.target.value)}
                  >
                    <NativeSelectOption value="">
                      Según su historial en el hospital
                    </NativeSelectOption>
                    {VISIT_KINDS.map(([value, label]) => (
                      <NativeSelectOption key={value} value={value}>
                        {label}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                  <FieldDescription>
                    Primera vez si el paciente no tiene citas anteriores en el hospital.
                  </FieldDescription>
                </UiField>
              )}
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
                if (appointmentBusy.current || !cancel) return;
                appointmentBusy.current = true;
                setSavingAppointment(true);
                try {
                  // The server finds the contact linked to this patient, wherever it is listed.
                  const [c] = await api<Contact[]>(
                    '/contacts?patientId=' + encodeURIComponent(cancel.patientId),
                  );
                  if (!c) {
                    toast.error('Vincula este paciente a un contacto antes de cancelar.');
                    return;
                  }
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
  const { data, error } = useSWR<Activity[]>('/activities', fetcher, { refreshInterval: 5000 });
  const admin = me.role === 'admin';
  const agentActions = data?.filter((a) => a.actorRole === 'agent_ai');
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
              <ContactPicker
                id="assistant-contact"
                label="Contacto para acciones"
                empty="Solo consultar métricas"
                onChange={setContactId}
              />
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
            <ErrorBox error={error} />
            <ActivityTimeline
              items={agentActions?.slice(0, 10)}
              proposal="Propuesta de cita registrada"
              zone={me.tenant.timeZone}
            />
            {data && !agentActions?.length && (
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
