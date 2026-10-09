'use client';
import { useId, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { AlertCircle, Users, UserPlus, ArrowUpRight, Copy, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Me, type Member, type Conversation } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from './ui/card';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field } from './ui/field';
import { Input } from './ui/input';
import { Item, ItemActions, ItemContent, ItemTitle } from './ui/item';
import { Label } from './ui/label';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Spinner } from './ui/spinner';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';

export type WorkloadMember = Member & { open: number; pending: number; snoozed: number };
export type Workload = { members: WorkloadMember[]; unassigned: number };
export const managesTeam = (me: Me) => me.role === 'admin';
const roles: Record<string, string> = {
  admin: 'Administrador',
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
  const id = useId();
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
    <div className="assignment-bar flex flex-wrap items-center gap-x-2 gap-y-1">
      <Label htmlFor={id} className="shrink-0">
        <Users className="size-4 text-muted-foreground" /> Responsable
      </Label>
      <Field className="max-w-72 min-w-0 flex-1 basis-40">
        <NativeSelect
          id={id}
          size="sm"
          aria-label="Asignar conversación"
          value={conversation.assignedTo ?? ''}
          disabled={busy || !canEdit || !data || !!error}
          onChange={(e) => assign(e.target.value)}
        >
          <NativeSelectOption value="">Sin asignar</NativeSelectOption>
          {conversation.assignedTo &&
            !data?.members.some((m) => m.subject === conversation.assignedTo) && (
              <NativeSelectOption value={conversation.assignedTo}>
                Responsable actual
              </NativeSelectOption>
            )}
          {data?.members
            .filter((m) => !m.disabled || m.subject === conversation.assignedTo)
            .map((m) => (
              <NativeSelectOption key={m.subject} value={m.subject} disabled={m.disabled}>
                {memberLabel(m)}
              </NativeSelectOption>
            ))}
        </NativeSelect>
      </Field>
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
      {!canEdit && (
        <span className="text-xs text-muted-foreground">
          El responsable o un administrador puede transferirla.
        </span>
      )}
      {error && (
        <span role="alert" className="text-xs text-destructive">
          No se pudo cargar el equipo.
        </span>
      )}
      <Button asChild variant="link" size="sm" className="ml-auto">
        <a href="/?view=team">Ver equipo ↗</a>
      </Button>
    </div>
  );
}

// Teammates join by signing in with their Hospital account: the only thing to hand over is this link.
export function CopyAccessLink(
  props: Pick<React.ComponentProps<typeof Button>, 'size' | 'variant'>,
) {
  return (
    <Button
      {...props}
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
  const admin = me.role === 'admin';
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
  // The number the agent offers a patient who asks for a person. Empty takes the doctor off that list.
  async function saveWhatsApp(m: WorkloadMember, phone: string) {
    if (phone.trim() === (m.whatsAppPhone ?? '')) return;
    setBusy(m.subject);
    try {
      await api('/members/' + encodeURIComponent(m.subject) + '/whatsapp', 'PUT', {
        phone: phone.trim(),
      });
      await mutate();
      toast.success(phone.trim() ? 'WhatsApp del doctor guardado' : 'WhatsApp del doctor quitado');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return (
    <>
      <Card>
        <CardHeader className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex min-w-0 flex-col gap-2">
            <CardTitle>
              <h2>Equipo de atención</h2>
            </CardTitle>
            <CardDescription>
              Cada persona puede atender varias conversaciones de los números del hospital.
            </CardDescription>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw />
              Actualizar
            </Button>
            <Button size="sm" onClick={() => setOnboarding(true)}>
              <UserPlus />
              Incorporar compañero
            </Button>
          </div>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <Item variant="muted">
            <ItemContent>
              <ItemTitle>
                <strong>{data?.unassigned ?? '—'}</strong> conversaciones activas sin responsable
              </ItemTitle>
            </ItemContent>
            <ItemActions>
              <Button variant="outline" size="sm" onClick={() => onOpenInbox('unassigned')}>
                Revisar pendientes <ArrowUpRight />
              </Button>
            </ItemActions>
          </Item>
          {error && (
            <Alert variant="destructive">
              <AlertCircle />
              <AlertDescription>{error.message}</AlertDescription>
            </Alert>
          )}
          {!data && !error && (
            <p className="flex items-center gap-2 text-sm text-muted-foreground">
              <Spinner role={undefined} aria-label={undefined} aria-hidden />
              Cargando equipo…
            </p>
          )}
          {data && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Persona</TableHead>
                  <TableHead>Rol del Hospital</TableHead>
                  <TableHead>Abiertas</TableHead>
                  <TableHead>Pendientes</TableHead>
                  <TableHead>Pospuestas</TableHead>
                  <TableHead>Atención</TableHead>
                  {admin && <TableHead>WhatsApp para pacientes</TableHead>}
                  {admin && <TableHead>Acceso a Recepción</TableHead>}
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.members
                  .filter((m) =>
                    m.name.toLocaleLowerCase('es').includes(search.toLocaleLowerCase('es')),
                  )
                  .map((m) => (
                    <TableRow key={m.subject}>
                      <TableCell>
                        <div className="flex items-center gap-2">
                          <span className="font-medium">{m.name}</span>
                          {m.subject === me.subject && <Badge variant="secondary">Tú</Badge>}
                          {m.disabled && <Badge variant="outline">Desactivado</Badge>}
                        </div>
                      </TableCell>
                      <TableCell>{roles[m.role] ?? m.role}</TableCell>
                      <TableCell>{m.open}</TableCell>
                      <TableCell>{m.pending}</TableCell>
                      <TableCell>{m.snoozed}</TableCell>
                      <TableCell>
                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() => onOpenInbox('member:' + m.subject)}
                        >
                          Ver conversaciones
                        </Button>
                      </TableCell>
                      {admin && (
                        <TableCell>
                          {m.role === 'doctor' ? (
                            <form
                              key={m.whatsAppPhone ?? ''}
                              className="flex items-center gap-2"
                              onSubmit={(e) => {
                                e.preventDefault();
                                saveWhatsApp(
                                  m,
                                  String(new FormData(e.currentTarget).get('phone') ?? ''),
                                );
                              }}
                            >
                              <Input
                                name="phone"
                                type="tel"
                                className="w-40"
                                aria-label={`WhatsApp de ${m.name} para pacientes`}
                                defaultValue={m.whatsAppPhone ?? ''}
                                placeholder="+503 7000 0000"
                                maxLength={20}
                              />
                              <Button variant="outline" size="sm" disabled={busy !== null}>
                                Guardar
                              </Button>
                            </form>
                          ) : (
                            '—'
                          )}
                        </TableCell>
                      )}
                      {admin && (
                        <TableCell>
                          <Button
                            variant="outline"
                            size="sm"
                            disabled={busy !== null || m.subject === me.subject}
                            onClick={() => toggle(m)}
                          >
                            {m.disabled ? 'Restaurar acceso' : 'Desactivar acceso'}
                          </Button>
                        </TableCell>
                      )}
                    </TableRow>
                  ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
        <CardFooter className="flex-col items-start gap-2 text-sm text-muted-foreground">
          <p>
            Los administradores pueden repartir conversaciones individualmente o en lote. Cada
            recepcionista puede tomar las que no tienen responsable y transferir las suyas. Los
            cambios quedan en el historial del cliente.
          </p>
          {admin && (
            <p>
              Si un doctor tiene WhatsApp para pacientes, el agente ofrece un botón para escribirle
              cuando un paciente pide hablar con una persona o con el doctor. Déjalo vacío para no
              ofrecerlo.
            </p>
          )}
        </CardFooter>
      </Card>
      <Dialog open={onboarding} onOpenChange={setOnboarding}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Incorporar a una persona del hospital</DialogTitle>
            <DialogDescription>
              Recepción usa la misma cuenta y el mismo hospital que el sistema Hospital.
            </DialogDescription>
          </DialogHeader>
          <ol className="flex list-decimal flex-col gap-2 pl-5 text-sm">
            <li>
              El administrador configura la cuenta y el rol en la identidad compartida del Hospital.
            </li>
            <li>La persona inicia sesión en Recepción con esa cuenta.</li>
            <li>Aparece automáticamente en este equipo y ya puedes asignarle conversaciones.</li>
          </ol>
          <p className="text-sm text-muted-foreground">
            La creación y los cambios de rol aún no están disponibles desde Recepción. No necesitas
            crear otra contraseña aquí.
          </p>
          <DialogFooter>
            <CopyAccessLink />
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
