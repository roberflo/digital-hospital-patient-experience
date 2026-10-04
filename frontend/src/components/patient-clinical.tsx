'use client';
import { useEffect, useState } from 'react';
import { FileHeart, RefreshCw, ArrowLeft } from 'lucide-react';
import { api, type Chat, type Me } from '@/lib/api';
import { SESSION_EXPIRED } from '@/lib/session-client';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from './ui/dialog';

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
        <div role="alert">
          <p className="error">{error}</p>
          <Button variant="outline" onClick={() => setAttempt(attempt + 1)}>
            <RefreshCw />
            Reintentar consulta
          </Button>
        </div>
      ) : data ? (
        children(data)
      ) : (
        <p role="status">Consultando Hospital…</p>
      )}
    </>
  );
}

function PresenceView({ value }: { value: Presence }) {
  return (
    <>
      <p className="tag">{label(value.state)}</p>
      {value.note && <p>{value.note}</p>}
      {value.reason && <p>Motivo: {value.reason}</p>}
      {value.assertedAt && (
        <small>
          Declarado: {value.assertedAt.slice(0, 10)} · {value.assertedBy ?? 'Autor no informado'}
        </small>
      )}
      {value.items?.map((fact, index) => (
        <article className="clinical-fact" key={fact.id ?? fact.allergyId ?? index}>
          <strong>{fact.label ?? fact.substance}</strong>
          <p>
            {[
              fact.severity && label(fact.severity),
              fact.verification && label(fact.verification),
              fact.provenance,
            ]
              .filter(Boolean)
              .join(' · ')}
          </p>
          {fact.isActive === false && <p>Registro inactivo</p>}
          {fact.currentTreatment && <p>Tratamiento registrado: {fact.currentTreatment}</p>}
          {fact.note && <p>{fact.note}</p>}
          <small>
            {fact.assertedAt?.slice(0, 10)} · {fact.assertedBy ?? 'Autor no informado'}
          </small>
        </article>
      ))}
    </>
  );
}

function PrescriptionView({ value }: { value: Prescription }) {
  return (
    <>
      <p>
        <span className="tag">{label(value.state)}</span> · {value.clinicalDate}{' '}
        {value.documentSerial && `· ${value.documentSerial}`}
      </p>
      {value.state !== 'signed' && (
        <p className="error">
          Documento histórico {label(value.state).toLowerCase()}. No representa una indicación
          vigente.
        </p>
      )}
      {value.signedOverAllergyOverride && (
        <p className="error">Hospital registra una excepción por alergia al firmar esta receta.</p>
      )}
      {value.contentWithheld ? (
        <p role="status">Hospital ha restringido el contenido de esta receta.</p>
      ) : (
        value.lines.map((line) => (
          <article className="clinical-fact" key={line.medicationLineId}>
            <h4>{line.drugName}</h4>
            {line.strengthAmount != null && (
              <p>
                Concentración: {line.strengthAmount} {line.strengthUnit}
              </p>
            )}
            <p>
              Dosis registrada: {line.doseAmount} {label(line.doseUnit)} · Vía: {label(line.route)}
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
          </article>
        ))
      )}
    </>
  );
}

function NoteView({ value }: { value: Note }) {
  return (
    <>
      <p>
        <span className="tag">{label(value.note.state)}</span> ·{' '}
        {value.note.signer?.displayName ?? 'Firmante sin nombre registrado'} ·{' '}
        {value.note.signer?.signedAt.slice(0, 10)}
      </p>
      {Object.entries(value.note.sectionText).map(([id, text]) => {
        const spec = value.template.sections.find((s) => s.id === id);
        const title =
          spec?.labels.find((l) => l.language === 'es')?.text ??
          spec?.labels[0]?.text ??
          spec?.role ??
          'Sección clínica';
        return (
          <article className="clinical-fact" key={id}>
            <h4>{title}</h4>
            <p>{text}</p>
          </article>
        );
      })}
      {value.note.diagnoses.length > 0 && (
        <article className="clinical-fact">
          <h4>Diagnósticos registrados</h4>
          {value.note.diagnoses.map((d, i) => (
            <p key={i}>
              {d.code} · {d.displaySnapshot} {d.clarifier}
            </p>
          ))}
        </article>
      )}
    </>
  );
}

function ClinicalReader({ chat }: { chat: Chat }) {
  const [tab, setTab] = useState('timeline');
  const [cursors, setCursors] = useState<string[]>([]);
  const [onlyPrescriptions, setOnlyPrescriptions] = useState(false);
  const [selected, setSelected] = useState<Entry>();
  const base = `/hospital/conversations/${chat.conversation.id}/clinical`;
  const path = `${base}/${tab}${tab === 'timeline' && cursors.length ? '?cursor=' + encodeURIComponent(cursors.at(-1)!) : ''}`;
  return (
    <>
      <nav className="clinical-tabs" aria-label="Secciones del expediente">
        {[
          ['timeline', 'Historial y recetas'],
          ['antecedentes', 'Antecedentes'],
          ['allergies', 'Alergias'],
        ].map(([id, title]) => (
          <Button
            key={id}
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
        <>
          <Button variant="ghost" onClick={() => setSelected(undefined)}>
            <ArrowLeft />
            Volver al historial
          </Button>
          <h3>
            {label(selected.entryType ?? '')} ·{' '}
            {selected.signerDisplay ??
              selected.authorDisplay ??
              'Profesional sin nombre registrado'}
          </h3>
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
        </>
      ) : tab === 'timeline' ? (
        <ClinicalRead<Timeline> key={path} path={path}>
          {(data) => (
            <>
              <label className="clinical-filter">
                <input
                  type="checkbox"
                  checked={onlyPrescriptions}
                  onChange={(e) => setOnlyPrescriptions(e.target.checked)}
                />
                Solo recetas de esta página
              </label>
              <p className="hint">
                Historial de Hospital · 30 registros por página. Las recetas anteriores no implican
                tratamiento vigente.
              </p>
              {data.recordOrigin === 'migrated' && (
                <p>Expediente migrado: parte del historial puede no estar digitalizada.</p>
              )}
              {data.items.filter(
                (entry) => !onlyPrescriptions || entry.entryType === 'prescription',
              ).length === 0 && <p>No hay registros para esta vista en la página consultada.</p>}
              {data.items
                .filter((entry) => !onlyPrescriptions || entry.entryType === 'prescription')
                .map((entry) => (
                  <article className="clinical-fact" key={entry.entryId}>
                    <div className="clinical-entry-heading">
                      <strong>{label(entry.entryType ?? 'Registro')}</strong>
                      <span className="tag">{label(entry.state)}</span>
                    </div>
                    <p>
                      {entry.clinicalDate} ·{' '}
                      {entry.signerDisplay ??
                        entry.authorDisplay ??
                        'Profesional sin nombre registrado'}
                    </p>
                    {entry.summaryKey === 'entered-in-error' && <p>{label(entry.summaryKey)}</p>}
                    {entry.sourceRef &&
                    entry.state !== 'draft' &&
                    entry.summaryKey !== 'entered-in-error' &&
                    ['prescription', 'note'].includes(entry.entryType ?? '') ? (
                      <Button variant="outline" size="sm" onClick={() => setSelected(entry)}>
                        Ver {entry.entryType === 'prescription' ? 'receta' : 'nota clínica'}
                      </Button>
                    ) : (
                      <small>Detalle disponible en Hospital según tus permisos.</small>
                    )}
                  </article>
                ))}
              <div className="clinical-pagination">
                <Button
                  variant="outline"
                  disabled={!cursors.length}
                  onClick={() => setCursors(cursors.slice(0, -1))}
                >
                  Página anterior
                </Button>
                <Button
                  variant="outline"
                  disabled={!data.nextCursor}
                  onClick={() => setCursors([...cursors, data.nextCursor!])}
                >
                  Siguiente página
                </Button>
              </div>
            </>
          )}
        </ClinicalRead>
      ) : tab === 'antecedentes' ? (
        <ClinicalRead<Antecedentes> key={path} path={path}>
          {(data) => (
            <>
              {Object.entries(data.byCategory).map(([category, value]) => (
                <section key={category}>
                  <h3>{label(category)}</h3>
                  <PresenceView value={value} />
                </section>
              ))}
            </>
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
    <div className="clinical-access">
      <Button variant="outline" size="sm" disabled={!allowed} onClick={() => setOpen(true)}>
        <FileHeart />
        Expediente y recetas
      </Button>
      {!allowed && (
        <small>
          {!chat.contact.patientId
            ? 'Vincula al paciente con Hospital desde sus detalles.'
            : 'Toma la conversación o solicita que te la asignen para consultar el expediente.'}
        </small>
      )}
      <Dialog open={open && allowed} onOpenChange={setOpen}>
        <DialogContent className="clinical-reader">
          <DialogTitle>Expediente y recetas · {chat.contact.name}</DialogTitle>
          <DialogDescription>
            Consulta médica en Hospital. La información permanece en esta ventana y no se envía al
            chat ni al agente.
          </DialogDescription>
          {open && allowed && (
            <ClinicalReader key={`${chat.conversation.id}:${chat.contact.patientId}`} chat={chat} />
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
