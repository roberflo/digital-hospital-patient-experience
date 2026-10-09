'use client';
import { useId, useState } from 'react';
import useSWR from 'swr';
import { signIn } from 'next-auth/react';
import {
  AlertCircle,
  ArrowUpRight,
  CheckCircle2,
  ChevronRight,
  HeartPulse,
  RefreshCw,
  Search,
} from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Contact, type Me } from '@/lib/api';
import { refreshHospitalIdentity } from '@/lib/hospital-identity';
import { Alert, AlertDescription, AlertTitle } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardHeader, CardTitle } from './ui/card';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from './ui/dialog';
import {
  Field,
  FieldContent,
  FieldDescription,
  FieldGroup,
  FieldLabel,
  FieldTitle,
} from './ui/field';
import { Input } from './ui/input';
import { Item, ItemActions, ItemContent, ItemDescription, ItemTitle } from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { RadioGroup, RadioGroupItem } from './ui/radio-group';

type Connection = {
  hospital: Me['tenant'];
  configured: boolean;
  hospitalUrl?: string;
  sharedIdentity: boolean;
  hospitalLoginAvailable: boolean;
  name: string;
  role: string;
};
// `platform`: el dueño de plataforma ve el estado de la conexión de la recepción elegida, sin lo
// que es del hospital (agenda, contactos, equipo, cambio de cuenta).
export function HospitalConnection({ platform = false }: { platform?: boolean }) {
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
    <Card>
      <CardHeader>
        <CardTitle>
          <h2 className="flex items-center gap-2">
            <HeartPulse className="size-4" /> {platform ? 'Conexión con Hospital' : 'Mi hospital'}
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {error && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>
                {platform
                  ? 'No pudimos leer la conexión de esta recepción.'
                  : 'No pudimos cargar tu hospital.'}
              </p>
              <Button variant="outline" size="sm" onClick={() => mutate()}>
                Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        )}
        {!data && !error && (
          <p className="text-sm text-muted-foreground">
            {platform ? 'Leyendo la conexión…' : 'Cargando tu hospital…'}
          </p>
        )}
        {data && (
          <>
            <div className="flex flex-col items-start gap-2 sm:flex-row sm:justify-between sm:gap-6">
              <div className="flex min-w-0 flex-col gap-1">
                <h3 className="text-xl font-semibold tracking-tight">{data.hospital.name}</h3>
                {!platform && (
                  <p className="max-w-xl text-sm text-muted-foreground">
                    {data.sharedIdentity
                      ? `Has entrado como ${data.name}. Tu cuenta conserva los permisos del hospital.`
                      : 'Estás en un espacio de prueba. Para trabajar con tu equipo, usa tu cuenta del hospital.'}
                  </p>
                )}
              </div>
              <Badge role="status" variant={connected ? 'default' : 'secondary'}>
                {connected ? <CheckCircle2 /> : <HeartPulse />}
                {connected
                  ? 'Hospital conectado'
                  : isValidating
                    ? platform
                      ? 'Comprobando la conexión…'
                      : 'Conectando con tu hospital…'
                    : data.configured
                      ? 'Conexión interrumpida'
                      : 'Pendiente de conexión'}
              </Badge>
            </div>
            {!platform && !data.sharedIdentity && data.hospitalLoginAvailable && (
              <Alert role="note">
                <HeartPulse />
                <AlertTitle className="line-clamp-none">
                  <h4>Una cuenta para ambas aplicaciones</h4>
                </AlertTitle>
                <AlertDescription className="gap-3">
                  <p>
                    Inicia sesión con el usuario que ya utilizas en Hospital. Reconoceremos tu
                    hospital automáticamente.
                  </p>
                  <Button onClick={() => signIn('keycloak', { callbackUrl: '/?view=hospital' })}>
                    Conectar con mi hospital <ArrowUpRight />
                  </Button>
                </AlertDescription>
              </Alert>
            )}
            {platform && !data.configured && (
              <Alert role="note">
                <AlertCircle />
                <AlertDescription>
                  A esta recepción le falta el acceso a su hospital. Es una tarea del operador de la
                  instalación: no hay credenciales que introducir aquí.
                </AlertDescription>
              </Alert>
            )}
            {!platform && data.sharedIdentity && !data.configured && (
              <Alert role="note">
                <AlertCircle />
                <AlertDescription>
                  Tu cuenta ya está vinculada. Falta habilitar el acceso de Recepción a este
                  hospital; pide al administrador de la plataforma que lo complete. No necesitas
                  introducir contraseñas de conexión ni otros datos técnicos.
                </AlertDescription>
              </Alert>
            )}
            {healthError && (
              <Alert variant="destructive">
                <AlertCircle />
                <AlertDescription className="gap-3">
                  <p>
                    No pudimos comunicarnos con Hospital.
                    {!platform && ' Tu cuenta y tus datos siguen vinculados.'}
                  </p>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={isValidating}
                    onClick={() => check()}
                  >
                    <RefreshCw /> Volver a intentar
                  </Button>
                </AlertDescription>
              </Alert>
            )}
            {!platform && (
              <>
                <div className="flex flex-wrap items-center gap-2">
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
                        Abrir Hospital <ArrowUpRight />
                      </a>
                    </Button>
                  )}
                </div>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <Item variant="outline" asChild>
                    <a href="/?view=contacts">
                      <ItemContent>
                        <ItemTitle>Vincular pacientes</ItemTitle>
                        <ItemDescription>
                          Busca el expediente desde un contacto para consultar sus citas.
                        </ItemDescription>
                      </ItemContent>
                      <ItemActions>
                        <ChevronRight className="size-4" aria-hidden="true" />
                      </ItemActions>
                    </a>
                  </Item>
                  <Item variant="outline" asChild>
                    <a href="/?view=team">
                      <ItemContent>
                        <ItemTitle>Trabajar con tu equipo</ItemTitle>
                        <ItemDescription>
                          Tus compañeros entran con su cuenta del Hospital y aparecen aquí.
                        </ItemDescription>
                      </ItemContent>
                      <ItemActions>
                        <ChevronRight className="size-4" aria-hidden="true" />
                      </ItemActions>
                    </a>
                  </Item>
                </div>
                {data.sharedIdentity && data.hospitalLoginAvailable && (
                  <Button
                    variant="ghost"
                    size="sm"
                    className="self-start"
                    onClick={() =>
                      signIn('keycloak', { callbackUrl: '/?view=hospital' }, { prompt: 'login' })
                    }
                  >
                    Usar otra cuenta del hospital
                  </Button>
                )}
              </>
            )}
            <p className="text-sm text-muted-foreground">
              Los pacientes, las citas y las conversaciones permanecen en su hospital.
            </p>
          </>
        )}
      </CardContent>
    </Card>
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
  const fieldId = useId();
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
        <DialogHeader>
          <DialogTitle>
            {contact.patientId ? 'Paciente vinculado' : 'Vincular paciente'}
          </DialogTitle>
          <DialogDescription>
            {contact.name} · Hospital: {connection?.hospital.name ?? 'Cargando…'}. Se comprobará que
            el teléfono del expediente coincide con +{contact.phone}.
          </DialogDescription>
        </DialogHeader>
        {contact.patientId ? (
          <p className="text-sm">
            Este contacto ya está vinculado a su expediente. Sus citas y documentos autorizados se
            consultan desde la conversación.
          </p>
        ) : !allowed ? (
          <p className="text-sm">
            Un recepcionista o administrador debe vincular el paciente antes de consultar su
            información.
          </p>
        ) : connection && !connection.configured ? (
          <p className="text-sm">
            Primero{' '}
            <a className="underline underline-offset-4" href="/?view=hospital">
              conecta el hospital de tu cuenta
            </a>
            .
          </p>
        ) : (
          <>
            <form
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
              <FieldGroup className="gap-4">
                <Field>
                  <FieldLabel htmlFor={`${fieldId}-shape`}>Buscar por</FieldLabel>
                  <NativeSelect
                    id={`${fieldId}-shape`}
                    value={shape}
                    disabled={busy}
                    onChange={(e) => {
                      setShape(e.target.value);
                      reset();
                    }}
                  >
                    <NativeSelectOption value="name-tokens">Nombre y apellido</NativeSelectOption>
                    <NativeSelectOption value="dui">DUI</NativeSelectOption>
                    <NativeSelectOption value="record-number">
                      Número de expediente
                    </NativeSelectOption>
                  </NativeSelect>
                </Field>
                <Field>
                  <FieldLabel htmlFor={`${fieldId}-term`}>Datos del paciente</FieldLabel>
                  <Input
                    id={`${fieldId}-term`}
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
                </Field>
                <Field>
                  <Button
                    variant={selected ? 'outline' : 'default'}
                    disabled={busy || !connection?.configured}
                  >
                    <Search />
                    {busy ? 'Consultando…' : 'Buscar paciente'}
                  </Button>
                </Field>
              </FieldGroup>
            </form>
            {results && (
              <div
                className="flex max-h-64 flex-col gap-3 overflow-y-auto p-1"
                aria-label="Pacientes encontrados"
              >
                {results.length === 0 ? (
                  <p className="text-sm">
                    No se encontraron pacientes. Revisa los datos o registra al paciente desde
                    Hospital.
                  </p>
                ) : (
                  <>
                    <p className="text-sm text-muted-foreground">
                      Selecciona el expediente correcto. La coincidencia de nombre no vincula
                      automáticamente al paciente.
                    </p>
                    <RadioGroup
                      name="patient-match"
                      value={selected?.patientId ?? ''}
                      disabled={busy}
                      onValueChange={(id) =>
                        setSelected(results.find((p) => p.patientId === id) ?? null)
                      }
                    >
                      {results.map((p) => (
                        <FieldLabel key={p.patientId} htmlFor={`${fieldId}-${p.patientId}`}>
                          <Field orientation="horizontal">
                            <RadioGroupItem value={p.patientId} id={`${fieldId}-${p.patientId}`} />
                            <FieldContent>
                              <FieldTitle>{p.displayName}</FieldTitle>
                              <FieldDescription>
                                Expediente: {p.recordNumber ?? 'Sin número visible'}
                              </FieldDescription>
                            </FieldContent>
                          </Field>
                        </FieldLabel>
                      ))}
                    </RadioGroup>
                    {results.length >= 30 && (
                      <p className="text-sm text-muted-foreground">
                        Mostrando hasta 30 resultados. Afina la búsqueda si no ves al paciente.
                      </p>
                    )}
                  </>
                )}
              </div>
            )}
            {selected && (
              <Button
                className="h-auto min-h-9 whitespace-normal"
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
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        {connection?.hospitalUrl && (
          <Button asChild variant="link" className="justify-self-start">
            <a href={connection.hospitalUrl} target="_blank" rel="noreferrer">
              Abrir Hospital →
            </a>
          </Button>
        )}
      </DialogContent>
    </Dialog>
  );
}
