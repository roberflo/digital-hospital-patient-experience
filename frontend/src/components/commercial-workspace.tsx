'use client';
import { useEffect, useId, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { toast } from 'sonner';
import { AlertCircle, Building2, RefreshCw, ExternalLink, Plus, X } from 'lucide-react';
import { api, fetcher, type Contact, type Opportunity, type Me } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from './ui/card';
import { Checkbox } from './ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia } from './ui/empty';
import { Field, FieldDescription, FieldGroup, FieldLabel, FieldSet } from './ui/field';
import { Input } from './ui/input';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemMedia,
  ItemTitle,
} from './ui/item';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Separator } from './ui/separator';
import { Spinner } from './ui/spinner';
import { Tabs, TabsContent, TabsList, TabsTrigger } from './ui/tabs';

type Page<T> = { items: T[]; total: number; page: number; pageSize: number };
type Company = {
  id: string;
  name: string;
  active: boolean;
  taxId: string;
  agreement?: { name: string; active: boolean; discountPercent: number };
};
type Service = {
  id: string;
  name: string;
  code: string;
  kind: string;
  price: number;
  currency: string;
};
type Customer = { id: string; name: string; companyName?: string; source: string };
type Quote = {
  quoteVersion: string;
  customerId?: string;
  companyId?: string;
  currency: string;
  subtotal: number;
  discountPercent: number;
  discountAmount: number;
  total: number;
  lines: { serviceId: string; name: string; quantity: number; unitPrice: number; total: number }[];
};
type Purchase = { id: string; createdAt: string; status: string; quote: Quote };
const money = (amount: number, currency = 'USD') =>
  new Intl.NumberFormat('es-SV', { style: 'currency', currency }).format(amount);
const operator = (me?: Me) => !!me && ['admin', 'agent'].includes(me.role);
const hint = 'text-sm text-muted-foreground';
function Failure({ error }: { error?: Error }) {
  return error ? (
    <Alert variant="destructive">
      <AlertCircle />
      <AlertDescription>{error.message}</AlertDescription>
    </Alert>
  ) : null;
}
export function HospitalCommercialLink() {
  const { data } = useSWR<{ hospitalUrl?: string }>('/commercial/settings', fetcher);
  return data?.hospitalUrl ? (
    <Button variant="link" size="sm" asChild>
      <a href={data.hospitalUrl} target="_blank" rel="noreferrer">
        Administrar en Hospital <ExternalLink />
      </a>
    </Button>
  ) : (
    <span className={hint}>Se administra en la sección Empresas y servicios de Hospital.</span>
  );
}
function Pages<T>({
  data,
  page,
  setPage,
}: {
  data?: Page<T>;
  page: number;
  setPage: (page: number) => void;
}) {
  return (
    <div className="flex w-full flex-wrap items-center justify-between gap-2">
      <Button size="sm" variant="outline" disabled={page === 1} onClick={() => setPage(page - 1)}>
        Anterior
      </Button>
      <span className={hint}>
        Página {page} · {data?.total ?? 0} registros
      </span>
      <Button
        size="sm"
        variant="outline"
        disabled={!data || page * data.pageSize >= data.total}
        onClick={() => setPage(page + 1)}
      >
        Siguiente
      </Button>
    </div>
  );
}
export function HospitalCompanies({ search }: { search: string }) {
  const [page, setPage] = useState(1);
  const [tab, setTab] = useState('companies');
  useEffect(() => setPage(1), [search, tab]);
  const companies = useSWR<Page<Company>>(
    tab === 'companies'
      ? `/commercial/companies?q=${encodeURIComponent(search)}&page=${page}`
      : null,
    fetcher,
  );
  const services = useSWR<Page<Service>>(
    tab === 'services' ? `/commercial/services?q=${encodeURIComponent(search)}&page=${page}` : null,
    fetcher,
  );
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <h2>Empresas, convenios y servicios</h2>
        </CardTitle>
        <CardDescription>
          Datos del hospital. El convenio aplica un descuento porcentual general a sus servicios.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Tabs value={tab} onValueChange={setTab} className="gap-4">
          <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
            <div className="max-w-full overflow-x-auto">
              <TabsList>
                <TabsTrigger value="companies">Empresas y convenios</TabsTrigger>
                <TabsTrigger value="services">Servicios y precios</TabsTrigger>
              </TabsList>
            </div>
            <HospitalCommercialLink />
          </div>
          <TabsContent value={tab} className="flex min-w-0 flex-col gap-4">
            <Failure error={companies.error ?? services.error} />
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
              {tab === 'companies'
                ? companies.data?.items.map((c) => (
                    <Item variant="outline" asChild key={c.id}>
                      <article>
                        <ItemMedia variant="icon" className="self-start">
                          <Building2 />
                        </ItemMedia>
                        <ItemContent className="min-w-0 items-start">
                          <ItemTitle>
                            <h3>{c.name}</h3>
                          </ItemTitle>
                          <Badge variant={c.active ? 'secondary' : 'outline'}>
                            {c.active ? 'Empresa activa' : 'Inactiva'}
                          </Badge>
                          <ItemDescription>
                            {c.taxId || 'Sin identificación fiscal'}
                          </ItemDescription>
                          <strong className="text-sm font-semibold">
                            {c.active && c.agreement?.active
                              ? `${c.agreement.discountPercent}% de descuento`
                              : 'Sin descuento activo'}
                          </strong>
                          <p className="text-xs text-muted-foreground">
                            {c.agreement?.name ?? 'Sin convenio'}
                          </p>
                        </ItemContent>
                      </article>
                    </Item>
                  ))
                : services.data?.items.map((s) => (
                    <Item variant="outline" asChild key={s.id}>
                      <article>
                        <ItemContent className="min-w-0 items-start">
                          <Badge variant="outline">
                            {s.kind === 'consultation' ? 'Consulta' : 'Tratamiento'}
                          </Badge>
                          <ItemTitle>
                            <h3>{s.name}</h3>
                          </ItemTitle>
                          <ItemDescription>{s.code}</ItemDescription>
                          <strong className="text-sm font-semibold">
                            {money(s.price, s.currency)}
                          </strong>
                        </ItemContent>
                      </article>
                    </Item>
                  ))}
            </div>
            {!(tab === 'companies' ? companies.data : services.data) &&
              !companies.error &&
              !services.error && (
                <p className={`flex items-center gap-2 ${hint}`}>
                  <Spinner aria-hidden="true" role="presentation" />
                  Consultando Hospital…
                </p>
              )}
            {(tab === 'companies' ? companies.data?.total : services.data?.total) === 0 && (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <Building2 />
                  </EmptyMedia>
                  <EmptyDescription>
                    No hay registros. Agrégalos desde Hospital para utilizarlos aquí.
                  </EmptyDescription>
                </EmptyHeader>
              </Empty>
            )}
          </TabsContent>
        </Tabs>
      </CardContent>
      <CardFooter>
        {tab === 'companies' ? (
          <Pages data={companies.data} page={page} setPage={setPage} />
        ) : (
          <Pages data={services.data} page={page} setPage={setPage} />
        )}
      </CardFooter>
    </Card>
  );
}
export function CustomerCommercial({
  contact,
  onChange,
}: {
  contact: Contact;
  onChange: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState(false);
  const { data: me } = useSWR<Me>('/me', fetcher);
  const { mutate: globalMutate } = useSWRConfig();
  const { data, error, mutate } = useSWR<{
    contact: Contact;
    customer?: Customer;
    candidates: Customer[];
    purchases?: Page<Purchase>;
  }>(open ? `/commercial/contacts/${contact.id}?page=${page}` : null, fetcher);
  async function act(path: string, body?: unknown) {
    setBusy(true);
    try {
      await api(`/commercial/contacts/${contact.id}/${path}`, 'POST', body);
      await mutate();
      await globalMutate(
        (k) =>
          typeof k === 'string' && (k.startsWith('/contacts') || k.startsWith('/opportunities')),
      );
      onChange();
      toast.success('Vínculo con Hospital actualizado');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
        Cliente y compras
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Cliente Hospital · {contact.name}</DialogTitle>
            <DialogDescription>
              Un contacto se convierte en cliente al comprar su primer servicio o al vincular un
              cliente existente de Hospital.
            </DialogDescription>
          </DialogHeader>
          <div className="flex min-w-0 flex-col items-start gap-4">
            <Failure error={error} />
            <HospitalCommercialLink />
            {data && (
              <>
                <Badge variant={data.contact.isCustomer ? 'default' : 'secondary'}>
                  {data.contact.isCustomer ? 'Cliente vinculado' : 'Contacto sin compra vinculada'}
                </Badge>
                <p className="text-sm">
                  {data.customer?.companyName ?? 'Sin empresa asignada en Hospital'}
                </p>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busy || !operator(me)}
                  onClick={() => act('sync')}
                >
                  <RefreshCw />
                  Comprobar compras en Hospital
                </Button>
                {!data.contact.isCustomer && (
                  <div className="flex w-full min-w-0 flex-col gap-2">
                    <h3 className="text-sm font-medium">Clientes con el mismo teléfono</h3>
                    {data.candidates.length ? (
                      <ItemGroup className="gap-2">
                        {data.candidates.map((c) => (
                          <Item variant="outline" size="sm" role="listitem" key={c.id}>
                            <ItemContent className="min-w-0">
                              <ItemTitle>{c.name}</ItemTitle>
                              <ItemDescription>{c.companyName ?? 'Sin empresa'}</ItemDescription>
                            </ItemContent>
                            {operator(me) && (
                              <ItemActions>
                                <Button
                                  size="sm"
                                  disabled={busy}
                                  onClick={() => act('link', { customerId: c.id })}
                                >
                                  Vincular cliente
                                </Button>
                              </ItemActions>
                            )}
                          </Item>
                        ))}
                      </ItemGroup>
                    ) : (
                      <p className={hint}>
                        No hay clientes con este teléfono. Puedes vincular el expediente del
                        paciente o registrar su primera compra desde una oportunidad.
                      </p>
                    )}
                  </div>
                )}
                {data.purchases && (
                  <div className="flex w-full min-w-0 flex-col gap-2">
                    <h3 className="text-sm font-medium">Compras registradas en Hospital</h3>
                    {data.purchases.items.map((p) => (
                      <Item variant="outline" size="sm" asChild key={p.id}>
                        <article>
                          <ItemContent className="min-w-0">
                            <ItemTitle>{money(p.quote.total, p.quote.currency)}</ItemTitle>
                            <span className="text-xs text-muted-foreground">
                              {new Date(p.createdAt).toLocaleString('es-SV')}
                            </span>
                            <ItemDescription className="line-clamp-none">
                              {p.quote.lines.map((l) => `${l.quantity} × ${l.name}`).join(', ')}
                            </ItemDescription>
                            <small className="text-xs text-muted-foreground">
                              Descuento: {p.quote.discountPercent}% ·{' '}
                              {p.status === 'completed' ? 'Pagada' : p.status}
                            </small>
                          </ItemContent>
                        </article>
                      </Item>
                    ))}
                    {!data.purchases.total && <p className={hint}>Sin compras registradas.</p>}
                    <Pages data={data.purchases} page={page} setPage={setPage} />
                  </div>
                )}
              </>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
function readQuote(value?: string): Quote | null {
  try {
    return value ? JSON.parse(value) : null;
  } catch {
    return null;
  }
}
export function OpportunityCommercial({
  opportunity,
  onChange,
}: {
  opportunity: Opportunity;
  onChange: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [companyQuery, setCompanyQuery] = useState('');
  const [selected, setSelected] = useState<{ serviceId: string; quantity: number }[]>([]);
  const [company, setCompany] = useState('');
  const [quote, setQuote] = useState<Quote | null>(readQuote(opportunity.hospitalQuote));
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [reference, setReference] = useState(opportunity.paymentReference ?? '');
  const { data: me } = useSWR<Me>('/me', fetcher);
  const fieldId = useId();
  const services = useSWR<Page<Service>>(
    open ? `/commercial/services?q=${encodeURIComponent(query)}` : null,
    fetcher,
  );
  const companies = useSWR<Page<Company>>(
    open ? `/commercial/companies?q=${encodeURIComponent(companyQuery)}` : null,
    fetcher,
  );
  useEffect(() => {
    const q = readQuote(opportunity.hospitalQuote);
    setQuote(q);
    setSelected(q?.lines.map((l) => ({ serviceId: l.serviceId, quantity: l.quantity })) ?? []);
    setCompany(q?.companyId ?? '');
    setDirty(false);
  }, [opportunity.hospitalQuote]);
  useEffect(() => {
    setReference(opportunity.paymentReference ?? '');
  }, [opportunity.paymentReference]);
  const frozen = busy || !!opportunity.hospitalPurchaseId || !!opportunity.purchasePending;
  async function quoteNow() {
    setBusy(true);
    try {
      const q = await api<Quote>(`/commercial/opportunities/${opportunity.id}/quote`, 'POST', {
        lines: selected,
        companyId: company || null,
      });
      setQuote(q);
      setDirty(false);
      onChange();
      toast.success('Cotización calculada por Hospital');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function purchase() {
    if (!quote) return;
    setBusy(true);
    try {
      await api(`/commercial/opportunities/${opportunity.id}/purchase`, 'POST', {
        quoteVersion: quote.quoteVersion,
        paymentReceived: confirmed,
        paymentReference: reference,
      });
      toast.success('Compra registrada en Hospital. Contacto convertido en cliente.');
      setOpen(false);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      onChange();
      setBusy(false);
    }
  }
  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
        {opportunity.hospitalPurchaseId
          ? 'Ver compra Hospital'
          : opportunity.purchasePending
            ? 'Comprobar compra'
            : 'Servicios y cotización'}
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Servicios · {opportunity.title}</DialogTitle>
            <DialogDescription>
              Precios y convenio calculados por Hospital. Marcar un seguimiento como ganado no
              registra un pago.
            </DialogDescription>
          </DialogHeader>
          <div className="flex min-w-0 flex-col gap-4">
            <div className="flex">
              <HospitalCommercialLink />
            </div>
            <Failure error={services.error ?? companies.error} />
            {!opportunity.hospitalPurchaseId && (
              <FieldSet disabled={frozen || !operator(me)} className="min-w-0 gap-4">
                <FieldGroup className="gap-4">
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-query`}>Buscar servicio</FieldLabel>
                    <Input
                      id={`${fieldId}-query`}
                      value={query}
                      onChange={(e) => setQuery(e.target.value)}
                      placeholder="Nombre o código del hospital"
                    />
                  </Field>
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-service`}>Agregar servicio</FieldLabel>
                    <NativeSelect
                      id={`${fieldId}-service`}
                      value=""
                      onChange={(e) => {
                        if (
                          e.target.value &&
                          !selected.some((l) => l.serviceId === e.target.value)
                        ) {
                          setSelected([...selected, { serviceId: e.target.value, quantity: 1 }]);
                          setDirty(true);
                        }
                      }}
                    >
                      <NativeSelectOption value="">
                        Selecciona consulta o tratamiento
                      </NativeSelectOption>
                      {services.data?.items.map((s) => (
                        <NativeSelectOption key={s.id} value={s.id}>
                          {s.name} · {money(s.price, s.currency)}
                        </NativeSelectOption>
                      ))}
                    </NativeSelect>
                  </Field>
                  {selected.map((line, i) => (
                    <div className="flex min-w-0 items-center gap-2" key={line.serviceId}>
                      <span className="min-w-0 flex-1 truncate text-sm">
                        {services.data?.items.find((s) => s.id === line.serviceId)?.name ??
                          quote?.lines.find((l) => l.serviceId === line.serviceId)?.name ??
                          'Servicio seleccionado'}
                      </span>
                      <Input
                        className="w-20"
                        aria-label={`Cantidad servicio ${i + 1}`}
                        type="number"
                        min={1}
                        max={1000}
                        value={line.quantity}
                        onChange={(e) => {
                          setSelected(
                            selected.map((l) =>
                              l.serviceId === line.serviceId
                                ? { ...l, quantity: Number(e.target.value) }
                                : l,
                            ),
                          );
                          setDirty(true);
                        }}
                      />
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Quitar servicio ${i + 1}`}
                        onClick={() => {
                          setSelected(selected.filter((l) => l.serviceId !== line.serviceId));
                          setDirty(true);
                        }}
                      >
                        <X />
                      </Button>
                    </div>
                  ))}
                  {!quote?.customerId && (
                    <>
                      <Field className="gap-2">
                        <FieldLabel htmlFor={`${fieldId}-company-query`}>Buscar empresa</FieldLabel>
                        <Input
                          id={`${fieldId}-company-query`}
                          value={companyQuery}
                          onChange={(e) => setCompanyQuery(e.target.value)}
                        />
                      </Field>
                      <Field className="gap-2">
                        <FieldLabel htmlFor={`${fieldId}-company`}>
                          Empresa para esta compra
                        </FieldLabel>
                        <NativeSelect
                          id={`${fieldId}-company`}
                          value={company}
                          onChange={(e) => {
                            setCompany(e.target.value);
                            setDirty(true);
                          }}
                        >
                          <NativeSelectOption value="">Sin empresa</NativeSelectOption>
                          {companies.data?.items
                            .filter((c) => c.active)
                            .map((c) => (
                              <NativeSelectOption key={c.id} value={c.id}>
                                {c.name}
                              </NativeSelectOption>
                            ))}
                          {company && !companies.data?.items.some((c) => c.id === company) && (
                            <NativeSelectOption value={company}>
                              Empresa de la cotización
                            </NativeSelectOption>
                          )}
                        </NativeSelect>
                      </Field>
                    </>
                  )}
                  <FieldDescription>
                    Si ya es cliente, Hospital aplica la empresa asignada a su ficha.
                  </FieldDescription>
                  <Button
                    className="self-start"
                    variant={quote && !dirty ? 'outline' : 'default'}
                    disabled={!selected.length || frozen || !operator(me)}
                    onClick={quoteNow}
                  >
                    <Plus />
                    Cotizar con Hospital
                  </Button>
                </FieldGroup>
              </FieldSet>
            )}
            {quote && (
              <>
                <Separator />
                <section className="flex flex-col gap-2 text-sm" aria-label="Cotización Hospital">
                  <h3 className="font-medium">
                    {opportunity.hospitalPurchaseId ? 'Compra pagada' : 'Cotización Hospital'}
                  </h3>
                  {quote.lines.map((l) => (
                    <p className="flex justify-between gap-4" key={l.serviceId}>
                      <span className="min-w-0">
                        {l.quantity} × {l.name}
                      </span>
                      <strong className="font-medium">{money(l.total, quote.currency)}</strong>
                    </p>
                  ))}
                  <p className="flex justify-between gap-4 text-muted-foreground">
                    <span>Subtotal</span>
                    <span>{money(quote.subtotal, quote.currency)}</span>
                  </p>
                  <p className="flex justify-between gap-4 text-muted-foreground">
                    <span>Convenio {quote.discountPercent}%</span>
                    <span>−{money(quote.discountAmount, quote.currency)}</span>
                  </p>
                  <Separator />
                  <p className="flex justify-between gap-4">
                    <strong className="font-semibold">Total</strong>
                    <strong className="font-semibold">{money(quote.total, quote.currency)}</strong>
                  </p>
                  {dirty && (
                    <p className="text-muted-foreground">
                      Recalcula la cotización para aplicar los cambios.
                    </p>
                  )}
                </section>
              </>
            )}
            {opportunity.purchasePending && (
              <Alert role="status">
                <AlertCircle />
                <AlertDescription>
                  Compra en comprobación. El reintento recupera el mismo registro de Hospital;
                  conserva su referencia de pago.
                </AlertDescription>
              </Alert>
            )}
            {quote && !opportunity.hospitalPurchaseId && operator(me) && (
              <>
                <Separator />
                <FieldGroup className="gap-4">
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-reference`}>Referencia del pago</FieldLabel>
                    <Input
                      id={`${fieldId}-reference`}
                      maxLength={120}
                      value={reference}
                      disabled={busy || opportunity.purchasePending}
                      onChange={(e) => setReference(e.target.value)}
                    />
                  </Field>
                  <Field orientation="horizontal" className="items-start">
                    <Checkbox
                      id={`${fieldId}-confirmed`}
                      checked={confirmed}
                      disabled={busy}
                      onCheckedChange={(value) => setConfirmed(value === true)}
                    />
                    <FieldLabel htmlFor={`${fieldId}-confirmed`} className="font-normal">
                      Confirmo que el pago ya fue recibido. Esta acción registra la compra y no
                      cobra dinero.
                    </FieldLabel>
                  </Field>
                </FieldGroup>
                <DialogFooter>
                  <Button disabled={!confirmed || busy || dirty} onClick={purchase}>
                    {busy
                      ? 'Comprobando Hospital…'
                      : opportunity.purchasePending
                        ? 'Recuperar compra registrada'
                        : 'Registrar compra pagada'}
                  </Button>
                </DialogFooter>
              </>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
