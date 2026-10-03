'use client';
import { useEffect, useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { toast } from 'sonner';
import { Building2, RefreshCw, ExternalLink, Plus, X } from 'lucide-react';
import { api, fetcher, type Contact, type Opportunity, type Me } from '@/lib/api';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogTitle, DialogDescription } from './ui/dialog';

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
const operator = (me?: Me) =>
  !!me && ['admin', 'platform_admin', 'supervisor', 'agent'].includes(me.role);
function Failure({ error }: { error?: Error }) {
  return error ? (
    <p className="error" role="alert">
      {error.message}
    </p>
  ) : null;
}
export function HospitalCommercialLink() {
  const { data } = useSWR<{ hospitalUrl?: string }>('/commercial/settings', fetcher);
  return data?.hospitalUrl ? (
    <a
      className="commercial-hospital-link"
      href={data.hospitalUrl}
      target="_blank"
      rel="noreferrer"
    >
      Administrar en Hospital <ExternalLink size={14} />
    </a>
  ) : (
    <span className="hint">Se administra en la sección Empresas y servicios de Hospital.</span>
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
    <div className="card-toolbar">
      <Button size="sm" variant="outline" disabled={page === 1} onClick={() => setPage(page - 1)}>
        Anterior
      </Button>
      <span className="hint">
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
    <section className="content-card">
      <div className="card-toolbar">
        <h2>Empresas, convenios y servicios</h2>
        <HospitalCommercialLink />
      </div>
      <p className="commercial-intro">
        Datos del hospital. El convenio aplica un descuento porcentual general a sus servicios.
      </p>
      <div className="card-toolbar">
        <Button
          size="sm"
          variant={tab === 'companies' ? 'default' : 'outline'}
          onClick={() => setTab('companies')}
        >
          Empresas y convenios
        </Button>
        <Button
          size="sm"
          variant={tab === 'services' ? 'default' : 'outline'}
          onClick={() => setTab('services')}
        >
          Servicios y precios
        </Button>
      </div>
      <Failure error={companies.error ?? services.error} />
      <div className="company-grid">
        {tab === 'companies'
          ? companies.data?.items.map((c) => (
              <article className="company-card" key={c.id}>
                <Building2 size={22} />
                <h3>{c.name}</h3>
                <span className="tag">{c.active ? 'Empresa activa' : 'Inactiva'}</span>
                <p>{c.taxId || 'Sin identificación fiscal'}</p>
                <strong>
                  {c.active && c.agreement?.active
                    ? `${c.agreement.discountPercent}% de descuento`
                    : 'Sin descuento activo'}
                </strong>
                <p className="hint">{c.agreement?.name ?? 'Sin convenio'}</p>
              </article>
            ))
          : services.data?.items.map((s) => (
              <article className="company-card" key={s.id}>
                <span className="tag">
                  {s.kind === 'consultation' ? 'Consulta' : 'Tratamiento'}
                </span>
                <h3>{s.name}</h3>
                <p>{s.code}</p>
                <strong>{money(s.price, s.currency)}</strong>
              </article>
            ))}
      </div>
      {!(tab === 'companies' ? companies.data : services.data) &&
        !companies.error &&
        !services.error && <p className="commercial-intro">Consultando Hospital…</p>}
      {(tab === 'companies' ? companies.data?.total : services.data?.total) === 0 && (
        <p className="commercial-intro">
          No hay registros. Agrégalos desde Hospital para utilizarlos aquí.
        </p>
      )}
      {tab === 'companies' ? (
        <Pages data={companies.data} page={page} setPage={setPage} />
      ) : (
        <Pages data={services.data} page={page} setPage={setPage} />
      )}
    </section>
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
        <DialogContent>
          <DialogTitle>Cliente Hospital · {contact.name}</DialogTitle>
          <DialogDescription>
            Un contacto se convierte en cliente al comprar su primer servicio o al vincular un
            cliente existente de Hospital.
          </DialogDescription>
          <div className="commercial-detail">
            <Failure error={error} />
            <HospitalCommercialLink />
            {data && (
              <>
                <span className="tag">
                  {data.contact.isCustomer ? 'Cliente vinculado' : 'Contacto sin compra vinculada'}
                </span>
                <p>{data.customer?.companyName ?? 'Sin empresa asignada en Hospital'}</p>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busy || !operator(me)}
                  onClick={() => act('sync')}
                >
                  <RefreshCw size={14} />
                  Comprobar compras en Hospital
                </Button>
                {!data.contact.isCustomer && (
                  <div>
                    <h3>Clientes con el mismo teléfono</h3>
                    {data.candidates.length ? (
                      data.candidates.map((c) => (
                        <div className="commercial-candidate" key={c.id}>
                          <span>
                            <strong>{c.name}</strong>
                            <small>{c.companyName ?? 'Sin empresa'}</small>
                          </span>
                          {operator(me) && (
                            <Button
                              size="sm"
                              disabled={busy}
                              onClick={() => act('link', { customerId: c.id })}
                            >
                              Vincular cliente
                            </Button>
                          )}
                        </div>
                      ))
                    ) : (
                      <p className="hint">
                        No hay clientes con este teléfono. Puedes vincular el expediente del
                        paciente o registrar su primera compra desde una oportunidad.
                      </p>
                    )}
                  </div>
                )}
                {data.purchases && (
                  <>
                    <h3>Compras registradas en Hospital</h3>
                    {data.purchases.items.map((p) => (
                      <article className="commercial-purchase" key={p.id}>
                        <strong>{money(p.quote.total, p.quote.currency)}</strong>
                        <span>{new Date(p.createdAt).toLocaleString('es-SV')}</span>
                        <p>{p.quote.lines.map((l) => `${l.quantity} × ${l.name}`).join(', ')}</p>
                        <small>
                          Descuento: {p.quote.discountPercent}% ·{' '}
                          {p.status === 'completed' ? 'Pagada' : p.status}
                        </small>
                      </article>
                    ))}
                    {!data.purchases.total && <p className="hint">Sin compras registradas.</p>}
                    <Pages data={data.purchases} page={page} setPage={setPage} />
                  </>
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
        <DialogContent>
          <DialogTitle>Servicios · {opportunity.title}</DialogTitle>
          <DialogDescription>
            Precios y convenio calculados por Hospital. Marcar un seguimiento como ganado no
            registra un pago.
          </DialogDescription>
          <div className="commercial-detail">
            <HospitalCommercialLink />
            <Failure error={services.error ?? companies.error} />
            {!opportunity.hospitalPurchaseId && (
              <fieldset disabled={frozen || !operator(me)} className="commercial-fields">
                <label>
                  Buscar servicio
                  <input
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder="Nombre o código del hospital"
                  />
                </label>
                <label>
                  Agregar servicio
                  <select
                    value=""
                    onChange={(e) => {
                      if (e.target.value && !selected.some((l) => l.serviceId === e.target.value)) {
                        setSelected([...selected, { serviceId: e.target.value, quantity: 1 }]);
                        setDirty(true);
                      }
                    }}
                  >
                    <option value="">Selecciona consulta o tratamiento</option>
                    {services.data?.items.map((s) => (
                      <option key={s.id} value={s.id}>
                        {s.name} · {money(s.price, s.currency)}
                      </option>
                    ))}
                  </select>
                </label>
                {selected.map((line, i) => (
                  <div className="commercial-line" key={line.serviceId}>
                    <span>
                      {services.data?.items.find((s) => s.id === line.serviceId)?.name ??
                        quote?.lines.find((l) => l.serviceId === line.serviceId)?.name ??
                        'Servicio seleccionado'}
                    </span>
                    <input
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
                    <button
                      type="button"
                      aria-label={`Quitar servicio ${i + 1}`}
                      onClick={() => {
                        setSelected(selected.filter((l) => l.serviceId !== line.serviceId));
                        setDirty(true);
                      }}
                    >
                      <X size={16} />
                    </button>
                  </div>
                ))}
                {!quote?.customerId && (
                  <>
                    <label>
                      Buscar empresa
                      <input
                        value={companyQuery}
                        onChange={(e) => setCompanyQuery(e.target.value)}
                      />
                    </label>
                    <label>
                      Empresa para esta compra
                      <select
                        value={company}
                        onChange={(e) => {
                          setCompany(e.target.value);
                          setDirty(true);
                        }}
                      >
                        <option value="">Sin empresa</option>
                        {companies.data?.items
                          .filter((c) => c.active)
                          .map((c) => (
                            <option key={c.id} value={c.id}>
                              {c.name}
                            </option>
                          ))}
                        {company && !companies.data?.items.some((c) => c.id === company) && (
                          <option value={company}>Empresa de la cotización</option>
                        )}
                      </select>
                    </label>
                  </>
                )}
                <p className="hint">
                  Si ya es cliente, Hospital aplica la empresa asignada a su ficha.
                </p>
                <Button disabled={!selected.length || frozen || !operator(me)} onClick={quoteNow}>
                  <Plus size={14} />
                  Cotizar con Hospital
                </Button>
              </fieldset>
            )}
            {quote && (
              <section className="commercial-quote" aria-label="Cotización Hospital">
                <h3>{opportunity.hospitalPurchaseId ? 'Compra pagada' : 'Cotización Hospital'}</h3>
                {quote.lines.map((l) => (
                  <p key={l.serviceId}>
                    <span>
                      {l.quantity} × {l.name}
                    </span>
                    <strong>{money(l.total, quote.currency)}</strong>
                  </p>
                ))}
                <p>
                  <span>Subtotal</span>
                  <span>{money(quote.subtotal, quote.currency)}</span>
                </p>
                <p>
                  <span>Convenio {quote.discountPercent}%</span>
                  <span>−{money(quote.discountAmount, quote.currency)}</span>
                </p>
                <p>
                  <strong>Total</strong>
                  <strong>{money(quote.total, quote.currency)}</strong>
                </p>
                {dirty && <p className="hint">Recalcula la cotización para aplicar los cambios.</p>}
              </section>
            )}
            {opportunity.purchasePending && (
              <p role="status" className="hint">
                Compra en comprobación. El reintento recupera el mismo registro de Hospital;
                conserva su referencia de pago.
              </p>
            )}
            {quote && !opportunity.hospitalPurchaseId && operator(me) && (
              <div className="commercial-fields">
                <label>
                  Referencia del pago
                  <input
                    maxLength={120}
                    value={reference}
                    disabled={busy || opportunity.purchasePending}
                    onChange={(e) => setReference(e.target.value)}
                  />
                </label>
                <label className="commercial-confirmation">
                  <input
                    type="checkbox"
                    checked={confirmed}
                    disabled={busy}
                    onChange={(e) => setConfirmed(e.target.checked)}
                  />
                  Confirmo que el pago ya fue recibido. Esta acción registra la compra y no cobra
                  dinero.
                </label>
                <Button disabled={!confirmed || busy || dirty} onClick={purchase}>
                  {busy
                    ? 'Comprobando Hospital…'
                    : opportunity.purchasePending
                      ? 'Recuperar compra registrada'
                      : 'Registrar compra pagada'}
                </Button>
              </div>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
