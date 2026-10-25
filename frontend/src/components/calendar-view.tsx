'use client';
import { useState, useRef } from 'react';
import useSWR from 'swr';
import { CalendarDays, Plus, ChevronLeft, ChevronRight, Stethoscope } from 'lucide-react';
import { toast } from 'sonner';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field as UiField, FieldDescription, FieldGroup, FieldLabel } from './ui/field';
import { Input } from './ui/input';
import { Item, ItemActions, ItemContent, ItemDescription, ItemMedia, ItemTitle } from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Spinner } from './ui/spinner';
import { api, fetcher, type Me, type Contact } from '@/lib/api';
import { dayInZone, zoneName } from '@/lib/format';
import { TomorrowReminders } from './appointment-reminders';
import { time, PersonAvatar, EmptyState, ErrorBox, Loading } from './workspace-shared';
import { ContactPicker } from './form-dialog';
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
export function CalendarView({ search, me }: { search: string; me: Me }) {
  const today = dayInZone(new Date(), me.tenant.timeZone);
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
  // First visit or follow-up by whether the patient came to an earlier appointment in Hospital (MR-43); the team may choose another kind.
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
            <CalendarDays className="size-4" /> Agenda del hospital · {zoneName(me.tenant.timeZone)}
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
                  Si no aparece, vincúlalo desde{' '}
                  <a className="underline underline-offset-4" href="/?view=contacts">
                    Contactos
                  </a>{' '}
                  → Vincular paciente.
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
                  No hay doctores con horario ese día. Elige otra fecha o pide al administrador que
                  cargue los horarios en Hospital.
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
                    Primera vez si el paciente no ha llegado a ninguna cita anterior en el hospital.
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
      {/* Today's work for the desk: who confirmed tomorrow and whom to call (A11: never a doctor). */}
      {(me.role === 'admin' || me.role === 'agent') && <TomorrowReminders me={me} />}
    </>
  );
}
