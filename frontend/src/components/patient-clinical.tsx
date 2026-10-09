'use client';
import { useEffect, useId, useState } from 'react';
import { AlertCircle, AlertTriangle, FileHeart, RefreshCw, ArrowLeft } from 'lucide-react';
import { api, type Chat, type Me } from '@/lib/api';
import { SESSION_EXPIRED } from '@/lib/session-client';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Checkbox } from './ui/checkbox';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field, FieldLabel } from './ui/field';
import { Item, ItemActions, ItemContent, ItemGroup, ItemTitle } from './ui/item';
import { Spinner } from './ui/spinner';

type Entry = {
  entryId: string;
  entryType: string | null;
  clinicalDate: string;
  authorDisplay?: string;
  signerDisplay?: string;
  state: string;
  summaryKey?: string;
  sourceRef?: { kind: string; id: string };
};
type Timeline = { items: Entry[]; nextCursor?: string; recordOrigin: string };
type Fact = {
  id?: string;
  allergyId?: string;
  label?: string;
  substance?: string;
  severity?: string;
  verification?: string;
  provenance?: string;
  note?: string;
  currentTreatment?: string;
  assertedBy?: string;
  assertedAt?: string;
  isActive?: boolean;
};
type Presence = {
  state: string;
  note?: string;
  reason?: string;
  items?: Fact[];
  assertedBy?: string;
  assertedAt?: string;
};
type Antecedentes = { byCategory: Record<string, Presence> };
type Prescription = {
  state: string;
  clinicalDate: string;
  documentSerial?: string;
  contentWithheld: boolean;
  signedOverAllergyOverride: boolean;
  lines: {
    medicationLineId: string;
    drugName: string;
    strengthAmount?: number;
    strengthUnit?: string;
    doseAmount: number;
    doseUnit: string;
    route: string;
    frequencyIntervalHours?: number;
    frequencyIsSingleDose: boolean;
    administrationCondition?: string;
    durationDays?: number;
    durationIsIndefinite: boolean;
    indication?: string;
    specialInstructions?: string;
    quantityToDispense?: number;
  }[];
};
type Note = {
  note: {
    state: string;
    sectionText: Record<string, string>;
    signer?: { displayName?: string; signedAt: string };
    diagnoses: { code: string; displaySnapshot: string; clarifier?: string }[];
  };
  template: {
    sections: { id: string; role?: string; labels: { language: string; text: string }[] }[];
  };
};
const words: Record<string, string> = {
  signed: 'Firmada',
  draft: 'Borrador',
  cancelled: 'Cancelada',
  voided: 'Anulada',
  note: 'Nota clínica',
  prescription: 'Receta',
  order: 'Orden',
  attachment: 'Archivo',
  'observation-set': 'Signos vitales',
  'observation-correction': 'Corrección de signos vitales',
  'entered-in-error': 'Registrado por error · contenido restringido',
  NotAsked: 'No se ha preguntado',
  AssertedAbsent: 'Ausencia declarada',
  Asserted: 'Registrado',
  Unobtainable: 'No se pudo obtener',
  medico: 'Antecedentes médicos',
  quirurgico: 'Antecedentes quirúrgicos',
  alergico: 'Antecedentes alérgicos',
  familiar: 'Antecedentes familiares',
  Confirmed: 'Confirmado',
  Unconfirmed: 'Sin confirmar',
  Refuted: 'Refutado',
  Mild: 'Leve',
  Moderate: 'Moderada',
  Severe: 'Grave',
  Unknown: 'Desconocida',
  oral: 'oral',
  topical: 'tópica',
  intravenous: 'intravenosa',
  intramuscular: 'intramuscular',
};
const label = (value: string) => words[value] ?? value;

// No shared cache or browser storage for clinical content. Closing the reader discards its state.
function ClinicalRead<T>({
  path,
  children,
}: {
  path: string;
  children: (value: T) => React.ReactNode;
}) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let alive = true;
    setData(undefined);
    setError('');
    api<T>(path)
      .then((value) => {
        if (alive) setData(value);
      })
      .catch((err) => {
        if (!alive) return;
        const message = err.message as string;
        setError(
          message.includes('doctor_login_required')
            ? 'Inicia sesión con tu cuenta médica de Hospital para consultar el expediente.'
            : err.status === 403
              ? 'No tienes acceso clínico. Comprueba tu cuenta médica y que sigues a cargo de esta conversación.'
              : err.status === 404
                ? 'No se pudo verificar el paciente o el documento. Comprueba el vínculo y el teléfono en Hospital.'
                : message.includes('not_configured')
                  ? 'Este negocio todavía no tiene configurada la conexión con Hospital.'
                  : message,
        );
      });
    return () => {
      alive = false;
    };
  }, [path, attempt]);
  return (
    <>
      {error ? (
        <div className="flex flex-col items-start gap-3">
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>{error}</AlertDescription>
          </Alert>
          <Button variant="outline" onClick={() => setAttempt(attempt + 1)}>
            <RefreshCw />
            Reintentar consulta
          </Button>
        </div>
      ) : data ? (
        children(data)
      ) : (
        <p role="status" className="flex items-center gap-2 text-sm text-muted-foreground">
          <Spinner role={undefined} aria-label={undefined} aria-hidden />
          Consultando Hospital…
        </p>
      )}
    </>
  );
}

function PresenceView({ value }: { value: Presence }) {
  return (
    <div className="flex flex-col items-start gap-2">
      <Badge variant="secondary">{label(value.state)}</Badge>
      {value.note && <p className="text-sm break-words whitespace-pre-wrap">{value.note}</p>}
      {value.reason && (
        <p className="text-sm break-words whitespace-pre-wrap">Motivo: {value.reason}</p>
      )}
      {value.assertedAt && (
        <p className="text-xs text-muted-foreground">
          Declarado: {value.assertedAt.slice(0, 10)} · {value.assertedBy ?? 'Autor no informado'}
        </p>
      )}
      {!!value.items?.length && (
        <ItemGroup className="w-full gap-2">
          {value.items.map((fact, index) => (
            <Item variant="outline" size="sm" key={fact.id ?? fact.allergyId ?? index}>
              <ItemContent className="min-w-0">
                <ItemTitle>{fact.label ?? fact.substance}</ItemTitle>
                <p className="text-sm break-words whitespace-pre-wrap">
                  {[
                    fact.severity && label(fact.severity),
                    fact.verification && label(fact.verification),
                    fact.provenance,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </p>
                {fact.isActive === false && <p className="text-sm">Registro inactivo</p>}
                {fact.currentTreatment && (
                  <p className="text-sm break-words whitespace-pre-wrap">
                    Tratamiento registrado: {fact.currentTreatment}
                  </p>
                )}
                {fact.note && (
                  <p className="text-sm break-words whitespace-pre-wrap">{fact.note}</p>
                )}
                <p className="text-xs text-muted-foreground">
                  {fact.assertedAt?.slice(0, 10)} · {fact.assertedBy ?? 'Autor no informado'}
                </p>
              </ItemContent>
            </Item>
          ))}
        </ItemGroup>
      )}
    </div>
  );
}

function PrescriptionView({ value }: { value: Prescription }) {
  return (
    <div className="flex flex-col gap-3">
      <p className="flex flex-wrap items-center gap-2 text-sm">
        <Badge variant="secondary">{label(value.state)}</Badge> · {value.clinicalDate}{' '}
        {value.documentSerial && `· ${value.documentSerial}`}
      </p>
      {value.state !== 'signed' && (
        <Alert variant="destructive">
          <AlertTriangle />
          <AlertDescription>
            Documento histórico {label(value.state).toLowerCase()}. No representa una indicación
            vigente.
          </AlertDescription>
        </Alert>
      )}
      {value.signedOverAllergyOverride && (
        <Alert variant="destructive">
          <AlertTriangle />
          <AlertDescription>
            Hospital registra una excepción por alergia al firmar esta receta.
          </AlertDescription>
        </Alert>
      )}
      {value.contentWithheld ? (
        <Alert role="status">
          <AlertCircle />
          <AlertDescription>Hospital ha restringido el contenido de esta receta.</AlertDescription>
        </Alert>
      ) : (
        <ItemGroup className="gap-2">
          {value.lines.map((line) => (
            <Item variant="outline" size="sm" key={line.medicationLineId}>
              <ItemContent className="min-w-0 text-sm break-words whitespace-pre-wrap">
                <h4 className="font-medium">{line.drugName}</h4>
                {line.strengthAmount != null && (
                  <p>
                    Concentración: {line.strengthAmount} {line.strengthUnit}
                  </p>
                )}
                <p>
                  Dosis registrada: {line.doseAmount} {label(line.doseUnit)} · Vía:{' '}
                  {label(line.route)}
                </p>
                <p>
                  Frecuencia:{' '}
                  {line.frequencyIsSingleDose
                    ? 'Dosis única'
                    : line.frequencyIntervalHours != null
                      ? `Cada ${line.frequencyIntervalHours} horas`
                      : 'No informada'}
                </p>
                <p>
                  Duración:{' '}
                  {line.durationIsIndefinite
                    ? 'Indefinida según receta'
                    : line.durationDays != null
                      ? `${line.durationDays} días`
                      : 'No informada'}
                </p>
                {line.administrationCondition && (
                  <p>Condición: {label(line.administrationCondition)}</p>
                )}
                {line.indication && <p>Indicación: {line.indication}</p>}
                {line.specialInstructions && <p>Instrucciones: {line.specialInstructions}</p>}
                {line.quantityToDispense != null && (
                  <p>Cantidad a dispensar: {line.quantityToDispense}</p>
                )}
              </ItemContent>
            </Item>
          ))}
        </ItemGroup>
      )}
    </div>
  );
}

function NoteView({ value }: { value: Note }) {
  return (
    <div className="flex flex-col gap-3">
      <p className="flex flex-wrap items-center gap-2 text-sm">
        <Badge variant="secondary">{label(value.note.state)}</Badge> ·{' '}
        {value.note.signer?.displayName ?? 'Firmante sin nombre registrado'} ·{' '}
        {value.note.signer?.signedAt.slice(0, 10)}
      </p>
      <ItemGroup className="gap-2">
        {Object.entries(value.note.sectionText).map(([id, text]) => {
          const spec = value.template.sections.find((s) => s.id === id);
          const title =
            spec?.labels.find((l) => l.language === 'es')?.text ??
            spec?.labels[0]?.text ??
            spec?.role ??
            'Sección clínica';
          return (
            <Item variant="outline" size="sm" key={id}>
              <ItemContent className="min-w-0 text-sm break-words whitespace-pre-wrap">
                <h4 className="font-medium">{title}</h4>
                <p>{text}</p>
              </ItemContent>
            </Item>
          );
        })}
        {value.note.diagnoses.length > 0 && (
          <Item variant="outline" size="sm">
            <ItemContent className="min-w-0 text-sm break-words whitespace-pre-wrap">
              <h4 className="font-medium">Diagnósticos registrados</h4>
              {value.note.diagnoses.map((d, i) => (
                <p key={i}>
                  {d.code} · {d.displaySnapshot} {d.clarifier}
                </p>
              ))}
            </ItemContent>
          </Item>
        )}
      </ItemGroup>
    </div>
  );
}

function ClinicalReader({ chat }: { chat: Chat }) {
  const [tab, setTab] = useState('timeline');
  const [cursors, setCursors] = useState<string[]>([]);
  const [onlyPrescriptions, setOnlyPrescriptions] = useState(false);
  const [selected, setSelected] = useState<Entry>();
  const filterId = useId();
  const base = `/hospital/conversations/${chat.conversation.id}/clinical`;
  const path = `${base}/${tab}${tab === 'timeline' && cursors.length ? '?cursor=' + encodeURIComponent(cursors.at(-1)!) : ''}`;
  return (
    <>
      <nav className="flex flex-wrap gap-2" aria-label="Secciones del expediente">
        {[
          ['timeline', 'Historial y recetas'],
          ['antecedentes', 'Antecedentes'],
          ['allergies', 'Alergias'],
        ].map(([id, title]) => (
          <Button
            key={id}
            size="sm"
            variant={id === tab ? 'default' : 'outline'}
            aria-pressed={id === tab}
            onClick={() => {
              setTab(id);
              setSelected(undefined);
            }}
          >
            {title}
          </Button>
        ))}
      </nav>
      {selected ? (
        <div className="flex flex-col items-start gap-3">
          <Button variant="ghost" size="sm" onClick={() => setSelected(undefined)}>
            <ArrowLeft />
            Volver al historial
          </Button>
          <h3 className="font-semibold">
            {label(selected.entryType ?? '')} ·{' '}
            {selected.signerDisplay ??
              selected.authorDisplay ??
              'Profesional sin nombre registrado'}
          </h3>
          <div className="w-full">
            {selected.entryType === 'prescription' ? (
              <ClinicalRead<Prescription>
                key={selected.entryId}
                path={`${base}/prescriptions/${selected.sourceRef!.id}`}
              >
                {(value) => <PrescriptionView value={value} />}
              </ClinicalRead>
            ) : (
              <ClinicalRead<Note>
                key={selected.entryId}
                path={`${base}/notes/${selected.sourceRef!.id}`}
              >
                {(value) => <NoteView value={value} />}
              </ClinicalRead>
            )}
          </div>
        </div>
      ) : tab === 'timeline' ? (
        <ClinicalRead<Timeline> key={path} path={path}>
          {(data) => (
            <div className="flex flex-col gap-3">
              <Field orientation="horizontal">
                <Checkbox
                  id={filterId}
                  checked={onlyPrescriptions}
                  onCheckedChange={(checked) => setOnlyPrescriptions(checked === true)}
                />
                <FieldLabel htmlFor={filterId}>Solo recetas de esta página</FieldLabel>
              </Field>
              <p className="text-xs text-muted-foreground">
                Historial de Hospital · 30 registros por página. Las recetas anteriores no implican
                tratamiento vigente.
              </p>
              {data.recordOrigin === 'migrated' && (
                <p className="text-sm">
                  Expediente migrado: parte del historial puede no estar digitalizada.
                </p>
              )}
              {data.items.filter(
                (entry) => !onlyPrescriptions || entry.entryType === 'prescription',
              ).length === 0 && (
                <p className="text-sm text-muted-foreground">
                  No hay registros para esta vista en la página consultada.
                </p>
              )}
              <ItemGroup className="gap-2">
                {data.items
                  .filter((entry) => !onlyPrescriptions || entry.entryType === 'prescription')
                  .map((entry) => {
                    const readable =
                      entry.sourceRef &&
                      entry.state !== 'draft' &&
                      entry.summaryKey !== 'entered-in-error' &&
                      ['prescription', 'note'].includes(entry.entryType ?? '');
                    return (
                      <Item variant="outline" size="sm" key={entry.entryId}>
                        <ItemContent className="min-w-0">
                          <ItemTitle>
                            {label(entry.entryType ?? 'Registro')}
                            <Badge variant="secondary">{label(entry.state)}</Badge>
                          </ItemTitle>
                          <p className="text-sm text-muted-foreground">
                            {entry.clinicalDate} ·{' '}
                            {entry.signerDisplay ??
                              entry.authorDisplay ??
                              'Profesional sin nombre registrado'}
                          </p>
                          {entry.summaryKey === 'entered-in-error' && (
                            <p className="text-sm">{label(entry.summaryKey)}</p>
                          )}
                          {!readable && (
                            <p className="text-xs text-muted-foreground">
                              Detalle disponible en Hospital según tus permisos.
                            </p>
                          )}
                        </ItemContent>
                        {readable && (
                          <ItemActions>
                            <Button variant="outline" size="sm" onClick={() => setSelected(entry)}>
                              Ver {entry.entryType === 'prescription' ? 'receta' : 'nota clínica'}
                            </Button>
                          </ItemActions>
                        )}
                      </Item>
                    );
                  })}
              </ItemGroup>
              <div className="flex flex-wrap gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!cursors.length}
                  onClick={() => setCursors(cursors.slice(0, -1))}
                >
                  Página anterior
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!data.nextCursor}
                  onClick={() => setCursors([...cursors, data.nextCursor!])}
                >
                  Siguiente página
                </Button>
              </div>
            </div>
          )}
        </ClinicalRead>
      ) : tab === 'antecedentes' ? (
        <ClinicalRead<Antecedentes> key={path} path={path}>
          {(data) => (
            <div className="flex flex-col gap-4">
              {Object.entries(data.byCategory).map(([category, value]) => (
                <section className="flex flex-col gap-2" key={category}>
                  <h3 className="font-semibold">{label(category)}</h3>
                  <PresenceView value={value} />
                </section>
              ))}
            </div>
          )}
        </ClinicalRead>
      ) : (
        <ClinicalRead<Presence> key={path} path={path}>
          {(value) => <PresenceView value={value} />}
        </ClinicalRead>
      )}
    </>
  );
}

export function PatientClinical({ chat, me }: { chat: Chat; me: Me }) {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    const clear = () => setOpen(false);
    window.addEventListener(SESSION_EXPIRED, clear);
    return () => window.removeEventListener(SESSION_EXPIRED, clear);
  }, []);
  if (me.role !== 'doctor') return null;
  const assigned =
    chat.conversation.assignedTo === me.subject && chat.conversation.status !== 'agent';
  const allowed = assigned && !!chat.contact.patientId;
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-2">
      <Button variant="outline" size="sm" disabled={!allowed} onClick={() => setOpen(true)}>
        <FileHeart />
        Expediente y recetas
      </Button>
      {!allowed && (
        <span className="max-w-72 text-xs text-muted-foreground">
          {!chat.contact.patientId
            ? 'Vincula al paciente con Hospital desde sus detalles.'
            : 'Toma la conversación o solicita que te la asignen para consultar el expediente.'}
        </span>
      )}
      <Dialog open={open && allowed} onOpenChange={setOpen}>
        <DialogContent showCloseButton={false} className="max-h-dvh overflow-y-auto sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>Expediente y recetas · {chat.contact.name}</DialogTitle>
            <DialogDescription>
              Consulta médica en Hospital. La información permanece en esta ventana y no se envía al
              chat ni al agente.
            </DialogDescription>
          </DialogHeader>
          {open && allowed && (
            <ClinicalReader key={`${chat.conversation.id}:${chat.contact.patientId}`} chat={chat} />
          )}
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="outline">Cerrar</Button>
            </DialogClose>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
