'use client';
import { endSession } from '@/lib/session-client';
import {
  HeartPulse,
  Building2,
  Settings,
  ChevronRight,
  Sparkles,
  LogOut,
  MessageCircle,
  Pause,
} from 'lucide-react';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuAction,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarSeparator,
  useSidebar,
} from './ui/sidebar';
import { type Me } from '@/lib/api';
import { nav, PersonAvatar } from './workspace-shared';
import { roles } from './team-workspace';
export const descriptions: Record<string, string> = {
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
export function AppSidebar({
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
                  {id === 'inbox' && !!waiting && (
                    // MR-61: the figure says what it counts.
                    <SidebarMenuBadge title={`${waiting} esperando a tu equipo`}>
                      {waiting}
                      <span className="sr-only"> esperando a tu equipo</span>
                    </SidebarMenuBadge>
                  )}
                  {id === 'agent' && <SidebarMenuBadge>IA</SidebarMenuBadge>}
                </SidebarMenuItem>
              ))}
              {me?.role === 'admin' && (
                <SidebarMenuItem>
                  <SidebarMenuButton asChild>
                    <a href="/whatsapp">
                      <MessageCircle />
                      <span>WhatsApp · números</span>
                    </a>
                  </SidebarMenuButton>
                </SidebarMenuItem>
              )}
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
                    : me?.role === 'admin'
                      ? 'Actívalo cuando esté configurado'
                      : 'Lo activa el administrador'}
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
                  <span className="truncate text-xs">{roles[me?.role ?? ''] ?? ''}</span>
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
