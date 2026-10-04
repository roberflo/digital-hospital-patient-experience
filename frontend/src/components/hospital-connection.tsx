'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { signIn } from 'next-auth/react';
import { HeartPulse, Search, CheckCircle2, RefreshCw, ArrowUpRight } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Contact, type Me } from '@/lib/api';
import { refreshHospitalIdentity } from '@/lib/hospital-identity';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogTitle, DialogDescription } from './ui/dialog';

type Connection = {
  hospital: Me['tenant'];
  configured: boolean;
  hospitalUrl?: string;
  sharedIdentity: boolean;
  hospitalLoginAvailable: boolean;
  name: string;
  role: string;
};
export function HospitalConnection() {
  const { data, error, mutate } = useSWR<Connection>('/hospital/connection', fetcher);
  // A real read verifies availability; configured credentials alone never mean connected.
  const {
    data: health,
    error: healthError,
    isValidating,
    mutate: check,
  } = useSWR<{ connected: boolean }>(
    data?.configured ? ['/hospital/connection/check', data.hospital.id] : null,
    ([path]: [string, string]) => api(path, 'POST', {}),
    {
      revalidateOnFocus: false,
      shouldRetryOnError: false,
      dedupingInterval: 30000,
      onSuccess: refreshHospitalIdentity,
    },
  );
  const connected = !!health?.connected && !healthError;
  return (
    <section className="content-card hospital-connection">
      <div className="card-toolbar">
        <h2>
          <HeartPulse size={20} /> Mi hospital
        </h2>
      </div>
      <div className="hospital-connection-body">
        {error && (
          <p role="alert">
            No pudimos cargar tu hospital.{' '}
            <Button variant="outline" onClick={() => mutate()}>
              Reintentar
            </Button>
          </p>
        )}
        {!data && !error && <p>Cargando tu hospital…</p>}
        {data && (
          <>
            <div className="hospital-overview">
              <div>
                <h3>{data.hospital.name}</h3>
                <p>
                  {data.sharedIdentity
                    ? `Has entrado como ${data.name}. Tu cuenta conserva los permisos del hospital.`
                    : 'Estás en un espacio de prueba. Para trabajar con tu equipo, usa tu cuenta del hospital.'}
                </p>
              </div>
              <span role="status" className={`hospital-status ${connected ? 'is-connected' : ''}`}>
                {connected ? <CheckCircle2 size={16} /> : <HeartPulse size={16} />}
                {connected
                  ? 'Hospital conectado'
                  : isValidating
                    ? 'Conectando con tu hospital…'
                    : data.configured
                      ? 'Conexión interrumpida'
                      : 'Pendiente de conexión'}
              </span>
            </div>
            {!data.sharedIdentity && data.hospitalLoginAvailable && (
              <div className="hospital-access-callout">
                <h4>Una cuenta para ambas aplicaciones</h4>
                <p>
                  Inicia sesión con el usuario que ya utilizas en Hospital. Reconoceremos tu
                  hospital automáticamente.
                </p>
                <Button onClick={() => signIn('keycloak', { callbackUrl: '/?view=hospital' })}>
                  Conectar con mi hospital <ArrowUpRight />
                </Button>
              </div>
            )}
            {data.sharedIdentity && !data.configured && (
              <p className="hospital-access-callout">
                Tu cuenta ya está vinculada. Falta habilitar el acceso de Recepción a este hospital;
                pide al administrador de la plataforma que lo complete. No necesitas introducir
                contraseñas de conexión ni otros datos técnicos.
              </p>
            )}
            {healthError && (
              <div role="alert" className="hospital-access-callout">
                <p>
                  No pudimos comunicarnos con Hospital. Tu cuenta y tus datos siguen vinculados.
                </p>
                <Button variant="outline" disabled={isValidating} onClick={() => check()}>
                  <RefreshCw /> Volver a intentar
                </Button>
              </div>
            )}
            <div className="hospital-actions">
              {connected && (
                <Button asChild>
                  <a href="/?view=calendar">
                    Abrir agenda <ArrowUpRight />
                  </a>
                </Button>
              )}
              {data.hospitalUrl && (
                <Button asChild variant="outline">
                  <a href={data.hospitalUrl} target="_blank" rel="noreferrer">
                    Abrir Hospital <ArrowUpRight size={15} />
                  </a>
                </Button>
              )}
            </div>
            <div className="hospital-next-steps">
              <a href="/?view=contacts">
                <strong>Vincular pacientes</strong>
                <span>Busca el expediente desde un contacto para consultar sus citas.</span>
                <span aria-hidden="true">→</span>
              </a>
              <a href="/?view=team">
                <strong>Trabajar con tu equipo</strong>
                <span>Tus compañeros entran con su cuenta del Hospital y aparecen aquí.</span>
                <span aria-hidden="true">→</span>
              </a>
            </div>
            {data.sharedIdentity && data.hospitalLoginAvailable && (
              <Button
                variant="ghost"
                size="sm"
                onClick={() =>
                  signIn('keycloak', { callbackUrl: '/?view=hospital' }, { prompt: 'login' })
                }
              >
                Usar otra cuenta del hospital
              </Button>
            )}
            <p className="hint">
              Los pacientes, las citas y las conversaciones permanecen en su hospital.
            </p>
          </>
        )}
      </div>
    </section>
  );
}

type PatientHit = { patientId: string; displayName: string; recordNumber?: string };
export function PatientLink({
  contact,
  open,
  onClose,
  onChange,
}: {
  contact: Contact;
  open: boolean;
  onClose: () => void;
  onChange: () => void;
}) {
  const [shape, setShape] = useState('name-tokens');
  const [term, setTerm] = useState('');
  const [results, setResults] = useState<PatientHit[] | null>(null);
  const [selected, setSelected] = useState<PatientHit | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const { data: connection } = useSWR<Connection>(open ? '/hospital/connection' : null, fetcher);
  const { data: me } = useSWR<Me>(open ? '/me' : null, fetcher);
  const allowed = me && ['admin', 'agent'].includes(me.role);
  function reset() {
    setResults(null);
    setSelected(null);
    setError('');
  }
  return (
    <Dialog
      open={open}
      onOpenChange={(value) => {
        if (!value && !busy) {
          reset();
          setTerm('');
          onClose();
        }
      }}
    >
      <DialogContent>
        <DialogTitle>{contact.patientId ? 'Paciente vinculado' : 'Vincular paciente'}</DialogTitle>
        <DialogDescription>
          {contact.name} · Hospital: {connection?.hospital.name ?? 'Cargando…'}. Se comprobará que
          el teléfono del expediente coincide con +{contact.phone}.
        </DialogDescription>
        {contact.patientId ? (
          <p>
            Este contacto ya está vinculado a su expediente. Sus citas y documentos autorizados se
            consultan desde la conversación.
          </p>
        ) : !allowed ? (
          <p>
            Un recepcionista o administrador debe vincular el paciente antes de consultar su
            información.
          </p>
        ) : connection && !connection.configured ? (
          <p>
            Primero <a href="/?view=hospital">conecta el hospital de tu cuenta</a>.
          </p>
        ) : (
          <>
            <form
              className="dialog-form"
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                reset();
                try {
                  const page = await api<{ results: PatientHit[] }>(
                    `/hospital/contacts/${contact.id}/patients/search`,
                    'POST',
                    { queryShape: shape, term },
                  );
                  setResults(page.results);
                } catch {
                  setError('No se pudo buscar en Hospital. Revisa la conexión e intenta de nuevo.');
                } finally {
                  setBusy(false);
                }
              }}
            >
              <label>
                Buscar por
                <select
                  value={shape}
                  disabled={busy}
                  onChange={(e) => {
                    setShape(e.target.value);
                    reset();
                  }}
                >
                  <option value="name-tokens">Nombre y apellido</option>
                  <option value="dui">DUI</option>
                  <option value="record-number">Número de expediente</option>
                </select>
              </label>
              <label>
                Datos del paciente
                <input
                  value={term}
                  onChange={(e) => {
                    setTerm(e.target.value);
                    reset();
                  }}
                  required
                  minLength={3}
                  maxLength={100}
                  disabled={busy}
                  autoComplete="off"
                  placeholder={
                    shape === 'name-tokens'
                      ? 'Nombre y apellido completos'
                      : 'Identificador registrado en Hospital'
                  }
                />
              </label>
              <Button disabled={busy || !connection?.configured}>
                <Search />
                {busy ? 'Consultando…' : 'Buscar paciente'}
              </Button>
            </form>
            {results && (
              <div className="patient-link-results" aria-label="Pacientes encontrados">
                {results.length === 0 ? (
                  <p>
                    No se encontraron pacientes. Revisa los datos o registra al paciente desde
                    Hospital.
                  </p>
                ) : (
                  <>
                    <p className="hint">
                      Selecciona el expediente correcto. La coincidencia de nombre no vincula
                      automáticamente al paciente.
                    </p>
                    {results.map((p) => (
                      <label key={p.patientId} className="patient-link-result">
                        <input
                          type="radio"
                          name="patient-match"
                          checked={selected?.patientId === p.patientId}
                          disabled={busy}
                          onChange={() => setSelected(p)}
                        />
                        <span>
                          <strong>{p.displayName}</strong>
                          <small>Expediente: {p.recordNumber ?? 'Sin número visible'}</small>
                        </span>
                      </label>
                    ))}
                    {results.length >= 30 && (
                      <p>Mostrando hasta 30 resultados. Afina la búsqueda si no ves al paciente.</p>
                    )}
                  </>
                )}
              </div>
            )}
            {selected && (
              <Button
                disabled={busy}
                onClick={async () => {
                  setBusy(true);
                  setError('');
                  try {
                    await api(`/contacts/${contact.id}/patient`, 'POST', {
                      patientId: selected.patientId,
                    });
                    toast.success('Paciente vinculado al hospital y al CRM');
                    onChange();
                    reset();
                    setTerm('');
                    onClose();
                  } catch (e) {
                    setError(
                      (e as Error).message.includes('patient_phone_mismatch')
                        ? 'El teléfono del expediente no coincide con el contacto. Corrige los datos en Hospital antes de vincular.'
                        : (e as Error).message,
                    );
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                Confirmar vínculo con {selected.displayName}
              </Button>
            )}
          </>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {connection?.hospitalUrl && (
          <a
            href={connection.hospitalUrl}
            target="_blank"
            rel="noreferrer"
            className="mt-4 inline-block text-sm font-semibold text-primary"
          >
            Abrir Hospital →
          </a>
        )}
      </DialogContent>
    </Dialog>
  );
}
