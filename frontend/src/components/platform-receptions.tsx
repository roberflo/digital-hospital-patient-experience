'use client';
import { Fragment, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import {
  AlertCircle,
  Building2,
  HeartPulse,
  MessageCircle,
  Plus,
  RefreshCw,
  Search,
} from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher } from '@/lib/api';
import { sessionFetch } from '@/lib/session-client';
import { cn } from '@/lib/utils';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardHeader, CardTitle } from './ui/card';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from './ui/empty';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemGroup,
  ItemMedia,
  ItemSeparator,
  ItemTitle,
} from './ui/item';
import { Spinner } from './ui/spinner';

type Tenant = { id: string; name: string; hospitalConfigured: boolean; whatsAppConnected: boolean };
type Hospital = { id: string; name: string; hasReception: boolean };

// The choice is sealed by the server. A full load afterwards drops every cached read and any
// half-finished connection link, so nothing from one reception can be shown under another.
export async function setActing(tenantId?: string) {
  const response = await sessionFetch('/api/platform/acting', {
    method: tenantId ? 'POST' : 'DELETE',
    ...(tenantId
      ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tenantId }) }
      : {}),
  });
  if (!response.ok) {
    const refusal = await response.json().catch(() => null);
    throw new Error(refusal?.title ?? 'No se pudo completar la operación.');
  }
  window.location.assign('/');
}
const plain = (text: string) =>
  text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase();
// ponytail: filtrado en cliente con tope de filas pintadas; paginar en la API si la lista pasa de miles.
const SHOWN = 100;

export function ReceptionPicker() {
  const { data, error, mutate, isValidating } = useSWR<{ tenants: Tenant[] }>(
    '/platform/tenants',
    fetcher,
  );
  const [query, setQuery] = useState('');
  const [choosing, setChoosing] = useState<string | null>(null);
  const [failure, setFailure] = useState('');
  // SWR keeps the last list when a revalidation fails: the failure wins, a stale list is not shown.
  const unreadable = !!error || (!!data && !Array.isArray(data.tenants));
  const tenants = unreadable ? undefined : data?.tenants;
  const matches = tenants?.filter((t) => plain(t.name).includes(plain(query.trim()))) ?? [];
  return (
    <Card role="region" aria-labelledby="receptions-title">
      <CardHeader>
        <CardTitle>
          <h2 id="receptions-title" className="flex items-center gap-2">
            <Building2 className="size-4" /> Recepciones
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {tenants && tenants.length > 0 && (
          <InputGroup className="sm:max-w-xs">
            <InputGroupInput
              aria-label="Buscar recepción por nombre"
              placeholder="Buscar por nombre…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              autoComplete="off"
            />
            <InputGroupAddon>
              <Search />
            </InputGroupAddon>
          </InputGroup>
        )}
        {unreadable ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>
                No pudimos leer la lista de recepciones. La lectura falló: no significa que no haya
                ninguna.
              </p>
              <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !tenants ? (
          <div className="flex items-center justify-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner /> Leyendo las recepciones…
          </div>
        ) : tenants.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Building2 />
              </EmptyMedia>
              <EmptyTitle>Aún no hay recepciones</EmptyTitle>
              <EmptyDescription>
                Una recepción aparece aquí cuando la abres para un hospital, más abajo, o cuando su
                Administrador entra por primera vez.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <>
            <p className="text-sm text-muted-foreground" role="status">
              {matches.length === tenants.length
                ? `${tenants.length} recepciones`
                : `${matches.length} de ${tenants.length} recepciones`}
              {matches.length > SHOWN && ` · se muestran ${SHOWN}; escribe el nombre para afinar`}
            </p>
            {failure && (
              <Alert variant="destructive">
                <AlertCircle />
                <AlertDescription>{failure}</AlertDescription>
              </Alert>
            )}
            {matches.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Ninguna recepción coincide con «{query.trim()}».
              </p>
            ) : (
              <ItemGroup className="max-h-144 overflow-y-auto">
                {matches.slice(0, SHOWN).map((t, index) => (
                  <Fragment key={t.id}>
                    {index > 0 && <ItemSeparator />}
                    <Item size="sm">
                      <ItemMedia variant="icon">
                        <Building2 />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle>{t.name}</ItemTitle>
                        <div className="flex flex-wrap gap-2">
                          <Badge variant={t.hospitalConfigured ? 'secondary' : 'outline'}>
                            <HeartPulse />
                            {t.hospitalConfigured
                              ? 'Hospital configurado'
                              : 'Hospital sin configurar'}
                          </Badge>
                          <Badge variant={t.whatsAppConnected ? 'secondary' : 'outline'}>
                            <MessageCircle />
                            {t.whatsAppConnected ? 'WhatsApp conectado' : 'Sin número de WhatsApp'}
                          </Badge>
                        </div>
                      </ItemContent>
                      <ItemActions>
                        <Button
                          variant="outline"
                          size="sm"
                          disabled={!!choosing}
                          aria-label={`Elegir: ${t.name}`}
                          onClick={async () => {
                            setChoosing(t.id);
                            setFailure('');
                            try {
                              await setActing(t.id);
                            } catch (e) {
                              setFailure((e as Error).message);
                              setChoosing(null);
                            }
                          }}
                        >
                          {choosing === t.id && <Spinner />}
                          Elegir
                        </Button>
                      </ItemActions>
                    </Item>
                  </Fragment>
                ))}
              </ItemGroup>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}

// REC-3: abrir la recepción de un hospital que aún no la tiene. Al abrirla queda elegida.
// Lo que la API contó de un hospital al intentar abrirlo; el texto libre es un fallo sin código.
type OpenNote = 'pending' | 'already' | 'unknown' | (string & {});
const openNotes: Record<string, string> = {
  pending:
    'La cuenta de servicio de este hospital se está creando; vuelve a intentarlo en un minuto.',
  already: 'Esta recepción ya está abierta.',
  unknown: 'Hospital no reconocido.',
};
export function ReceptionOpener() {
  const { mutate: refresh } = useSWRConfig();
  const [open, setOpen] = useState(false);
  // Lazy: Hospital is only asked once this section is opened, and not again on every focus.
  const { data, error, mutate, isValidating } = useSWR<{ hospitals: Hospital[] }>(
    open ? '/platform/hospitals' : null,
    fetcher,
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, OpenNote>>({});
  const unreadable = !!error || (!!data && !Array.isArray(data.hospitals));
  // A hospital found already open stays listed so it can be chosen from its own row.
  const pending = unreadable
    ? undefined
    : data?.hospitals.filter((h) => !h.hasReception || notes[h.id] === 'already');
  const matches = pending?.filter((h) => plain(h.name).includes(plain(query.trim()))) ?? [];
  // Homonyms (dev data has hundreds of «Hospital sin nombre») are told apart by the start of
  // their id, so the owner opens the one they mean.
  const repeated = new Set(
    (pending ?? []).map((h) => h.name).filter((name, i, all) => all.indexOf(name) !== i),
  );
  const note = (id: string, value: OpenNote = '') => setNotes((all) => ({ ...all, [id]: value }));
  async function act(hospital: Hospital) {
    setBusy(hospital.id);
    try {
      // A reception carries its hospital's id; a fresh one is chosen by the id the API returned.
      let reception = hospital.id;
      if (notes[hospital.id] !== 'already') {
        note(hospital.id);
        let failure: OpenNote = '';
        try {
          reception = (
            await api<{ id: string }>('/platform/tenants', 'POST', { hospitalId: hospital.id })
          ).id;
        } catch (e) {
          const { status, code, message } = e as { status?: number; code?: string } & Error;
          failure =
            code === 'already_open'
              ? 'already'
              : code === 'service_account_pending'
                ? 'pending'
                : status === 404
                  ? 'unknown'
                  : message || 'No se pudo abrir la recepción.';
        }
        // Open now, or someone else opened it: it belongs in the list above either way, and if
        // choosing it fails below it can still be chosen from this row.
        if (!failure || failure === 'already') {
          note(hospital.id, 'already');
          void refresh('/platform/tenants');
        } else note(hospital.id, failure);
        // Found already open: offered, not chosen on the owner's behalf.
        if (failure) return;
      }
      // Leaves the page on success.
      await setActing(reception);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return (
    <Card role="region" aria-labelledby="open-reception-title">
      <CardHeader>
        <CardTitle>
          <h2 id="open-reception-title" className="flex items-center gap-2">
            <Plus className="size-4" /> Abrir la recepción de un hospital
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          {open && pending && pending.length > 0 && (
            <InputGroup className="sm:max-w-xs">
              <InputGroupInput
                aria-label="Buscar hospital por nombre"
                placeholder="Buscar por nombre…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                autoComplete="off"
              />
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
            </InputGroup>
          )}
          <Button
            variant="outline"
            size="sm"
            aria-expanded={open}
            onClick={() => setOpen((value) => !value)}
          >
            {open ? 'Ocultar' : 'Ver hospitales sin recepción'}
          </Button>
        </div>
        {!open ? (
          <p className="text-sm text-muted-foreground">
            Para un hospital que ya existe en Hospital y todavía no tiene Recepción. Al abrirla
            queda elegida para configurarla.
          </p>
        ) : unreadable ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>
                No pudimos leer los hospitales desde Hospital. La lectura falló: no significa que
                todos tengan ya recepción.
              </p>
              <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !pending ? (
          <div className="flex items-center justify-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner /> Leyendo los hospitales…
          </div>
        ) : pending.length === 0 ? (
          <p className="text-sm text-muted-foreground">Todos los hospitales ya tienen recepción.</p>
        ) : matches.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Ningún hospital coincide con «{query.trim()}».
          </p>
        ) : (
          <>
            <p className="text-sm text-muted-foreground" role="status">
              {matches.length === pending.length
                ? `${pending.length} sin recepción`
                : `${matches.length} de ${pending.length} sin recepción`}
              {matches.length > SHOWN && ` · se muestran ${SHOWN}; escribe el nombre para afinar`}
            </p>
            <ItemGroup className="max-h-144 overflow-y-auto">
              {matches.slice(0, SHOWN).map((h, index) => {
                const said = notes[h.id] || undefined;
                const label = repeated.has(h.name) ? `${h.name} (${h.id.slice(0, 8)})` : h.name;
                return (
                  <Fragment key={h.id}>
                    {index > 0 && <ItemSeparator />}
                    <Item size="sm">
                      <ItemMedia variant="icon">
                        <Building2 />
                      </ItemMedia>
                      <ItemContent className="min-w-0">
                        <ItemTitle>
                          {h.name}
                          {repeated.has(h.name) && (
                            <span className="font-normal text-muted-foreground">
                              · {h.id.slice(0, 8)}
                            </span>
                          )}
                        </ItemTitle>
                        {said && (
                          <p
                            role={said === 'already' ? 'status' : 'alert'}
                            className={cn(
                              'text-sm',
                              said === 'already' ? 'text-muted-foreground' : 'text-destructive',
                            )}
                          >
                            {openNotes[said] ?? said}
                          </p>
                        )}
                      </ItemContent>
                      {said !== 'unknown' && (
                        <ItemActions>
                          <Button
                            variant={said ? 'outline' : 'default'}
                            size="sm"
                            disabled={!!busy}
                            aria-label={`${said === 'already' ? 'Elegir' : said ? 'Reintentar' : 'Abrir'}: ${label}`}
                            onClick={() => act(h)}
                          >
                            {busy === h.id ? (
                              <Spinner />
                            ) : said && said !== 'already' ? (
                              <RefreshCw />
                            ) : null}
                            {said === 'already' ? 'Elegir' : said ? 'Reintentar' : 'Abrir'}
                          </Button>
                        </ItemActions>
                      )}
                    </Item>
                  </Fragment>
                );
              })}
            </ItemGroup>
          </>
        )}
      </CardContent>
    </Card>
  );
}
