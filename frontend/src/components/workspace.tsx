'use client';
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import { Search, ShieldCheck } from 'lucide-react';
import { Badge } from './ui/badge';
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from './ui/breadcrumb';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import { Separator } from './ui/separator';
import { SidebarInset, SidebarProvider, SidebarTrigger } from './ui/sidebar';
import { fetcher, type Me } from '@/lib/api';
import { cn } from '@/lib/utils';
import { inZone } from '@/lib/format';
import { HospitalCompanies } from './commercial-workspace';
import { ActivityWorkspace } from './activity-workspace';
import { HospitalConnection } from './hospital-connection';
import { DashboardView } from './dashboard-view';
import { FirstSteps } from './first-steps';
import { ThemeToggle } from './theme-toggle';
import { TeamWorkspace } from './team-workspace';
import { nav, views, viewOf, ErrorBox, Loading } from './workspace-shared';
import { descriptions, AppSidebar } from './app-sidebar';
import { InboxView } from './inbox-view';
import { ContactsView } from './contacts-view';
import { OpportunitiesView } from './opportunities-view';
import { CalendarView } from './calendar-view';
import { AgentView } from './agent-view';
import { SettingsView } from './settings-view';
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
        inZone(new Date(), me.tenant.timeZone, {
          weekday: 'short',
          day: 'numeric',
          month: 'long',
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
          {/* MR-56: on a phone an open chat takes the page; globals.css hides this heading then. */}
          <div data-page-heading className="flex flex-wrap items-center justify-between gap-3">
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
