'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { signIn } from 'next-auth/react';
import { HeartPulse, Search, CheckCircle2, RefreshCw, ArrowUpRight } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Contact, type Me } from '@/lib/api';
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
  const [checking, setChecking] = useState(false);
  const [checked, setChecked] = useState(false);
  const [failure, setFailure] = useState('');
  return (
    <section className="content-card hospital-connection">
      <div className="card-toolbar">
        <h2>
          <HeartPulse size={20} /> Tu hospital conectado
        </h2>
      </div>
      <div className="hospital-connection-body">
        {error && (
          <p role="alert">
            No se pudo consultar la conexión.{' '}
            <Button variant="outline" onClick={() => mutate()}>
              Reintentar
            </Button>
          </p>
        )}
        {!data && !error && <p>Cargando conexión…</p>}
        {data && (
          <>
            <h3>{data.hospital.name}</h3>
            <p>
              Este es el hospital de tu sesión. Las citas, los pacientes y el equipo pertenecen a
              este espacio.
            </p>
            {['admin', 'platform_admin'].includes(data.role) && (
              <HospitalSetup
                onConnected={() => {
                  setChecked(true);
                  setFailure('');
                  mutate();
                }}
              />
            )}
            <ol className="hospital-steps">
              <li>
                <strong>1. Tu cuenta del hospital</strong>
                <p>
                  {data.sharedIdentity
                    ? `Sesión compartida activa · ${data.name}`
                    : 'Estás usando una cuenta de demostración. Entra con tu cuenta del Hospital para usar tus permisos y tu equipo.'}
                </p>
                {data.hospitalLoginAvailable && (
                  <Button
                    variant="outline"
                    onClick={() =>
                      signIn('keycloak', { callbackUrl: '/?view=hospital' }, { prompt: 'login' })
                    }
                  >
                    {data.sharedIdentity
                      ? 'Entrar con otra cuenta del hospital'
                      : 'Continuar con mi cuenta del hospital'}
                  </Button>
                )}
              </li>
              <li>
                <strong>2. Agenda y servicios</strong>
                <p>
                  {data.configured
                    ? checked
                      ? 'Conexión comprobada con la agenda de este hospital.'
                      : 'Conexión configurada. Comprueba que Hospital responde antes de trabajar.'
                    : 'La integración de este hospital todavía no está configurada. Entra con la cuenta del hospital conectado o solicita la conexión al administrador de la plataforma.'}
                </p>
                <div className="hospital-actions">
                  <Button
                    disabled={!data.configured || checking}
                    onClick={async () => {
                      setChecking(true);
                      setFailure('');
                      setChecked(false);
                      try {
                        await api('/hospital/connection/check', 'POST', {});
                        setChecked(true);
                      } catch {
                        setFailure(
                          'No se pudo comprobar la conexión. Tu administrador debe revisar el acceso de Recepción a este hospital.',
                        );
                      } finally {
                        setChecking(false);
                      }
                    }}
                  >
                    {checked ? <CheckCircle2 /> : <RefreshCw />}{' '}
                    {checking ? 'Comprobando…' : 'Comprobar conexión'}
                  </Button>
                  {data.hospitalUrl && (
                    <a href={data.hospitalUrl} target="_blank" rel="noreferrer">
                      Abrir Hospital <ArrowUpRight size={15} />
                    </a>
                  )}
                </div>
                {failure && (
                  <p role="alert" className="error">
                    {failure}
                  </p>
                )}
              </li>
              <li>
                <strong>3. Pacientes y equipo</strong>
                <p>
                  En Contactos o en una conversación, usa «Vincular paciente» para buscar su
                  expediente. Cada compañero aparece en Equipo al entrar con su cuenta del Hospital.
                </p>
                <div className="hospital-actions">
                  <a href="/?view=contacts">Ir a Contactos →</a>
                  <a href="/?view=team">Ver equipo →</a>
                </div>
              </li>
            </ol>
            <p className="hint">
              El hospital se determina por tu cuenta. Cambiar de cuenta no mueve contactos ni
              conversaciones entre hospitales.
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
  const allowed = me && ['admin', 'platform_admin', 'supervisor', 'agent'].includes(me.role);
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
          <a href={connection.hospitalUrl} target="_blank" rel="noreferrer">
            Abrir Hospital →
          </a>
        )}
      </DialogContent>
    </Dialog>
  );
}

function HospitalSetup({ onConnected }: { onConnected: () => void }) {
  const { data } = useSWR<{ allowedOrigins: string[]; publicUrl: string }>(
    '/hospital/connection/setup',
    fetcher,
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  return (
    <details className="hospital-setup">
      <summary>Conectar o actualizar Hospital</summary>
      <p>
        Usa la cuenta de servicio de Recepción creada en el Hospital. El hospital se obtiene de tu
        sesión; no necesitas copiar su identificador.
      </p>
      {!data ? (
        <p>Cargando opciones de conexión…</p>
      ) : data.allowedOrigins.length === 0 ? (
        <p>
          En Easypanel, añade la dirección de la API de Hospital a{' '}
          <code>HOSPITAL_ALLOWED_API_ORIGINS</code> y reinicia la API de Recepción. Tu administrador
          configura esto una sola vez.
        </p>
      ) : (
        <form
          className="dialog-form"
          onSubmit={async (e) => {
            e.preventDefault();
            const form = e.currentTarget;
            const values = new FormData(form);
            setBusy(true);
            setError('');
            try {
              await api('/hospital/connection', 'PUT', Object.fromEntries(values));
              form.reset();
              onConnected();
              toast.success('Hospital conectado y verificado');
            } catch (e) {
              const message = (e as Error).message;
              setError(
                message.includes('hospital.')
                  ? 'Hospital no aceptó esta conexión. Revisa las credenciales, los permisos de Recepción y que la cuenta de servicio pertenezca al mismo hospital que tu usuario.'
                  : message,
              );
            } finally {
              setBusy(false);
            }
          }}
        >
          <label>
            API de Hospital
            <select name="baseUrl" required disabled={busy}>
              {data.allowedOrigins.map((url) => (
                <option key={url} value={url}>
                  {url}
                </option>
              ))}
            </select>
          </label>
          <label>
            Dirección de la aplicación Hospital
            <input
              name="publicUrl"
              type="url"
              placeholder="https://hospital.tudominio.com"
              defaultValue={data.publicUrl}
              required
              disabled={busy}
            />
          </label>
          <label>
            Identificador de conexión
            <input
              name="clientId"
              required
              maxLength={150}
              placeholder="recepcion-servicio"
              autoComplete="off"
              disabled={busy}
            />
          </label>
          <label>
            Secreto de conexión
            <input
              name="clientSecret"
              type="password"
              required
              maxLength={4096}
              autoComplete="new-password"
              disabled={busy}
            />
          </label>
          <p className="hint">
            Las credenciales se guardan cifradas. Si la comprobación falla, se conserva la conexión
            anterior. Los agentes y envíos no se activan con este formulario.
          </p>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <Button disabled={busy}>{busy ? 'Verificando hospital…' : 'Comprobar y conectar'}</Button>
        </form>
      )}
    </details>
  );
}
