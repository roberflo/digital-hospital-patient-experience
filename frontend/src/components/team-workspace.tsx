'use client';
import { useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { Users, UserPlus, ArrowUpRight, Copy, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Me, type Member, type Conversation } from '@/lib/api';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from './ui/dialog';

export type WorkloadMember = Member & { open: number; pending: number; snoozed: number };
export type Workload = { members: WorkloadMember[]; unassigned: number };
export const managesTeam = (me: Me) => ['admin', 'platform_admin', 'supervisor'].includes(me.role);
const roles: Record<string, string> = {
  admin: 'Administrador',
  platform_admin: 'Administrador de plataforma',
  supervisor: 'Supervisor',
  agent: 'Recepcionista',
  doctor: 'Doctor',
};
export const memberLabel = (m: WorkloadMember) =>
  `${m.name} · ${m.open + m.pending + m.snoozed} activas${m.disabled ? ' · acceso desactivado' : ''}`;

export function AssignmentControl({
  conversation,
  me,
  onChange,
}: {
  conversation: Conversation;
  me: Me;
  onChange: () => void;
}) {
  const { data, error, mutate } = useSWR<Workload>('/members/workload', fetcher, {
    refreshInterval: 10000,
  });
  const [busy, setBusy] = useState(false);
  const canEdit =
    managesTeam(me) || !conversation.assignedTo || conversation.assignedTo === me.subject;
  async function assign(subject: string) {
    setBusy(true);
    try {
      await api('/conversations/assign', 'POST', {
        conversations: [{ id: conversation.id, expectedRevision: conversation.revision }],
        assignedTo: subject || null,
      });
      toast.success('Responsable actualizado');
      await mutate();
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
      onChange();
    }
  }
  return (
    <div className="assignment-bar">
      <label>
        <Users size={15} /> Responsable
        <select
          aria-label="Asignar conversación"
          value={conversation.assignedTo ?? ''}
          disabled={busy || !canEdit || !data || !!error}
          onChange={(e) => assign(e.target.value)}
        >
          <option value="">Sin asignar</option>
          {conversation.assignedTo &&
            !data?.members.some((m) => m.subject === conversation.assignedTo) && (
              <option value={conversation.assignedTo}>Responsable actual</option>
            )}
          {data?.members
            .filter((m) => !m.disabled || m.subject === conversation.assignedTo)
            .map((m) => (
              <option key={m.subject} value={m.subject} disabled={m.disabled}>
                {memberLabel(m)}
              </option>
            ))}
        </select>
      </label>
      {!conversation.assignedTo && (
        <Button
          variant="outline"
          size="sm"
          disabled={busy || !data || !!error}
          onClick={() => assign(me.subject)}
        >
          Asignarme
        </Button>
      )}
      {!canEdit && <small>El responsable o un supervisor puede transferirla.</small>}
      {error && <small role="alert">No se pudo cargar el equipo.</small>}
      <a href="/?view=team">Ver equipo ↗</a>
    </div>
  );
}

export function TeamWorkspace({
  me,
  search,
  onOpenInbox,
}: {
  me: Me;
  search: string;
  onOpenInbox: (assignment: string) => void;
}) {
  const { data, error, mutate } = useSWR<Workload>('/members/workload', fetcher, {
    refreshInterval: 10000,
  });
  const { mutate: refresh } = useSWRConfig();
  const [onboarding, setOnboarding] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const admin = ['admin', 'platform_admin'].includes(me.role);
  async function toggle(m: WorkloadMember) {
    setBusy(m.subject);
    try {
      await api('/members/' + encodeURIComponent(m.subject), 'PATCH', { disabled: !m.disabled });
      await Promise.all([mutate(), refresh('/members')]);
      toast.success(m.disabled ? 'Acceso restaurado' : 'Acceso desactivado');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return (
    <>
      <section className="content-card team-workspace">
        <div className="card-toolbar">
          <div>
            <h2>Equipo de atención</h2>
            <p className="hint">
              Cada persona puede atender varias conversaciones de los números del hospital.
            </p>
          </div>
          <div className="team-toolbar-actions">
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw />
              Actualizar
            </Button>
            <Button size="sm" onClick={() => setOnboarding(true)}>
              <UserPlus />
              Incorporar compañero
            </Button>
          </div>
        </div>
        <div className="team-queue">
          <span>
            <strong>{data?.unassigned ?? '—'}</strong> conversaciones activas sin responsable
          </span>
          <Button variant="outline" size="sm" onClick={() => onOpenInbox('unassigned')}>
            Revisar pendientes <ArrowUpRight />
          </Button>
        </div>
        {error && (
          <p className="team-help" role="alert">
            {error.message}
          </p>
        )}
        {!data && !error && <p className="team-help">Cargando equipo…</p>}
        {data && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Persona</th>
                  <th>Rol del Hospital</th>
                  <th>Abiertas</th>
                  <th>Pendientes</th>
                  <th>Pospuestas</th>
                  <th>Atención</th>
                  {admin && <th>Acceso a Recepción</th>}
                </tr>
              </thead>
              <tbody>
                {data.members
                  .filter((m) =>
                    m.name.toLocaleLowerCase('es').includes(search.toLocaleLowerCase('es')),
                  )
                  .map((m) => (
                    <tr key={m.subject}>
                      <td>
                        <strong>{m.name}</strong>
                        {m.subject === me.subject && <span className="tag">Tú</span>}
                        {m.disabled && <span className="tag">Desactivado</span>}
                      </td>
                      <td>{roles[m.role] ?? m.role}</td>
                      <td data-label="Abiertas">{m.open}</td>
                      <td data-label="Pendientes">{m.pending}</td>
                      <td data-label="Pospuestas">{m.snoozed}</td>
                      <td>
                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() => onOpenInbox('member:' + m.subject)}
                        >
                          Ver conversaciones
                        </Button>
                      </td>
                      {admin && (
                        <td>
                          <Button
                            variant="outline"
                            size="sm"
                            disabled={busy !== null || m.subject === me.subject}
                            onClick={() => toggle(m)}
                          >
                            {m.disabled ? 'Restaurar acceso' : 'Desactivar acceso'}
                          </Button>
                        </td>
                      )}
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="team-help">
          Supervisores y administradores pueden repartir conversaciones individualmente o en lote.
          Cada recepcionista puede tomar las que no tienen responsable y transferir las suyas. Los
          cambios quedan en el historial del cliente.
        </p>
      </section>
      <Dialog open={onboarding} onOpenChange={setOnboarding}>
        <DialogContent>
          <DialogTitle>Incorporar a una persona del hospital</DialogTitle>
          <DialogDescription>
            Recepción usa la misma cuenta y el mismo hospital que el sistema Hospital.
          </DialogDescription>
          <ol className="team-onboarding">
            <li>
              El administrador configura la cuenta y el rol en la identidad compartida del Hospital.
            </li>
            <li>La persona inicia sesión en Recepción con esa cuenta.</li>
            <li>Aparece automáticamente en este equipo y ya puedes asignarle conversaciones.</li>
          </ol>
          <p className="hint">
            La creación y los cambios de rol aún no están disponibles desde Recepción. No necesitas
            crear otra contraseña aquí.
          </p>
          <Button
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(location.origin + '/login');
                toast.success('Enlace de acceso copiado');
              } catch {
                toast.error('No se pudo copiar el enlace');
              }
            }}
          >
            <Copy />
            Copiar enlace de acceso
          </Button>
        </DialogContent>
      </Dialog>
    </>
  );
}
